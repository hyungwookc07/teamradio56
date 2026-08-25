"""
실차 데이터 진단 — LMU가 공유 메모리 필드를 실제로 어떻게 채우는지 확인.

게임 세션에 들어간 상태에서 실행하면 2초마다 원시 값을 표로 출력한다.
순위/갭/섹터 플래그가 게임 화면과 다를 때 이 출력을 보고 원인을 특정한다.

사용법:
    python tools/diagnose.py                    # 공유 메모리 (게임 필요)
    python tools/diagnose.py --replay 파일.jsonl
    python tools/diagnose.py --seconds 30       # 30초간 출력 후 종료
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
import os
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from telemetry import SharedMemoryTelemetry, ReplayTelemetry  # noqa: E402

# 차량별 첫 관측 기준 (실존 판별: 좌표/진행도가 이후 얼마나 움직였나)
_first: dict[int, tuple] = {}

CONTROL_NAMES = {255: "없음", 0: "로컬", 1: "AI", 2: "원격", 3: "리플레이"}


def _movement(v) -> tuple[float, float]:
    """첫 관측 대비 (좌표 이동 m, lapDist 이동 m)."""
    pos = v.get("pos") or [0.0, 0.0, 0.0]
    base = _first.setdefault(v["id"], (list(pos), v["lap_dist"]))
    d_pos = ((pos[0] - base[0][0]) ** 2 + (pos[1] - base[0][1]) ** 2
             + (pos[2] - base[0][2]) ** 2) ** 0.5
    return d_pos, abs(v["lap_dist"] - base[1])


def dump(snap) -> None:
    ses = snap.session
    print("=" * 100)
    print(f"phase={ses['game_phase']} yellow={ses['yellow_state']} "
          f"sector_flags={ses.get('sector_flags')} "
          f"track_len={ses['track_len']} pit_limit={ses.get('pit_speed_limit')} "
          f"num_vehicles={ses['num_vehicles']} session_type={ses['session_type']} "
          f"ET={ses['current_et']:.0f}/{ses['end_et']:.0f}")
    p = snap.player
    if p:
        print(f"내 텔레메트리: fuel={p.get('fuel')}L cap={p.get('fuel_capacity')} "
              f"speed={p.get('speed_kmh')}km/h in_pitlane={p.get('in_pitlane')} "
              f"limiter={p.get('speed_limiter')} "
              f"wear={[w['wear'] for w in (p.get('wheels') or [])]}")
    print(f"{'순위':>4} {'드라이버':<16} {'클래스':<12} {'랩':>3} {'lapDist':>8} "
          f"{'tbNext':>7} {'estLap':>7} {'pathLat':>7} {'핏':>2} {'차고':>2} {'완주':>2} "
          f"{'조종':<5} {'채점':>2} {'Δ좌표':>7} {'ΔlapD':>7}")
    ghosts = []
    for v in sorted(snap.vehicles, key=lambda x: x["place"])[:14]:
        me_mark = "→" if v["is_player"] else " "
        d_pos, d_lap = _movement(v)
        ctrl = CONTROL_NAMES.get(v.get("control", -1), "-")   # "-" = 미기록/미지값
        print(f"{me_mark}P{v['place']:<3} {v['driver'][:15]:<16} {v['cls'][:11]:<12} "
              f"{v['total_laps']:>3} {v['lap_dist']:>8.1f} "
              f"{v['time_behind_next']:>7.2f} {v['estimated_lap']:>7.1f} "
              f"{v.get('path_lat', 0):>7.2f} "
              f"{int(v['in_pits']):>2} {int(v['in_garage']):>2} {v['finish_status']:>2} "
              f"{ctrl:<5} {int(v.get('server_scored', 0)):>2} "
              f"{d_pos:>7.1f} {d_lap:>7.1f}")
        # 프라이빗 세션 판별의 핵심: 타이밍(lapDist)은 움직이는데 좌표가 고정
        if not v["is_player"] and d_lap > 50 and d_pos < 5:
            ghosts.append(v["driver"])
    if ghosts:
        print(f"⚠ 타이밍 전용(물리적 부재) 의심: {', '.join(ghosts)}"
              " — 프라이빗 세션의 '달리는 유령'. 트래픽 콜에서 자동 제외됩니다.")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--replay", metavar="PATH")
    parser.add_argument("--speed", type=float, default=1.0)
    parser.add_argument("--seconds", type=float, default=20.0, help="출력 시간")
    parser.add_argument("--interval", type=float, default=2.0)
    args = parser.parse_args()

    source = ReplayTelemetry(args.replay, args.speed) if args.replay \
        else SharedMemoryTelemetry()
    print(f"{args.seconds:.0f}초간 {args.interval:.0f}초 간격으로 원시 데이터를 출력합니다."
          " 이 출력을 게임 화면(순위표/갭/플래그)과 비교하세요.\n")
    deadline = time.monotonic() + args.seconds
    last_print = 0.0
    try:
        while time.monotonic() < deadline:
            snap = source.poll()
            now = time.monotonic()
            if snap is None or not snap.connected:
                if now - last_print >= 2.0:
                    print("게임 대기 중...")
                    last_print = now
            elif snap.in_session and now - last_print >= args.interval:
                dump(snap)
                last_print = now
            time.sleep(0.2)
    except KeyboardInterrupt:
        pass
    finally:
        source.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
