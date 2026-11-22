"""
기대 콜 대조 — 합성 리플레이의 기대값 사이드카(<이름>.expect.json)와 수락 이벤트를 맞춘다.
파이썬 ↔ C# 일치(compare_calls.py)만으로는 두 엔진이 같이 틀리면 통과하므로, 생성기가 규칙대로
적어 둔 정답과 직접 대조한다.

  사이드카: {"types": [이벤트 종류...], "events": [{"t", "type", "data", "say"?, "message"?}, ...]}
  calls 파일마다 types에 든 종류의 수락 이벤트만 골라 events와 순서·개수까지 같아야 한다
  (사이드카에 "keys"가 있으면 그 dedup 키의 이벤트만 — 같은 종류의 다른 콜은 범위 밖, 예: 얼라인)
  (t ±0.011초, data 완전 일치, say·message가 있으면 문장도 일치 — 세션 브리핑은 조립 문장
  message). 기대 목록에 없는 콜 하나도 실패.

사용법: python tools/expect_calls.py --expect f.expect.json --calls py.jsonl [--calls cs.jsonl ...]
종료 코드: 0 = 전부 일치, 1 = 불일치.
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
MAX_SHOWN = 10


def load_jsonl(path):
    out = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                out.append(json.loads(line))
    return out


def brief(rec):
    return f"t={rec.get('t')} {rec.get('type')} {json.dumps(rec.get('data'), ensure_ascii=False, sort_keys=True)}" \
        + (f" «{rec['say']}»" if rec.get("say") else "")


def check(calls, expect):
    types = set(expect["types"])
    keys = set(expect["keys"]) if "keys" in expect else None
    got = [c for c in calls if c.get("type") in types
           and (keys is None or c.get("key") in keys)]
    want = expect["events"]
    probs = []
    for i in range(max(len(got), len(want))):
        g = got[i] if i < len(got) else None
        w = want[i] if i < len(want) else None
        if g is None:
            probs.append(f"#{i} 빠짐: {brief(w)}")
            continue
        if w is None:
            probs.append(f"#{i} 기대에 없는 콜: {brief(g)}")
            continue
        d = []
        if abs(float(g["t"]) - float(w["t"])) > T_TOL:
            d.append(f"t {g['t']} != {w['t']}")
        if g.get("type") != w["type"]:
            d.append(f"type {g.get('type')} != {w['type']}")
        if g.get("data") != w["data"]:
            d.append(f"data {json.dumps(g.get('data'), ensure_ascii=False, sort_keys=True)} != "
                     f"{json.dumps(w['data'], ensure_ascii=False, sort_keys=True)}")
        if "say" in w and g.get("say") != w["say"]:
            d.append(f"say «{g.get('say')}» != «{w['say']}»")
        if "message" in w and g.get("message") != w["message"]:
            d.append(f"message «{g.get('message')}» != «{w['message']}»")
        if d:
            probs.append(f"#{i}: " + "; ".join(d))
    return got, probs


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--expect", required=True)
    ap.add_argument("--calls", action="append", required=True)
    args = ap.parse_args()
    with open(args.expect, "r", encoding="utf-8") as f:
        expect = json.load(f)
    ok = True
    for path in args.calls:
        got, probs = check(load_jsonl(path), expect)
        label = os.path.basename(path)
        kinds = "/".join(expect["types"]) + (
            f" [{'/'.join(expect['keys'])}]" if "keys" in expect else "")
        if probs:
            ok = False
            print(f"❌ {label}: {kinds} 콜이 기대와 다름 (수락 {len(got)} / 기대 {len(expect['events'])}):")
            for p in probs[:MAX_SHOWN]:
                print(f"   {p}")
            if len(probs) > MAX_SHOWN:
                print(f"   … 외 {len(probs) - MAX_SHOWN}건")
        else:
            print(f"✅ {label}: {kinds} 콜 {len(got)}개 = 기대 (순서·시각·데이터·문장)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
