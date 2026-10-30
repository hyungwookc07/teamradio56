"""
세션 시작 브리핑 문장 조립 — main.py와 리플레이 회귀(tools/replay_calls.py)가
같은 함수를 쓴다. C# SessionBriefer.Compose 미러 (리플레이 en/ko diff로 대조).
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import Optional

from messages import brief_length_text, is_ko, msg
from speech_domain import (BRIEF_LAPS_MAX, BRIEF_MINUTES_MAX, brief_fuel_ok,
                           brief_grid_ok, brief_temp_ok, known_track_en)

log = logging.getLogger("briefing")


@dataclass
class BriefingInputs:
    """브리핑 문장의 재료 — 스냅샷에서 뽑은 값만 (C# SessionBriefer.BriefingInputs)."""
    session_type: int = 0
    track: str = ""
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


def briefing_text(b: BriefingInputs) -> str:
    """
    세션 종류/길이/그리드/날씨/연료 브리핑 — 순수 조립 (C# SessionBriefer.ComposeText).
    도메인 밖 숫자(100랩 넘는 세션, 그리드/순위/노면/연료 범위 밖)는 그 문장만 뺀다.
    en은 트랙 이름 목록에 없는 트랙이면 '{track}.' 대신 무전 체크로 연다 (팩에 없는 이름).
    """
    stype = b.session_type
    is_race = stype >= 10
    kind = (msg("kind_race") if is_race else msg("kind_warmup") if stype == 9
            else msg("kind_quali") if stype >= 5 else msg("kind_practice"))
    track = (b.track or "").strip()
    if track and (is_ko() or known_track_en(track)):
        parts = [msg("brief_track", track=track, kind=kind)]
    else:
        parts = [msg("brief_radio", kind=kind)]

    # 세션 길이 — 시간제(잔여 기준)와 랩제를 구분. 미기입 거대값은 무시.
    end_et, cur_et, max_laps = b.end_et, b.cur_et, b.max_laps
    mid_join = b.mid_join
    if 0 < end_et < 86400:
        minutes = max(int(round((end_et - cur_et) / 60)), 1)
        if minutes <= BRIEF_MINUTES_MAX:
            parts.append(msg("brief_len_remaining" if mid_join else "brief_len_long",
                             length=brief_length_text(minutes)))
    elif 0 < max_laps <= BRIEF_LAPS_MAX:
        parts.append(msg("brief_lap") if max_laps == 1
                     else msg("brief_laps", n=max_laps))

    if is_race and b.cls_count > 1 and brief_grid_ok(b.cls_count, b.class_place):
        parts.append(msg("brief_grid_mid" if mid_join else "brief_grid",
                         cp=b.class_place, n=b.cls_count))

    if b.track_temp > 0 and brief_temp_ok(b.track_temp):
        parts.append(msg("brief_temp", t=b.track_temp))
    if b.raining >= 0.05:
        parts.append(msg("brief_rain"))

    if b.fuel and brief_fuel_ok(b.fuel):
        parts.append(msg("brief_fuel", f=b.fuel))

    # 스타트 전용: 포메이션/그린 콜 안내 + 스타트 스포터 모드 예고 —
    # 그리드 대기 ~40초를 채우는 실전 브리핑
    if is_race and not mid_join:
        parts.append(msg("brief_formation"))
        if b.start_spotter_sec > 0:
            parts.append(msg("brief_start_spotter"))

    parts.append(msg("brief_carry_on") if mid_join
                 else msg("brief_calm") if is_race
                 else msg("brief_out"))
    return " ".join(parts)


_unknown_logged: set = set()


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
    track = (ses.get("track") or "").strip()
    if track and not is_ko() and not known_track_en(track) \
            and track not in _unknown_logged:
        _unknown_logged.add(track)
        log.info("트랙 이름 목록에 없어 브리핑에서 생략: %s "
                 "(voice_lines/track_names_en.txt에 추가)", track)

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
        session_type=stype, track=track,
        end_et=ses.get("end_et", 0.0) or 0.0, cur_et=cur_et,
        max_laps=ses.get("max_laps", 0) or 0, mid_join=mid_join,
        cls_count=cls_count, class_place=cp,
        track_temp=ses.get("track_temp", 0.0), raining=ses.get("raining", 0.0),
        fuel=snap.player.get("fuel") or 0.0, start_spotter_sec=start_spotter_sec))
