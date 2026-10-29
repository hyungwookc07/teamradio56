"""
무전 효과(radiofx.process) 회귀 가드 — 출력 바이트 md5 고정.

시드 고정 합성 입력(24kHz, 2.5초, 노이즈 엔벨로프를 곱한 처프)을
radiofx.process에 통과시키고 결과 wav 파일의 md5를 EXPECTED와 대조한다.
배포 캐시(_rfx3.wav)를 만드는 체인이므로, 리팩터로 출력이 1비트라도 바뀌면
여기서 잡힌다. 의도한 변경이면 radiofx.VERSION을 올리고 EXPECTED를 갱신.

numpy/soundfile이 없으면 건너뛴다 (종료 코드 0).

사용법: python tools/radiofx_selftest.py
종료 코드: 0 = 일치(또는 건너뜀), 1 = 불일치.
"""

from __future__ import annotations

import hashlib
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# radiofx.py 현재 구현(VERSION 3)으로 기록한 값
EXPECTED = "031c7716a8ceaccb1d515540aac65397"

SR = 24000
DURATION_SEC = 2.5


def synth_input(np):
    """결정적 입력 — 200→1800Hz 처프 × 저역 노이즈 엔벨로프 (음절 비슷한 굴곡)."""
    rng = np.random.default_rng(1)
    n = int(SR * DURATION_SEC)
    t = np.arange(n) / SR
    f0, f1 = 200.0, 1800.0
    phase = 2 * np.pi * (f0 * t + (f1 - f0) * t * t / (2 * DURATION_SEC))
    chirp = np.sin(phase)
    # 60ms 이동평균으로 다듬은 노이즈 → 0..1 엔벨로프
    raw = np.abs(rng.normal(0.0, 1.0, n))
    win = int(SR * 0.06)
    env = np.convolve(raw, np.ones(win) / win, mode="same")
    env = env / float(np.max(env))
    return (0.8 * chirp * env).astype(np.float32)


def main() -> int:
    try:
        import numpy as np
        import soundfile as sf
    except ImportError:
        print("⏭  radiofx 셀프테스트 건너뜀 — numpy/soundfile 미설치")
        return 0
    import radiofx

    with tempfile.TemporaryDirectory() as tmp:
        src = os.path.join(tmp, "in.wav")
        dst = os.path.join(tmp, "out.wav")
        sf.write(src, synth_input(np), SR, subtype="PCM_16")
        if radiofx.process(src, dst) is None:
            print("❌ radiofx.process 실패 (None 반환)")
            return 1
        with open(dst, "rb") as f:
            digest = hashlib.md5(f.read()).hexdigest()

    if digest != EXPECTED:
        print(f"❌ radiofx 출력 변경 — md5 {digest} (기대 {EXPECTED})")
        print("   의도한 변경이면 radiofx.VERSION을 올리고 EXPECTED를 갱신하세요.")
        return 1
    print(f"✅ radiofx 출력 동일 — md5 {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
