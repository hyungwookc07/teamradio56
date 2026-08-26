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


def _cache_name_line(tone: str, text: str) -> str:
    """C# VoiceCache.CandidateFileNames와 같은 순서·기본값으로 파일명 나열."""
    from tts import EdgeTTSEngine, _cache_path

    kokoro = os.path.basename(
        _cache_path("", f"kokoro|bm_george|{tone}|{text}", ext="wav"))
    extra, pitch = EdgeTTSEngine.TONE_DELIVERY.get(
        tone, EdgeTTSEngine.TONE_DELIVERY["casual"])
    rate = f"{10 + extra:+d}%"
    edge = os.path.basename(
        _cache_path("", f"edge|en-GB-RyanNeural|{rate}|{pitch}|{text}"))
    return f"{tone}\t{kokoro}\t{edge}\t{text}"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=None)
    parser.add_argument("--lang", default="en", choices=("en", "ko"))
    parser.add_argument("--cache-names", action="store_true",
                        help="캐시 파일명 규약 대조용 덤프 (C# --dump-cache-names와 diff)")
    args = parser.parse_args()

    # C# 대조(replay-check)는 en 고정 — ko는 수동 확인용
    set_language(args.lang)
    pool = PhrasePool(lines_file(args.lang))
    pairs = set(iter_pregen_texts(pool))
    if args.cache_names:
        items = sorted(_cache_name_line(tone, text) for tone, text in pairs)
    else:
        items = sorted(f"{tone}\t{text}" for tone, text in pairs)

    out = open(args.out, "w", encoding="utf-8") if args.out else sys.stdout
    for line in items:
        out.write(line + "\n")
    if args.out:
        out.close()
        print(f"사전 캐시 텍스트 {len(items)}개 → {args.out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
