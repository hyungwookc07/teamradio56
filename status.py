"""
엔진 상태를 파일로 내보낸다 — SimHub 플러그인 설정 화면이 읽어서 표시.

형식은 플러그인 설정 파일과 같은 key=value. JSON 파서 없이 양쪽이
읽고 쓸 수 있어 의존성이 생기지 않는다. 1초에 한 번만 쓴다.
"""

from __future__ import annotations

import logging
import os
import tempfile
import time
from typing import Optional

log = logging.getLogger("status")


class StatusWriter:
    """상태 파일 기록기. 경로가 없으면 아무것도 하지 않는다 (단독 실행 시)."""

    def __init__(self, path: Optional[str], interval: float = 1.0):
        self.path = path
        self.interval = interval
        self._last = 0.0
        self._failed = False
        if path:
            log.info("상태 파일: %s", path)

    def write(self, fields: dict, force: bool = False) -> None:
        if not self.path or self._failed:
            return
        now = time.monotonic()
        if not force and now - self._last < self.interval:
            return
        self._last = now

        lines = ["# teamradio56 엔진 상태 (자동 생성)",
                 f"updated = {time.strftime('%H:%M:%S')}"]
        for key, value in fields.items():
            if isinstance(value, bool):
                value = "true" if value else "false"
            elif isinstance(value, float):
                value = f"{value:.1f}"
            # 줄바꿈은 형식을 깨뜨리므로 공백으로
            lines.append(f"{key} = {str(value).replace(chr(10), ' ')}")

        try:
            self._atomic_write("\n".join(lines) + "\n")
        except OSError as e:
            self._failed = True     # 상태 파일 때문에 엔진이 죽지 않게
            log.warning("상태 파일 기록 실패 (이후 생략): %s", e)

    def _atomic_write(self, text: str) -> None:
        """플러그인이 반쯤 쓴 파일을 읽지 않도록 임시 파일 후 교체."""
        directory = os.path.dirname(os.path.abspath(self.path)) or "."
        os.makedirs(directory, exist_ok=True)
        fd, tmp = tempfile.mkstemp(dir=directory, suffix=".tmp")
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                f.write(text)
            os.replace(tmp, self.path)
        except BaseException:
            try:
                os.unlink(tmp)
            except OSError:
                pass
            raise

    def close(self) -> None:
        """엔진 종료를 알린다 (플러그인이 '중지됨'으로 표시)."""
        if not self.path or self._failed:
            return
        self.write({"running": False, "state": "엔진 종료됨"}, force=True)
