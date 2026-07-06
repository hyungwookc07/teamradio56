"""
트레이닝 모드 — 사후 분석만 (실시간 코너 코칭은 지연 문제로 하지 않는다).

  1) 랩 직후 피드백: 직전 랩/세션 베스트와의 섹터 델타만 짧게.
     레퍼런스 랩 시스템은 만들지 않는다 (복잡도 대비 가치 낮음).
  2) 세션 종료 디브리핑: 세션 전체를 LLM으로 종합 → 강점/약점/다음 포커스.
  3) 장기 추세: 트랙별로 세션 JSON을 축적, 같은 트랙 재방문 시 과거 대비 코멘트.
"""

from __future__ import annotations

import glob
import json
import logging
import os
import time
from typing import Optional

from events import Event, EventBus, EventType, Priority
from state import SessionState

log = logging.getLogger("training")

SECTOR_DELTA_MIN = 0.15    # 이 이상 차이날 때만 언급 (침묵 기본값)


class LapCoach:
    """랩 완료 직후 섹터 델타 피드백. 직전 랩/세션 베스트와만 비교한다."""

    def on_lap(self, state: SessionState, bus: EventBus) -> None:
        valid = [l for l in state.laps if l.valid and l.s1 > 0 and l.s2 > 0 and l.s3 > 0]
        if len(valid) < 3 or valid[-1] is not state.laps[-1]:
            return
        last, prev = valid[-1], valid[-2]

        deltas = [(i + 1, getattr(last, f"s{i+1}") - getattr(prev, f"s{i+1}"))
                  for i in range(3)]
        sector, delta = max(deltas, key=lambda d: abs(d[1]))
        if abs(delta) < SECTOR_DELTA_MIN:
            return   # 유의미한 변화 없음 → 침묵

        # 세션 베스트 섹터 대비도 참고 정보로
        best_s = min(getattr(l, f"s{sector}") for l in valid[:-1])
        is_personal_best = getattr(last, f"s{sector}") < best_s

        if delta < 0:
            msg = f"섹터{sector}에서 {abs(delta):.1f}초 좋아졌어"
            msg += ", 세션 베스트야. 그 감 유지해." if is_personal_best else ". 그 감 유지해."
        else:
            msg = f"섹터{sector}에서 {delta:.1f}초 새고 있어. 다음 랩에 다시 잡자."

        bus.push(Event(
            type=EventType.LAP_FEEDBACK, priority=Priority.NORMAL,
            message=msg, dedup_key=f"feedback_{last.lap_number}", ttl=30.0,
        ))


class TrackHistory:
    """
    트랙별 장기 추세. data/race_*.json에서 같은 트랙 과거 세션을 찾아
    세션 초반에 한 번만 과거 대비 코멘트를 만든다.
    """

    def __init__(self, data_dir: str):
        self.data_dir = data_dir
        self._done = False

    def reset(self) -> None:
        self._done = False

    def on_lap(self, state: SessionState, bus: EventBus) -> None:
        if self._done or not state.track:
            return
        valid = [l for l in state.laps if l.valid]
        if len(valid) < 4:
            return   # 페이스가 안정된 뒤에 한 번만
        self._done = True

        past = self._load_past_best(state.track)
        if past is None:
            return
        past_best, days_ago = past
        cur_best = min(l.lap_time for l in valid)
        diff = past_best - cur_best
        if abs(diff) < 0.3:
            return   # 큰 차이 없으면 침묵

        when = f"{days_ago}일 전" if days_ago < 21 else f"{days_ago // 7}주 전"
        if diff > 0:
            msg = (f"이 트랙 {when} 베스트가 {fmt(past_best)}였는데 "
                   f"오늘 벌써 {fmt(cur_best)}이야. 확실히 늘었네.")
        else:
            msg = (f"이 트랙 {when}엔 {fmt(past_best)}까지 갔었어. "
                   f"오늘은 {fmt(cur_best)} — 감 다시 찾아보자.")
        bus.push(Event(
            type=EventType.TRACK_TREND, priority=Priority.NORMAL,
            message=msg, dedup_key="track_trend", ttl=60.0,
        ))

    def _load_past_best(self, track: str) -> Optional[tuple[float, int]]:
        best: Optional[float] = None
        newest_mtime = 0.0
        try:
            for path in glob.glob(os.path.join(self.data_dir, "race_*.json")):
                with open(path, "r", encoding="utf-8") as f:
                    d = json.load(f)
                if d.get("track") != track:
                    continue
                times = [l["lap_time"] for l in d.get("laps", [])
                         if l.get("valid") and l.get("lap_time", 0) > 0]
                if not times:
                    continue
                b = min(times)
                if best is None or b < best:
                    best = b
                newest_mtime = max(newest_mtime, os.path.getmtime(path))
        except (OSError, json.JSONDecodeError, KeyError) as e:
            log.debug("과거 기록 로드 실패: %s", e)
            return None
        if best is None:
            return None
        days = max(int((time.time() - newest_mtime) / 86400), 1)
        return best, days


class Debriefer:
    """세션 종료 시 전체 데이터를 LLM으로 종합해 자연어 코칭 디브리핑."""

    def build_summary(self, state: SessionState) -> Optional[str]:
        valid = [l for l in state.laps if l.valid]
        if len(valid) < 5:
            return None
        times = [l.lap_time for l in valid]
        best = min(times)
        avg = sum(times) / len(times)
        # 전/후반 비교 (집중력 패턴)
        half = len(times) // 2
        early, late = times[:half], times[half:]
        lines = [
            "[세션 데이터 요약]",
            f"트랙 {state.track}, 총 {len(state.laps)}랩 (유효 {len(valid)}랩)",
            f"베스트 {fmt(best)}, 평균 {fmt(avg)}, 편차 {max(times)-min(times):.1f}초",
            f"전반 평균 {fmt(sum(early)/len(early))} / 후반 평균 {fmt(sum(late)/len(late))}",
            f"섹터 베스트 합(이론상 베스트): "
            f"{fmt(min(l.s1 for l in valid) + min(l.s2 for l in valid) + min(l.s3 for l in valid))}",
        ]
        if state.narrative:
            lines.append("[세션 중 있었던 일]")
            lines.extend(state.narrative[-8:])
        lines.append(
            "[지금 말할 주제] 세션 디브리핑. 데이터 낭독 금지. "
            "강점 1개, 약점/패턴 1개, 다음 세션 포커스 1개를 자연어 코칭으로 2~3문장.")
        return "\n".join(lines)

    def run(self, state: SessionState, llm, data_dir: str) -> Optional[str]:
        summary = self.build_summary(state)
        if summary is None:
            return None
        text = llm.generate(summary) if llm.available else None
        if not text:
            return None
        log.info("📋 [디브리핑] %s", text)
        try:
            os.makedirs(data_dir, exist_ok=True)
            stamp = time.strftime("%Y%m%d_%H%M%S")
            with open(os.path.join(data_dir, f"debrief_{stamp}.txt"),
                      "w", encoding="utf-8") as f:
                f.write(text + "\n")
        except OSError:
            pass
        return text


def fmt(sec: float) -> str:
    m, s = divmod(sec, 60)
    return f"{int(m)}:{s:04.1f}"
