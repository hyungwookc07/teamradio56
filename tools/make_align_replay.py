"""
얼라인(조향 쏠림) 콜 회귀용 합성 리플레이 생성기 — 리플레이 + 기대 콜(expect) 사이드카.

사용자 제보: "스티어링 풀(Steering pull on the straights…)이 차가 멀쩡할 때도 나온다".
예전 감지는 150km/h 이상·|조향| ≤ 0.20 샘플 전부의 EMA를 충격 순간 값과 75샘플 뒤에
비교했다. |조향| 0.20은 핸들 70~110°라 고속 스위퍼(조향 0.1~0.18)가 전부 '직선'으로 들어가고,
EMA가 충격 지점과 15초 뒤 지점의 코너 구성(좌우 비율) 차이를 오프셋 변화로 읽었다 —
스위퍼 구간 직전 가벼운 접촉 하나로 멀쩡한 차에 콜. 녹화 리플레이에는 조향·요레이트·횡속도
필드가 아예 없어서(race_full 등) 이 감지는 한 번도 재생된 적이 없다.

여기서는 레이스 몇 개에 경우를 나눠 넣고(경우 사이는 피트 진입 = 얼라인 상태 리셋), 어떤 콜이
몇 초에 나와야 하는지를 data/replays/<이름>.expect.json에 같이 쓴다 (tools/expect_calls.py가
파이썬·C# 둘 다 대조 — align_damage/align_severe 키만). 리플레이 하니스는 버스를 비우지 않아
같은 키 콜이 리플레이마다 한 번만 수락되므로 경미·심각 콜이 각각 하나씩 넘지 않게 나눈다:
  · align — 스위퍼 직전 접촉(침묵) · 피트 아웃 직후 기준 없음(침묵) · 펑크(침묵) · 경미 · 심각
    (스위퍼는 전부 오른쪽 — 충격 뒤 창이 한쪽으로 쏠려 직선 게이트 없는 중앙값이면 콜한다)
  · align_gate — 메인 직선 접촉 뒤 게이트 하나만 어기는 직선 틱: 슬라이드(횡속도 4.2m/s만 밖)와
    플랫 굽이(요레이트×속도 3m/s²만 밖) → 침묵. 게이트 하나만 빼도 콜이 나간다
  · align_rebase — 1랩 첫 직선의 기준 없는 접촉 뒤 한 랩 더 달리고 실제 손상 → 경미
    (6e8bc8e는 기준 없는 충격 하나가 피트까지 감지를 꺼서 침묵)
  · align_crab — 리어 토 손상으로 비스듬히 곧게 감(직선 횡속도 1~2.5m/s) → 경미 · 심각
    (6e8bc8e는 횡속도 1.0m/s 상한이 고속 직선 샘플을 버려 판정 버퍼가 차지 않아 침묵)
생성기는 새 경우가 직전 규칙(6e8bc8e)에선 콜이 안 나는 모양이라는 것도 확인한다.
두 직선 게이트가 서로 대신하지 못한다는 것도 확인한다 — 보통 틱은 횡속도 = SLIP × 횡가속이라
두 게이트가 같이 열리고 닫히므로, 게이트 하나만 뺀 규칙(요레이트×속도 / 횡속도 상한 없음)으로
다시 판정해 healthy_sweepers(요레이트)·healthy_slide_bend(둘 다)의 판정이 바뀌어야 하고, 게이트
하나 어김 셋(슬라이드·굽이)은 각각 그 셋만 더 세어도 중앙값이 0.03 넘게 움직여야 한다. 스위퍼
경우는 |조향| ≤ 0.20만 거른 충격~판정 사이 샘플 중앙값이 기준에서 0.03 이상 벗어나야 한다.

규칙 (기대값의 근거, analyzers/health.py STEER_* · C# HealthAnalyzer와 같은 값):
  · 샘플 = 피트레인 밖 · 150km/h 이상 · |요레이트|×속도 ≤ 1.5m/s² · |횡속도| ≤ 3.0m/s ·
    |조향| ≤ 0.20 — 차가 실제로 곧게 가는 순간만 (스위퍼 안은 빠지고, 스위퍼 사이 짧은
    직선·코너 시작 틱은 들어간다). 리어 토가 틀어져 비스듬히 곧게 가는 '게걸음'(횡속도
    1~2.5m/s, 요레이트 0)도 샘플이다
  · 기준 = 첫 충격 전 최근 101샘플 중앙값, 25샘플 미만이면 기준 없음 → 그 충격만 콜 없음,
    기준 버퍼는 그 충격 뒤부터 새로 모아 다음 충격이 기준을 굳힌다
  · 판정 = 충격 뒤 새 버퍼 51샘플이 찼을 때 중앙값 − 기준: ≥ 0.03 경미, ≥ 0.08 심각
  · 타이어 펑크/탈락·공기압 누출 중엔 콜하지 않는다 (쏠림은 타이어 탓)
  · 판정 시각은 점검 리포트(충격 +8초)의 damage_report 쿨다운 20초 밖 — 재시도 없이 그 틱

모델: 혼자 달리는 레이스(시간제). 랩 = 직선 S1(290) · 시케인(110) · 직선 S2(250) · 고속 스위퍼
구간 F(200km/h, 오른쪽 4코너, 조향 최대 0.17) · 중속 코너(130) · 직선 S3(270) · 헤어핀(80).
구간 안은 등속이고 코너는 반 사인 곡률. 조향 = 오프셋 + GAIN × 곡률 + 손떨림, 요레이트 = 속도 ×
곡률, 횡속도 = 0.06 × 횡가속 + 작은 흔들림 — 손상된 차도 드라이버가 오프셋을 잡고 곧게 가므로
직선의 요레이트는 0 근처. 리어 손상(crab) 경우는 차체 슬립각 β = 오프셋 × 락(17.5°)으로
비스듬히 달려 횡속도에 속도 × sin β가 더해진다. 피트 랩은 S1 자리를 피트레인(~78km/h,
12초 정지)으로 달린다. 구간 경계는 전부 틱 위라 코너 첫 틱은
곡률 0(곧게 감), 다음 틱부터 확실히 코너다. align_gate의 슬라이드(카운터 −0.15, 횡속도 4.2m/s,
요레이트×속도 0.4m/s²)·플랫 굽이(조향 +0.06, 요레이트×속도 3m/s², 횡속도 0.18m/s)는 이 모델을
벗어나 값을 직접 정한 직선 틱이다 (각각 6초 = 30틱 — 판정 버퍼 51개의 과반).
위치·시각은 구간 표에서 해석적으로 구한다 (dt 누적 없음 — 재생성 결과가 머신과 무관하게 같다).

사용법: python tools/make_align_replay.py [--out-dir data/replays] [--check]
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
END_ET = 3000.0             # 시간제 50분 — 리플레이(~19분) 안에 하프·남은 시간 안내가 없다
T_FORMATION_END = 20.0      # 0~20 페이즈 3, 20~30 페이즈 4 (카운트다운), 30부터 그린
T_GREEN = 30.0
GRID = -30.0                # 그리드 위치 (라인 뒤 m)
IMPACT_MAG = 700.0          # damage_impact_mag(500) 이상, 큰 충격(×4) 아님 — 가벼운 접촉
REPORT_DELAY = 8.0          # health.REPORT_DELAY_SEC
REPORT_COOLDOWN = 20.0      # config cooldowns.damage_report

# 감지 규칙 (analyzers/health.py STEER_*)
SPEED_MIN = 150.0
STEER_MAX = 0.20
LAT_ACC_MAX = 1.5
LAT_VEL_MAX = 3.0
SHIFT_MIN = 0.03
SHIFT_SEVERE = 0.08
BASE_N = 101
BASE_MIN = 25
JUDGE_N = 51
# 예전 감지 (EMA) — 경우 (a)가 예전 코드에서 콜이 나는 모양인지 확인용
OLD_ALPHA = 0.02
OLD_JUDGE = 75
# 직전 중앙값 감지 (6e8bc8e) — 기준 없는 충격 하나가 스틴트 전체를 끄고, 횡속도 1.0m/s
# 상한이 리어 토 게걸음 샘플을 버리던 규칙. 새 경우가 그 코드에서 실패하는지 확인용
PREV_LAT_VEL_MAX = 1.0
LOCK_DEG = 17.5             # 조향 풀 락의 로드휠 각 (LMU 프로토타입 근사) — 게걸음 슬립각

# 게이트 하나만 어기는 직선 틱 (healthy_slide_bend) — 두 게이트가 서로 대신하지 못함을 보이는 셋.
# 다른 틱은 횡속도 = SLIP × 횡가속이라 요레이트 게이트와 횡속도 게이트가 늘 같이 열리고 닫힌다
SLIDE_LAT = 4.2             # m/s 슬라이드 횡속도 (상한 3.0 밖) — 요레이트 0.55rad/s 미만이라 리어 불안정 감지도 침묵
SLIDE_ACC = 0.4             # m/s² 슬라이드 중 요레이트×속도 (상한 1.5 안 — 차는 돌지 않는다)
SLIDE_STEER = -0.15         # 카운터 스티어
BEND_ACC = 3.0              # m/s² 플랫 굽이의 요레이트×속도 (상한 1.5 밖)
BEND_STEER = 0.06           # 굽이에서 잡은 조향 — 횡속도는 SLIP × 3 = 0.18m/s (상한 안)

GAIN = 21.0                 # 조향(락 대비) / 곡률(1/m) — 고속 언더스티어 포함 근사
SLIP = 0.06                 # 횡속도(m/s) / 횡가속(m/s²) — 정상 코너링 1~2m/s

SAY_MILD = ("Steering pull on the straights. Alignment's off from that hit. "
            "It'll eat the tyre. Repair call on pace.")
SAY_SEVERE = "Alignment is badly out. You're steering on the straights. Box for repairs."

# 구간: (이름, 종류, 시간 s, km/h, 코너 목록) — 코너 = (시작 s, 길이 s, 건강한 차의 최대 조향)
# 전부 오른쪽 — 충격 뒤 창이 한쪽으로 쏠려 있어야 '직선 게이트 없는 중앙값'이 여기서 콜한다
SWEEPERS = [(0.0, 5.0, 0.15), (6.0, 3.0, 0.10), (10.0, 6.0, 0.17), (17.0, 4.0, 0.12)]
LAP = [
    ("S1", "straight", 14.0, 290.0, []),
    ("CH", "corner", 6.0, 110.0, [(0.0, 3.0, 0.32), (3.0, 3.0, -0.32)]),
    ("S2", "straight", 8.0, 250.0, []),
    ("F", "corner", 22.0, 200.0, SWEEPERS),
    ("MC", "corner", 5.0, 130.0, [(0.0, 5.0, 0.22)]),
    ("S3", "straight", 12.0, 270.0, []),
    ("HP", "corner", 6.0, 80.0, [(0.0, 6.0, 0.45)]),
]
TL = sum(dur * kmh / 3.6 for _, _, dur, kmh, _ in LAP)
PIT_HALF_S = 26.0           # 피트레인 반 구간 시간 (S1 길이의 반, ~78km/h) — 구간 경계가 틱에 맞게
PIT_STOP_S = 12.0
LAP_S = sum(seg[2] for seg in LAP)

# 랩 계획 — 랩 번호(0부터)별: pit = 이 랩 S1을 피트레인으로, impact = (경우 이름, 구간, 구간 안 시각)
# 충격 뒤 상태(offset 조향 오프셋, flat 우측 앞 펑크, crab 리어 토 게걸음)는 다음 피트 진입까지
# 유지된다 (같은 스틴트의 다음 충격이 오면 그 충격의 상태). no_base = 기준 없는 충격이어야 함.
# 리플레이 하니스는 버스를 비우지 않아 dedup 키(align_damage/align_severe)가 리플레이마다 한 번만
# 수락된다 — 경미·심각 콜이 각각 하나를 넘으면 리플레이를 나눈다.
SCENARIOS = {
    "align": dict(pit_laps={2, 4, 6, 8}, n_laps=11, cases=[
        dict(name="healthy_sweepers", lap=1, seg="S2", at=7.5, offset=0.0, expect=None, gates={"yaw"},
             note="멀쩡한 차 + 오른쪽 스위퍼 4개 직전 가벼운 접촉 → 콜 없음 "
                  "(예전: 스위퍼 조향이 EMA에 섞여 align_mild 오탐. 요레이트 게이트 없는 중앙값도 "
                  "한쪽으로 쏠린 스위퍼 조향을 읽어 콜)"),
        dict(name="no_baseline", lap=2, no_base=True, seg="S2", at=3.0, offset=0.05, expect=None,
             note="피트 아웃 직후(기준 직선 샘플 16개 < 25) 충격 + 실제 오프셋 +0.05 → 기준 없음, 콜 없음"),
        dict(name="flat_tyre_pull", lap=5, seg="S2", at=7.5, offset=0.05, flat=True, expect=None,
             note="충격 + 우측 앞 펑크로 +0.05 쏠림 → 타이어 탓, 얼라인 콜 없음 (점검 리포트가 펑크를 부른다)"),
        dict(name="align_mild", lap=7, seg="S2", at=7.5, offset=0.05, expect="mild",
             note="충격 뒤 직선 조향 오프셋 +0.05 → align_mild 하나"),
        dict(name="align_severe", lap=9, seg="S2", at=7.5, offset=0.10, expect="severe",
             note="충격 뒤 직선 조향 오프셋 +0.10 → align_severe 하나"),
    ]),
    # 직선 게이트 둘이 각각 판정을 지킨다 — 게이트 하나만 어기는 직선 틱. 게이트를 뺀 엔진의 콜이
    # 이 리플레이의 첫 얼라인 콜이 되게 따로 둔다 (슬라이드는 심각 콜 모양 — align의 심각 콜 뒤면
    # 같은 dedup 키라 수락되지 않아 회귀가 안 보인다)
    "align_gate": dict(pit_laps=set(), n_laps=3, cases=[
        # 판정 버퍼가 S2·스위퍼 사이 직선 틱으로 차서 판정은 F 안 (충격 +33초, 리포트 쿨다운 밖)
        dict(name="healthy_slide_bend", lap=1, seg="S1", at=0.6, offset=0.0, expect=None,
             slide=("S1", 1.0, 7.0), bend=("S1", 7.0, 13.0), judge_seg="F", gates={"yaw", "lat"},
             note="멀쩡한 차 + 메인 직선 초입 가벼운 접촉 뒤 같은 직선에서 6초 슬라이드(횡속도 4.2m/s, "
                  "요레이트×속도 0.4m/s², 카운터 −0.15)와 6초 플랫 굽이(요레이트×속도 3m/s², 횡속도 "
                  "0.2m/s, 조향 +0.06) → 콜 없음 (횡속도 게이트만 빼면 슬라이드가, 요레이트 게이트만 "
                  "빼면 굽이가 판정 버퍼의 과반이 되어 중앙값을 0.03 넘게 옮긴다)"),
    ]),
    # 기준 없는 충격은 그 충격만 — 1랩 첫 직선의 접촉 뒤에도 다음 충격은 판정한다
    "align_rebase": dict(pit_laps=set(), n_laps=3, cases=[
        dict(name="opening_tap", lap=0, no_base=True, seg="S1", at=3.0, offset=0.0, expect=None,
             note="1랩 첫 직선 3초(기준 직선 샘플 16개 < 25)에 가벼운 접촉(멀쩡) → 이 충격만 콜 없음, "
                  "기준은 이 충격 뒤부터 다시 모은다"),
        dict(name="after_no_baseline", lap=1, seg="S2", at=7.5, offset=0.05, expect="mild",
             note="기준 없는 접촉 뒤 한 랩 달린 다음 실제 손상(+0.05) → align_mild "
                  "(직전 코드: 기준 없는 충격 하나가 피트까지 감지를 꺼서 침묵)"),
    ]),
    # 리어 토 손상 — 요레이트 0으로 비스듬히 곧게 간다 (횡속도 = 속도 × sin β)
    "align_crab": dict(pit_laps={2}, n_laps=5, cases=[
        dict(name="rear_crab_mild", lap=1, seg="S2", at=7.5, offset=0.05, crab=True, expect="mild",
             note="리어 토 손상 — 오프셋 +0.05로 비스듬히 곧게 감(직선 횡속도 1.0~1.25m/s) → align_mild "
                  "(직전 코드: 횡속도 1.0m/s 상한이 고속 직선 샘플을 버려 판정 버퍼가 안 참)"),
        dict(name="rear_crab_severe", lap=3, seg="S2", at=7.5, offset=0.10, crab=True, expect="severe",
             note="리어 토 심한 손상 — 오프셋 +0.10, 직선 횡속도 2.0~2.5m/s → align_severe "
                  "(직전 코드: 샘플이 전부 빠져 콜 없음)"),
    ]),
}
NAME = "align"
CASES: list = []
PIT_LAPS: set = set()
N_LAPS = 0


class Seg:
    def __init__(self, name, kind, t0, dur, v, d0, corners, lap, pit):
        self.name, self.kind, self.t0, self.dur, self.v = name, kind, t0, dur, v
        self.d0, self.corners, self.lap, self.pit = d0, corners, lap, pit
        self.t1 = t0 + dur
        self.d1 = d0 + v * dur


def build_track():
    segs = []
    t, d = T_GREEN, GRID
    segs.append(Seg("LAUNCH", "straight", t, 1.0, -GRID, d, [], -1, False))
    t, d = segs[-1].t1, 0.0
    for lap in range(N_LAPS):
        for name, kind, dur, kmh, corners in LAP:
            if name == "S1" and lap in PIT_LAPS:
                v = TL_S1 / 2 / PIT_HALF_S
                for nm, du, vv in (("PIT_IN", PIT_HALF_S, v), ("PIT_STOP", PIT_STOP_S, 0.0),
                                   ("PIT_OUT", PIT_HALF_S, v)):
                    segs.append(Seg(nm, "pit", t, du, vv, d, [], lap, True))
                    t, d = segs[-1].t1, segs[-1].d1
                continue
            segs.append(Seg(name, kind, t, dur, kmh / 3.6, d, corners, lap, False))
            t, d = segs[-1].t1, segs[-1].d1
            if lap == N_LAPS - 1 and name == "S1":
                return segs
    return segs


TL_S1 = LAP[0][2] * LAP[0][3] / 3.6
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
    return s.d0 + s.v * (min(t, s.t1) - s.t0)


def time_at(x):
    """누적 거리 x에 처음 닿는 시각."""
    for s in SEGS:
        if s.v > 0 and s.d0 <= x < s.d1 + 1e-9:
            return s.t0 + (x - s.d0) / s.v
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


def curvature(s, t, n):
    """구간 s의 시각 t 곡률(1/m) — 코너 반 사인 + 직선 흔들림 (틱 n 기반 결정적)."""
    wob = 4e-5 * math.sin(n * 0.23) + 3e-5 * math.sin(n * 1.13)
    if s.kind == "pit" or s.v == 0:
        return 0.0
    k = 0.0
    u = t - s.t0
    for c0, cl, peak in s.corners:
        if c0 <= u < c0 + cl:
            k = peak / GAIN * math.sin(math.pi * (u - c0) / cl)
    return k + wob


def odd_at(n):
    """틱 n이 경우의 게이트 하나 어김 구간이면 그 종류("slide"/"bend"), 아니면 None."""
    s = seg_at(n / HZ)
    if s is None:
        return None
    for c in CASES:
        for kind in ("slide", "bend"):
            w = c.get(kind)
            if w and s.lap == c["lap"] and s.name == w[0] \
                    and tick(s.t0 + w[1]) <= n < tick(s.t0 + w[2]):
                return kind
    return None


def phase_at(t):
    if t < T_FORMATION_END:
        return 3
    if t < T_GREEN:
        return 4
    return 5


def motion(n):
    """(속도 km/h, 조향, 요레이트, 횡속도, 피트레인) — 반올림은 telemetry.py와 같은 자리수."""
    t = n / HZ
    s = seg_at(t)
    if s is None or s.v == 0:
        return 0.0, 0.0, 0.0, 0.0, bool(s and s.pit)
    k = curvature(s, t, n)
    dmg = damage_at(t)
    offset = dmg[1]["offset"] if dmg else 0.0
    steer = offset + GAIN * k + 0.0015 * math.sin(n * 1.7) + 0.0008 * math.sin(n * 0.37)
    yaw = s.v * k
    lat = SLIP * s.v * s.v * k + 0.05 * math.sin(n * 0.71)
    if dmg and dmg[1].get("crab"):
        lat += s.v * math.sin(math.radians(offset * LOCK_DEG))   # 리어 토 — 비스듬히 곧게 감
    odd = odd_at(n)
    if odd is not None:
        jit = steer - offset - GAIN * k
        if odd == "slide":       # 뒤가 흘러 카운터를 잡고 미끄러짐 — 차는 돌지 않는다
            steer, yaw = offset + SLIDE_STEER + jit, SLIDE_ACC / s.v
            lat = SLIDE_LAT + 0.05 * math.sin(n * 0.71)
        else:                    # 직선의 플랫 굽이 — 돌지만 미끄러지지 않는다
            steer, yaw = offset + BEND_STEER + jit, BEND_ACC / s.v
            lat = SLIP * BEND_ACC + 0.05 * math.sin(n * 0.71)
    return (round(s.v * 3.6, 1), round(steer, 4), round(yaw, 4), round(lat, 2), s.pit)


def snapshot(n):
    t = round(n / HZ, 3)
    d = dist(t)
    laps = max(int(math.floor(d / TL)), 0) if d >= 0 else 0
    crossings = [time_at(k * TL) for k in range(0, laps + 1)] if d >= 0 else []
    lse = crossings[-1] if crossings else 0.0
    times = [crossings[k] - crossings[k - 1] for k in range(1, len(crossings))]
    last = times[-1] if times else 0.0
    ld = d % TL if d >= 0 else d + TL
    speed, steer, yaw, lat, pit = motion(n)
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
            "front_wing_height": 0.0,
            "dent_severity": [0, 1, 0, 0, 0, 0, 0, 0] if dmg else [0] * 8,
            "last_impact_et": last_imp[-1][0] if last_imp else 0.0,
            "last_impact_mag": IMPACT_MAG if last_imp else 0.0,
            "in_pitlane": pit, "speed_limiter": pit,
            "steering": steer, "yaw_rate": yaw, "lat_vel": lat,
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


def median(xs):
    return sorted(xs)[len(xs) // 2]


def judge(snaps, lat_max=LAT_VEL_MAX, stint_off=False, acc_max=LAT_ACC_MAX, count=None):
    """
    규칙대로 경우마다 기준·판정을 구한다 (기대값). lat_max/stint_off는 직전 코드(6e8bc8e)
    규칙 — 횡속도 1.0m/s 상한, 기준 없는 충격이 피트까지 감지를 끔 — 재현용.
    acc_max/lat_max = inf는 게이트 하나를 뺀 규칙, count(스냅샷)가 참인 틱은 게이트와 무관하게
    샘플로 센다 (게이트 하나 어김 셋만 더한 규칙) — 두 게이트가 각각 판정을 지킨다는 근거.
    기본 규칙에선 샘플 분류가 경계에서 멀어야 한다 — 직선 샘플은 요레이트×속도 한계의
    2/3 안·횡속도 한계의 0.9 안, 코너 샘플은 요레이트×속도 한계의 1.5배 밖 (생성 모델이
    판정 경계에 걸쳐 엔진 반올림 차이로 갈리지 않게). 횡속도 상한은 리어 토 게걸음
    경우(2.5m/s까지)에서 실제로 쓰인다.
    """
    default = lat_max == LAT_VEL_MAX and acc_max == LAT_ACC_MAX and not stint_off and count is None
    out = {}
    pre, post, base, no_base, cur, n_base = [], None, None, False, None, 0
    ema, old_base, old_n, old_call = None, None, 0, {}
    pit_prev = False
    for s in snaps:
        p, t = s["player"], s["t"]
        if p["in_pitlane"] and not pit_prev:
            pre, post, base, no_base, cur = [], None, None, False, None
            ema, old_base, old_n = None, None, 0
        pit_prev = p["in_pitlane"]
        v = p["speed_kmh"]
        acc = abs(p["yaw_rate"]) * v / 3.6
        ok = (not p["in_pitlane"] and v >= SPEED_MIN and abs(p["steering"]) <= STEER_MAX
              and ((acc <= acc_max and abs(p["lat_vel"]) <= lat_max) or bool(count and count(s))))
        if v >= SPEED_MIN and not p["in_pitlane"]:
            if default:
                if ok:
                    assert acc <= LAT_ACC_MAX * 2 / 3 and abs(p["lat_vel"]) <= LAT_VEL_MAX * 0.9, t
                else:       # 코너(요레이트) 또는 슬라이드(횡속도) — 둘 중 하나는 확실히 밖
                    assert acc >= LAT_ACC_MAX * 1.5 or abs(p["lat_vel"]) >= LAT_VEL_MAX * 1.25, \
                        (t, acc, p["lat_vel"])
            # 예전 감지 (EMA, |조향| ≤ 0.20 전부)
            if abs(p["steering"]) <= STEER_MAX:
                ema = p["steering"] if ema is None else (1 - OLD_ALPHA) * ema + OLD_ALPHA * p["steering"]
                if old_base is not None:
                    old_n += 1
                    if old_n >= OLD_JUDGE and cur and cur["name"] not in old_call \
                            and abs(ema - old_base) >= SHIFT_MIN:
                        old_call[cur["name"]] = (t, ema - old_base)
        if ok:
            if base is None and not no_base:
                pre = (pre + [p["steering"]])[-BASE_N:]
            elif post is not None:
                post = (post + [p["steering"]])[-JUDGE_N:]
                if len(post) == JUDGE_N and cur["name"] not in out:
                    shift = median(post) - base
                    out[cur["name"]] = dict(t=t, base=base, post=median(post), shift=shift,
                                            n_base=n_base)
                    post = None
        hit = next((c for ti, c in IMPACTS if abs(ti - t) < 1e-9), None)
        if hit is not None:
            cur = hit
            if old_base is None:
                old_base, old_n = ema, 0
            if base is None and not no_base:
                if len(pre) >= BASE_MIN:
                    base, n_base = median(pre), len(pre)
                else:
                    out[hit["name"]] = dict(t=t, base=None, n_pre=len(pre))
                    if stint_off:
                        no_base = True          # 직전 코드: 피트까지 감지 꺼짐
                    pre = []                    # 새 규칙: 이 충격만, 기준은 다시 모은다
                    continue
            if base is not None:
                post = []
    return out, old_call


def r4(x):
    return round(x, 4) + 0.0        # -0.0 → 0.0


def decide(r):
    if r is None or r.get("base") is None:
        return None
    sh = abs(r["shift"])
    return "severe" if sh >= SHIFT_SEVERE else "mild" if sh >= SHIFT_MIN else None


def build():
    snaps = [snapshot(n) for n in range(tick(T_END) + 1)]
    res, old_call = judge(snaps)
    prev, _ = judge(snaps, lat_max=PREV_LAT_VEL_MAX, stint_off=True)
    # 게이트 하나씩 뺀 규칙 — 요레이트×속도 게이트 없음 / 횡속도 게이트 없음
    no_gate = {"yaw": judge(snaps, acc_max=math.inf)[0], "lat": judge(snaps, lat_max=math.inf)[0]}
    events, cases = [], []
    for ti, c in IMPACTS:
        ungated_post = None
        r = res.get(c["name"])
        assert r is not None, f"{c['name']}: 판정 버퍼가 차지 않음"
        if c.get("no_base"):
            # 피트 아웃 직후·1랩 첫 직선 충격 — 기준 없음 (이 충격만)
            assert r["base"] is None and r["n_pre"] < BASE_MIN, c["name"]
            decision = None
        else:
            assert r["n_base"] >= BASE_MIN, c["name"]
            decision = decide(r)
            # 판정 시각이 점검 리포트(+8초) 쿨다운(20초) 밖 — 재시도 경계에 걸치지 않게
            assert r["t"] >= ti + REPORT_DELAY + REPORT_COOLDOWN + 1.0, c["name"]
            # 판정 시각에 아직 스위퍼(F)를 지나 S3 — 같은 랩 안 (게이트 셋 경우는 F 안 직선 틱)
            seg = seg_at(r["t"])
            assert seg.lap == c["lap"] and seg.name == c.get("judge_seg", "S3"), (c["name"], seg.name)
        if c.get("flat"):
            assert decision == "mild", "flat_tyre_pull: 펑크 규칙만 없으면 콜이 나갈 모양이어야 한다"
            decision = None
        elif c["name"] == "no_baseline":
            assert c["offset"] >= SHIFT_MIN, "no_baseline: 기준만 있으면 콜이 나갈 손상이어야 한다"
        assert decision == c["expect"], (c["name"], decision, r)
        gate_off = {g: None if c.get("flat") else decide(no_gate[g].get(c["name"])) for g in no_gate}
        for g in c.get("gates", ()):
            # 이 게이트 하나만 빼면 판정이 바뀐다 — 리플레이가 이 게이트를 지킨다
            assert gate_off[g] != decision, (c["name"], g, no_gate[g].get(c["name"]))
        if c["name"] == "healthy_sweepers":
            # 충격 뒤 창은 한쪽(오른쪽 스위퍼)으로 쏠려 있다 — 직선 게이트 없이 |조향| ≤ 0.20만
            # 거른 충격~판정 사이 샘플 중앙값은 기준에서 0.03 넘게 벗어나고, 게이트를 거친 중앙값은 안 벗어난다
            raw = [x["player"]["steering"] for x in snaps if ti < x["t"] <= r["t"]
                   and not x["player"]["in_pitlane"] and x["player"]["speed_kmh"] >= SPEED_MIN
                   and abs(x["player"]["steering"]) <= STEER_MAX]
            assert median(raw) - r["base"] >= SHIFT_MIN, (c["name"], median(raw), r["base"])
            ungated_post = median(raw)
            assert abs(r["shift"]) < SHIFT_MIN, (c["name"], r)
            assert no_gate["lat"].get(c["name"])["shift"] == r["shift"], "스위퍼는 요레이트 게이트만 거른다"
        for kind, g in (("slide", "lat"), ("bend", "yaw")):
            if not c.get(kind):
                continue
            ns = {n for n in range(len(snaps)) if odd_at(n) == kind}
            assert len(ns) == tick(c[kind][2]) - tick(c[kind][1]), (c["name"], kind, len(ns))
            for n in ns:
                # 게이트 정확히 하나만 어긴다 (경계에서 멀리) — 속도·|조향| 조건은 통과
                p = snaps[n]["player"]
                acc, lat = abs(p["yaw_rate"]) * p["speed_kmh"] / 3.6, abs(p["lat_vel"])
                assert snaps[n]["t"] > ti and p["speed_kmh"] >= SPEED_MIN \
                    and abs(p["steering"]) <= STEER_MAX and not p["in_pitlane"], (kind, n)
                if kind == "slide":
                    assert acc <= LAT_ACC_MAX * 2 / 3 and lat >= LAT_VEL_MAX * 1.25, (kind, n, acc, lat)
                else:
                    assert acc >= LAT_ACC_MAX * 1.5 and lat <= LAT_VEL_MAX * 0.9, (kind, n, acc, lat)
            # 이 셋만 세어도 판정이 바뀐다 (중앙값이 0.03 넘게 움직임) — 게이트 하나가 이 셋을 홀로 막는다
            plus = judge(snaps, count=lambda x, ns=ns: tick(x["t"]) in ns)[0][c["name"]]
            assert abs(plus["shift"]) >= SHIFT_MIN and abs(plus["shift"] - r["shift"]) >= SHIFT_MIN, \
                (c["name"], kind, plus)
            assert decide(plus) == gate_off[g] != decision, (c["name"], kind, plus, gate_off[g])
        if c["name"] == "healthy_sweepers":
            # 예전 감지라면 이 경우에서 콜 — 이 리플레이가 회귀를 잡는다는 근거
            assert c["name"] in old_call, "healthy_sweepers: 예전 EMA 감지가 콜하는 모양이 아님"
        prev_decision = None if c.get("flat") else decide(prev.get(c["name"]))
        if NAME in ("align_rebase", "align_crab"):
            # 직전 코드(기준 없는 충격이 스틴트를 끔 · 횡속도 1.0m/s 상한)는 여기서 침묵
            assert prev_decision is None, (c["name"], prev.get(c["name"]))
        if c.get("crab"):
            # 횡속도 상한이 실제로 쓰인다 — 게걸음 직선 샘플이 예전 상한(1.0m/s)을 넘는다
            lats = [abs(x["player"]["lat_vel"]) for x in snaps
                    if ti < x["t"] <= r["t"] and seg_at(x["t"]).kind == "straight"]
            assert min(lats) > PREV_LAT_VEL_MAX, (c["name"], min(lats))
        if decision is not None:
            events.append({
                "t": round(r["t"], 2),
                "type": "damage_report",
                "data": {},
                "message": SAY_SEVERE if decision == "severe" else SAY_MILD,
            })
        case = {"name": c["name"], "impact_t": ti, "call": decision is not None, "note": c["note"],
                "prev_call": prev_decision is not None,
                "call_without_yaw_gate": gate_off["yaw"] is not None,
                "call_without_lat_vel_gate": gate_off["lat"] is not None}
        if r.get("base") is not None:
            case.update(base=r4(r["base"]), post=r4(r["post"]), shift=r4(r["shift"]), judge_t=r["t"])
        if ungated_post is not None:
            case["ungated_post"] = r4(ungated_post)
        if c["name"] in old_call:
            case["old_call_t"], case["old_shift"] = old_call[c["name"]][0], round(old_call[c["name"]][1], 4)
        cases.append(case)

    sidecar = {
        "scenario": NAME,
        "note": "얼라인 콜 — 곧게 가는 고속 샘플(요레이트×속도 ≤ 1.5m/s², 횡속도 ≤ 3m/s)의 조향 중앙값을 "
                "첫 충격 전(101개, 최소 25) ↔ 충격 뒤(51개가 찼을 때) 비교, 0.03 경미 · 0.08 심각. "
                "스위퍼 조향은 섞이지 않고, 리어 토 게걸음은 샘플이다. 기준 부족이면 그 충격만, "
                "타이어 펑크면 콜 없음. call_without_yaw_gate/call_without_lat_vel_gate = 그 게이트 "
                "하나만 뺀 규칙의 판정 (생성기가 각 게이트가 판정을 바꾸는 경우를 확인)",
        "types": ["damage_report"],
        "keys": ["align_damage", "align_severe"],
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
            print(f"❌ {NAME}: 파일 없음 — 재생성 필요 (python3 tools/make_align_replay.py)")
            return 1
        with open(mine_side, "rb") as a, open(side, "rb") as b:
            same = read_gz(mine_gz) == read_gz(gz) and a.read() == b.read()
    if same:
        print(f"✅ {NAME}: 생성기 출력과 동일 (스냅샷 {len(snaps)}개)")
        return 0
    print(f"❌ {NAME}: 생성기 출력과 다름 — 재생성 필요 (python3 tools/make_align_replay.py)")
    return 1


if __name__ == "__main__":
    sys.exit(main())
