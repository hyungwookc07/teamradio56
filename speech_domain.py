"""
발화 숫자 도메인 — 음성 팩(문장 세그먼트)과 런타임 판단이 같은 값을 보는 단일 출처.

  - SEG_*: 문장 단위 사전 생성 도메인 (voice.iter_pregen_sentences가 열거).
  - 한계/술어: 분석기가 무의미한 값(피트·FCY·스핀·센서 튐)을 말하지 않게 거르는 기준.
    술어는 모두 *표시 반올림한* 값으로 판단한다 — 사전 생성 문장과 같은 숫자를 봐야
    "도메인 안 = 팩에 있음"이 성립한다.

C# SpeechDomain.cs와 값·순서가 같아야 한다 (replay-check가 문장 집합과 퍼즈 결과를 대조).
이 모듈은 messages만 import한다 (analyzers/voice/tts 어디서나 순환 없이 쓰도록).
"""

from __future__ import annotations

import math
import os
import sys

# -- 표시 반올림 -----------------------------------------------------------------


def fixed(x: float, d: int) -> float:
    """f"{x:.{d}f}"로 말하는 값 (C# double.Parse(Messages.PyFixed(x, d)))."""
    return float(f"{x:.{d}f}")


def gap_text_value(g: float) -> float:
    """seconds_text와 같은 분기 — 10초 미만은 소수 1자리, 이상은 정수."""
    return fixed(g, 1) if g < 10 else fixed(g, 0)


def laps_value(n: float) -> int:
    """laps_text가 말하는 정수 (파이썬 round = 짝수 반올림)."""
    return int(round(n))


# -- 한계 (술어 기준) ------------------------------------------------------------

GAP_SEG_MAX = 600                  # 팩이 말하는 갭 상한 (정수 초) — 그 위는 kokoro 세션에서 자막만
RATE_MAX = 9.9
PACE_MIN, PACE_MAX = 0.7, 9.9
RIVAL_DIFF_MIN, RIVAL_DIFF_MAX = 0.3, 5.0
RIVAL_LAPS_MIN, RIVAL_LAPS_MAX = 1, 16
RIVAL_PIT_WHOLE_MAX = 30            # 통문장 P1~30, 그 위는 문장 조립
TYRE_HOT_MIN, TYRE_HOT_MAX = 12, 80
TYRE_WEAR_MAX = 30
PIT_WINDOW_WHOLE_MAX = 40           # 통문장 0~40, 그 위는 문장 조립
PIT_WINDOW_MAX = 99
WING_MM_MIN, WING_MM_MAX = 10, 100
BRAKE_T_MIN, BRAKE_T_MAX, BRAKE_T_STEP = 700, 1500, 10
REPAIR_MIN, REPAIR_MAX = 0.5, 9.9
FUEL_TARGET_MIN, FUEL_TARGET_MAX = 0.5, 15.0
FUEL_SAVE_MIN, FUEL_SAVE_MAX = 0.1, 3.0
CLASS_POS_MAX = 60
BRIEF_MINUTES_MAX = 1440
BRIEF_LAPS_MAX = 100
BRIEF_CARS_MIN, BRIEF_CARS_MAX = 2, 60
BRIEF_POS_MAX = 60
BRIEF_TEMP_MIN, BRIEF_TEMP_MAX = 1, 60
BRIEF_FUEL_MIN, BRIEF_FUEL_MAX = 1, 130
LAPTIME_T10_MIN, LAPTIME_T10_MAX = 600, 2699    # 1:00.0 ~ 4:29.9
STATUS_FUEL_MAX = 99
STATUS_TYRE_MAX = 20
COACH_MIN, COACH_MAX = 0.1, 2.9
COACH_SECTORS = (1, 2, 3)
MULTI_MAX_CARS = 3
DENT_MAX_ZONES = 3
MULTI_CLASSES = ("Hypercar", "LMP2", "GT3", "GTE", "faster car")

# -- 문장 단위 사전 생성 도메인 (C# SpeechDomain.Seg*와 값·순서 동일) -------------
# 도메인 밖 값은 위 술어가 거르거나(억제) kokoro 세션에서 자막만 나간다.
SEG_GAPS = [i / 10 for i in range(100)] + [float(s) for s in range(10, GAP_SEG_MAX + 1)]
SEG_RATES = [i / 10 for i in range(1, 100)]
SEG_PACE = [i / 10 for i in range(7, 100)]
SEG_RIVAL_DIFF = [i / 10 for i in range(3, 51)]
SEG_RIVAL_LAPS = range(RIVAL_LAPS_MIN, RIVAL_LAPS_MAX + 1)
SEG_TYRE_HOT = range(TYRE_HOT_MIN, TYRE_HOT_MAX + 1)
SEG_TYRE_LAPS = range(0, TYRE_WEAR_MAX + 1)
SEG_MULTI_CLASSES = MULTI_CLASSES
SEG_MULTI_MAX_CARS = MULTI_MAX_CARS
SEG_DENT_MAX_ZONES = DENT_MAX_ZONES
# 2단계 — 차량 상태/연료 세이브/세션 브리핑
SEG_WING_MM = range(WING_MM_MIN, WING_MM_MAX + 1)
SEG_BRAKE_T = range(BRAKE_T_MIN, BRAKE_T_MAX + 1, BRAKE_T_STEP)   # health: 10도 단위 반올림
SEG_REPAIR = [i / 10 for i in range(5, 100)]                     # 10.0 이상은 repair_cost_big
SEG_FUEL_TARGET = [i / 10 for i in range(5, 151)]
SEG_FUEL_SAVE = [i / 10 for i in range(1, 31)]
SEG_PIT_WINDOW = range(PIT_WINDOW_WHOLE_MAX + 1, PIT_WINDOW_MAX + 1)   # 0~40은 통문장
SEG_RIVAL_PIT = range(RIVAL_PIT_WHOLE_MAX + 1, CLASS_POS_MAX + 1)     # 1~30은 통문장
SEG_COACH = [i / 10 for i in range(1, 30)]
# 3단계 — HUD 대체 리포트 (랩타임 콜, 상황 리포트)
SEG_LAPTIME_T10 = range(LAPTIME_T10_MIN, LAPTIME_T10_MAX + 1)
SEG_STATUS_POS = range(1, CLASS_POS_MAX + 1)
SEG_STATUS_FUEL = range(0, STATUS_FUEL_MAX + 1)
SEG_STATUS_TYRES = range(0, STATUS_TYRE_MAX + 1)
SEG_BRIEF_MINUTES = range(1, BRIEF_MINUTES_MAX + 1)   # brief_length_text가 126개 값으로 묶는다
SEG_BRIEF_LAPS = range(1, BRIEF_LAPS_MAX + 1)
SEG_BRIEF_CARS = range(BRIEF_CARS_MIN, BRIEF_CARS_MAX + 1)
SEG_BRIEF_POS = range(1, BRIEF_POS_MAX + 1)
SEG_BRIEF_TEMP = range(BRIEF_TEMP_MIN, BRIEF_TEMP_MAX + 1)
SEG_BRIEF_FUEL = range(BRIEF_FUEL_MIN, BRIEF_FUEL_MAX + 1)

# -- 술어 (파이썬 snake_case ↔ C# PascalCase) -------------------------------------


def gap_rate_ok(rate: float) -> bool:
    """변화율 랩당 10초 이상은 피트/FCY 산물 — 말하지 않는다.
    큰 갭 자체는 내구 레이스에서 정상이라 거르지 않는다."""
    return fixed(abs(rate), 1) <= RATE_MAX


def gap_in_pack(gap: float) -> bool:
    return gap_text_value(gap) <= GAP_SEG_MAX


def pace_ok(delta: float) -> bool:
    return fixed(abs(delta), 1) <= PACE_MAX


def rival_diff_ok(diff: float) -> bool:
    return fixed(diff, 1) <= RIVAL_DIFF_MAX


def rival_pit_ok(p: int) -> bool:
    return p <= CLASS_POS_MAX


def tyre_hot_ok(delta: float) -> bool:
    return fixed(delta, 0) <= TYRE_HOT_MAX


def tyre_wear_ok(laps_left: float) -> bool:
    # NaN/Inf(센서 튐)는 int()가 예외 — 말하지 않는다 (C#도 같은 판정)
    return math.isfinite(laps_left) and laps_value(laps_left) <= TYRE_WEAR_MAX


def pit_window_ok(n: int) -> bool:
    return n <= PIT_WINDOW_MAX


def wing_ok(drop_m: float) -> bool:
    return fixed(drop_m * 1000, 0) <= WING_MM_MAX


def brake_ok(t: int) -> bool:
    return t <= BRAKE_T_MAX


def repair_big(delta: float) -> bool:
    return fixed(delta, 1) > REPAIR_MAX


def fuel_save_ok(target: float, delta: float) -> bool:
    return (FUEL_TARGET_MIN <= fixed(target, 1) <= FUEL_TARGET_MAX
            and fixed(delta, 1) <= FUEL_SAVE_MAX)


def coach_ok(delta: float) -> bool:
    return fixed(abs(delta), 1) <= COACH_MAX


def laptime_t10(sec: float) -> int:
    """랩타임을 0.1초 정수로 — f"{sec:.1f}"에서 소수점을 뺀 값."""
    return int(f"{sec:.1f}".replace(".", ""))


def laptime_in_pack(sec: float) -> bool:
    return LAPTIME_T10_MIN <= laptime_t10(sec) <= LAPTIME_T10_MAX


def status_fuel_ok(fuel_laps: float) -> bool:
    return math.isfinite(fuel_laps) and 0 <= laps_value(fuel_laps) <= STATUS_FUEL_MAX


def brief_grid_ok(n: int, cp: int) -> bool:
    return BRIEF_CARS_MIN <= n <= BRIEF_CARS_MAX and 1 <= cp <= BRIEF_POS_MAX


def brief_temp_ok(t: float) -> bool:
    return BRIEF_TEMP_MIN <= fixed(t, 0) <= BRIEF_TEMP_MAX


def brief_fuel_ok(f: float) -> bool:
    return BRIEF_FUEL_MIN <= fixed(f, 0) <= BRIEF_FUEL_MAX


# -- 트랙 이름 목록 (en 브리핑 첫 문장) -------------------------------------------


def _base_dir() -> str:
    # PyInstaller onefile 번들에서는 데이터 파일이 sys._MEIPASS에 풀린다
    return getattr(sys, "_MEIPASS", None) or os.path.dirname(os.path.abspath(__file__))


TRACK_NAMES_FILE = os.path.join(_base_dir(), "voice_lines", "track_names_en.txt")


def load_track_names(path: str) -> list[str]:
    """트랙 이름 목록 파일 — 한 줄에 하나, 빈 줄과 '#' 주석은 무시.
    utf-8-sig: 윈도 편집기가 붙인 BOM을 C# File.ReadAllLines(UTF8)처럼 떼어낸다."""
    with open(path, "r", encoding="utf-8-sig") as f:
        return [ln.strip() for ln in f
                if ln.strip() and not ln.strip().startswith("#")]


_TRACKS: tuple | None = None


def track_names_en() -> tuple:
    global _TRACKS
    if _TRACKS is None:
        try:
            _TRACKS = tuple(load_track_names(TRACK_NAMES_FILE))
        except OSError:
            _TRACKS = ()
    return _TRACKS


def known_track_en(name: str) -> bool:
    return name in track_names_en()
