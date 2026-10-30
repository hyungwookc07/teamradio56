"""
레이스 종료 오라클 — 마지막 랩·체커 판단을 정답(truth 사이드카)과 대조하고, 파이썬/C#
레이스 종료 트레이스(--race-end-trace)가 서로 같은지 본다.

  T1  트레이스 2개: 줄 수 같고, 줄마다 t(±0.011)·lap·laps_left·finished·chequered 일치
  O1  정답 라인 통과(lap ≥ trace_exact_from_lap)마다 트레이스의 laps_left가 정답과 같다
  O2  피니시 전에 finished=true인 트레이스 줄이 없다
  O3  calls 파일마다 '마지막 랩'(race_milestone + data.final_lap)이 final_calls와 정확히 같다
  O4  calls 파일마다 race_end가 피니시 시각에 딱 하나, 클래스 순위도 같다

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


def check_trace_truth(trace, truth):
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
    early = [r for r in trace if r.get("finished") and float(r["t"]) < fin_t - T_TOL]
    if early:
        probs.append(f"피니시({fin_t}) 전에 finished: t={early[0]['t']}")
    return probs


def final_calls_of(calls):
    return [c for c in calls
            if c.get("type") == "race_milestone" and (c.get("data") or {}).get("final_lap")]


def check_calls(calls, truth):
    probs = []
    got = final_calls_of(calls)
    want = truth["final_calls"]
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
    fin = truth["finish"]
    if not (len(ends) == 1 and near(ends[0]["t"], fin["t"])
            and (ends[0].get("data") or {}).get("class_place") == fin["class_place"]):
        probs.append("체커 콜 "
                     + (", ".join(f"t={e['t']} P{(e.get('data') or {}).get('class_place')}"
                                  for e in ends) or "없음")
                     + f" — 정답 t={fin['t']} P{fin['class_place']}")
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
            ok &= report(f"마지막 랩·체커 콜 = 정답 ({label})", check_calls(load_jsonl(path), truth))

    if not traces and not args.truth:
        print("⏭  정답·트레이스 없음 — 대조할 것 없음")
    if not ok and args.report_only:
        print("⏭  보고 전용 — 실패를 게이트에 반영하지 않음")
        return 0
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
