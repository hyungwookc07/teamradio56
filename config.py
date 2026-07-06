"""
config.yaml 로드. 파일이 없으면 기본값으로 동작한다.

우선순위: config.yaml > 기본값. API 키는 환경변수(ANTHROPIC_API_KEY)로도
지정 가능하며 환경변수가 있으면 그것을 우선한다.
"""

from __future__ import annotations

import copy
import logging
import os
from typing import Any

import yaml

log = logging.getLogger("config")

DEFAULTS: dict[str, Any] = {
    "app": {
        "poll_hz": 5,               # 스코어링 폴링 주기
        "console_status_sec": 1.0,  # 콘솔 상태 출력 간격
        "log_level": "INFO",
        "save_race_json": True,     # 레이스 종료 시 랩 히스토리 JSON 저장
        "speech_log": True,         # 발화 로그 JSONL (반복감/타이밍 검토용)
        "data_dir": "data",         # 녹화/결과 저장 디렉토리
    },
    "voice": {
        "enabled": True,
        "volume": 0.9,
        "language": "ko",
        "persona": (
            "당신은 20년 경력의 내구레이스 크루치프다. 침착하고 간결하며, "
            "드라이버를 신뢰하는 톤. 숫자를 나열하지 않고 판단을 말한다. "
            "무전 특유의 짧은 문장을 쓴다."
        ),
        "driver_name": "드라이버",   # 멘트에서 부르는 호칭
    },
    "tts": {
        "engine": "edge",           # edge | elevenlabs
        "edge_voice": "ko-KR-InJoonNeural",
        "edge_rate": "+10%",
        "elevenlabs_api_key": "",
        "elevenlabs_voice_id": "",
        "cache_dir": "audio_cache",
    },
    "llm": {
        "enabled": True,
        "model": "claude-haiku-4-5",
        "api_key": "",              # 비우면 ANTHROPIC_API_KEY 환경변수 사용
        "max_tokens": 200,
        "timeout_sec": 10,
        "budget_per_hour": 15,      # LLM 호출 예산 (레이스 2시간 기준 10~30회 목표)
    },
    "thresholds": {
        "fuel_warn_laps": 3.0,        # 남은 연료가 N랩 이하이면 경고
        "fuel_critical_laps": 1.5,    # 긴급 경고
        "pace_delta_sec": 0.7,        # 평소 대비 랩타임 편차가 이 이상이면 코멘트
        "gap_change_sec_per_lap": 0.4,  # 갭 변화율이 이 이상이면 코멘트
        "traffic_eta_sec": 10.0,      # 접근 예고: 도달 예상 N초 이내 (3~10초 권장)
        "proximity_m": 50.0,          # 근접(NEARBY) 진입 거리 (m)
        "alongside_m": 12.0,          # 나란히(ALONGSIDE) 판정 거리 (m)
        "tyre_temp_imbalance": 12.0,  # 좌우/전후 온도 불균형 경고 (C)
        "tyre_wear_warn": 0.35,       # 남은 수명 비율이 이 이하로 예상되면 경고
        "damage_impact_mag": 500.0,   # 이 이상 충격이면 데미지 체크 콜
    },
    "cooldowns": {                    # 같은 유형 이벤트 재발화 최소 간격 (초)
        "fuel_warning": 240,
        "traffic": 20,                # 접근 예고
        "traffic_close": 8,           # 근접/나란히 긴급 콜 (짧게 — 안전 콜)
        "traffic_update": 15,         # 지나감/떨어짐 후속
        "traffic_multi": 25,          # 다중 차량 종합
        "bridge": 20,                 # 긴급 콜 뒤 LLM 후속
        "pace_comment": 120,
        "gap_comment": 90,
        "tyre_warning": 180,
        "damage": 60,
        "penalty": 30,
        "lap_analysis": 300,     # 판단형 전략 멘트 (LLM)
        "stint_briefing": 120,
        "default": 60,
    },
}


def _deep_merge(base: dict, override: dict) -> dict:
    out = copy.deepcopy(base)
    for k, v in override.items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = _deep_merge(out[k], v)
        else:
            out[k] = v
    return out


class Config:
    def __init__(self, data: dict):
        self._data = data

    def __getitem__(self, key: str) -> Any:
        return self._data[key]

    def get(self, path: str, default: Any = None) -> Any:
        """'tts.engine' 같은 점 표기 경로로 조회."""
        cur: Any = self._data
        for part in path.split("."):
            if not isinstance(cur, dict) or part not in cur:
                return default
            cur = cur[part]
        return cur

    @property
    def anthropic_api_key(self) -> str:
        return os.environ.get("ANTHROPIC_API_KEY") or self.get("llm.api_key", "")


def load_config(path: str = "config.yaml") -> Config:
    data = DEFAULTS
    if os.path.exists(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                user = yaml.safe_load(f) or {}
            data = _deep_merge(DEFAULTS, user)
            log.info("설정 로드: %s", path)
        except yaml.YAMLError as e:
            log.error("config.yaml 파싱 실패, 기본값 사용: %s", e)
    else:
        log.info("config.yaml 없음, 기본값 사용 (config.yaml.example 참고)")
    return Config(data)
