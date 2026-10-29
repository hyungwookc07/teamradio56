"""
사전 캐시 대상 (톤, 텍스트) 전체를 "톤\t텍스트" 정렬·중복 제거로 출력.

C# 러너의 --dump-pregen 출력과 diff해 멘트 데이터/슬롯 포매팅 포팅을
검증한다 (simhub/verify/replay-check.sh가 사용).

--sentences는 문장 단위 사전 생성 대상(C# --dump-sentences와 diff),
--seg-names는 그 세그먼트 파일명(C# --dump-seg-names와 diff).

사용법: python tools/dump_pregen.py [--out f]
        python tools/dump_pregen.py --sentences|--seg-names [--tiers 1,2] [--tracks f]
"""

from __future__ import annotations

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from messages import set_language                            # noqa: E402
from voice import (PhrasePool, iter_pregen_sentences,         # noqa: E402
                   iter_pregen_texts, lines_file, load_track_names)

# C# VoiceCache.SegSuffix와 일치 — 세그먼트 렌더 규약이 바뀌면 함께 올린다
SEG_SUFFIX = "_seg1.wav"


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


def _seg_name_line(tone: str, sentence: str) -> str:
    """C# VoiceCache.SegmentFileName과 같은 규약 — kokoro 키 + 세그먼트 접미사."""
    from tts import _cache_path

    kokoro = os.path.basename(
        _cache_path("", f"kokoro|bm_george|{tone}|{sentence}", ext="wav"))
    return f"{tone}\t{kokoro[:-len('.wav')]}{SEG_SUFFIX}\t{sentence}"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=None)
    parser.add_argument("--lang", default="en", choices=("en", "ko"))
    parser.add_argument("--cache-names", action="store_true",
                        help="캐시 파일명 규약 대조용 덤프 (C# --dump-cache-names와 diff)")
    parser.add_argument("--sentences", action="store_true",
                        help="문장 단위 사전 생성 대상 (C# --dump-sentences와 diff)")
    parser.add_argument("--seg-names", action="store_true",
                        help="세그먼트 파일명 (C# --dump-seg-names와 diff)")
    parser.add_argument("--tiers", default="1,2",
                        help="문장 단계 목록 (예: 1 또는 1,2)")
    parser.add_argument("--tracks", default=None,
                        help="트랙 이름 목록 파일 (2단계 세션 브리핑용)")
    args = parser.parse_args()

    # C# 대조(replay-check)는 en 고정 — ko는 수동 확인용
    set_language(args.lang)
    if args.sentences or args.seg_names:
        tiers = tuple(int(t) for t in args.tiers.split(",") if t.strip())
        tracks = load_track_names(args.tracks) if args.tracks else ()
        pairs = set(iter_pregen_sentences(tiers, tracks))
        if args.seg_names:
            items = sorted(_seg_name_line(tone, s) for tone, s in pairs)
        else:
            items = sorted(f"{tone}\t{s}" for tone, s in pairs)
    else:
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
        what = "사전 생성 문장" if (args.sentences or args.seg_names) else "사전 캐시 텍스트"
        print(f"{what} {len(items)}개 → {args.out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
