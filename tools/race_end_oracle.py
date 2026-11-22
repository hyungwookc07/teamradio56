"""
레이스 종료 오라클 — 마지막 랩·체커 판단을 정답(truth 사이드카)과 대조하고, 파이썬/C#
레이스 종료 트레이스(--race-end-trace)가 서로 같은지 본다.

  T1  트레이스 2개: 줄 수 같고, 줄마다 t(±0.011)·lap·laps_left·finished·chequered 일치
  O1  정답 라인 통과(lap ≥ trace_exact_from_lap)마다 트레이스의 laps_left가 정답과 같다
  O2  피니시 전에 finished=true인 트레이스 줄이 없다
  O3  calls 파일마다 '마지막 랩'(race_milestone + data.final_lap)이 final_calls와 정확히 같다
  O4  calls 파일마다 race_end가 피니시 시각에 딱 하나, 클래스 순위도 같다 — 정답에 등급
      (pool·start_place·gained)이 있으면 그것까지
  O5  체커 뒤 콜 — 피니시부터 다음 레이스 전까지 cooldown_lap·race_summary·post_race_pit
      (그리고 정답 post에 적힌 종류)이 post와 순서·시각·풀·still_racing/rank까지 같다
  O6  체커 뒤 침묵 — 같은 구간의 수락 콜은 전부 허용 목록 안 (체커 뒤 무전, 피트 리미터,
      섹터 옐로, 휠 탈락, 아직 달리는 차의 나란히/접근/종합/정지 차량 경고)
  정답에 "races"가 있으면(재시작 시나리오) 레이스마다 위를 적용한다 — O2는 t0 이후 구간,
  O3/O4는 레이스 순서대로 이어 붙인 목록과 대조.

사용법: python tools/race_end_oracle.py [--truth f.truth.json] [--calls py.jsonl --calls cs.jsonl]
                                       [--trace py_trace.jsonl --trace cs_trace.jsonl] [--report-only]
종료 코드: 0 = 통과(또는 --report-only), 1 = 실패.
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
import json
import os

T_TOL = 0.011
TRACE_KEYS = ("lap", "laps_left", "finished", "chequered")
MAX_SHOWN = 8
POST_TYPES = ("cooldown_lap", "race_summary", "post_race_pit")
FINISH_KEYS = ("pool", "start_place", "gained")
# 내 피니시 뒤에도 나갈 수 있는 콜 (O6) — traffic_close는 나란히 풀만, traffic_update는 정지 차량 경고만
AFTER_FINISH_OK = {"race_end", "cooldown_lap", "race_summary", "post_race_pit", "pit_limiter",
                   "sector_yellow", "wheel_damage", "traffic_approach", "traffic_multi"}


def load_jsonl(path):
    out = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                out.append(json.loads(line))
    return out


def near(a, b):
    return abs(float(a) - float(b)) <= T_TOL


def check_trace_parity(a, b):
    probs = []
    if len(a) != len(b):
        probs.append(f"트레이스 줄 수 불일치: 파이썬 {len(a)} vs C# {len(b)}")
    for i in range(min(len(a), len(b))):
        ra, rb = a[i], b[i]
        d = []
        if not near(ra["t"], rb["t"]):
            d.append(f"t {ra['t']} != {rb['t']}")
        for k in TRACE_KEYS:
            if ra.get(k) != rb.get(k):
                d.append(f"{k} {ra.get(k)!r} != {rb.get(k)!r}")
        if d:
            probs.append(f"#{i}: " + "; ".join(d))
    return probs


def races_of(truth):
    return truth.get("races") or [truth]


def check_trace_truth(trace, truth):
    probs = []
    for race in races_of(truth):
        probs += check_trace_race(trace, race)
    return probs


def check_trace_race(trace, truth):
    probs = []
    for c in truth["crossings"]:
        if c["lap"] < truth["trace_exact_from_lap"]:
            continue
        hit = [r for r in trace if near(r["t"], c["t"])]
        if not hit:
            probs.append(f"t={c['t']} 랩 {c['lap']}: 트레이스 줄 없음")
            continue
        r = hit[0]
        want_fin = c["laps_left"] == 0
        if r.get("laps_left") != c["laps_left"] or bool(r.get("finished")) != want_fin:
            probs.append(f"t={c['t']} 랩 {c['lap']}: 남은 랩 {r.get('laps_left')}"
                         f"(finished={r.get('finished')}) — 정답 {c['laps_left']}")
    fin_t = truth["finish"]["t"]
    t0 = truth.get("t0", float("-inf"))
    early = [r for r in trace if r.get("finished")
             and t0 - T_TOL <= float(r["t"]) < fin_t - T_TOL]
    if early:
        probs.append(f"피니시({fin_t}) 전에 finished: t={early[0]['t']}")
    return probs


def final_calls_of(calls):
    return [c for c in calls
            if c.get("type") == "race_milestone" and (c.get("data") or {}).get("final_lap")]


def check_calls(calls, truth):
    probs = []
    races = races_of(truth)
    got = final_calls_of(calls)
    want = [w for r in races for w in r["final_calls"]]
    ok = len(got) == len(want) and all(
        near(g["t"], w["t"]) and (g.get("data") or {}).get("lap") == w["lap"]
        for g, w in zip(got, want))
    if not ok:
        probs.append("마지막 랩 콜 "
                     + (", ".join(f"t={g['t']} 랩 {(g.get('data') or {}).get('lap')}" for g in got)
                        or "없음")
                     + " — 정답 "
                     + ", ".join(f"t={w['t']} 랩 {w['lap']}" for w in want))
    ends = [c for c in calls if c.get("type") == "race_end"]
    fins = [r["finish"] for r in races]

    def same_end(e, f):
        d = e.get("data") or {}
        return (near(e["t"], f["t"]) and d.get("class_place") == f["class_place"]
                and all(d.get(k) == f[k] for k in FINISH_KEYS if k in f))

    if not (len(ends) == len(fins) and all(same_end(e, f) for e, f in zip(ends, fins))):
        def desc(t, d):
            return f"t={t} P{d.get('class_place')} " + " ".join(
                f"{k}={d.get(k)}" for k in FINISH_KEYS if k in d)
        probs.append("체커 콜 "
                     + (", ".join(desc(e["t"], e.get("data") or {}) for e in ends) or "없음")
                     + " — 정답 " + ", ".join(desc(f["t"], f) for f in fins))
    return probs


def windows(truth):
    """레이스마다 (정답, 체커 뒤 구간 [피니시, 다음 레이스 t0))."""
    races = races_of(truth)
    out = []
    for i, r in enumerate(races):
        end = races[i + 1]["t0"] if i + 1 < len(races) else float("inf")
        out.append((r, float(r["finish"]["t"]) - T_TOL, end - T_TOL))
    return out


def check_post(calls, truth):
    """O5 — 체커 뒤 무전 순서·시각·풀."""
    probs = []
    for race, lo, hi in windows(truth):
        if "post" not in race:
            continue
        want = race["post"]
        types = set(POST_TYPES) | {w["type"] for w in want}
        got = [c for c in calls if c.get("type") in types and lo <= float(c["t"]) < hi]

        def same(g, w):
            d = g.get("data") or {}
            return (near(g["t"], w["t"]) and g.get("type") == w["type"]
                    and d.get("pool") == w["pool"]
                    and all(d.get(k) == w[k] for k in ("still_racing", "rank") if k in w))

        if not (len(got) == len(want) and all(same(g, w) for g, w in zip(got, want))):
            def desc(t, typ, d):
                extra = " ".join(f"{k}={d[k]}" for k in ("still_racing", "rank") if k in d)
                return f"t={t} {typ}/{d.get('pool')}" + (f" {extra}" if extra else "")
            probs.append("체커 뒤 콜 "
                         + (", ".join(desc(g["t"], g["type"], g.get("data") or {}) for g in got)
                            or "없음")
                         + " — 정답 " + (", ".join(desc(w["t"], w["type"], w) for w in want)
                                        or "없음"))
    return probs


def after_finish_ok(c):
    typ = c.get("type")
    if typ in AFTER_FINISH_OK:
        return True
    if typ == "traffic_close":
        return str((c.get("data") or {}).get("pool", "")).startswith("alongside")
    if typ == "traffic_update":
        return str(c.get("key", "")).startswith("hazard_")
    return False


def check_silence(calls, truth):
    """O6 — 내 피니시 뒤엔 허용 목록 밖의 콜이 하나도 없다."""
    probs = []
    for race, lo, hi in windows(truth):
        bad = [c for c in calls if lo <= float(c["t"]) < hi and not after_finish_ok(c)]
        for c in bad:
            probs.append(f"피니시({race['finish']['t']}) 뒤 콜 t={c['t']} {c.get('type')} "
                         f"{json.dumps(c.get('data'), ensure_ascii=False, sort_keys=True)}"
                         + (f" «{c['message']}»" if c.get("message") else ""))
    return probs


def report(title, probs):
    if probs:
        print(f"❌ {title}:")
        for p in probs[:MAX_SHOWN]:
            print(f"   {p}")
        if len(probs) > MAX_SHOWN:
            print(f"   … 외 {len(probs) - MAX_SHOWN}건")
        return False
    print(f"✅ {title}")
    return True


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--truth", default=None)
    ap.add_argument("--calls", action="append", default=[])
    ap.add_argument("--trace", action="append", default=[])
    ap.add_argument("--report-only", action="store_true",
                    help="실패를 보고만 하고 종료 코드는 0")
    args = ap.parse_args()
    if len(args.trace) not in (0, 2):
        print("--trace는 0개 또는 2개(파이썬, C#)")
        return 2

    ok = True
    traces = [load_jsonl(p) for p in args.trace]
    if traces:
        probs = check_trace_parity(traces[0], traces[1])
        ok &= report(f"트레이스 파이썬 ↔ C# 일치 ({len(traces[0])}줄)", probs)

    if args.truth:
        with open(args.truth, "r", encoding="utf-8") as f:
            truth = json.load(f)
        if traces:
            ok &= report("남은 랩·피니시 판단 = 정답", check_trace_truth(traces[0], truth))
        for path in args.calls:
            label = os.path.basename(path)
            calls = load_jsonl(path)
            ok &= report(f"마지막 랩·체커 콜 = 정답 ({label})", check_calls(calls, truth))
            if any("post" in r for r in races_of(truth)):
                ok &= report(f"체커 뒤 무전 = 정답 ({label})", check_post(calls, truth))
            ok &= report(f"체커 뒤 침묵 — 허용 콜만 ({label})", check_silence(calls, truth))

    if not traces and not args.truth:
        print("⏭  정답·트레이스 없음 — 대조할 것 없음")
    if not ok and args.report_only:
        print("⏭  보고 전용 — 실패를 게이트에 반영하지 않음")
        return 0
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
