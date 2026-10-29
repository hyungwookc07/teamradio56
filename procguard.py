"""
엔진 프로세스 가드 — "목소리 두 개가 겹치는" 상황을 구조적으로 막는다.

두 가지를 담당:
  1. 독립 실행(launch_with_lmu.bat 등, --settings 없음)은 SimHub 플러그인이
     떠 있으면 물러난다. 플러그인은 로드되는 동안 이름 있는 뮤텍스
     PLUGIN_MUTEX를 쥐고 있고(모드 무관), 독립 엔진은 시작 시와 주기적으로
     그 존재를 확인한다. 둘 다 같은 레이스를 읽으므로 같이 돌면 모든 콜이
     서로 다른 목소리로 두 번 나온다.
  2. 플러그인이 띄운 엔진(--parent-pid)은 부모(SimHub)가 사라지면 종료한다.
     SimHub이 비정상 종료하면 End()가 돌지 않아 엔진이 고아로 남고, 다음
     세션부터 새 엔진과 함께 두 번 말하게 된다.

Windows 전용 기능은 ctypes로 직접 호출하고, 다른 OS(리플레이 개발용)에서는
조용히 아무것도 하지 않는다.
"""

from __future__ import annotations

import logging
import os
import sys
import threading
import time
from typing import Callable

log = logging.getLogger("procguard")

# C# TeamRadio56Plugin.PluginMutexName과 반드시 같아야 한다
PLUGIN_MUTEX = "Local\\teamradio56-plugin"

# 플러그인이 자식 엔진에 부모 PID를 넘기는 환경변수 — 명령줄 인자로 주면
# 그 인자를 모르는 구버전 엔진(번들 exe)이 argparse 오류로 즉시 죽는다
PARENT_PID_ENV = "TEAMRADIO56_PARENT_PID"


def parent_pid_from_env() -> int:
    try:
        return int(os.environ.get(PARENT_PID_ENV, "0") or 0)
    except ValueError:
        return 0

_SYNCHRONIZE = 0x00100000
_INFINITE = 0xFFFFFFFF


def _kernel32():
    if sys.platform != "win32":
        return None
    try:
        import ctypes
        from ctypes import wintypes
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.OpenMutexW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
        k32.OpenMutexW.restype = wintypes.HANDLE
        k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        k32.OpenProcess.restype = wintypes.HANDLE
        k32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        k32.WaitForSingleObject.restype = wintypes.DWORD
        k32.CloseHandle.argtypes = [wintypes.HANDLE]
        k32.CloseHandle.restype = wintypes.BOOL
        return k32
    except Exception:  # noqa: BLE001 — 가드는 절대 엔진을 죽이면 안 된다
        return None


_ERROR_ACCESS_DENIED = 5


def plugin_active() -> bool:
    """
    SimHub 플러그인(teamradio56)이 지금 무전 엔진을 돌리고 있는가 — 뮤텍스
    존재 여부. 플러그인은 엔진(builtin 또는 자식 파이썬)이 실제로 돌 때만
    이 뮤텍스를 쥔다. 접근 거부(SimHub이 관리자 권한)도 '존재'로 본다.
    """
    k32 = _kernel32()
    if k32 is None:
        return False
    import ctypes
    handle = k32.OpenMutexW(_SYNCHRONIZE, False, PLUGIN_MUTEX)
    if handle:
        k32.CloseHandle(handle)
        return True
    return ctypes.get_last_error() == _ERROR_ACCESS_DENIED


def watch_plugin(on_detect: Callable[[], None], interval: float = 3.0) -> None:
    """독립 실행 엔진용: 플러그인이 나중에 뜨면 on_detect()를 한 번 부른다."""
    if _kernel32() is None:
        return

    def worker() -> None:
        while True:
            time.sleep(interval)
            if plugin_active():
                log.warning("SimHub teamradio56 플러그인이 시작됨 — "
                            "독립 실행 엔진은 물러납니다 (두 목소리 겹침 방지)")
                on_detect()
                return

    threading.Thread(target=worker, name="plugin-watch", daemon=True).start()


def watch_parent(pid: int, on_exit: Callable[[], None],
                 hard_exit_after: float = 8.0) -> None:
    """
    플러그인 자식 엔진용: 부모 프로세스가 끝나면 on_exit()로 정상 종료를
    요청하고, 그래도 hard_exit_after초 안에 안 끝나면 프로세스를 끊는다.
    """
    if not pid or pid <= 0:
        return

    def fire() -> None:
        log.warning("SimHub(부모 PID %d)이 종료됨 — 엔진도 종료합니다", pid)
        try:
            on_exit()
        except Exception:  # noqa: BLE001
            log.exception("정상 종료 요청 실패")
        time.sleep(hard_exit_after)
        os._exit(0)

    k32 = _kernel32()
    if k32 is not None:
        handle = k32.OpenProcess(_SYNCHRONIZE, False, pid)
        if not handle:
            log.warning("부모 프로세스(PID %d)를 열 수 없음 — 이미 종료됐다고 보고 종료", pid)
            threading.Thread(target=fire, name="parent-watch", daemon=True).start()
            return

        def win_worker() -> None:
            k32.WaitForSingleObject(handle, _INFINITE)
            k32.CloseHandle(handle)
            fire()

        threading.Thread(target=win_worker, name="parent-watch", daemon=True).start()
        return

    def posix_worker() -> None:
        while True:
            time.sleep(1.0)
            try:
                os.kill(pid, 0)
            except OSError:
                fire()
                return

    threading.Thread(target=posix_worker, name="parent-watch", daemon=True).start()
