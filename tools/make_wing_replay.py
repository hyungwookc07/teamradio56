"""
프론트 윙(스플리터) 높이 콜 회귀용 합성 리플레이 생성기 — 리플레이 + 기대 콜(expect) 사이드카.

리뷰 재현: 예전 감지는 150km/h 이상 샘플 전부의 mFrontWingHeight EMA를 첫 충격 순간에 얼려 그 뒤
매 틱 비교했다. 스플리터 높이는 공력 하중으로 속도² 에 비례해 눌리고(LMH 근사 300km/h에서 ~19~23mm)
고속 제동에선 브레이크 다이브(~3mm/g)가 더해져, 건강한 차도 150km/h 이상 대역 안에서 15mm 넘게
움직인다. 중속 구간(170~210km/h)에서 가볍게 닿은 뒤 긴 직선(325km/h)을 달리면 EMA가 고속 높이로
내려가 기준(중속 높이)과 10mm 넘게 벌어진다 — 멀쩡한 차에 "Front aero down 10 millimetres" 오탐.
녹화 리플레이(race_full 등)는 이 필드가 0이라 이 감지는 한 번도 재생된 적이 없다.

리플레이 셋 (data/replays/<이름>.jsonl.gz + .expect.json — tools/expect_calls.py가 파이썬·C#
둘 다 wing_damage 키 콜만 대조). 리플레이 하니스는 버스를 비우지 않아 같은 키 콜이 리플레이마다
한 번만 수락되므로 침묵 경우를 앞에, 콜 하나를 맨 뒤에 둔다. 경우 사이는 피트 진입(윙 상태 리셋):
  · wing — 멀쩡한 차 + 중속 구간 가벼운 접촉 뒤 긴 직선(침묵 — 예전 EMA 감지는 콜) · 정적 필드
    (높이 상수, 침묵) · 충격 + 우측 앞 펑크로 높이 하락(타이어 탓, 침묵) · 실제 스플리터 손상
    (메인 직선 접촉, h0 −15mm → 콜 — 구간은 몇 초 만에 차지만 판정은 점검 리포트 뒤, 콜은 리포트
    쿨다운 뒤 재시도로)
  · wing_rebase — 1랩 첫 직선 3초 접촉(기준 부족 → 그 충격만 판정 안 함, 기준 다시 모음) · 다음 랩
    멀쩡한 접촉(기준을 굳히고 판정, 침묵) · 같은 스틴트 다음 랩 실제 손상(첫 기준 유지 · 충격마다
    새 판정 버퍼 → 콜)
  · wing_report — 대기 콜 중 다음 충격 (점검 리포트까지 대조): 메인 직선 실제 손상 → 리포트 직후 판정,
    리포트 쿨다운에 걸려 대기 → 리포트 +16초 두 번째 충격. 펑크를 남기면 그 충격의 리포트(펑크·박스)가
    나가고 대기 콜은 피트까지 붙든다, 멀쩡하면 리포트 먼저·대기 콜은 그 리포트 쿨다운 뒤
    (b60e335는 대기 콜이 쿨다운 끝 틱에 나가 두 번째 리포트를 잃었다)

규칙 (기대값의 근거, analyzers/health.py WING_* · C# HealthAnalyzer와 같은 값):
  · 샘플 = 피트레인 밖 · 150km/h 이상 · 높이 > 0 · 직전 두 틱 구간 감속 모두 ≤ 4.9m/s²(0.5g)
    (틱 간격 0~0.5초) — 제동 다이브와 그 복원은 빠진다
  · 속도 구간 bin = floor((v − 150) / 10)
  · 기준 = 첫 충격 전 최근 301샘플의 구간별 묶음, 5샘플 이상 구간이 3개 미만이면 그 충격만
    판정 없음(기준 버퍼는 그 충격 뒤부터 새로)
  · 판정 = 충격 뒤 새 버퍼(최근 301개)에서 양쪽 다 5샘플 이상인 구간이 3개가 되는 첫 틱 (그 충격의
    점검 리포트가 나간 뒤) 한 번 — 하락 = 구간별 (기준 중앙값 − 충격 후 중앙값)의 중앙값 ≥ 10mm면
    콜. 중앙값은 정렬 목록의 (len−1)//2 번째. 쿨다운(damage_report 20초 — 방금 나간 점검 리포트)에
    거절되면 대기로 남아 다음 틱에 다시 민다 → 콜 시각 = 리포트 + 20초 첫 틱. 대기 중 새 충격이 오면
    그 충격의 점검 리포트가 나갈 때까지 쉬고, 타이어 펑크/탈락·공기압 누출 중엔 붙든다
  · 타이어 펑크/탈락·공기압 누출 중엔 콜하지 않는다 (높이 하락은 타이어 탓)

모델: 혼자 달리는 레이스(시간제). 랩(95초) = 메인 직선 S1(240→305) · T1 강한 제동(2.2g) · 헤어핀
(85) · 가속 · 중속 구간 M(170~210, 0.6~0.7g 가벼운 제동 두 번) · 제동(1.2g) · 코너(115) · 긴 직선
(115→325) · 강한 제동(2.1g) · 코너(100) · 가속. 구간 안 속도는 시간에 선형, 경계는 전부 틱 위.
높이 h = h0 − 22mm × (v/300)² − 3mm/g × (지금 틱 구간 감속 + 0.5 × 직전 틱 구간 감속, 피치 복원
지연) + 1mm/g × 가속 + 작은 노이즈(±0.5mm), 0.1mm 반올림(telemetry.py round 4). 손상 = h0 −15mm,
펑크 = h0 −15mm + 우측 앞 flat, 정적 필드 = 스틴트 내내 0.0520m. 피트 랩은 S1·T1 자리를 피트레인
(~78km/h, 12초 정지)으로 달린다. 위치·시각은 구간 표에서 해석적으로 구한다 (dt 누적 없음).

생성기는 예전 EMA 감지가 멀쩡한 차 경우에서 콜하는 모양이라는 것, 샘플 감속이 경계에서 멀다는 것,
판정 하락이 임계·반올림 경계에서 멀다는 것을 생성 시점에 확인한다.

사용법: python tools/make_wing_replay.py [--out-dir data/replays] [--check]
"""

from __future__ import annotations

# Windows 콘솔(cp949) 인코딩 가드
import sys
if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

import argparse
import gzip
import json
import math
import os
import tempfile

HZ = 5
INT_MAX = 2147483647
END_ET = 3000.0             # 시간제 50분 — 리플레이(~15분) 안에 하프·남은 시간 안내가 없다
T_FORMATION_END = 20.0      # 0~20 페이즈 3, 20~30 페이즈 4 (카운트다운), 30부터 그린
T_GREEN = 30.0
GRID = -30.0                # 그리드 위치 (라인 뒤 m)
IMPACT_MAG = 700.0          # damage_impact_mag(500) 이상, 큰 충격(×4) 아님 — 가벼운 접촉
REPORT_DELAY = 8.0          # health.REPORT_DELAY_SEC
REPORT_COOLDOWN = 20.0      # config cooldowns.damage_report

# 감지 규칙 (analyzers/health.py WING_*)
SPEED_MIN = 150.0
BIN_KMH = 10.0
DROP_M = 0.010
DECEL_MAX = 4.9
GAP_MAX = 0.5
BASE_N = 301
POST_N = 301
BIN_MIN = 5
MIN_BINS = 3
# 예전 감지 (EMA) — 멀쩡한 차 경우가 예전 코드에서 콜이 나는 모양인지 확인용
OLD_ALPHA = 0.03

# 높이 모델 (m)
H0 = 0.045                  # 정지 높이
AERO_C = 0.022              # 300km/h에서의 공력 압축 (LMH 근사 19~23mm의 위쪽)
DIVE_PER_G = 0.003          # 브레이크 다이브
LIFT_PER_G = 0.001          # 가속 스쿼트 (앞이 든다)
STATIC_H = 0.0520           # 정적 필드 경우의 상수 높이
DAMAGE_DROP = 0.015         # 스플리터 손상 / 펑크의 h0 하락
G = 9.81

SAY_WING = ("Front aero down {mm:.0f} millimetres. Splitter damage. "
            "Careful in the fast stuff. Repair call on pace.")
# 점검 리포트 (덴트 1 하나 — 자국뿐 / 우측 앞 펑크) — reports 시나리오만 대조
SAY_REPORT_MARKS = "Check done. Just marks. Wheels, tyres, pressures all fine. Carry on."
SAY_REPORT_BOX = "Check done. Front right puncture. Prepare to box."

# 랩: (이름, 시간 s, 시작 km/h, 끝 km/h) — 구간 안 속도는 시간에 선형
LAP = [
    ("S1", 14.0, 240.0, 305.0),     # 메인 직선
    ("B1", 2.8, 305.0, 85.0),       # T1 강한 제동 (2.2g)
    ("H1", 5.0, 85.0, 85.0),        # 헤어핀
    ("A1", 8.0, 85.0, 195.0),
    ("M1", 1.2, 195.0, 168.0),      # 중속 구간 — 가벼운 제동 (0.64g)
    ("M2", 5.0, 168.0, 168.0),      # 중속 코너
    ("M3", 5.0, 168.0, 210.0),
    ("M4", 1.0, 210.0, 185.0),      # 가벼운 제동 (0.71g)
    ("M5", 4.0, 185.0, 200.0),
    ("B2", 2.0, 200.0, 115.0),      # 제동 (1.2g)
    ("C2", 4.0, 115.0, 115.0),
    ("A2", 12.0, 115.0, 270.0),     # 긴 직선
    ("A2B", 14.0, 270.0, 325.0),
    ("B3", 3.0, 325.0, 100.0),      # 강한 제동 (2.1g)
    ("C3", 4.0, 100.0, 100.0),
    ("A3", 10.0, 100.0, 240.0),
]
PIT_REPLACES = ("S1", "B1")         # 피트 랩은 이 두 구간 자리를 피트레인으로
PIT_HALF_S = 28.0                   # 피트레인 반 구간 시간 (~78km/h) — 구간 경계가 틱에 맞게
PIT_STOP_S = 12.0
LAP_S = sum(seg[1] for seg in LAP)


def seg_len(dur, v0, v1):
    return dur * (v0 + v1) / 2 / 3.6


TL = sum(seg_len(d, a, b) for _, d, a, b in LAP)
PIT_LEN = sum(seg_len(d, a, b) for n, d, a, b in LAP if n in PIT_REPLACES)

# 랩 계획 — 랩 번호(0부터)별: pit = 이 랩 S1·B1을 피트레인으로, impact = (경우 이름, 구간, 구간 안 시각).
# 충격 뒤 상태(drop h0 하락, flat 우측 앞 펑크)는 다음 피트 진입까지 유지된다 (같은 스틴트의 다음
# 충격이 오면 그 충격의 상태). static = 그 경우가 든 스틴트 내내 높이 상수. no_base = 기준 없는 충격.
SCENARIOS = {
    "wing": dict(pit_laps={2, 4, 6}, n_laps=10, cases=[
        dict(name="healthy_tap", lap=1, seg="M5", at=2.0, drop=0.0, expect=False,
             note="멀쩡한 차 + 중속 구간 출구(~192km/h) 가벼운 접촉 → 긴 직선(325km/h)을 달려도 같은 속도끼리라 "
                  "콜 없음 (예전: 중속 높이 기준 ↔ 고속 EMA 차이로 wing_damage 오탐)"),
        dict(name="static_field", lap=3, seg="M5", at=2.0, drop=0.0, static=True, expect=False,
             note="필드가 정적(높이 0.0520m 상수)인 차 + 충격 → 충격 전후 같은 높이, 하락 0 → 콜 없음"),
        dict(name="flat_tyre_drop", lap=5, seg="M5", at=2.0, drop=DAMAGE_DROP, flat=True, expect=False,
             note="충격 + 우측 앞 펑크로 높이 15mm 하락 → 타이어 탓, 윙 콜 없음 (점검 리포트가 펑크를 부른다)"),
        dict(name="splitter_damage", lap=8, seg="S1", at=2.0, drop=DAMAGE_DROP, expect=True,
             note="메인 직선 접촉으로 실제 스플리터 손상(h0 −15mm) → wing_damage 하나. 직선 가속 중이라 "
                  "충격 몇 초 만에 구간이 차지만 판정은 점검 리포트(+8초) 뒤, 콜은 리포트 쿨다운 20초 뒤 "
                  "재시도로 (리포트를 밀어내지 않는다)"),
    ]),
    # 기준 없는 충격은 그 충격만 — 1랩 첫 직선의 접촉 뒤에도 다음 충격은 판정하고, 그 다음 충격은
    # 첫 기준을 유지한 채 새 판정 버퍼로 판정한다
    "wing_rebase": dict(pit_laps=set(), n_laps=4, cases=[
        dict(name="opening_tap", lap=0, no_base=True, seg="S1", at=3.0, drop=0.0, expect=False,
             note="1랩 첫 직선 3초(5샘플 이상 속도 구간 3개 미만)에 가벼운 접촉(멀쩡) → 이 충격만 판정 "
                  "없음, 기준은 이 충격 뒤부터 다시 모은다 (예전 EMA 감지는 첫 직선 끝에서 오탐)"),
        dict(name="second_tap", lap=1, seg="M5", at=2.0, drop=0.0, expect=False,
             note="기준 없는 접촉 뒤 한 랩 — 멀쩡한 차 가벼운 접촉 → 기준을 굳히고 판정, 하락 없음"),
        dict(name="damage_after_taps", lap=2, seg="M5", at=2.0, drop=DAMAGE_DROP, expect=True,
             note="같은 스틴트 다음 랩 실제 스플리터 손상(h0 −15mm) → 두 번째 접촉 때 굳힌 기준 유지, 새 "
                  "판정 버퍼로 wing_damage 하나"),
    ]),
    # 대기 콜과 다음 충격의 점검 리포트 — 판정이 콜이어도 점검 리포트 쿨다운(20초)에 걸려 대기하는
    # 동안 두 번째 충격이 오면(리포트 +12~20초 — 그 충격의 리포트는 첫 리포트 쿨다운 뒤라 나갈 수
    # 있다) 그 충격의 점검 리포트가 먼저, 타이어 문제 중엔 대기 콜을 붙든다. b60e335는 대기 콜을
    # 리포트 쿨다운 끝 틱에 바로 밀어 같은 쿨다운을 차지해 두 번째 리포트(펑크·박스)를 잃었다
    "wing_report": dict(pit_laps={2}, n_laps=5, reports=True, cases=[
        dict(name="damage_then_flat", lap=1, seg="S1", at=2.0, drop=DAMAGE_DROP, held=True, expect=False,
             note="메인 직선 접촉으로 실제 스플리터 손상(h0 −15mm) → 점검 리포트(자국뿐) 직후 콜 판정, "
                  "리포트 쿨다운에 걸려 대기. 대기 중 두 번째 충격이 우측 앞 펑크를 남김 → 대기 콜은 "
                  "피트까지 붙든다 (펑크·박스 리포트가 우선, b60e335: 쿨다운 끝 틱에 윙 콜)"),
        dict(name="flat_during_wait", lap=1, seg="A1", at=4.2, drop=DAMAGE_DROP, flat=True, second=True,
             expect=False,
             note="대기 중 두 번째 충격(첫 리포트 +16초) + 우측 앞 펑크 → 이 충격의 점검 리포트 "
                  "'Front right puncture. Prepare to box.'가 나간다 (b60e335: 대기 윙 콜이 쿨다운을 "
                  "차지해 이 리포트를 잃음)"),
        dict(name="damage_then_tap", lap=3, seg="S1", at=2.0, drop=DAMAGE_DROP, expect=True,
             note="피트 뒤 같은 모양의 실제 스플리터 손상 → 리포트 직후 콜 판정·대기"),
        dict(name="tap_during_wait", lap=3, seg="A1", at=4.2, drop=DAMAGE_DROP, second=True, expect=False,
             note="대기 중 두 번째 가벼운 접촉(첫 리포트 +16초, 타이어 멀쩡) → 이 충격의 점검 리포트가 "
                  "먼저, 대기 윙 콜은 그 리포트의 쿨다운 뒤 (b60e335: 윙 콜이 먼저 나가 이 리포트를 잃음)"),
    ]),
}
NAME = "wing"
CASES: list = []
PIT_LAPS: set = set()
N_LAPS = 0


class Seg:
    def __init__(self, name, t0, dur, v0, v1, d0, lap, pit):
        self.name, self.t0, self.dur, self.lap, self.pit = name, t0, dur, lap, pit
        self.v0, self.v1 = v0 / 3.6, v1 / 3.6     # m/s
        self.d0 = d0
        self.t1 = t0 + dur
        self.a = (self.v1 - self.v0) / dur if dur > 0 else 0.0
        self.d1 = d0 + seg_len(dur, v0, v1)

    def v_at(self, t):
        return self.v0 + self.a * (t - self.t0)

    def d_at(self, t):
        u = min(t, self.t1) - self.t0
        return self.d0 + self.v0 * u + 0.5 * self.a * u * u


def build_track():
    segs = []
    t, d = T_GREEN, GRID
    segs.append(Seg("LAUNCH", t, 1.0, -GRID * 3.6, -GRID * 3.6, d, -1, False))
    t, d = segs[-1].t1, 0.0
    for lap in range(N_LAPS):
        for name, dur, v0, v1 in LAP:
            if lap in PIT_LAPS and name in PIT_REPLACES:
                if name == PIT_REPLACES[0]:
                    v = PIT_LEN / 2 / PIT_HALF_S * 3.6
                    for nm, du, vv in (("PIT_IN", PIT_HALF_S, v), ("PIT_STOP", PIT_STOP_S, 0.0),
                                       ("PIT_OUT", PIT_HALF_S, v)):
                        segs.append(Seg(nm, t, du, vv, vv, d, lap, True))
                        t, d = segs[-1].t1, segs[-1].d1
                continue
            segs.append(Seg(name, t, dur, v0, v1, d, lap, False))
            t, d = segs[-1].t1, segs[-1].d1
            if lap == N_LAPS - 1 and name == "S1":
                return segs
    return segs


SEGS: list = []
T_END = 0.0


def seg_at(t):
    if t < T_GREEN:
        return None
    for s in SEGS:
        if s.t0 <= t < s.t1:
            return s
    return SEGS[-1]


def dist(t):
    s = seg_at(t)
    if s is None:
        return GRID
    return s.d_at(t)


def speed_ms(t):
    s = seg_at(t)
    if s is None:
        return 0.0
    return s.v_at(min(t, s.t1))


def time_at(x):
    """누적 거리 x에 처음 닿는 시각."""
    for s in SEGS:
        if s.d1 > s.d0 and s.d0 <= x < s.d1 + 1e-9:
            dx = x - s.d0
            if abs(s.a) < 1e-12:
                return s.t0 + dx / s.v0
            return s.t0 + (-s.v0 + math.sqrt(s.v0 * s.v0 + 2 * s.a * dx)) / s.a
    raise ValueError(x)


def tick(t):
    return int(round(t * HZ))


def case_impact_t(c):
    s = next(s for s in SEGS if s.lap == c["lap"] and s.name == c["seg"])
    t = tick(s.t0 + c["at"]) / HZ
    assert s.t0 < t < s.t1, c["name"]
    return t


IMPACTS: list = []
PIT_ENTRIES: list = []


def configure(name):
    """리플레이 하나의 랩 계획으로 모듈 상태를 다시 짠다 (구간 표·충격·피트 진입)."""
    global NAME, CASES, PIT_LAPS, N_LAPS, SEGS, T_END, IMPACTS, PIT_ENTRIES
    sc = SCENARIOS[name]
    NAME, CASES, PIT_LAPS, N_LAPS = name, sc["cases"], sc["pit_laps"], sc["n_laps"]
    SEGS = build_track()
    T_END = SEGS[-1].t1
    IMPACTS = [(case_impact_t(c), c) for c in CASES]
    PIT_ENTRIES = sorted(s.t0 for s in SEGS if s.name == "PIT_IN")


def damage_at(t):
    """t 시점의 손상 상태 — 가장 최근 충격(다음 피트 진입 전까지 유지)."""
    cur = None
    for ti, c in IMPACTS:
        if ti <= t:
            nxt = [p for p in PIT_ENTRIES if p > ti]
            if not nxt or t < nxt[0]:
                cur = (ti, c)
    return cur


def stint(t):
    return sum(1 for p in PIT_ENTRIES if p <= t)


def static_at(t):
    """t가 든 스틴트에 정적 필드 경우가 있나."""
    st = stint(t)
    return any(c.get("static") and stint(ti) == st for ti, c in IMPACTS)


def phase_at(t):
    if t < T_FORMATION_END:
        return 3
    if t < T_GREEN:
        return 4
    return 5


def decel_g(n):
    """틱 n−1 → n 구간의 평균 감속 (g, 가속이면 음수) — 모델 속도(반올림 전)."""
    if n <= 0:
        return 0.0
    return (speed_ms((n - 1) / HZ) - speed_ms(n / HZ)) / (1.0 / HZ) / G


def height(n):
    t = n / HZ
    s = seg_at(t)
    if static_at(t):
        return STATIC_H
    v = speed_ms(t) * 3.6
    dmg = damage_at(t)
    h0 = H0 - (dmg[1]["drop"] if dmg else 0.0)
    g_now, g_prev = decel_g(n), decel_g(n - 1)
    h = h0 - AERO_C * (v / 300.0) ** 2
    if s is not None and not s.pit:
        h -= DIVE_PER_G * (max(g_now, 0.0) + 0.5 * max(g_prev, 0.0))
        h += LIFT_PER_G * max(-g_now, 0.0)
    h += 0.0003 * math.sin(n * 1.37) + 0.0002 * math.sin(n * 0.53)
    return round(h, 4)


def snapshot(n):
    t = round(n / HZ, 3)
    d = dist(t)
    s = seg_at(t)
    pit = bool(s and s.pit)
    laps = max(int(math.floor(d / TL)), 0) if d >= 0 else 0
    crossings = [time_at(k * TL) for k in range(0, laps + 1)] if d >= 0 else []
    lse = crossings[-1] if crossings else 0.0
    times = [crossings[k] - crossings[k - 1] for k in range(1, len(crossings))]
    last = times[-1] if times else 0.0
    ld = d % TL if d >= 0 else d + TL
    speed = round(speed_ms(t) * 3.6, 1)
    dmg = damage_at(t)
    flat = bool(dmg and dmg[1].get("flat"))
    pitstops = sum(1 for p in SEGS if p.name == "PIT_OUT" and p.t1 <= t)
    laps_frac = max(d, 0.0) / TL

    def wheel(i):
        bad = flat and i == 1
        return {"brake_temp": 350.0, "pressure": 60.0 if bad else 172.0,
                "temps": [85.0, 88.0, 91.0], "carcass_temp": 85.0,
                "wear": round(max(1.0 - 0.008 * laps_frac, 0.0), 4),
                "flat": bad, "detached": False}

    last_imp = [(ti, c) for ti, c in IMPACTS if ti <= t]
    moving = speed > 0
    return {
        "t": t,
        "connected": True,
        "in_session": True,
        "session": {
            "track": "Circuit de Test",
            "session_type": 10,
            "current_et": t,
            "end_et": END_ET,
            "max_laps": INT_MAX,
            "track_len": round(TL, 2),
            "game_phase": phase_at(t),
            "yellow_state": 0,
            "sector_flags": [0, 0, 0],
            "pit_speed_limit": 80.0,
            "in_realtime": True,
            "raining": 0.0, "dark_cloud": 0.1,
            "ambient_temp": 22.0, "track_temp": 31.0,
            "avg_wetness": 0.0,
            "num_vehicles": 1,
        },
        "player": {
            "id": 0,
            "lap_number": laps + 1,
            "lap_start_et": round(lse, 3),
            "speed_kmh": speed,
            "rpm": 7200 if moving else 3000,
            "max_rpm": 9250,
            "gear": 5 if speed >= 150 else (3 if moving else 1),
            "fuel": round(max(70.0 - 1.2 * laps_frac, 0.0), 2),
            "fuel_capacity": 90.0,
            "water_temp": 88.0, "oil_temp": 102.0,
            "overheating": False, "detached": False,
            "front_wing_height": height(n) if t >= T_GREEN else (STATIC_H if static_at(t) else H0),
            "dent_severity": [0, 1, 0, 0, 0, 0, 0, 0] if dmg else [0] * 8,
            "last_impact_et": last_imp[-1][0] if last_imp else 0.0,
            "last_impact_mag": IMPACT_MAG if last_imp else 0.0,
            "in_pitlane": pit, "speed_limiter": pit,
            "wheels": [wheel(i) for i in range(4)],
        },
        "vehicles": [{
            "id": 0, "driver": "나", "vehicle": "Porsche 963", "cls": "Hypercar",
            "is_player": True, "place": 1,
            "total_laps": laps,
            "lap_dist": round(ld, 1),
            "path_lat": 0.0,
            "sector": 1 + int(ld / TL * 3) % 3,
            "last_lap": round(last, 3),
            "best_lap": round(min(times), 3) if times else 0.0,
            "last_s1": round(last / 3, 3) if last else 0.0,
            "last_s2": round(last * 2 / 3, 3) if last else 0.0,
            "time_behind_next": 0.0, "laps_behind_next": 0, "time_behind_leader": 0.0,
            "in_pits": pit, "pit_state": 3 if pit else 0,
            "num_pitstops": pitstops,
            "num_penalties": 0, "finish_status": 0,
            "flag_blue": False,
            "estimated_lap": LAP_S,
            "time_into_lap": round(t - lse, 3),
            "lap_start_et": round(lse, 3),
            "pos": [round(math.cos(ld / TL * 2 * math.pi) * 700, 1), 0.0,
                    round(math.sin(ld / TL * 2 * math.pi) * 700, 1)],
            "in_garage": False,
        }],
    }


def low_median(xs):
    return sorted(xs)[(len(xs) - 1) // 2]


def bins_of(samples):
    out = {}
    for b, h in samples:
        out.setdefault(b, []).append(h)
    return out


def compare(base, post):
    by_post = bins_of(post)
    matched = sorted(b for b, hs in by_post.items()
                     if len(hs) >= BIN_MIN and len(base.get(b, ())) >= BIN_MIN)
    if len(matched) < MIN_BINS:
        return None
    return low_median([low_median(base[b]) - low_median(by_post[b]) for b in matched]), matched


def judge(snaps, report_wait=True, tyre_hold=True):
    """
    규칙대로 경우마다 기준·판정·콜 시각을 구한다 (기대값) — 엔진과 독립인 재구현. 같은 스냅샷
    값(반올림된 속도·높이)으로 같은 산술을 하므로 판정 하락이 바이트까지 같다. 점검 리포트와
    damage_report 쿨다운(리포트 +20초 — 그 사이 윙 콜은 대기·재시도)도 따라간다. 대기 콜 재시도는
    새 충격의 점검 리포트가 예약돼 있으면 쉬고(report_wait), 타이어 문제 중엔 붙든다(tyre_hold) —
    둘 다 False면 b60e335 규칙 (대기 콜이 쿨다운 끝 틱에 바로 나가 다음 리포트를 잃을 수 있다).
    calls = 수락된 콜 [(시각, dedup 키, 문장)] 시각순 — 점검 리포트 포함.
    예전 EMA 감지(첫 충격 순간 EMA 기준 ↔ 이후 EMA, 10mm)의 첫 콜 모양 시각도 같이 구한다.
    """
    default = report_wait and tyre_hold
    calls = []
    out, old, old_max = {}, {}, {}
    pre, base, base_n, post, cur = [], None, 0, None, None
    pending, warned = None, False
    prev_t, prev_v, prev_calm = None, 0.0, False
    report_due, last_report = None, None
    ema, old_base, old_cur, old_warned = None, None, None, False
    last_hit, last_hit_et = None, None
    pit_prev = False
    calm_decels, gated_decels = [], []
    for s in snaps:
        p, t = s["player"], s["t"]
        if p["in_pitlane"] and not pit_prev:
            pre, base, base_n, post, cur, pending, warned = [], None, 0, None, None, None, False
            ema, old_base, old_cur, old_warned = None, None, None, False
        pit_prev = p["in_pitlane"]
        v, h = p["speed_kmh"], p["front_wing_height"]
        # 감속 (직전 두 틱 구간)
        calm = False
        if prev_t is not None:
            dt = t - prev_t
            if 0.0 < dt <= GAP_MAX:
                dec = (prev_v - v) / 3.6 / dt
                calm = dec <= DECEL_MAX
                if v >= SPEED_MIN and not p["in_pitlane"]:
                    (calm_decels if calm else gated_decels).append(dec)
        steady = calm and prev_calm
        prev_t, prev_v, prev_calm = t, v, calm
        # 예전 감지
        if h > 0 and not p["in_pitlane"] and v >= SPEED_MIN:
            ema = h if ema is None else (1 - OLD_ALPHA) * ema + OLD_ALPHA * h
            if old_base is not None:
                old_max[old_cur] = max(old_max.get(old_cur, -1.0), old_base - ema)
            if old_base is not None and not old_warned and old_base - ema >= DROP_M:
                old[old_cur] = (t, old_base - ema)
                old_warned = True
        flat = any(w["flat"] or w["detached"] for w in p["wheels"])
        if pending is not None:
            if report_wait and report_due is not None:
                pass                                # 새 충격의 점검 리포트가 먼저
            elif tyre_hold and flat:
                out[cur].setdefault("pending_hold_t", t)
            elif last_report is None or not (t - last_report < REPORT_COOLDOWN):
                out[cur]["call_t"] = t
                calls.append((t, "wing_damage", SAY_WING.format(mm=pending * 1000)))
                last_report, pending, warned = t, None, True
        elif (h > 0 and not p["in_pitlane"] and v >= SPEED_MIN and steady):
            sample = (int(math.floor((v - SPEED_MIN) / BIN_KMH)), h)
            if base is None:
                pre = (pre + [sample])[-BASE_N:]
            elif post is not None and not warned:
                post = (post + [sample])[-POST_N:]
                if report_due is not None and compare(base, post) is not None:
                    out[cur].setdefault("ready_t", t)     # 리포트 대기만 없으면 판정했을 틱
                if report_due is None:
                    r = compare(base, post)
                    if r is not None:
                        o = out[cur]
                        if flat:
                            o.setdefault("hold_t", t)
                            o.setdefault("hold_drop", r[0])
                        else:
                            o.update(judge_t=t, drop=r[0], bins=r[1], n_post=len(post))
                            post = None
                            if r[0] >= DROP_M:
                                pending = r[0]
                                o["pending_t"] = t
                                if last_report is None or not (t - last_report < REPORT_COOLDOWN):
                                    o["call_t"] = t
                                    calls.append((t, "wing_damage", SAY_WING.format(mm=r[0] * 1000)))
                                    last_report, pending, warned = t, None, True
        # 충격
        hit = next((c for ti, c in IMPACTS if abs(ti - t) < 1e-9), None)
        if hit is not None:
            name = last_hit = hit["name"]
            last_hit_et = p["last_impact_et"]
            out[name] = {"impact_t": t, "pending_at_impact": pending is not None}
            if old_base is None:
                old_base = ema
            old_cur = name
            ok = True
            if base is None:
                b = bins_of(pre)
                full = sum(1 for hs in b.values() if len(hs) >= BIN_MIN)
                out[name]["pre_full_bins"] = full
                if full < MIN_BINS:
                    out[name]["no_base"] = True
                    pre, ok = [], False
                else:
                    base, base_n = b, len(pre)
            if ok:
                out[name]["base_n"] = base_n
                if not warned and pending is None:
                    post, cur = [], name
            report_due = t + REPORT_DELAY
        if report_due is not None and t >= report_due:
            report_due = None
            out[last_hit]["report_t"] = t
            if last_report is not None and t - last_report < REPORT_COOLDOWN:
                assert not default, f"{NAME}: 점검 리포트가 쿨다운에 걸림 t={t}"
                out[last_hit]["report_lost"] = True     # 예전 규칙 — 이 리포트를 잃는다
            else:
                last_report = t
                calls.append((t, f"dmg_report_{last_hit_et}",
                              SAY_REPORT_BOX if flat else SAY_REPORT_MARKS))
    # 샘플 감속이 경계(4.9m/s²)에서 멀다 — 엔진 반올림 차이로 갈리지 않게
    assert max(calm_decels) <= DECEL_MAX * 0.5, max(calm_decels)
    assert min(gated_decels) >= DECEL_MAX * 1.2, min(gated_decels)
    for name, (t, d) in old.items():
        old[name] = (t, d, old_max[name])
    return out, old, calls


def build():
    snaps = [snapshot(n) for n in range(tick(T_END) + 1)]
    res, old, calls = judge(snaps)
    reports = SCENARIOS[NAME].get("reports", False)
    variants = {}
    if reports:
        # 대기 콜 규칙 하나씩 뺀 판정 — 각 규칙이 이 리플레이의 기대 콜을 바꿔야 한다
        for label, kw in (("no_report_wait", dict(report_wait=False)),
                          ("no_tyre_hold", dict(tyre_hold=False)),
                          ("b60e335", dict(report_wait=False, tyre_hold=False))):
            vres, _, vcalls = judge(snaps, **kw)
            assert vcalls != calls, (NAME, label)
            variants[label] = (vres, vcalls)
    events, cases = [], []
    for ti, c in IMPACTS:
        r = res[c["name"]]
        if c.get("second"):
            # 대기 콜이 있는 동안의 두 번째 충격 — 판정 버퍼를 새로 열지 않고, 이 충격의 점검 리포트는
            # 앞 리포트 쿨다운 밖(+12~20초 창)이라 나간다. 예전 규칙은 이 리포트를 잃는다
            assert r["pending_at_impact"] and "judge_t" not in r, (c["name"], r)
            assert abs(r["report_t"] - (ti + REPORT_DELAY)) < 1e-6 and not r.get("report_lost"), (c["name"], r)
            prev_report = max(x["report_t"] for x in res.values()
                              if x.get("report_t", math.inf) < ti)
            assert prev_report + 12.0 <= ti < prev_report + REPORT_COOLDOWN, (c["name"], ti, prev_report)
            assert variants["b60e335"][0][c["name"]].get("report_lost"), (c["name"], "b60e335")
            call = False
        elif c.get("no_base"):
            assert r.get("no_base") and r["pre_full_bins"] < MIN_BINS, (c["name"], r)
            call = False
        elif c.get("flat"):
            # 펑크 규칙만 없으면 콜이 나갈 하락이어야 한다
            assert "judge_t" not in r and r["hold_drop"] >= DROP_M * 1.3, (c["name"], r)
            call = False
        else:
            assert not r.get("no_base") and "judge_t" in r, (c["name"], r)
            assert len(r["bins"]) >= MIN_BINS and r["judge_t"] > r["report_t"], (c["name"], r)
            call = "call_t" in r
            if c.get("held"):
                # 콜 판정은 났지만(대기) 두 번째 충격의 펑크로 피트까지 붙든다
                assert not call and "pending_t" in r and r["pending_hold_t"] >= r["pending_t"], (c["name"], r)
                assert r["drop"] >= DROP_M * 1.3, (c["name"], r["drop"])
            if reports and not c.get("second"):
                # 판정이 리포트 쿨다운 안 — 콜은 대기로 시작한다
                assert r["report_t"] <= r["pending_t"] < r["report_t"] + REPORT_COOLDOWN, (c["name"], r)
            if c["name"] == "splitter_damage":
                # 리포트 대기 규칙이 실제로 쓰인다 — 대기만 없으면 리포트 전에 판정·콜했을 경우
                assert r.get("ready_t", r["report_t"]) < r["report_t"], (c["name"], r)
            if call:
                # 하락은 임계(10mm)에서 멀고, mm 반올림 경계(.5)에서도 멀다
                mm = r["drop"] * 1000
                assert r["drop"] >= DROP_M * 1.3 and abs(mm - math.floor(mm) - 0.5) > 0.05, (c["name"], mm)
                assert abs(r["drop"] - c["drop"]) <= 0.003, (c["name"], r["drop"])
            elif not c.get("held"):
                assert abs(r["drop"]) <= DROP_M * 0.3, (c["name"], r["drop"])
        assert call == c["expect"], (c["name"], r)
        if c["name"] == "healthy_tap":
            # 예전 감지라면 이 경우에서 콜 — 이 리플레이가 회귀를 잡는다는 근거
            assert c["name"] in old and old[c["name"]][2] >= DROP_M * 1.1, \
                ("healthy_tap: 예전 EMA 감지가 확실히 콜하는 모양이 아님", old.get(c["name"]))
        if c.get("static"):
            assert c["name"] not in old and r["drop"] == 0.0, (c["name"], r)
        if call and not reports:
            events.append({
                "t": round(r["call_t"], 2),
                "type": "damage_report",
                "data": {},
                "message": SAY_WING.format(mm=r["drop"] * 1000),
            })
        case = {"name": c["name"], "impact_t": ti, "call": call, "note": c["note"]}
        for k in ("report_t", "ready_t", "judge_t", "call_t", "hold_t", "n_post", "base_n", "pre_full_bins") \
                + (("pending_t", "pending_hold_t") if reports else ()):
            if k in r:
                case[k] = r[k]
        for label, (vres, _) in variants.items():
            v = vres[c["name"]]
            case[label] = {"call_t": v.get("call_t"), "report_lost": bool(v.get("report_lost"))}
        if "drop" in r:
            case["drop_mm"] = round(r["drop"] * 1000, 2)
            case["bins_kmh"] = [int(SPEED_MIN + b * BIN_KMH) for b in r["bins"]]
        if "hold_drop" in r:
            case["hold_drop_mm"] = round(r["hold_drop"] * 1000, 2)
        if c["name"] in old:
            case["old_call_t"] = old[c["name"]][0]
            case["old_max_drop_mm"] = round(old[c["name"]][2] * 1000, 2)
        cases.append(case)

    keys = ["wing_damage"]
    if reports:
        # 점검 리포트까지 대조 — 대기 윙 콜과 같은 damage_report 쿨다운을 나눠 쓴다
        assert sum(1 for _, k, _ in calls if k == "wing_damage") <= 1, calls
        events = [{"t": round(t, 2), "type": "damage_report", "data": {}, "message": m}
                  for t, _, m in calls]
        keys += sorted({k for _, k, _ in calls if k != "wing_damage"}
                       | {f"dmg_report_{ti}" for ti, _ in IMPACTS})
    sidecar = {
        "scenario": NAME,
        "note": "윙 콜 — 제동이 아닌(직전 두 틱 감속 ≤ 0.5g) 150km/h 이상 샘플의 프론트 윙 높이를 10km/h "
                "속도 구간별로 첫 충격 전(301개) ↔ 충격 뒤 비교, 양쪽 5샘플 이상 구간 3개부터 판정, "
                "구간별 중앙값 차의 중앙값 ≥ 10mm면 콜. 기준 부족이면 그 충격만, 타이어 펑크면 콜 없음",
        "types": ["damage_report"],
        "keys": keys,
        "events": events,
        "cases": cases,
    }
    return snaps, sidecar


def write(out_dir, snaps, sidecar):
    body = "".join(json.dumps(s, ensure_ascii=False, separators=(",", ":")) + "\n"
                   for s in snaps).encode("utf-8")
    gz = os.path.join(out_dir, NAME + ".jsonl.gz")
    with open(gz, "wb") as raw:
        # mtime=0·빈 파일명 — 같은 입력이면 같은 바이트 (zlib 버전 차이는 --check가 해제 후 비교)
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
            f.write(body)
    side = os.path.join(out_dir, NAME + ".expect.json")
    with open(side, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(sidecar, indent=1, sort_keys=True, ensure_ascii=False) + "\n")
    return gz, side


def read_gz(path):
    with gzip.open(path, "rb") as f:
        return f.read()


def main() -> int:
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default=os.path.join(root, "data", "replays"))
    ap.add_argument("--check", action="store_true",
                    help="임시 폴더에 다시 만들어 저장소 파일과 대조 (압축 해제 바이트)")
    args = ap.parse_args()
    rc = 0
    for name in SCENARIOS:
        configure(name)
        rc |= one(args)
    return rc


def one(args) -> int:
    snaps, sidecar = build()
    if not args.check:
        os.makedirs(args.out_dir, exist_ok=True)
        gz, side = write(args.out_dir, snaps, sidecar)
        print(f"{gz}: 스냅샷 {len(snaps)}개 (+ {os.path.basename(side)}, "
              f"기대 콜 {len(sidecar['events'])}개 / 경우 {len(sidecar['cases'])}개)")
        return 0
    with tempfile.TemporaryDirectory() as tmp:
        gz, side = write(tmp, snaps, sidecar)
        mine_gz = os.path.join(args.out_dir, NAME + ".jsonl.gz")
        mine_side = os.path.join(args.out_dir, NAME + ".expect.json")
        if not (os.path.exists(mine_gz) and os.path.exists(mine_side)):
            print(f"❌ {NAME}: 파일 없음 — 재생성 필요 (python3 tools/make_wing_replay.py)")
            return 1
        with open(mine_side, "rb") as a, open(side, "rb") as b:
            same = read_gz(mine_gz) == read_gz(gz) and a.read() == b.read()
    if same:
        print(f"✅ {NAME}: 생성기 출력과 동일 (스냅샷 {len(snaps)}개)")
        return 0
    print(f"❌ {NAME}: 생성기 출력과 다름 — 재생성 필요 (python3 tools/make_wing_replay.py)")
    return 1


if __name__ == "__main__":
    sys.exit(main())
