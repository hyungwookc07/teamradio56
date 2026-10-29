"""
고정소수점 표기 대조 — C# Messages.PyFixed가 파이썬 f"{v:.Nf}"와 같은지.

반올림 경계값(정확히 한가운데인 이진값, 0.15처럼 한가운데처럼 보이지만
아닌 값), 음수/음의 0, 큰 값·작은 값, 무작위 값을 "자릿수\\t값" 줄로 쓰고
기대 출력을 따로 쓴다. replay-check.sh가 러너 --fixed-check 출력과 diff.

사용법: python tools/fixed_check.py --in values.tsv --expect expected.txt
"""

from __future__ import annotations

import argparse
import random


def values() -> list[float]:
    vals: set[float] = set()
    for k in range(-400, 401):
        vals.add(k / 2)          # .0f 경계 (정확한 x.5)
        vals.add(k / 4)          # .1f 경계 (정확한 x.25 / x.75)
        vals.add(k / 20)         # x.x5 — 대부분 이진으로 정확하지 않음
        vals.add(k / 10)
        vals.add(k / 100)
    for k in range(0, 1300):
        vals.add(k / 10 + 0.05)  # 갭/연료/페이스 영역의 x.x5
    rng = random.Random(56)
    for _ in range(3000):
        vals.add(rng.uniform(-200, 200))
        vals.add(rng.uniform(0, 10))
    vals.update([0.0, -0.0, 1e-9, -1e-9, 5e-324, 1e15 + 0.5, 123456789.25,
                 2.675, 1.005, 0.045, 59.95, 59.96, 119.96, 0.0499999999999999])
    return sorted(vals, key=lambda v: (v, str(v)))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--in", dest="inp", required=True)
    parser.add_argument("--expect", required=True)
    args = parser.parse_args()
    with open(args.inp, "w", encoding="utf-8") as fi, \
            open(args.expect, "w", encoding="utf-8") as fe:
        n = 0
        for v in values():
            for digits in (0, 1):
                fi.write(f"{digits}\t{v!r}\n")
                fe.write(f"{v:.{digits}f}\n")
                n += 1
    print(f"고정소수점 대조 값 {n}개")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
