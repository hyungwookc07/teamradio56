"""
LMU 내장 REST API 보조 소스 (스캐폴딩).

LMU의 게임 UI는 로컬 웹앱이라 게임 실행 중 로컬 HTTP 서버가 떠 있다
(커뮤니티 확인 기준 기본 포트 6397). 공유 메모리에 없는 LMU 고유 정보
(가상 에너지, 날씨 예보, 피트 전략)를 여기서 저주파로 보충한다.

설계 원칙:
  - 메인 루프를 절대 블로킹하지 않는다 — 전용 스레드에서 폴링(기본 3초).
  - 게임이 없거나 포트가 다르면 조용히 비활성 (앱은 공유 메모리만으로 동작).
  - 엔드포인트/응답 스키마는 실차 검증 전이므로, 파싱 getter는 전부
    방어적으로 작성하고 실패 시 None을 반환한다. 실제 응답을 확보하면
    (tools/probe_rest.py 참고) 아래 TODO 지점만 채우면 된다.
"""

from __future__ import annotations

import json
import logging
import threading
import time
import urllib.request
from typing import Any, Optional

log = logging.getLogger("rest")

# 폴링할 엔드포인트 후보 — 실차 검증 후 확정한다 (tools/probe_rest.py로 수집).
# key: 내부 이름, value: 경로
ENDPOINTS = {
    "sessions": "/rest/sessions",
    "standings": "/rest/watch/standings",
    "session_info": "/rest/watch/sessionInfo",
    "pit_estimate": "/rest/strategy/pitstop-estimate",
    "garage": "/rest/garage/getPlayerGarageData",
    "weather": "/rest/sessions/weather",
}

CONNECT_RETRY_SEC = 30.0     # 연결 실패 시 재시도 간격
FAIL_DISABLE_COUNT = 5       # 연속 실패 이 이상이면 '미연결'로 전환


class RestTelemetry:
    """LMU REST 폴러 — 최신 응답을 보관하고 타입 getter로 노출한다."""

    def __init__(self, cfg):
        self.enabled = bool(cfg.get("rest.enabled", True))
        self.base_url = cfg.get("rest.base_url", "http://localhost:6397").rstrip("/")
        self.poll_sec = float(cfg.get("rest.poll_sec", 3.0))
        self._data: dict[str, Any] = {}          # 엔드포인트 키 → 마지막 JSON
        self._data_t: dict[str, float] = {}      # 엔드포인트 키 → 수신 시각
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self.connected = False
        self._announced = False

    # -- 라이프사이클 ---------------------------------------------------------

    def start(self) -> None:
        if not self.enabled or self._thread is not None:
            return
        self._thread = threading.Thread(target=self._run, name="rest-poller",
                                        daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=2.0)

    def _run(self) -> None:
        log.info("REST 폴러 시작 (%s, %.0f초 간격) — 미지원 환경이면 자동 비활성",
                 self.base_url, self.poll_sec)
        fails = 0
        while not self._stop.is_set():
            ok_any = False
            for key, path in ENDPOINTS.items():
                if self._stop.is_set():
                    return
                payload = self._fetch(path)
                if payload is not None:
                    ok_any = True
                    with self._lock:
                        self._data[key] = payload
                        self._data_t[key] = time.monotonic()
            if ok_any:
                fails = 0
                if not self.connected:
                    self.connected = True
                    if not self._announced:
                        self._announced = True
                        log.info("LMU REST API 연결됨 — 보조 데이터 사용 가능")
                self._stop.wait(self.poll_sec)
            else:
                fails += 1
                if self.connected and fails >= FAIL_DISABLE_COUNT:
                    self.connected = False
                    log.info("LMU REST API 연결 끊김 — 공유 메모리만으로 계속")
                self._stop.wait(CONNECT_RETRY_SEC if not self.connected
                                else self.poll_sec)

    def _fetch(self, path: str) -> Optional[Any]:
        try:
            req = urllib.request.Request(self.base_url + path,
                                         headers={"Accept": "application/json"})
            with urllib.request.urlopen(req, timeout=2.0) as resp:
                if resp.status != 200:
                    return None
                return json.loads(resp.read().decode("utf-8", errors="replace"))
        except Exception:
            return None    # 게임 없음/포트 다름/엔드포인트 없음 — 전부 조용히

    # -- 원시 접근 ------------------------------------------------------------

    def raw(self, key: str, max_age_sec: float = 30.0) -> Optional[Any]:
        """엔드포인트 키의 최신 응답. 오래됐으면 None."""
        with self._lock:
            t = self._data_t.get(key)
            if t is None or time.monotonic() - t > max_age_sec:
                return None
            return self._data.get(key)

    # -- 타입 getter (실차 응답 확보 후 TODO 채우기) ---------------------------
    #
    # 아래 getter들은 응답 스키마가 확정되지 않아 '후보 키를 방어적으로 탐색'
    # 하는 형태다. tools/probe_rest.py로 실제 JSON을 확보하면 정확한 경로로
    # 교체한다. 실패하면 None — 호출부는 항상 None을 처리해야 한다.

    def virtual_energy(self) -> Optional[dict]:
        """
        가상 에너지 상태 (하이퍼카/LMP2 연료 전략의 실제 단위).
        기대 형태: {"remaining": float(0..1 또는 절대값), "per_lap": float|None}
        TODO(실차): pit_estimate/garage 응답에서 실제 키 확인 후 확정.
        """
        for src in ("pit_estimate", "garage"):
            payload = self.raw(src)
            found = _find_first(payload, ("virtualEnergy", "virtual_energy",
                                          "energyRemaining", "energy"))
            if isinstance(found, (int, float)):
                return {"remaining": float(found), "per_lap": None, "source": src}
            if isinstance(found, dict):
                return {**found, "source": src}
        return None

    def weather_forecast(self) -> Optional[list]:
        """
        세션 날씨 타임라인. 기대 형태: [{"time": ..., "rain": ...}, ...]
        TODO(실차): weather/sessions 응답의 실제 구조 확인 후 확정.
        """
        for src in ("weather", "sessions"):
            payload = self.raw(src)
            found = _find_first(payload, ("forecast", "weatherBlocks",
                                          "forecastBlocks", "weather"))
            if isinstance(found, list) and found:
                return found
        return None

    def pit_strategy(self) -> Optional[dict]:
        """
        현재 피트 전략 (주유량/타이어/수리). 스톱 전 브리핑용.
        TODO(실차): pit_estimate 응답 구조 확인 후 확정.
        """
        payload = self.raw("pit_estimate")
        return payload if isinstance(payload, dict) else None


def _find_first(obj: Any, keys: tuple, depth: int = 0) -> Optional[Any]:
    """중첩 JSON에서 후보 키 중 처음 발견되는 값 (스키마 확정 전 임시 탐색)."""
    if obj is None or depth > 6:
        return None
    if isinstance(obj, dict):
        for k in keys:
            if k in obj:
                return obj[k]
        for v in obj.values():
            found = _find_first(v, keys, depth + 1)
            if found is not None:
                return found
    elif isinstance(obj, list):
        for item in obj[:20]:
            found = _find_first(item, keys, depth + 1)
            if found is not None:
                return found
    return None
