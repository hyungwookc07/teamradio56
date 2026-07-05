"""
트래픽 분석기 — 5Hz 매 틱 호출 (유일하게 랩 완료를 기다리지 않는 분석기).

멀티클래스 LMU의 핵심: 뒤에서 접근하는 상위 클래스 차량을 도달 몇 초 전에
예고한다. gap_m은 lapDist 차이(결승선 넘어가는 경우 트랙 길이로 래핑 보정),
closing_rate는 gap의 시간 미분(EMA 평활)로 계산해 도달 시점을 예측한다.

사이드 콜(car left/right)은 5Hz 스코어링 데이터로는 신뢰도가 낮아 v1에서 제외.
"""

from __future__ import annotations

import logging
import time

from events import Event, EventBus, EventType, Priority
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("traffic")

GREEN_PHASES = (5, 6)          # 그린 플래그, FCY(서행 중에도 근접 콜은 유효)
FASTER_CLASS_MARGIN = 3.0      # 예상 랩타임이 이보다 빠르면 상위 클래스로 간주 (초)
MIN_CLOSING_MS = 2.0           # 이 이상 접근 중일 때만 콜 (m/s, 노이즈 컷)
REANNOUNCE_SEC = 60.0          # 같은 차 재예고 최소 간격


def wrap_gap(delta_m: float, track_len: float) -> float:
    """lapDist 차이를 [-L/2, L/2) 범위의 부호 있는 거리로 보정. +면 내 앞."""
    half = track_len / 2.0
    return (delta_m + half) % track_len - half


class TrafficAnalyzer:
    def __init__(self, cfg):
        self.warn_gap_sec = cfg.get("thresholds.traffic_warn_gap_sec", 4.0)
        self.proximity_m = cfg.get("thresholds.proximity_m", 50.0)
        self._gap_hist: dict[int, tuple[float, float]] = {}   # id → (t, gap_m)
        self._rate: dict[int, float] = {}                     # id → dgap/dt EMA (m/s)
        self._announced: dict[int, float] = {}                # id → 마지막 예고 시각
        self._close_announced: dict[int, float] = {}

    def on_tick(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        me = snap.player_scoring()
        if me is None or me["in_pits"] or me["in_garage"]:
            return
        if snap.session["game_phase"] not in GREEN_PHASES:
            return
        track_len = snap.session["track_len"]
        if track_len <= 0:
            return
        now = snap.t
        my_est = me["estimated_lap"] if me["estimated_lap"] > 0 else \
            (state.baseline_lap_time() or 0)

        for v in snap.vehicles:
            if v["is_player"] or v["in_pits"] or v["in_garage"] or v["finish_status"] != 0:
                continue
            gap_m = wrap_gap(v["lap_dist"] - me["lap_dist"], track_len)
            rate = self._update_rate(v["id"], now, gap_m, track_len)

            self._check_proximity(v, gap_m, now, bus)
            if rate is not None:
                self._check_approach(v, gap_m, rate, my_est, now, bus)

    # ------------------------------------------------------------------

    def _update_rate(self, cid: int, now: float, gap_m: float,
                     track_len: float) -> float | None:
        prev = self._gap_hist.get(cid)
        self._gap_hist[cid] = (now, gap_m)
        if prev is None:
            return None
        dt = now - prev[0]
        if dt <= 0.01 or dt > 3.0:      # 너무 촘촘하거나 오래된 샘플은 버림
            return None
        dgap = wrap_gap(gap_m - prev[1], track_len)
        inst = dgap / dt
        ema = self._rate.get(cid)
        ema = inst if ema is None else (0.6 * ema + 0.4 * inst)
        self._rate[cid] = ema
        return ema

    def _check_approach(self, v: dict, gap_m: float, rate: float,
                        my_est: float, now: float, bus: EventBus) -> None:
        """뒤에서 접근하는 상위 클래스 차량 예고."""
        if gap_m >= 0:                      # 내 앞에 있음
            return
        if rate < MIN_CLOSING_MS:           # 뒤차 gap_m<0이 0으로 커져야 접근 (rate>0)
            return
        faster_class = (v["estimated_lap"] > 0 and my_est > 0
                        and v["estimated_lap"] < my_est - FASTER_CLASS_MARGIN)
        if not faster_class:
            return
        eta = -gap_m / rate
        if not (0 < eta <= self.warn_gap_sec):
            return
        last = self._announced.get(v["id"])
        if last is not None and now - last < REANNOUNCE_SEC:
            return
        self._announced[v["id"]] = now
        bus.push(Event(
            type=EventType.TRAFFIC_APPROACH,
            priority=Priority.CRITICAL,
            data={
                "cls": v["cls"],
                "driver": v["driver"],
                "gap_sec": max(round(eta), 1),
            },
            dedup_key=f"appr_{v['id']}",
            ttl=5.0,
        ))
        log.debug("접근 예고: %s(%s) eta %.1fs", v["driver"], v["cls"], eta)

    def _check_proximity(self, v: dict, gap_m: float, now: float,
                         bus: EventBus) -> None:
        """앞뒤 50m 이내 근접 경고 (클래스 무관)."""
        if abs(gap_m) > self.proximity_m:
            self._close_announced.pop(v["id"], None)   # 멀어지면 재경고 허용
            return
        last = self._close_announced.get(v["id"])
        if last is not None and now - last < REANNOUNCE_SEC:
            return
        self._close_announced[v["id"]] = now
        bus.push(Event(
            type=EventType.TRAFFIC_CLOSE,
            priority=Priority.CRITICAL,
            data={
                "direction": "ahead" if gap_m > 0 else "behind",
                "cls": v["cls"],
                "driver": v["driver"],
                "gap_m": round(abs(gap_m)),
            },
            dedup_key=f"close_{v['id']}",
            ttl=4.0,
        ))
