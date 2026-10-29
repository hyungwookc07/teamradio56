"""
세션 시작 브리핑 문장 조립 — main.py와 리플레이 회귀(tools/replay_calls.py)가
같은 함수를 쓴다. C# SessionBriefer.Compose 미러 (리플레이 en/ko diff로 대조).
"""

from __future__ import annotations

from typing import Optional

from messages import brief_length_text, msg


def compose_session_briefing(snap, state, start_spotter_sec) -> Optional[str]:
    """
    세션 종류/길이/그리드/날씨/연료를 한 번에 브리핑하는 문장.
    플레이어 차량이 아직 안 잡혔으면 None (호출자가 다음 틱에 다시 시도).
    앱을 세션 중간에 켜도 현재 상황 기준으로 브리핑한다.
    """
    me = snap.player_scoring()
    if me is None:
        return None

    ses = snap.session
    stype = ses.get("session_type", 0)
    is_race = stype >= 10
    kind = (msg("kind_race") if is_race else msg("kind_warmup") if stype == 9
            else msg("kind_quali") if stype >= 5 else msg("kind_practice"))
    track = (ses.get("track") or "").strip()
    parts = [msg("brief_track", track=track, kind=kind) if track
             else msg("brief_radio", kind=kind)]

    # 세션 길이 — 시간제(잔여 기준)와 랩제를 구분. 미기입 거대값은 무시.
    end_et = ses.get("end_et", 0.0) or 0.0
    cur_et = ses.get("current_et", 0.0) or 0.0
    max_laps = ses.get("max_laps", 0) or 0
    if is_race:
        # 그리드/포메이션 대기(롤링 스타트 전 ~40초)는 '시작'이다.
        # 세션 시계는 대기 중에도 흐르므로 경과 시간이 아니라 페이즈로
        # 판정한다 — 그린(5) 전이면 스타트 브리핑, 이후 합류면 중간 합류.
        mid_join = (ses.get("game_phase", 0) or 0) >= 5 \
            or me.get("total_laps", 0) > 0
    else:
        mid_join = cur_et > 120 or me.get("total_laps", 0) > 0
    if 0 < end_et < 86400:
        minutes = max(int(round((end_et - cur_et) / 60)), 1)
        parts.append(msg("brief_len_remaining" if mid_join else "brief_len_long",
                         length=brief_length_text(minutes)))
    elif 0 < max_laps < 10000:
        parts.append(msg("brief_lap") if max_laps == 1
                     else msg("brief_laps", n=max_laps))

    if is_race:
        cls_count = sum(1 for v in snap.vehicles if v["cls"] == me["cls"])
        cp = state.class_place_of(snap, me)
        if cls_count > 1:
            parts.append(msg("brief_grid_mid" if mid_join else "brief_grid",
                             cp=cp, n=cls_count))

    if ses.get("track_temp", 0.0) > 0:
        parts.append(msg("brief_temp", t=ses["track_temp"]))
    if ses.get("raining", 0.0) >= 0.05:
        parts.append(msg("brief_rain"))

    fuel = snap.player.get("fuel")
    if fuel:
        parts.append(msg("brief_fuel", f=fuel))

    # 스타트 전용: 포메이션/그린 콜 안내 + 스타트 스포터 모드 예고 —
    # 그리드 대기 ~40초를 채우는 실전 브리핑
    if is_race and not mid_join:
        parts.append(msg("brief_formation"))
        if start_spotter_sec > 0:
            parts.append(msg("brief_start_spotter"))

    parts.append(msg("brief_carry_on") if mid_join
                 else msg("brief_calm") if is_race
                 else msg("brief_out"))
    return " ".join(parts)
