"""
무전기 효과 후처리 — TTS 출력을 실제 팀라디오처럼 들리게 한다.

깨끗한 TTS는 기계음이 그대로 드러나지만, 무전은 원래 대역이 좁고 뭉개진
소리라 같은 음성도 무전 효과를 입히면 훨씬 자연스럽게(그리고 몰입감 있게)
들린다. 실제 크루치프 앱들이 쓰는 표준 기법.

체인: 밴드패스(300~3100Hz) → 새추레이션(컴프레션 질감) → 배경 노이즈
      → 키 해제 스켈치(정적 버스트)

캐시 시점에 한 번만 처리하므로 런타임 비용 없음. numpy/soundfile이 없거나
디코드에 실패하면 None을 반환하고 원본을 그대로 쓴다 (앱은 죽지 않는다).
"""

from __future__ import annotations

import logging
from typing import Optional

log = logging.getLogger("radiofx")

_warned = False

BAND_LO_HZ = 300.0      # 무전 대역 하한
BAND_HI_HZ = 3100.0     # 무전 대역 상한
SATURATION = 2.8        # tanh 드라이브 (클수록 더 눌린 소리)
SQUELCH_SEC = 0.06      # 키 해제 정적 버스트 길이
SQUELCH_LEVEL = 0.10


def process(src_path: str, dst_path: str, noise: float = 0.004) -> Optional[str]:
    """src(mp3/wav) → 무전 효과 wav. 실패 시 None (호출부는 원본으로 폴백)."""
    global _warned
    try:
        import numpy as np
        import soundfile as sf
    except ImportError:
        if not _warned:
            _warned = True
            log.warning("numpy/soundfile 미설치 — 무전 효과 없이 원본 재생 "
                        "(pip install numpy soundfile)")
        return None
    try:
        data, sr = sf.read(src_path, dtype="float32", always_2d=True)
    except Exception as e:
        if not _warned:
            _warned = True
            log.warning("오디오 디코드 실패(%s) — 무전 효과 없이 원본 재생", e)
        return None
    x = data.mean(axis=1)
    n = len(x)
    if n < int(sr * 0.05):
        return None

    # 1) 새추레이션 — 좁은 다이내믹레인지의 눌린 무전 질감 (송신단 컴프레서).
    #    왜곡 하모닉은 뒤의 밴드패스(수신단 필터)가 정리한다.
    peak = float(np.max(np.abs(x))) or 1.0
    x = np.tanh(SATURATION * x / peak)

    # 2) 밴드패스 — FFT 마스크 (에지는 완만하게)
    spec = np.fft.rfft(x)
    freqs = np.fft.rfftfreq(n, 1.0 / sr)
    mask = np.ones_like(freqs)
    low = freqs < BAND_LO_HZ
    mask[low] = (freqs[low] / BAND_LO_HZ) ** 2
    high = freqs > BAND_HI_HZ
    mask[high] = np.exp(-(freqs[high] - BAND_HI_HZ) / 600.0)
    x = np.fft.irfft(spec * mask, n)

    # 3) 배경 노이즈 (시드 고정 — 같은 입력이면 같은 출력)
    rng = np.random.default_rng(56)
    if noise > 0:
        x = x + rng.normal(0.0, noise, n)

    # 4) 키 해제 스켈치 — 말끝에 짧은 정적 버스트
    burst_n = int(sr * SQUELCH_SEC)
    burst = rng.normal(0.0, SQUELCH_LEVEL, burst_n) * np.linspace(1.0, 0.3, burst_n)
    x = np.concatenate([x, np.zeros(int(sr * 0.02)), burst])

    # 5) 정규화 후 저장
    x = 0.9 * x / max(float(np.max(np.abs(x))), 1e-6)
    try:
        sf.write(dst_path, x.astype(np.float32), sr, subtype="PCM_16")
    except Exception as e:
        log.warning("무전 효과 저장 실패: %s", e)
        return None
    return dst_path
