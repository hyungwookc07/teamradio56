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
        "require_realtime": True,   # 모니터/메뉴(mInRealtime=false)에선 발화 중단
        "data_dir": "data",         # 녹화/결과 저장 디렉토리
    },
    "voice": {
        "enabled": True,
        "volume": 0.9,
        "language": "en",           # en(기본, 자연스러움) | ko(레거시 풀만)
        "persona": (
            "You are an endurance-racing crew chief with 20 years of "
            "experience. Calm, concise, trusts the driver. States judgment, "
            "not raw numbers. Talks like real team radio: short, clipped, "
            "word-first."
        ),
        "driver_name": "드라이버",   # 멘트에서 부르는 호칭
    },
    "tts": {
        "engine": "edge",           # edge | elevenlabs
        "edge_voice": "en-GB-RyanNeural",   # 영국 레이스 엔지니어 톤
        "edge_rate": "+10%",
        "elevenlabs_api_key": "",
        "elevenlabs_voice_id": "",
        "cache_dir": "audio_cache",
        "radio_fx": True,           # 무전기 효과 (밴드패스+새추레이션+스켈치)
        "radio_noise": 0.004,       # 무전 배경 노이즈 레벨 (0 = 노이즈 없음)
    },
    "reports": {                    # HUD 대체용 정기 무전 (기본 꺼짐 — 침묵 철학의 예외)
        "laptime_every_lap": False,  # 매 랩 랩타임 콜
        "status_every_laps": 0,      # N랩마다 순위/갭/연료/타이어 리포트 (0=끔)
    },
    "rest": {                       # LMU 내장 REST API 보조 소스 (스캐폴딩)
        "enabled": True,            # 미지원 환경이면 자동 비활성 — 꺼도 무방
        "base_url": "http://localhost:6397",
        "poll_sec": 3.0,            # 저주파 폴링 (전략 정보라 3초면 충분)
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
        "traffic_race_only": False,   # true면 연습/퀄리(고스트 많음)에선 트래픽 콜 끔
        "sector_yellow_calls": True,  # LMU가 mSectorFlag를 이상하게 채우면 false로
        "proximity_m": 50.0,          # 근접(NEARBY) 진입 거리 (m)
        "alongside_m": 4.6,           # 나란히 기준 거리 (m) = 차 한 대 길이 (진짜 오버랩).
                                      # 접근 속도 × 0.5초 리드 보정이 자동으로 붙어
                                      # 빠르게 파고드는 차는 그만큼 일찍 콜이 나간다
        "side_invert": False,         # 좌우 콜이 반대로 나오면 true (mPathLateral 부호)
        "start_spotter_sec": 45.0,    # 스타트 후 스포터 모드 시간 (좌우 점유만 즉시 콜)
        "tyre_temp_imbalance": 12.0,  # 좌우/전후 온도 불균형 경고 (C)
        "tyre_wear_warn": 0.35,       # 남은 수명 비율이 이 이하로 예상되면 경고
        "damage_impact_mag": 500.0,   # 이 이상 충격이면 데미지 체크 콜
        "water_temp_warn": 105.0,     # 수온 경고 (C)
        "oil_temp_warn": 115.0,       # 유온 경고 (C)
        "brake_temp_warn": 700.0,     # 브레이크 평균 온도 경고 (C)
        "fuel_save_delta": 0.1,       # 랩당 목표 대비 이 이상 오버 소모 시 코칭 (L)
        "rival_pace_diff": 0.3,       # 라이벌 페이스 차이 인텔 기준 (초/랩)
        "wetness_crossover": 0.20,    # 이 이상이면 슬릭 한계로 판단
    },
    "cooldowns": {                    # 같은 유형 이벤트 재발화 최소 간격 (초)
        "fuel_warning": 240,
        "traffic": 20,                # 접근 예고
        "traffic_close": 8,           # 근접/나란히 긴급 콜 (짧게 — 안전 콜)
        "traffic_update": 15,         # 지나감/떨어짐 후속
        "traffic_multi": 25,          # 다중 차량 종합
        "spotter": 1,                 # 스타트 혼전 좌우 점유 콜 (즉답성 우선)
        "bridge": 20,                 # 긴급 콜 뒤 LLM 후속
        "pace_comment": 120,
        "gap_comment": 90,
        "tyre_warning": 180,
        "damage": 60,
        "damage_report": 20,     # 충격 후 자동 점검 결과
        "wheel_damage": 30,      # 휠 탈락
        "penalty": 30,
        "lap_analysis": 300,     # 판단형 전략 멘트 (LLM)
        "stint_briefing": 120,
        "lap_feedback": 150,     # 트레이닝: 섹터 델타 피드백
        "track_trend": 600,
        "race_control": 15,      # FCY/레이스 스타트
        "fcy_pit_open": 45,      # FCY 피트 오픈 (전용 — FCY 콜에 눌리지 않게)
        "green_flag": 30,        # 리스타트
        "sector_yellow": 45,
        "blue_flag": 45,         # 랩핑 트레인 통과 중 반복 억제
        "race_milestone": 60,
        "lap_time_report": 25,   # HUD 대체: 매 랩 랩타임 (짧은 트랙 대응)
        "status_report": 60,     # HUD 대체: 상황 리포트
        "position_change": 45,
        "rival_pit": 90,
        "rival_pace": 240,
        "pit_limiter": 15,
        "engine_warning": 240,
        "fuel_save": 180,
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


# SimHub 플러그인 설정 UI(teamradio56.settings.txt) → config 경로 매핑.
# 플러그인이 엔진으로 우리를 실행할 때, 사용자가 SimHub 화면에서 바꾼 값이
# 이 파일 하나로 전달된다. (C#/파이썬이 같은 파일을 읽어 설정이 갈라지지 않게)
PLUGIN_SETTINGS_MAP = {
    "VoiceEnabled": "voice.enabled",
    "VoiceLanguage": "voice.language",
    "Volume": "voice.volume",
    "EdgeVoice": "tts.edge_voice",
    "RadioFx": "tts.radio_fx",
    "RadioNoise": "tts.radio_noise",
    "AlongsideMeters": "thresholds.alongside_m",
    "StartSpotterSeconds": "thresholds.start_spotter_sec",
    "SideInvert": "thresholds.side_invert",
    "TrafficRaceOnly": "thresholds.traffic_race_only",
    "LapTimeEveryLap": "reports.laptime_every_lap",
    "StatusEveryLaps": "reports.status_every_laps",
    "LlmEnabled": "llm.enabled",
    "LlmApiKey": "llm.api_key",
    "LlmBudgetPerHour": "llm.budget_per_hour",
    "RequireRealtime": "app.require_realtime",
    "SpeechLog": "app.speech_log",
}

# 수다스러움 프리셋 → 쿨다운 배율 (클수록 조용)
CHATTER_SCALE = {"quiet": 1.8, "normal": 1.0, "chatty": 0.6}


def _parse_scalar(text: str) -> Any:
    low = text.strip().lower()
    if low in ("true", "false"):
        return low == "true"
    try:
        return int(text)
    except ValueError:
        pass
    try:
        return float(text)
    except ValueError:
        return text


def _set_path(data: dict, path: str, value: Any) -> None:
    parts = path.split(".")
    cur = data
    for part in parts[:-1]:
        cur = cur.setdefault(part, {})
    cur[parts[-1]] = value


def read_plugin_settings(path: str) -> dict:
    """
    SimHub 플러그인이 쓴 key=value 설정을 config 구조(dict)로 변환.
    파일이 없거나 깨져 있으면 빈 dict — 호출부가 기본값으로 진행한다.
    """
    out: dict = {}
    try:
        with open(path, "r", encoding="utf-8") as f:
            lines = f.readlines()
    except OSError:
        return out

    raw: dict[str, str] = {}
    for line in lines:
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        raw[key.strip()] = value.strip()

    for key, cfg_path in PLUGIN_SETTINGS_MAP.items():
        if key in raw:
            _set_path(out, cfg_path, _parse_scalar(raw[key]))

    # 말 속도는 정수(%) → edge-tts 형식("+10%")
    if "SpeechRatePercent" in raw:
        try:
            pct = int(float(raw["SpeechRatePercent"]))
            _set_path(out, "tts.edge_rate", f"{pct:+d}%")
        except ValueError:
            pass

    # 수다스러움 프리셋 → 전 쿨다운에 배율
    scale = CHATTER_SCALE.get(raw.get("ChatterPreset", "").lower())
    if scale and scale != 1.0:
        out["cooldowns"] = {k: round(v * scale)
                            for k, v in DEFAULTS["cooldowns"].items()}
    return out


def load_config(path: str = "config.yaml",
                plugin_settings: str | None = None) -> Config:
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

    # SimHub 플러그인 설정이 있으면 config.yaml보다 우선 (UI가 진실의 원천)
    if plugin_settings:
        override = read_plugin_settings(plugin_settings)
        if override:
            data = _deep_merge(data, override)
            log.info("SimHub 플러그인 설정 적용: %s (%d개 섹션)",
                     plugin_settings, len(override))
        else:
            log.info("플러그인 설정을 읽지 못함 — config.yaml/기본값 사용: %s",
                     plugin_settings)
    return Config(data)
