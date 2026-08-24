"""
사전 캐시 대상 (톤, 텍스트) 전체를 "톤\t텍스트" 정렬·중복 제거로 출력.

C# 러너의 --dump-pregen 출력과 diff해 멘트 데이터/슬롯 포매팅 포팅을
검증한다 (simhub/verify/replay-check.sh가 사용).

사용법: python tools/dump_pregen.py [--out f]
"""

from __future__ import annotations

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from messages import set_language                            # noqa: E402
from voice import PhrasePool, iter_pregen_texts, lines_file  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=None)
    parser.add_argument("--lang", default="en", choices=("en", "ko"))
    args = parser.parse_args()

    # C# 대조(replay-check)는 en 고정 — ko는 수동 확인용
    set_language(args.lang)
    pool = PhrasePool(lines_file(args.lang))
    items = sorted(set(f"{tone}\t{text}" for tone, text in iter_pregen_texts(pool)))

    out = open(args.out, "w", encoding="utf-8") if args.out else sys.stdout
    for line in items:
        out.write(line + "\n")
    if args.out:
        out.close()
        print(f"사전 캐시 텍스트 {len(items)}개 → {args.out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
