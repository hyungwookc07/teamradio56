"""
무전기 효과 후처리 — TTS 출력을 실제 팀라디오처럼 들리게 한다.

깨끗한 TTS는 기계음이 그대로 드러나지만, 무전은 원래 대역이 좁고 뭉개진
소리라 같은 음성도 무전 효과를 입히면 훨씬 자연스럽게(그리고 몰입감 있게)
들린다. 실제 크루치프 앱들이 쓰는 표준 기법.

"그냥 노이즈를 얹은 소리"와 "무전"을 가르는 요소들:
  - 노이즈도 같은 무전 대역을 통과해야 한다 (풀대역 화이트노이즈는 그냥 히스)
  - AGC 펌핑 — 목소리가 작아지면 게인이 올라가 노이즈가 같이 숨쉰다
  - 좁은 대역 + 1.8kHz 부근의 혼(honk) 공명 — 스피커 통울림
  - 키 클릭(송신 시작)과 스켈치 버스트(키 해제)
  - 코덱/양자화 그릿

체인: AGC → 새추레이션 → 밴드패스+혼 공명 → 비트 크러시
      → 대역 노이즈(역-엔벨로프 변조) → 키 클릭/스켈치

캐시 시점에 한 번만 처리하므로 런타임 비용 없음. numpy/soundfile이 없거나
디코드에 실패하면 None을 반환하고 원본을 그대로 쓴다 (앱은 죽지 않는다).
"""

from __future__ import annotations

import logging
from typing import Optional

log = logging.getLogger("radiofx")

_warned = False

# 캐시 파일명에 들어간다 — 효과 알고리즘/파라미터를 바꾸면 반드시 올릴 것.
# (안 올리면 기존 "_rfxN.wav" 캐시가 그대로 재생돼 변경이 안 들린다.)
VERSION = 3

# 출력 샘플레이트 — 무전 대역이 2.7kHz 이하라 8kHz(나이퀴스트 4kHz)면
# 손실 없이 담긴다. 파일 크기 1/3 = 배포 캐시 경량화.
TARGET_SR = 8000

BAND_LO_HZ = 350.0      # 무전 대역 하한
BAND_HI_HZ = 2700.0     # 무전 대역 상한 (좁을수록 "무전답다")
HONK_HZ = 1800.0        # 스피커 혼 공명 중심
HONK_GAIN = 0.6         # 공명 부스트 (0 = 없음)
SATURATION = 3.2        # tanh 드라이브 (클수록 더 눌린 소리)
AGC_STRENGTH = 0.65     # 0 = 없음, 1 = 완전 평탄화
AGC_WIN_SEC = 0.045     # 엔벨로프 창
CRUSH_BITS = 10         # 양자화 비트 (코덱 그릿)
KEY_CLICK_SEC = 0.022   # 송신 시작 클릭
SQUELCH_SEC = 0.07      # 키 해제 정적 버스트 길이
SQUELCH_LEVEL = 0.16


def _band_mask(freqs, np):
    """무전 대역 마스크 + 혼 공명. 목소리와 노이즈에 같은 마스크를 쓴다."""
    mask = np.ones_like(freqs)
    low = freqs < BAND_LO_HZ
    mask[low] = (freqs[low] / BAND_LO_HZ) ** 4
    high = freqs > BAND_HI_HZ
    mask[high] = np.exp(-(freqs[high] - BAND_HI_HZ) / 300.0)
    mask *= 1.0 + HONK_GAIN * np.exp(-((freqs - HONK_HZ) / 500.0) ** 2)
    return mask


def _bandpass(x, sr, np):
    spec = np.fft.rfft(x)
    freqs = np.fft.rfftfreq(len(x), 1.0 / sr)
    return np.fft.irfft(spec * _band_mask(freqs, np), len(x))


def _envelope(x, sr, np):
    """이동 평균 엔벨로프 (~45ms) — AGC와 노이즈 변조에 쓴다."""
    win = max(int(sr * AGC_WIN_SEC), 1)
    kernel = np.ones(win) / win
    env = np.convolve(np.abs(x), kernel, mode="same")
    return np.maximum(env, 1e-6)


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
    x = x / (float(np.max(np.abs(x))) or 1.0)

    # 1) AGC — 송신단 컴프레서. 다이내믹레인지를 눌러 "꽉 찬" 무전 밀도를
    #    만들고, 뒤에서 노이즈가 목소리 사이사이 숨쉬는 근거가 된다.
    env = _envelope(x, sr, np)
    gain = (env / float(np.max(env))) ** (-AGC_STRENGTH)
    x = x * np.minimum(gain, 8.0)
    x = x / (float(np.max(np.abs(x))) or 1.0)

    # 2) 새추레이션 — 눌린 무전 질감. 왜곡 하모닉은 뒤의 밴드패스가 정리.
    x = np.tanh(SATURATION * x)

    # 3) 밴드패스 + 혼 공명 — 수신단 필터와 스피커 통울림
    x = _bandpass(x, sr, np)

    # 4) 비트 크러시 — 코덱 그릿 (미묘하게)
    q = float(2 ** (CRUSH_BITS - 1))
    peak = float(np.max(np.abs(x))) or 1.0
    x = np.round(x / peak * q) / q * peak

    # 5) 대역 노이즈 — 같은 대역을 통과시킨 노이즈만 "전송 노이즈"로 들린다.
    #    역-엔벨로프 변조: 목소리가 빌 때 노이즈가 올라온다 (AGC 특성).
    rng = np.random.default_rng(56)
    if noise > 0:
        bed = _bandpass(rng.normal(0.0, 1.0, n), sr, np)
        bed = bed / (float(np.std(bed)) or 1.0)
        env_n = env / float(np.max(env))
        swell = 0.7 + 0.9 * (1.0 - np.minimum(env_n * 3.0, 1.0))
        # 대역 제한으로 깎인 에너지 보상 (×2.2) — 사용자 설정값의 체감 유지
        x = x + bed * swell * noise * 2.2

    # 6) 키 클릭(송신 시작) + 키 해제 스켈치 — 대역 노이즈 버스트
    click_n = int(sr * KEY_CLICK_SEC)
    click = _bandpass(rng.normal(0.0, 1.0, max(click_n, 32)), sr, np)[:click_n]
    click = click / (float(np.max(np.abs(click))) or 1.0)
    click *= 0.35 * np.exp(-np.linspace(0.0, 5.0, click_n))

    burst_n = int(sr * SQUELCH_SEC)
    burst = _bandpass(rng.normal(0.0, 1.0, max(burst_n, 32)), sr, np)[:burst_n]
    burst = burst / (float(np.std(burst)) or 1.0)
    burst *= SQUELCH_LEVEL * np.linspace(1.0, 0.25, burst_n)

    x = np.concatenate([click, x, np.zeros(int(sr * 0.03)), burst])

    # 7) 8kHz로 리샘플 — 대역 제한(≤2.7kHz)이라 손실 없음, 파일 1/3
    if sr > TARGET_SR:
        src_t = np.arange(len(x)) / sr
        dst_t = np.arange(int(len(x) * TARGET_SR / sr)) / TARGET_SR
        x = np.interp(dst_t, src_t, x)
        sr = TARGET_SR

    # 8) 정규화 후 저장
    x = 0.9 * x / max(float(np.max(np.abs(x))), 1e-6)
    try:
        sf.write(dst_path, x.astype(np.float32), sr, subtype="PCM_16")
    except Exception as e:
        log.warning("무전 효과 저장 실패: %s", e)
        return None
    return dst_path
