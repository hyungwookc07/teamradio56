"""
세션 시작 브리핑 문장 조립 — main.py와 리플레이 회귀(tools/replay_calls.py)가
같은 함수를 쓴다. C# SessionBriefer.Compose 미러 (리플레이가 문장 단위로 대조).

실제 레이스 엔지니어처럼 무전 체크로 열고 (트랙 이름은 말하지 않는다), 세션 종류마다
다른 내용을 말한다:
  연습   길이 · 노면/비 · 빌드업 런 계획 · "준비되면 나가"
  퀄리   길이 · 노면/비 · 아웃랩 공간 확보 · 트랙 리미트 · "한 랩에 모든 걸" (연료 없음)
  웜업   길이 · 노면/비 · 차 점검 · "준비되면 나가"
  레이스 길이 · 클래스 대수/스타트 순위 · 노면/비 · 연료 · 포메이션 · 스포터 예고 · 첫 랩
중간 합류는 남은 시간과 "이대로 가자"로 짧게. 문장 슬롯마다 변형 2~5개 중 하나를
스냅샷 값 시드의 FNV-1a 해시로 고른다 (messages.variant) — 파이썬과 C#이 같은 문장을 고른다.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

from messages import brief_length_text, msg, variant
from speech_domain import (BRIEF_LAPS_MAX, BRIEF_MINUTES_MAX, brief_fuel_ok,
                           brief_grid_ok, brief_temp_ok)


@dataclass
class BriefingInputs:
    """브리핑 문장의 재료 — 스냅샷에서 뽑은 값만 (C# SessionBriefer.BriefingInputs)."""
    session_type: int = 0
    end_et: float = 0.0
    cur_et: float = 0.0
    max_laps: int = 0
    mid_join: bool = False
    cls_count: int = 0
    class_place: int = 0
    track_temp: float = 0.0
    raining: float = 0.0
    fuel: float = 0.0
    start_spotter_sec: float = 0.0


def session_kind(session_type: int) -> str:
    """rF2 mSession → practice(0~4) | quali(5~8) | warmup(9) | race(10~)."""
    if session_type >= 10:
        return "race"
    if session_type == 9:
        return "warmup"
    if session_type >= 5:
        return "quali"
    return "practice"


def _fx(x: float, d: int) -> str:
    return f"{x:.{d}f}"


def briefing_seed(b: BriefingInputs) -> str:
    """변형 선택 시드 — 세션마다 다른 값(브리핑 시점 세션 시계·노면 온도·연료)을 넣어
    같은 트랙·같은 설정이라도 세션마다 다른 문장이 나오게 한다. 표기는 바이트 단위로
    C# SessionBriefer.Seed와 같다 (소수는 파이썬 f-string = Messages.PyFixed)."""
    return "|".join((
        str(int(b.session_type)), "1" if b.mid_join else "0",
        _fx(b.cur_et, 1), _fx(b.end_et, 0), str(int(b.max_laps)),
        _fx(b.track_temp, 1), _fx(b.fuel, 1),
        str(int(b.cls_count)), str(int(b.class_place)),
    ))


def briefing_parts(b: BriefingInputs) -> list[tuple[str, str]]:
    """
    (문장 그룹 키, 문장) 목록 — 순수 조립 (C# SessionBriefer.ComposeParts).
    도메인 밖 숫자(100랩 넘는 세션, 그리드/순위/노면/연료 범위 밖)는 그 문장만 뺀다.
    """
    seed = briefing_seed(b)
    parts: list[tuple[str, str]] = []

    def v(key: str, **kw) -> None:
        parts.append((key, variant(key, seed, **kw)))

    kind = session_kind(b.session_type)
    is_race = kind == "race"
    mid = b.mid_join
    v(f"brief_open_{kind}_mid" if mid else f"brief_open_{kind}")

    # 세션 길이 — 시간제(잔여 기준)와 랩제를 구분. 미기입 거대값은 무시.
    end_et, cur_et, max_laps = b.end_et, b.cur_et, b.max_laps
    if 0 < end_et < 86400:
        minutes = max(int(round((end_et - cur_et) / 60)), 1)
        if minutes <= BRIEF_MINUTES_MAX:
            v("brief_len_remaining" if mid else "brief_len_long",
              length=brief_length_text(minutes))
    elif 0 < max_laps <= BRIEF_LAPS_MAX:
        parts.append(("brief_laps", msg("brief_lap") if max_laps == 1
                      else msg("brief_laps", n=max_laps)))

    if is_race and b.cls_count > 1 and brief_grid_ok(b.cls_count, b.class_place):
        parts.append(("brief_cars", msg("brief_cars", n=b.cls_count)))
        v("brief_grid_mid" if mid else "brief_grid", cp=b.class_place)

    if b.track_temp > 0 and brief_temp_ok(b.track_temp):
        v("brief_temp", t=b.track_temp)
    if b.raining >= 0.05:
        v("brief_rain")

    # 연료는 레이스만 — 연습/퀄리/웜업은 런 계획마다 바뀌어 의미가 없다
    if is_race and b.fuel and brief_fuel_ok(b.fuel):
        v("brief_fuel", f=b.fuel)

    if mid:
        if kind == "quali":
            v("brief_limits_quali")
        v("brief_close_mid")
    elif kind == "practice":
        v("brief_plan_practice")
        v("brief_close_out")
    elif kind == "quali":
        v("brief_gap_quali")
        v("brief_limits_quali")
        v("brief_close_quali")
    elif kind == "warmup":
        v("brief_plan_warmup")
        v("brief_close_out")
    else:
        # 스타트 전용: 포메이션/그린 콜 안내 + 스타트 스포터 모드 예고 —
        # 그리드 대기 ~40초를 채우는 실전 브리핑
        v("brief_formation")
        if b.start_spotter_sec > 0:
            v("brief_start_spotter")
        v("brief_close_race")
    return parts


def briefing_text(b: BriefingInputs) -> str:
    """브리핑 한 덩어리 (C# SessionBriefer.ComposeText)."""
    return " ".join(text for _, text in briefing_parts(b))


def compose_session_briefing(snap, state, start_spotter_sec) -> Optional[str]:
    """
    세션 시작 브리핑 문장 — 스냅샷에서 재료를 뽑아 briefing_text로.
    플레이어 차량이 아직 안 잡혔으면 None (호출자가 다음 틱에 다시 시도).
    앱을 세션 중간에 켜도 현재 상황 기준으로 브리핑한다.
    """
    me = snap.player_scoring()
    if me is None:
        return None

    ses = snap.session
    stype = ses.get("session_type", 0)
    is_race = stype >= 10
    cur_et = ses.get("current_et", 0.0) or 0.0
    if is_race:
        # 그리드/포메이션 대기(롤링 스타트 전 ~40초)는 '시작'이다.
        # 세션 시계는 대기 중에도 흐르므로 경과 시간이 아니라 페이즈로
        # 판정한다 — 그린(5) 전이면 스타트 브리핑, 이후 합류면 중간 합류.
        mid_join = (ses.get("game_phase", 0) or 0) >= 5 \
            or me.get("total_laps", 0) > 0
    else:
        mid_join = cur_et > 120 or me.get("total_laps", 0) > 0

    cls_count = cp = 0
    if is_race:
        cls_count = sum(1 for v in snap.vehicles if v["cls"] == me["cls"])
        cp = state.class_place_of(snap, me)

    return briefing_text(BriefingInputs(
        session_type=stype,
        end_et=ses.get("end_et", 0.0) or 0.0, cur_et=cur_et,
        max_laps=ses.get("max_laps", 0) or 0, mid_join=mid_join,
        cls_count=cls_count, class_place=cp,
        track_temp=ses.get("track_temp", 0.0), raining=ses.get("raining", 0.0),
        fuel=snap.player.get("fuel") or 0.0, start_spotter_sec=start_spotter_sec))
