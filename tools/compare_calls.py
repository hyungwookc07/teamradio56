"""
리플레이 회귀 비교 — 파이썬(tools/replay_calls.py)과 C#(tests/TeamRadio56.Replay)
출력을 대조한다. t는 부동소수 라운딩 차이를 감안해 ±0.011초 허용,
나머지 필드(type/prio/tone/key/message/data)는 완전 일치를 요구한다.

사용법: python tools/compare_calls.py python_calls.jsonl csharp_calls.jsonl
종료 코드: 0 = 일치, 1 = 불일치.
"""

from __future__ import annotations

import json
import sys

T_TOLERANCE = 0.011
MAX_SHOWN = 12


def load(path: str) -> list[dict]:
    out = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                out.append(json.loads(line))
    return out


def describe(rec: dict) -> str:
    return (f"t={rec.get('t')} {rec.get('type')}"
            f"[{rec.get('key')}] tone={rec.get('tone')} data={rec.get('data')}")


def main() -> int:
    if len(sys.argv) != 3:
        print("사용법: compare_calls.py <python.jsonl> <csharp.jsonl>")
        return 2
    a, b = load(sys.argv[1]), load(sys.argv[2])
    problems = []

    for i in range(min(len(a), len(b))):
        ra, rb = a[i], b[i]
        diffs = []
        if abs(float(ra["t"]) - float(rb["t"])) > T_TOLERANCE:
            diffs.append(f"t {ra['t']} != {rb['t']}")
        for k in ("type", "prio", "tone", "key", "message"):
            if ra.get(k) != rb.get(k):
                diffs.append(f"{k} {ra.get(k)!r} != {rb.get(k)!r}")
        if ra.get("data") != rb.get("data"):
            diffs.append(f"data {ra.get('data')} != {rb.get('data')}")
        if diffs:
            problems.append(f"#{i}: " + "; ".join(diffs))

    if len(a) != len(b):
        problems.append(f"이벤트 수 불일치: 파이썬 {len(a)} vs C# {len(b)}")
        longer, name = (a, "파이썬") if len(a) > len(b) else (b, "C#")
        for j in range(min(len(a), len(b)), min(len(longer), min(len(a), len(b)) + 5)):
            problems.append(f"  {name}에만 있음: " + describe(longer[j]))

    if problems:
        print(f"❌ 불일치 {len(problems)}건 (이벤트 파이썬 {len(a)} / C# {len(b)}):")
        for p in problems[:MAX_SHOWN]:
            print(" ", p)
        if len(problems) > MAX_SHOWN:
            print(f"  ... 외 {len(problems) - MAX_SHOWN}건")
        return 1
    print(f"✅ 일치 — 수락 이벤트 {len(a)}개가 순서·타이밍·데이터까지 동일")
    return 0


if __name__ == "__main__":
    sys.exit(main())
