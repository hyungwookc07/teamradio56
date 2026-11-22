"""
차량 컨디션 분석기 — 파손(충격/차체/부품 탈락)과 엔진/브레이크 온도.

파손 흐름 (판단하는 엔지니어의 핵심 — 드라이버가 아니라 툴이 확인한다):
  1. 충격 감지 (5Hz): mLastImpactET 변화 + 크기 임계값 → 즉시 데미지 콜
     (mDentSeverity로 대략적 부위 추정, 브리지로 LLM 후속)
  2. 충격 8초 뒤 자동 점검 리포트: 덴트 심각도/휠·타이어/공기압 누출을
     데이터로 훑고 결과를 불러준다 ("체크했어, 가벼운 자국뿐이야" /
     "우측 앞 공기압 빠지는 중, 박스 준비")
  3. 이후 랩에서 충격 전 페이스와 비교 → 유의미하게 느려졌으면
     "수리할지 말지" LLM 판단 트리거, 영향 없으면 안심 멘트 후 이슈 해제
  4. 피트 인 = 수리로 간주하고 데미지 이슈 리셋

충격과 무관한 상시 감시: 휠 탈락(즉시 콜), 슬로우 펑처(공기압 지속 하락).
임계값 초과 시에만 발화. 쿨다운으로 반복 억제.

내 레이스가 끝나면(체커·DNF) 휠 탈락 콜만 남긴다 — 쿨다운 랩에서도 차를 잃을 수 있는
안전 문제다. 충격·점검 리포트·윙·얼라인·리어 불안정·슬로우 펑처·부품 탈락·엔진/브레이크
온도·수리 판단은 차가 곧 차고로 들어가므로 말하지 않는다.
"""

from __future__ import annotations

import logging
import math
from typing import Optional

from events import Event, EventBus, EventType, Priority
from messages import and_list, join_problems, msg, wheel_name, zone_display
from speech_domain import DENT_MAX_ZONES, brake_ok, repair_big, wing_ok
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("health")

# mDentSeverity 8개 존의 위치 근사 (정면부터 시계방향 관례).
# 정확한 존 매핑은 게임/차량마다 다를 수 있어 '대략적 위치'로만 사용한다.
DENT_ZONES = ["front", "front right", "right", "rear right",
              "rear", "rear left", "left", "front left"]

REPAIR_PACE_DELTA = 0.5      # 데미지 후 랩당 이 이상 느려지면 수리 판단 트리거
NO_EFFECT_DELTA = 0.2        # 이 이하면 "영향 없음"으로 이슈 해제
OBSERVE_LAPS = 2             # 충격 후 관찰 랩 수

WHEEL_NAMES = ("front left", "front right", "rear left", "rear right")   # FL FR RL RR
REPORT_DELAY_SEC = 8.0       # 충격 후 자동 점검 리포트까지 대기
IMPACT_PRESSURE_DROP_KPA = 12.0   # 충격 전후 이 이상 빠지면 누출로 판단
# 슬로우 펑처: 세션 중 최고 공기압 대비 이 이상 하락 (온도에 따른 자연 변동
# ±10~20kPa를 넘는 값으로 설정해 오탐 방지)
SLOW_PUNCTURE_DROP_KPA = 25.0

# 프론트 윙 감시 — mFrontWingHeight(m)가 충격 뒤 같은 속도에서 지속적으로 낮아졌으면 스플리터/
# 윙 손상. LMU 스포츠카에서 이 필드가 동적으로 채워지는지는 실차 검증 필요 — 정적(상수)이면
# 충격 전후가 같아 하락 0으로 판정이 끝나고, 0/없음이면 샘플이 아니라 조용히 쉰다. 리어 윙은
# 전용 필드가 없어 리어 존 덴트 + 부품 탈락 + 페이스 관찰로 커버한다.
#
# 같은 것끼리 비교한다: 예전 감지는 150km/h 이상 샘플 전부의 EMA를 첫 충격 순간에 얼려 그 뒤 매
# 틱 비교했다. 스플리터 높이는 공력 하중으로 속도² 에 비례해 눌리고(LMH 근사 170km/h ~6mm,
# 300~330km/h ~19~23mm) 고속 제동에선 브레이크 다이브(~3mm/g, 공력 포함 2~4g)가 더해져, 건강한
# 차의 높이가 150km/h 이상 대역 안에서만 15mm 넘게 움직인다 — 임계값(10mm)보다 크다. 중속
# 구간(~200km/h)에서 가볍게 닿은 뒤 긴 직선을 달리면 기준(중속 높이)과 EMA(고속 높이) 차이를
# 손상으로 읽어 멀쩡한 차에 "Front aero down 10 millimetres" 오탐 (리뷰 재현).
#
#   · 속도 구간: bin = floor((v − 150) / 10) — 10km/h 폭. 같은 구간 안 공력 압축 차이는 330km/h
#     · 압축 23mm@300 기준으로도 ~1.7mm(d/dv C·(v/300)² = 2·23mm·330/300² × 10km/h)라 임계값의
#     1/6 — 게다가 구간 중앙값끼리라 보통은 그보다 훨씬 작다.
#   · 제동·큰 과도 제외: 연속 틱 속도 차로 감속을 구한다 — (직전 속도 − 지금 속도)/3.6/dt.
#     직전 두 구간(틱 n−2→n−1, n−1→n) 모두 ≤ 4.9m/s²(0.5g)이고 각 dt가 0초 초과 0.5초 이하여야
#     샘플이다. 0.5g는 고속 리프트오프(공력 저항+엔진 브레이크 ~0.4~0.5g, 다이브 ≤ 1.5mm)까지는
#     넣고 실제 제동(1~4g, 다이브 3~12mm)은 빼는 경계. 두 구간을 보는 건 제동이 끝난 다음 틱에
#     아직 남은 피치 복원(~0.2초)을 빼려고. 틱 간격이 0.5초(5Hz의 2.5배)를 넘으면 그 속도 차는
#     감속이 아니다(일시정지·순간이동) — 다음 두 틱이 지나야 다시 샘플.
#     강한 제동은 10km/h 구간을 1~2틱에 지나가 구간 중앙값이 이미 이겨내지만, 긴 트레일 브레이킹
#     이나 제동 뒤 피치 흔들림이 한 구간에 몰리는 경우를 막는 방어다.
#     (텔레메트리에 종가속도 필드를 따로 매핑하지 않는다 — 파이썬 스냅샷·C# PlayerInfo·리플레이
#     로더가 같은 속도 필드로 같은 산술을 하니 두 엔진이 바이트까지 같고, 옛 리플레이도 돈다.)
#   · 피트레인 · 높이 없음/비유한/≤0 · 150km/h 미만은 샘플이 아니다.
#   · 기준 = 첫 충격 직전까지 모은 최근 301샘플(5Hz 고속 ~60초 — 보통 트랙 한 랩의 150km/h 이상
#     구간)의 구간별 묶음. 5샘플 이상인 구간이 3개 미만이면 기준 없음 → 그 충격만 판정하지 않고
#     기준 버퍼를 그 충격 뒤부터 새로 모은다 (얼라인 a48cf98과 같은 규칙 — 스틴트를 끄지 않는다).
#   · 판정 = 충격 뒤 새로 모은 샘플(최근 301개)에서 충격 후 5샘플 이상 · 기준 5샘플 이상인 구간이
#     3개가 되는 첫 틱에 한 번. 하락 = 맞춘 구간별 (기준 중앙값 − 충격 후 중앙값)의 중앙값 ≥ 10mm면
#     콜. 5샘플 = 1초(노이즈 ±0.5mm의 중앙값), 3구간 = 한 구간이 튀어도 중앙값이 버틴다.
#     중앙값은 정렬한 목록의 (len−1)//2 번째(짝수면 아래쪽 가운데 — 하락은 작은 쪽으로, C# 같은 정의).
#   · 그 충격의 점검 리포트(+8초)가 나가기 전엔 판정하지 않는다 — 같은 damage_report 쿨다운이라
#     윙 콜이 먼저 수락되면 리포트(펑크·휠 결과)가 버려진다. 판정 뒤 쿨다운으로 거절되면 대기로
#     남아 다음 틱에 다시 민다. 타이어 펑크/탈락·공기압 누출이 잡혔으면 높이 하락은 타이어 탓이라
#     콜하지 않는다 (판정만 미루고 샘플은 계속 모은다).
#   · 다중 충격은 최초 기준을 유지하고 충격마다 판정 버퍼를 새로 연다 (판정은 충격당 한 번).
#   · 횡가속은 보지 않는다 — 같은 속도면 공력 하중이 같고, 롤은 중앙 높이를 거의 바꾸지 않는다.
WING_SAMPLE_SPEED_KMH = 150.0
WING_BIN_KMH = 10.0          # 속도 구간 폭
WING_DROP_M = 0.010          # 같은 속도에서 10mm 이상 하락이면 윙 손상 의심
WING_DECEL_MAX = 4.9         # m/s² (0.5g) — 직전 두 틱 구간 감속 상한 (제동 다이브 제외)
WING_TICK_GAP_MAX = 0.5      # 초 — 이보다 긴 틱 간격의 속도 차는 감속으로 보지 않는다
WING_BASE_SAMPLES = 301      # 충격 전 기준 버퍼 (최근 고속 샘플 ~1랩)
WING_POST_SAMPLES = 301      # 충격 후 판정 버퍼 상한
WING_BIN_MIN = 5             # 구간 하나에 필요한 샘플 (기준·충격 후 각각)
WING_MIN_BINS = 3            # 판정에 필요한 맞춘 구간 수

# 얼라인(조향 쏠림) 감시 — 서스펜션/스티어링 지오메트리가 틀어지면 직선에서도
# 조향을 한쪽으로 유지해야 차가 곧게 간다. '차가 실제로 곧게 가는' 샘플의 조향만
# 모아 충격 전후 중앙값을 비교한다. (트랙 캠버/바람에 의한 상시 오프셋은 전후 비교로
# 상쇄된다.)
#
# 직선 판정은 조향 크기가 아니라 차의 움직임으로 한다: |조향| ≤ 0.20만 보던 예전
# 판정은 150km/h 넘는 고속 스위퍼(조향 0.1~0.18)를 전부 '직선'으로 넣어서, EMA가
# 충격 지점과 15초 뒤 지점의 코너 구성(좌우 비율) 차이를 오프셋 변화로 읽었다 —
# 멀쩡한 차에 가벼운 접촉 뒤 "Steering pull" 오탐 (사용자 제보).
#   · 횡가속 |요레이트| × 속도 ≤ 1.5m/s² (0.15g): 150km/h 이상에서 조향이 필요한 코너는
#     어떤 것이든 8m/s² 이상이고, 1.5m/s²는 250km/h에서 반경 3km — 사실상 직선이다.
#     직선의 미세 수정·노면 요철은 이 안에 든다. 손상된 차도 드라이버가 오프셋을 잡고
#     곧게 가므로 요레이트는 0 근처 — 오프셋이 그대로 측정된다.
#   · |횡속도| ≤ 3.0m/s: 슬라이드(4m/s+, INSTAB_LAT_SLIP_MS)만 뺀다. 코너링은 위
#     요레이트 조건이 이미 뺀다. 리어 토가 틀어지면 차는 요레이트 0으로 비스듬히(차체
#     슬립각 β ≈ 리어 토) 곧게 가고 드라이버는 같은 각만큼 조향을 잡는다 — 횡속도 =
#     속도 × sin β. 락 ~17.5°에서 오프셋 0.05는 β 0.9°(290km/h에서 1.2m/s), 0.10은
#     β 1.75°(2.5m/s), 300km/h에서 β 2°도 2.9m/s — 이 '게걸음' 샘플이 바로 재야 할
#     오프셋이라 빠지면 안 된다 (1.0m/s였을 땐 리어 손상의 고속 샘플이 전부 빠져 판정
#     버퍼가 차지 않았다).
#   · |조향| ≤ 0.20은 센서 튐 방지용 상한으로만 남긴다.
# 요레이트/횡속도/조향 중 하나라도 없으면 샘플이 아니다 (감지가 조용히 쉬고, 콜은 없다).
#
# 기준 = 첫 충격 직전까지 모은 직선 샘플 최근 101개의 중앙값 (25개 = 직선 5초 미만이면
# 기준 없음 → 그 충격으로는 콜하지 않는다). 기준 없는 충격(1랩 T1 접촉·피트 아웃 직후·
# 피트레인 안 접촉·세션 중간 합류의 묵은 충격 기록)은 그 충격만 판정하지 않고, 기준
# 버퍼를 그 충격 뒤부터 새로 모은다 — 다음 충격이 25개 이상이면 기준을 굳히고 판정한다
# (그 충격이 이미 얼라인을 틀었다면 기준에 섞여 콜이 없다 — 오탐보다 나은 쪽). 판정 = 충격 뒤 새로 모은 직선 샘플 51개의
# 중앙값을 버퍼가 찼을 때 한 번. 중앙값은 정렬한 목록의 len//2 번째 (C#과 같은 정의 —
# 크기가 홀수라 가운데 원소). 같은 위치 비교가 아니어도 직선끼리라 트랙 배치가 섞이지 않는다.
# 타이어가 펑크/탈락이거나 공기압 누출이 잡혔으면 쏠림은 타이어 탓이라 콜하지 않는다
# (슬로우 펑처·점검 리포트가 부른다) — 판정만 미루고 샘플은 계속 모은다.
STEER_SAMPLE_SPEED_KMH = 150.0
STEER_SAMPLE_MAX = 0.20      # 센서 튐 상한 (직선 판정은 아래 두 값)
STEER_LAT_ACC_MAX = 1.5      # m/s² — |요레이트|×속도가 이 이하일 때만 '곧게 가는 중'
STEER_LAT_VEL_MAX = 3.0      # m/s — |횡속도| 이 이하 (슬라이드 아님, 리어 토 게걸음은 포함)
STEER_SHIFT_MIN = 0.03       # 락 대비 3% 이상 오프셋 변화면 얼라인 손상 의심
STEER_SHIFT_SEVERE = 0.08    # 이 이상이면 주행 자체가 힘든 수준 → 즉시 박스 권고
STEER_BASE_SAMPLES = 101     # 충격 전 기준 버퍼 (최근 직선 샘플, 홀수)
STEER_BASE_MIN = 25          # 기준에 필요한 최소 샘플 (직선 5초)
STEER_JUDGE_SAMPLES = 51     # 충격 후 판정 버퍼 (직선 ~10초, 홀수) — 찼을 때 판정


# 리어 불안정(오버스티어) 감시 — 리어 윙/서스가 죽으면 코너마다 미끄러진다.
# 판정은 '횡방향 속도'(mLocalVel.x): 정상 코너링은 1~2m/s, 진짜 슬라이드는
# 4m/s 이상. (요레이트+저조향 조합은 고속 코너를 슬라이드로 오인하고,
# 카운터 중엔 조향이 커져 진짜 슬라이드를 놓침 — 실차에서 확인된 오탐.)
#
# 드라이버 실수와의 구분: 슬라이드는 상시 카운트해서 '평소 빈도'를 기준선으로
# 잡고, 손상 후 빈도가 기준선의 2배(최소 3회)를 넘어야만 콜한다.
# 실수는 가끔, 손상은 코너마다 — 빈도 변화가 신호다.
INSTAB_WATCH_SEC = 180.0     # 손상 후 감시 시간
INSTAB_SPEED_KMH = 80.0
INSTAB_YAW_RAD_S = 0.55      # |요레이트| 동반 조건 (직선 휠스핀 배제)
INSTAB_LAT_SLIP_MS = 4.0     # |횡속도| 이 이상 = 실제 슬라이드
INSTAB_MIN_COUNT = 3         # 손상 후 최소 이 횟수 (실수 1~2회는 통과)
INSTAB_GAP_SEC = 3.0         # 같은 슬라이드를 중복 카운트하지 않는 간격


def damage_report_message(detached: list, flat: list, losing: list,
                          dents: list) -> tuple[Optional[str], bool, list]:
    """
    충격 뒤 점검 리포트 문장 — (문장, 박스 필요, 문제 목록). 바퀴별 플래그(4)와
    덴트 심각도(8)만 보는 순수 함수 (공기압 비교 같은 상태는 호출자가 losing으로).
    C# HealthAnalyzer.DamageReportMessage.
    """
    problems: list[str] = []
    det = [wheel_name(i) for i in range(min(len(detached), 4)) if detached[i]]
    flats = [wheel_name(i) for i in range(min(len(flat), 4))
             if flat[i] and not detached[i]]
    if det:
        problems.append(msg("item_wheel_damage", names=and_list(det)))
    if flats:
        problems.append(msg("item_puncture", names=and_list(flats)))
    for i in range(min(len(losing), 4)):
        if losing[i]:
            problems.append(msg("item_losing_pressure", name=wheel_name(i)))
    dents = list(dents or [])[:8]
    heavy_zones = [zone_display(DENT_ZONES[i]) for i, s in enumerate(dents) if s >= 2]
    light = any(s == 1 for s in dents)
    if len(heavy_zones) > DENT_MAX_ZONES:
        problems.append(msg("item_heavy_body_all"))    # 존 조합은 3개까지만 팩에 있다
    elif heavy_zones:
        problems.append(msg("item_heavy_body", zones=and_list(heavy_zones)))

    need_box = bool(det or flats)
    if problems:
        advice = msg("report_advice_box") if need_box else msg("report_advice_keep")
        return (msg("report_problems", problems=join_problems(problems), advice=advice),
                need_box, problems)
    if light:
        return msg("report_marks"), need_box, problems
    return msg("report_clean"), need_box, problems


def wing_message(drop_m: float) -> Optional[str]:
    """프론트 윙 하락 콜 — 100mm 넘는 하락은 센서 튐이라 None. C# WingMessage."""
    if not wing_ok(drop_m):
        return None
    return msg("wing_down", mm=drop_m * 1000)


def brake_message(avg: float) -> Optional[str]:
    """브레이크 평균 온도 콜 — 10도 단위 반올림, 1500도 넘으면 None. C# BrakeMessage."""
    # 10도 단위 반올림 — 명시적 floor(x+0.5)라 C#과 결과가 같다
    t = int(math.floor(avg / 10.0 + 0.5)) * 10
    if not brake_ok(t):
        return None
    return msg("brake_warn", t=t)


def repair_message(delta: float) -> str:
    """데미지 페이스 손실 → 수리 권고 — 랩당 10초 이상이면 숫자 없이. C# RepairMessage."""
    if repair_big(delta):
        return msg("repair_cost_big")
    return msg("repair_cost", delta=delta)


def steer_median(samples: list) -> float:
    """정렬한 목록의 len//2 번째 — C# HealthAnalyzer.SteerMedian과 같은 정의."""
    return sorted(samples)[len(samples) // 2]


def low_median(samples: list) -> float:
    """정렬한 목록의 (len−1)//2 번째 (짝수면 아래쪽 가운데) — C# HealthAnalyzer.LowMedian."""
    return sorted(samples)[(len(samples) - 1) // 2]


def wing_bin(speed_kmh: float) -> int:
    """윙 높이 속도 구간 — floor((v − 150) / 10). C# HealthAnalyzer.WingBin."""
    return int(math.floor((speed_kmh - WING_SAMPLE_SPEED_KMH) / WING_BIN_KMH))


def wing_bins(samples: list) -> dict:
    """(구간, 높이) 목록 → 구간별 높이 목록."""
    out: dict = {}
    for b, h in samples:
        out.setdefault(b, []).append(h)
    return out


def wing_compare(base: dict, post: list) -> Optional[tuple]:
    """
    기준(구간별 높이) ↔ 충격 후 (구간, 높이) 목록 — 양쪽 다 WING_BIN_MIN개 이상인 구간이
    WING_MIN_BINS개 이상이면 (하락 m, 맞춘 구간 목록), 아니면 None. C# HealthAnalyzer.WingCompare.
    하락 = 구간별 (기준 중앙값 − 충격 후 중앙값)의 중앙값 (구간 오름차순, low_median).
    """
    by_post = wing_bins(post)
    matched = sorted(b for b, hs in by_post.items()
                     if len(hs) >= WING_BIN_MIN and len(base.get(b, ())) >= WING_BIN_MIN)
    if len(matched) < WING_MIN_BINS:
        return None
    drops = [low_median(base[b]) - low_median(by_post[b]) for b in matched]
    return low_median(drops), matched


def wing_base_ok(base: dict) -> bool:
    """기준으로 쓸 수 있나 — WING_BIN_MIN개 이상인 구간이 WING_MIN_BINS개 이상."""
    return sum(1 for hs in base.values() if len(hs) >= WING_BIN_MIN) >= WING_MIN_BINS


class HealthAnalyzer:
    def __init__(self, cfg):
        self.water_warn = cfg.get("thresholds.water_temp_warn", 105.0)
        self.oil_warn = cfg.get("thresholds.oil_temp_warn", 115.0)
        self.brake_warn = cfg.get("thresholds.brake_temp_warn", 700.0)
        self.impact_mag = cfg.get("thresholds.damage_impact_mag", 500.0)
        self.reset()

    def reset(self) -> None:
        self._last_impact_et = 0.0
        self._dents: Optional[list] = None
        self._impact_lap: Optional[int] = None
        self._pre_impact_pace: Optional[float] = None
        self._detached_warned = False
        self._report_due: Optional[float] = None    # 자동 점검 리포트 예정 시각
        self._pre_pressures: Optional[list] = None  # 충격 직전 휠 공기압
        self._wheel_detached_warned: set[int] = set()
        self._pressure_max = [0.0, 0.0, 0.0, 0.0]   # 세션/스틴트 중 최고 공기압
        self._puncture_warned: set[int] = set()
        self._was_in_pitlane = False
        self._wing_prev_t: Optional[float] = None   # 직전 틱 (시각, 속도) — 감속 계산
        self._wing_prev_v = 0.0
        self._wing_prev_calm = False                # 직전 틱 구간이 제동이 아니었나
        self._reset_wing()
        self._reset_alignment()
        self._last_impact_zone: Optional[str] = None
        self._instab_until = 0.0                    # 리어 불안정 감시 종료 시각
        self._instab_armed_t = 0.0                  # 감시 시작 시각
        self._instab_required = INSTAB_MIN_COUNT    # 기준선 반영된 콜 임계 횟수
        self._instab_last_t = 0.0
        self._instab_called = False
        self._slides: list[float] = []              # 슬라이드 시각 (상시 기록 — 기준선용)

    # -- 5Hz 틱: 충격/부품 탈락 즉시 감지 ---------------------------------------

    def on_tick(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        p = snap.player
        if not p:
            return
        over = state.race_over_now(snap.player_scoring())

        wheels = p.get("wheels") or []

        # 피트레인 진입 = 타이어 교체/수리 가능성 → 공기압/윙 기준 리셋
        if p.get("in_pitlane"):
            if not self._was_in_pitlane:
                self._pressure_max = [0.0] * 4
                self._puncture_warned.clear()
                self._wheel_detached_warned.clear()
                self._reset_wing()
                self._reset_alignment()
                self._instab_until = 0.0
                self._instab_required = INSTAB_MIN_COUNT
                self._instab_called = False
            self._was_in_pitlane = True
        else:
            self._was_in_pitlane = False

        if not over:
            self._track_wing(p, snap.t, state, bus)
            self._track_alignment(p, state, bus)

        impact_et = p.get("last_impact_et", 0.0)
        if impact_et and impact_et > self._last_impact_et + 0.5:
            prev_dents = self._dents
            self._dents = list(p.get("dent_severity") or [])
            self._last_impact_et = impact_et
            mag = p.get("last_impact_mag", 0.0)
            if mag >= self.impact_mag and not over:
                self._pre_pressures = [w.get("pressure", 0.0) for w in wheels[:4]]
                self._wing_on_impact(mag)
                self._align_on_impact(mag)
                self._report_due = snap.t + REPORT_DELAY_SEC
                self._on_impact(mag, prev_dents, snap.t, state, bus)

        if over:
            self._report_due = None      # 피니시 전 충격의 점검 리포트도 접는다
        # 충격 후 자동 점검 리포트 — 드라이버가 아니라 우리가 데이터로 확인
        if self._report_due is not None and snap.t >= self._report_due:
            self._report_due = None
            self._damage_report(p, state, bus)

        # 차체 부품 탈락 (미러/보닛/윙 등) — 펑크와 별개.
        # 직전 충격이 리어 쪽이면 리어 윙 가능성을 특정해서 부르고,
        # 어느 쪽이든 리어 불안정 감시를 켠다 (에어로 손실 대비).
        if p.get("detached") and not self._detached_warned and over:
            self._detached_warned = True     # 체커 뒤 — 차고로 들어가는 중이라 말하지 않는다
        if p.get("detached") and not self._detached_warned:
            self._detached_warned = True
            # 존 이름은 영문("rear left" 등) — 한국어로 검사하면 절대 안 걸린다
            if self._last_impact_zone and "rear" in self._last_impact_zone:
                text = msg("part_detached_rear")
                state.set_issue("damage", "리어 부품 탈락 — 리어 윙 손상 가능성")
            else:
                text = msg("part_detached")
                state.set_issue("damage", "차체 부품 탈락 — 에어로 손상 의심")
            bus.push(Event(
                type=EventType.PART_DETACHED, priority=Priority.CRITICAL,
                message=text, dedup_key="detached", tone="urgent", ttl=10.0,
            ))
            self._arm_instab(snap.t, "부품 탈락")
            if self._report_due is None:      # 점검 리포트가 예약 안 됐으면 예약
                self._report_due = snap.t + REPORT_DELAY_SEC

        # 휠 탈락 즉시 콜 (주행 불능급 — 페이스 관찰 없이 바로)
        for i, w in enumerate(wheels[:4]):
            if w.get("detached") and i not in self._wheel_detached_warned:
                self._wheel_detached_warned.add(i)
                bus.push(Event(
                    type=EventType.WHEEL_DAMAGE, priority=Priority.CRITICAL,
                    message=msg("wheel_gone", wheel=wheel_name(i)),
                    dedup_key=f"wheel_det_{i}", tone="urgent", ttl=10.0,
                ))
                state.set_issue("damage", f"{WHEEL_NAMES[i]} 휠 탈락 — 즉시 피트 필요")

        if not over:
            self._check_slow_puncture(p, wheels, state, bus)
            self._check_instability(p, snap.t, state, bus)

    def _on_impact(self, mag: float, prev_dents: Optional[list], now: float,
                   state: SessionState, bus: EventBus) -> None:
        zone = self._new_dent_zone(prev_dents, self._dents)
        heavy = mag >= self.impact_mag * 4
        where = f"{zone} 쪽" if zone else "위치 불명"
        self._last_impact_zone = zone
        # 리어 쪽 충격이나 큰 충격이면 리어 불안정 감시 시작
        # (존 이름은 영문 — "rear", "rear left", "rear right")
        if heavy or (zone and "rear" in zone):
            self._arm_instab(now, f"{where}, 충격 {mag:.0f}")
        bus.push(Event(
            type=EventType.DAMAGE, priority=Priority.CRITICAL,
            data={"pool": "damage", "zone": zone or "", "mag": round(mag)},
            dedup_key="impact", tone="urgent", ttl=8.0,
            bridge={"topic": f"방금 {'큰 ' if heavy else ''}충격이 있었다 ({where}). "
                             "우리가 지금 데이터(휠/공기압/보디)를 점검 중이고 곧 결과를 "
                             "부를 예정. 드라이버는 페이스만 유지하면 된다고 안심시켜라. "
                             "드라이버에게 확인을 시키지 마라."},
        ))
        state.set_issue("damage", f"접촉 데미지 ({where}) — 페이스 영향 관찰 중")
        state.add_narrative(f"(이벤트) 충격 감지 ({where}, 크기 {mag:.0f})")
        self._impact_lap = len(state.laps)
        self._pre_impact_pace = state.baseline_lap_time()

    def _damage_report(self, p: dict, state: SessionState, bus: EventBus) -> None:
        """충격 몇 초 뒤 데이터를 훑어 결과를 불러준다 — 점검은 툴의 일."""
        wheels = (p.get("wheels") or [])[:4]
        detached = [bool(w.get("detached")) for w in wheels]
        flat = [bool(w.get("flat")) for w in wheels]
        losing = [False] * len(wheels)

        # 충격 전후 공기압 비교 — 서서히 새는 누출 조기 발견
        if self._pre_pressures:
            for i, w in enumerate(wheels[:4]):
                if i in self._puncture_warned or w.get("flat") or w.get("detached"):
                    continue
                if i < len(self._pre_pressures) and self._pre_pressures[i] > 0 \
                        and self._pre_pressures[i] - w.get("pressure", 0.0) \
                        >= IMPACT_PRESSURE_DROP_KPA:
                    self._puncture_warned.add(i)
                    losing[i] = True
        self._pre_pressures = None

        message, _need_box, problems = damage_report_message(
            detached, flat, losing, list(p.get("dent_severity") or []))
        if problems:
            state.set_issue("damage", "점검 결과: " + ", ".join(problems))
        bus.push(Event(
            type=EventType.DAMAGE_REPORT, priority=Priority.HIGH,
            message=message, dedup_key=f"dmg_report_{self._last_impact_et}",
            ttl=20.0, tone="casual",
        ))
        state.add_narrative(f"(점검) {message}")

    def _tyre_trouble(self, p: dict) -> bool:
        """
        타이어 문제 — 펑크 경고(슬로우 펑처·점검 리포트의 공기압 누출) 또는 펑크/탈락 바퀴.
        윙 높이 하락·조향 쏠림은 타이어 탓일 수 있고, 펑크·박스 콜이 우선이라 윙·얼라인 콜은
        판정도 대기 콜 재시도도 붙든다 (피트 진입이 리셋). C# HealthAnalyzer.TyreTrouble.
        """
        wheels = (p.get("wheels") or [])[:4]
        return bool(self._puncture_warned) or any(w.get("flat") or w.get("detached") for w in wheels)

    def _reset_wing(self) -> None:
        """윙 감시 상태 — 세션 리셋과 피트레인 진입(수리 가능)에서 처음부터."""
        self._wing_pre: list[tuple[int, float]] = []    # 기준 전 (구간, 높이) — 최근 301개
        self._wing_base: Optional[dict] = None          # 굳힌 기준 (구간 → 높이 목록)
        self._wing_base_n = 0                           # 기준에 쓴 샘플 수
        self._wing_post: Optional[list] = None          # 충격 후 판정 버퍼 (None = 판정 대기 없음)
        self._wing_impact_mag = 0.0                     # 판정 중인 충격 크기 (로그용)
        self._wing_pending: Optional[float] = None      # 판정 끝난 콜의 하락(m) — 쿨다운 재시도
        self._wing_tyre_logged = False
        self._wing_warned = False

    def _wing_on_impact(self, mag: float) -> None:
        """
        기준 임계 이상 충격 — 기준이 아직 없으면 굳히고, 판정 버퍼를 새로 연다.
        기준이 모자라면(5샘플 이상 구간 3개 미만) 이 충격만 판정하지 않고 기준 버퍼를 이 충격
        뒤부터 새로 모은다 (다음 충격이 기준을 굳힐 수 있게 — 스틴트 전체를 끄지 않는다).
        """
        if self._wing_base is None:
            base = wing_bins(self._wing_pre)
            if not wing_base_ok(base):
                log.info("윙 판정 (충격 %.0f): 기준 없음 — 충격 전 고속 샘플 %d개, %d샘플 이상 "
                         "구간 %d개 < %d → no-baseline (이 충격만, 기준 다시 모음)",
                         mag, len(self._wing_pre), WING_BIN_MIN,
                         sum(1 for hs in base.values() if len(hs) >= WING_BIN_MIN), WING_MIN_BINS)
                self._wing_pre = []
                return
            self._wing_base = base                  # 다중 충격 시 최초 기준 유지
            self._wing_base_n = len(self._wing_pre)
        if self._wing_warned or self._wing_pending is not None:
            return
        self._wing_post = []                        # 충격마다 새 버퍼 — 마지막 충격 뒤 상태로 판정
        self._wing_impact_mag = mag
        self._wing_tyre_logged = False

    def _wing_calm(self, now: float, speed: float) -> bool:
        """
        직전 두 틱 구간 모두 감속 ≤ 0.5g(틱 간격 0~0.5초)인가 — 제동 다이브와 그 복원을 뺀다.
        매 틱(체커 전) 갱신한다. C# HealthAnalyzer.WingCalm.
        """
        calm = False
        if self._wing_prev_t is not None:
            dt = now - self._wing_prev_t
            if 0.0 < dt <= WING_TICK_GAP_MAX:
                calm = (self._wing_prev_v - speed) / 3.6 / dt <= WING_DECEL_MAX
        steady = calm and self._wing_prev_calm
        self._wing_prev_t = now
        self._wing_prev_v = speed
        self._wing_prev_calm = calm
        return steady

    def _track_wing(self, p: dict, now: float, state: SessionState, bus: EventBus) -> None:
        """
        프론트 윙 높이 감시 — 제동이 아닌 고속 샘플을 속도 구간별로 모아, 충격 뒤 같은 속도
        구간의 높이 중앙값이 충격 전보다 10mm 이상 낮으면 윙(스플리터) 손상으로 특정한다.
        규칙과 근거는 WING_* 주석.
        """
        speed = p.get("speed_kmh", 0.0) or 0.0
        steady = self._wing_calm(now, speed)
        if self._wing_pending is not None:
            # push가 쿨다운으로 거절되면 다음 틱에 재시도 (점검 리포트 직후 등). 그 사이 새 충격이
            # 오면 그 충격의 점검 리포트가 먼저 (같은 damage_report 쿨다운 — 대기 콜이 펑크·박스
            # 리포트를 밀어내지 않게, 콜은 리포트 쿨다운 뒤 재시도). 타이어 문제 중엔 붙든다
            if self._report_due is not None:
                return
            if self._tyre_trouble(p):
                if not self._wing_tyre_logged:
                    self._wing_tyre_logged = True
                    log.info("윙 콜 대기 (충격 %.0f): 하락 %+.1fmm → hold-tyre (펑크/휠/공기압 누출 "
                             "— 피트 진입까지)", self._wing_impact_mag, self._wing_pending * 1000)
                return
            self._push_wing(self._wing_pending, state, bus)
            return
        h = p.get("front_wing_height")
        if h is None or not math.isfinite(h) or h <= 0 or p.get("in_pitlane") \
                or speed < WING_SAMPLE_SPEED_KMH or not steady:
            return
        sample = (wing_bin(speed), h)
        if self._wing_base is None:
            self._wing_pre.append(sample)           # 기준 전 — 건강한 기준 샘플
            if len(self._wing_pre) > WING_BASE_SAMPLES:
                del self._wing_pre[0]
            return
        if self._wing_post is None or self._wing_warned:
            return
        self._wing_post.append(sample)
        if len(self._wing_post) > WING_POST_SAMPLES:
            del self._wing_post[0]
        if self._report_due is not None:
            return    # 그 충격의 점검 리포트가 먼저 (같은 쿨다운 — 윙 콜이 리포트를 밀어내지 않게)
        res = wing_compare(self._wing_base, self._wing_post)
        if res is None:
            return
        drop, matched = res
        bins = ",".join(str(int(WING_SAMPLE_SPEED_KMH + b * WING_BIN_KMH)) for b in matched)
        if self._tyre_trouble(p):
            # 높이 하락은 타이어 탓 — 슬로우 펑처/점검 리포트가 부른다. 샘플은 계속 모은다.
            if not self._wing_tyre_logged:
                self._wing_tyre_logged = True
                log.info("윙 판정 (충격 %.0f): 맞춘 속도 구간 %d개 [%s]km/h · 기준 %d샘플 · "
                         "충격 후 %d샘플 · 하락 %+.1fmm → hold-tyre (펑크/휠/공기압 누출)",
                         self._wing_impact_mag, len(matched), bins, self._wing_base_n,
                         len(self._wing_post), drop * 1000)
            return
        call = drop >= WING_DROP_M
        log.info("윙 판정 (충격 %.0f): 맞춘 속도 구간 %d개 [%s]km/h · 기준 %d샘플 · "
                 "충격 후 %d샘플 · 하락 %+.1fmm → %s", self._wing_impact_mag, len(matched), bins,
                 self._wing_base_n, len(self._wing_post), drop * 1000, "call" if call else "ok")
        self._wing_post = None                      # 이 충격의 판정은 끝 (다음 충격이 다시 연다)
        if not call:
            return
        self._wing_pending = drop
        self._wing_tyre_logged = False              # 대기 중 타이어 홀드 로그용으로 다시
        self._push_wing(drop, state, bus)

    def _push_wing(self, drop: float, state: SessionState, bus: EventBus) -> None:
        """판정된 윙 콜 — 쿨다운으로 거절되면 대기로 남아 다음 틱에 다시 민다."""
        text = wing_message(drop)
        # 100mm 넘는 하락은 말하지 않지만 이슈·서사는 남기고 한 번만 판정한다
        if text is not None:
            accepted = bus.push(Event(
                type=EventType.DAMAGE_REPORT, priority=Priority.HIGH,
                message=text,
                dedup_key="wing_damage", ttl=20.0, tone="casual",
            ))
            if not accepted:
                return
        self._wing_warned = True
        self._wing_pending = None
        state.set_issue("damage", f"프론트 윙 손상 (높이 -{drop * 1000:.0f}mm)")
        state.add_narrative("(점검) 프론트 윙 손상 감지")

    def _reset_alignment(self) -> None:
        """얼라인 감시 상태 — 세션 리셋과 피트레인 진입(수리 가능)에서 처음부터."""
        self._steer_pre: list[float] = []           # 기준 전 직선 조향 (최근 101개)
        self._pre_impact_steer: Optional[float] = None   # 기준 중앙값
        self._steer_base_n = 0                      # 기준에 쓴 샘플 수
        self._steer_post: Optional[list[float]] = None   # 충격 후 판정 버퍼 (None = 판정 대기 없음)
        self._align_impact_mag = 0.0                # 판정 중인 충격 크기 (로그용)
        self._align_pending: Optional[str] = None   # 판정 끝난 콜 ("mild"/"severe") — 쿨다운 재시도
        self._align_tyre_logged = False
        self._align_warned = False

    def _align_on_impact(self, mag: float) -> None:
        """
        기준 임계 이상 충격 — 기준이 아직 없으면 굳히고, 판정 버퍼를 새로 연다.
        기준 샘플이 모자라면 이 충격만 판정하지 않고 기준 버퍼를 이 충격 뒤부터 새로
        모은다 (다음 충격이 기준을 굳힐 수 있게 — 스틴트 전체를 끄지 않는다).
        """
        if self._pre_impact_steer is None:
            if len(self._steer_pre) < STEER_BASE_MIN:
                log.info("얼라인 판정 (충격 %.0f): 기준 없음 — 충격 전 직선 샘플 %d개 < %d → "
                         "no-baseline (이 충격만, 기준 다시 모음)",
                         mag, len(self._steer_pre), STEER_BASE_MIN)
                self._steer_pre = []
                return
            self._pre_impact_steer = steer_median(self._steer_pre)   # 다중 충격 시 최초 기준 유지
            self._steer_base_n = len(self._steer_pre)
        if self._align_warned or self._align_pending is not None:
            return
        self._steer_post = []                       # 충격마다 새 버퍼 — 마지막 충격 뒤 상태로 판정
        self._align_impact_mag = mag
        self._align_tyre_logged = False

    def _track_alignment(self, p: dict, state: SessionState, bus: EventBus) -> None:
        """
        얼라인 감시 — 차가 곧게 가는(요레이트·횡속도가 작은) 고속 샘플의 조향 중앙값이
        충격 전보다 한쪽으로 옮겨가 있으면 지오메트리가 틀어진 것. 드라이버가
        느끼기 전에(또는 느낀 것을 확증해서) 불러준다.
        """
        if self._align_pending is not None:
            # push가 쿨다운으로 거절되면 다음 틱에 재시도 (점검 리포트 직후 등). 새 충격의 점검
            # 리포트가 먼저, 타이어 문제 중엔 붙든다 (윙 대기 콜과 같은 규칙)
            if self._report_due is not None:
                return
            if self._tyre_trouble(p):
                if not self._align_tyre_logged:
                    self._align_tyre_logged = True
                    log.info("얼라인 콜 대기 (충격 %.0f): call-%s → hold-tyre (펑크/휠/공기압 누출 "
                             "— 피트 진입까지)", self._align_impact_mag, self._align_pending)
                return
            self._push_alignment(self._align_pending, state, bus)
            return
        steer = p.get("steering")
        yaw = p.get("yaw_rate")
        lat_vel = p.get("lat_vel")
        speed = p.get("speed_kmh", 0.0) or 0.0
        if steer is None or yaw is None or lat_vel is None or p.get("in_pitlane") \
                or speed < STEER_SAMPLE_SPEED_KMH:
            return
        if not (math.isfinite(steer) and math.isfinite(yaw) and math.isfinite(lat_vel)):
            return
        if abs(steer) > STEER_SAMPLE_MAX or abs(yaw) * (speed / 3.6) > STEER_LAT_ACC_MAX \
                or abs(lat_vel) > STEER_LAT_VEL_MAX:
            return    # 코너·슬라이드 — 곧게 가는 중이 아니다

        if self._pre_impact_steer is None:
            self._steer_pre.append(steer)           # 기준 전 — 건강한 기준 샘플
            if len(self._steer_pre) > STEER_BASE_SAMPLES:
                del self._steer_pre[0]
            return
        if self._steer_post is None or self._align_warned:
            return
        self._steer_post.append(steer)
        if len(self._steer_post) > STEER_JUDGE_SAMPLES:
            del self._steer_post[0]
        if len(self._steer_post) < STEER_JUDGE_SAMPLES:
            return
        if self._tyre_trouble(p):
            # 쏠림은 타이어 탓 — 슬로우 펑처/점검 리포트가 부른다. 샘플은 계속 모은다.
            if not self._align_tyre_logged:
                self._align_tyre_logged = True
                log.info("얼라인 판정 (충격 %.0f): 기준 %+.3f (%d샘플) · 충격 후 %+.3f (%d샘플) · "
                         "변화 %+.3f → hold-tyre (펑크/휠/공기압 누출)",
                         self._align_impact_mag, self._pre_impact_steer, self._steer_base_n,
                         steer_median(self._steer_post), len(self._steer_post),
                         steer_median(self._steer_post) - self._pre_impact_steer)
            return
        post = steer_median(self._steer_post)
        shift = post - self._pre_impact_steer
        if abs(shift) >= STEER_SHIFT_SEVERE:
            decision = "severe"
        elif abs(shift) >= STEER_SHIFT_MIN:
            decision = "mild"
        else:
            decision = None
        log.info("얼라인 판정 (충격 %.0f): 기준 %+.3f (%d샘플) · 충격 후 %+.3f (%d샘플) · "
                 "변화 %+.3f → %s", self._align_impact_mag, self._pre_impact_steer,
                 self._steer_base_n, post, len(self._steer_post), shift,
                 f"call-{decision}" if decision else "ok")
        self._steer_post = None                     # 이 충격의 판정은 끝 (다음 충격이 다시 연다)
        if decision is None:
            return
        self._align_pending = decision
        self._align_tyre_logged = False             # 대기 중 타이어 홀드 로그용으로 다시
        self._push_alignment(decision, state, bus)

    def _push_alignment(self, decision: str, state: SessionState, bus: EventBus) -> None:
        """판정된 얼라인 콜 — 쿨다운으로 거절되면 대기로 남아 다음 틱에 다시 민다."""
        if decision == "severe":
            # 심각 단계: 주행 자체가 힘든 수준 → 페이스 관찰 없이 즉시 박스 권고
            accepted = bus.push(Event(
                type=EventType.DAMAGE_REPORT, priority=Priority.CRITICAL,
                message=msg("align_severe"),
                dedup_key="align_severe", tone="urgent", ttl=15.0,
            ))
            if not accepted:
                return
            state.set_issue("damage", "얼라인 심각 손상 — 즉시 수리 권장")
            state.add_narrative("(점검) 얼라인 심각 손상 → 박스 콜")
        else:
            accepted = bus.push(Event(
                type=EventType.DAMAGE_REPORT, priority=Priority.HIGH,
                message=msg("align_mild"),
                dedup_key="align_damage", ttl=20.0, tone="casual",
            ))
            if not accepted:
                return
            state.set_issue("damage", "얼라인 틀어짐 의심 (직선 조향 오프셋 변화)")
            state.add_narrative("(점검) 얼라인 틀어짐 감지 — 직선 조향 오프셋 변화")
        self._align_warned = True
        self._align_pending = None

    def _check_instability(self, p: dict, now: float,
                           state: SessionState, bus: EventBus) -> None:
        """
        리어 불안정 감시 — 슬라이드(횡속도+요레이트 동반)는 상시 카운트해서
        드라이버의 '평소 빈도'를 기준선으로 삼고, 손상 후 빈도가 기준선을
        확실히 넘을 때만(2배, 최소 3회) 박스를 부른다. 드라이버 실수의
        가끔 슬라이드와 손상의 코너마다 슬라이드를 빈도 변화로 구분한다.
        """
        yaw = p.get("yaw_rate")
        lat_vel = p.get("lat_vel")
        if yaw is None or lat_vel is None or p.get("in_pitlane") \
                or (p.get("speed_kmh", 0.0) or 0.0) < INSTAB_SPEED_KMH:
            return
        if abs(yaw) < INSTAB_YAW_RAD_S or abs(lat_vel) < INSTAB_LAT_SLIP_MS:
            return
        if now - self._instab_last_t < INSTAB_GAP_SEC:
            return
        self._instab_last_t = now
        # 상시 기록 (기준선용). 오래된 것은 정리.
        self._slides.append(now)
        self._slides = [t for t in self._slides if now - t <= 2 * INSTAB_WATCH_SEC]

        if self._instab_called or now >= self._instab_until:
            return
        post = sum(1 for t in self._slides if t >= self._instab_armed_t)
        log.info("슬라이드 감지 (손상 후 %d/%d): 요레이트 %.2f, 횡속도 %.1fm/s",
                 post, self._instab_required, yaw, lat_vel)
        if post < self._instab_required:
            return
        self._instab_called = True
        bus.push(Event(
            type=EventType.DAMAGE_REPORT, priority=Priority.CRITICAL,
            message=msg("rear_instab"),
            dedup_key="rear_instab", tone="urgent", ttl=15.0,
        ))
        state.set_issue("damage", "리어 불안정 반복 (에어로/서스 손상 의심)")
        state.add_narrative("(점검) 손상 후 리어 불안정 반복 감지 → 박스 권장")

    def _arm_instab(self, now: float, reason: str) -> None:
        """
        리어 불안정 감시 시작 — 직전 3분간의 슬라이드 횟수를 기준선으로,
        콜에 필요한 손상 후 슬라이드 횟수를 정한다 (기준선×2, 최소 3회).
        이미 감시 중이면 창만 연장하고 기준선은 유지한다.
        """
        already = now < self._instab_until
        self._instab_until = now + INSTAB_WATCH_SEC
        if already:
            return
        baseline = sum(1 for t in self._slides
                       if now - INSTAB_WATCH_SEC <= t <= now)
        self._instab_armed_t = now
        self._instab_required = max(INSTAB_MIN_COUNT, baseline * 2)
        self._instab_called = False
        log.info("리어 불안정 감시 시작 (%s) — 기준선 %d회/3분, 콜 임계 %d회",
                 reason, baseline, self._instab_required)

    def _check_slow_puncture(self, p: dict, wheels: list,
                             state: SessionState, bus: EventBus) -> None:
        """충격과 무관한 상시 공기압 감시 — 최고치 대비 큰 하락 = 슬로우 펑처."""
        if p.get("in_pitlane") or (p.get("speed_kmh", 0.0) or 0.0) < 60:
            return    # 저속/피트에선 온도 하락으로 공기압이 자연히 떨어짐
        for i, w in enumerate(wheels[:4]):
            pr = w.get("pressure", 0.0)
            if pr <= 0:
                continue
            if pr > self._pressure_max[i]:
                self._pressure_max[i] = pr
            elif (self._pressure_max[i] - pr >= SLOW_PUNCTURE_DROP_KPA
                    and i not in self._puncture_warned and not w.get("flat")):
                self._puncture_warned.add(i)
                bus.push(Event(
                    type=EventType.TYRE_WARNING, priority=Priority.HIGH,
                    message=msg("slow_puncture", wheel=wheel_name(i)),
                    dedup_key=f"slowpunc_{i}", ttl=20.0, tone="casual",
                ))
                state.set_issue("tyres", f"{WHEEL_NAMES[i]} 슬로우 펑처 의심")

    @staticmethod
    def _new_dent_zone(prev: Optional[list], cur: Optional[list]) -> Optional[str]:
        """새로 생기거나 심해진 덴트 존 → 대략적 위치 이름."""
        if not cur:
            return None
        worst_i, worst_delta = None, 0
        for i, sev in enumerate(cur[:8]):
            before = prev[i] if prev and i < len(prev) else 0
            if sev - before > worst_delta:
                worst_i, worst_delta = i, sev - before
        if worst_i is None:
            return None
        return DENT_ZONES[worst_i]

    def on_lap(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        p = snap.player
        if not p or state.race_over:
            return

        self._check_damage_pace(state, bus)

        water = p.get("water_temp", 0.0)
        oil = p.get("oil_temp", 0.0)
        if p.get("overheating") or water >= self.water_warn or oil >= self.oil_warn:
            what = (msg("engine_what_water") if water >= self.water_warn
                    else msg("engine_what_oil") if oil >= self.oil_warn
                    else msg("engine_what_engine"))
            bus.push(Event(
                type=EventType.ENGINE_WARNING, priority=Priority.HIGH,
                message=msg("engine_warn", what=what),
                data={"water": water, "oil": oil}, ttl=30.0,
            ))
            state.set_issue("engine", f"엔진 온도 상승 (수온 {water:.0f}, 유온 {oil:.0f})")
        else:
            state.clear_issue("engine")

        wheels = p.get("wheels") or []
        if len(wheels) == 4:
            avg_brake = sum(w.get("brake_temp", 0.0) for w in wheels) / 4
            text = brake_message(avg_brake) if avg_brake >= self.brake_warn else None
            if text is not None:
                bus.push(Event(
                    type=EventType.BRAKE_WARNING, priority=Priority.NORMAL,
                    message=text,
                    data={"avg_brake": round(avg_brake)}, ttl=30.0,
                ))

    def _check_damage_pace(self, state: SessionState, bus: EventBus) -> None:
        """충격 후 관찰 랩이 지나면 페이스 영향으로 수리 여부 판단."""
        if "damage" not in state.issues or self._impact_lap is None:
            return
        # 피트 인 = 수리로 간주하고 리셋
        if state.laps and state.laps[-1].in_pits:
            state.clear_issue("damage")
            self._impact_lap = None
            self._detached_warned = False
            return
        post = [l for l in state.laps[self._impact_lap:] if l.valid]
        if len(post) < OBSERVE_LAPS or self._pre_impact_pace is None:
            return
        post_avg = sum(l.lap_time for l in post[-OBSERVE_LAPS:]) / OBSERVE_LAPS
        delta = post_avg - self._pre_impact_pace

        if delta >= REPAIR_PACE_DELTA:
            bus.push(Event(
                type=EventType.LAP_ANALYSIS, priority=Priority.NORMAL,
                data={"triggers": [
                    f"접촉 데미지 이후 랩당 {delta:.1f}초 느려졌다. "
                    "피트에서 수리할지, 그냥 달릴지 판단해라 (수리는 시간 손실, "
                    "방치는 랩마다 손실 누적)."]},
                message=repair_message(delta),
                dedup_key=f"repair_{self._impact_lap}",
            ))
            state.set_issue("damage", f"데미지로 랩당 {delta:.1f}초 손실 — 수리 권장")
            self._impact_lap = None    # 판단은 한 번만
        elif delta <= NO_EFFECT_DELTA:
            bus.push(Event(
                type=EventType.DAMAGE, priority=Priority.NORMAL,
                message=msg("dmg_no_effect"),
                dedup_key=f"dmg_ok_{self._impact_lap}", ttl=30.0,
            ))
            state.clear_issue("damage")
            self._impact_lap = None
