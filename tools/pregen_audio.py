"""
긴급 콜 오디오 사전 캐시 생성.

voice_lines/urgent_ko.yaml의 모든 변형 × 슬롯 값 조합을 TTS로 합성해
audio_cache에 저장한다. 런타임과 같은 캐시 키(md5)를 쓰므로 실전에서는
파일 재생만 하면 된다 → 긴급 콜 지연 0.

설정(config.yaml)의 tts 엔진/보이스를 그대로 사용한다. 보이스나 말 속도를
바꾸면 다시 실행할 것. 이미 캐시된 파일은 건너뛴다.

사용법: python tools/pregen_audio.py [--config config.yaml]
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
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from config import load_config           # noqa: E402
from tts import build_engine              # noqa: E402
from voice import PhrasePool, iter_pregen_texts  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default="config.yaml")
    parser.add_argument("--dry-run", action="store_true", help="합성 없이 대상 텍스트만 출력")
    args = parser.parse_args()

    cfg = load_config(args.config)
    pool = PhrasePool()
    texts = sorted(set(iter_pregen_texts(pool)))
    print(f"사전 캐시 대상: {len(texts)}개 멘트")

    if args.dry_run:
        for t in texts:
            print(" ", t)
        return 0

    engine = build_engine(cfg)
    ok = skip = fail = 0
    t0 = time.time()
    for i, text in enumerate(texts, 1):
        # 엔진의 synth가 캐시를 확인하므로 여기선 존재 여부만 미리 보고용으로 체크
        path = engine.synth(text)
        if path is None:
            fail += 1
            print(f"[{i}/{len(texts)}] 실패: {text}")
        elif os.path.getmtime(path) < t0:
            skip += 1
        else:
            ok += 1
            print(f"[{i}/{len(texts)}] {text}")
    print(f"완료: 신규 {ok}, 기존 {skip}, 실패 {fail}")
    return 1 if fail else 0


if __name__ == "__main__":
    main()
