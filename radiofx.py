"""
무전기 효과 후처리 — TTS 출력을 실제 팀라디오처럼 들리게 한다.

깨끗한 TTS는 기계음이 그대로 드러나지만, 무전은 원래 대역이 좁고 뭉개진
소리라 같은 음성도 무전 효과를 입히면 훨씬 자연스럽게(그리고 몰입감 있게)
들린다. 심레이싱 무전/스포터 앱들이 쓰는 표준 기법.

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
import os
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


def _load_mono(src_path: str, np):
    """src(mp3/wav) → (피크 1로 정규화한 모노, sr). 디코드 실패·너무 짧으면 None."""
    global _warned
    import soundfile as sf
    try:
        data, sr = sf.read(src_path, dtype="float32", always_2d=True)
    except Exception as e:
        if not _warned:
            _warned = True
            log.warning("오디오 디코드 실패(%s) — 무전 효과 없이 원본 재생", e)
        return None
    x = data.mean(axis=1)
    if len(x) < int(sr * 0.05):
        return None
    x = x / (float(np.max(np.abs(x))) or 1.0)
    return x, sr


def _body(x, sr, noise, rng, np):
    """목소리 체인 1)~5). rng 소비는 5)의 노이즈 베드 한 번뿐 (noise > 0일 때)."""
    n = len(x)

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
    if noise > 0:
        bed = _bandpass(rng.normal(0.0, 1.0, n), sr, np)
        bed = bed / (float(np.std(bed)) or 1.0)
        env_n = env / float(np.max(env))
        swell = 0.7 + 0.9 * (1.0 - np.minimum(env_n * 3.0, 1.0))
        # 대역 제한으로 깎인 에너지 보상 (×2.2) — 사용자 설정값의 체감 유지
        x = x + bed * swell * noise * 2.2
    return x


def _edge_clips(sr, rng, np):
    """6) 키 클릭(송신 시작) + 키 해제 스켈치 — 대역 노이즈 버스트. (click, burst)"""
    click_n = int(sr * KEY_CLICK_SEC)
    click = _bandpass(rng.normal(0.0, 1.0, max(click_n, 32)), sr, np)[:click_n]
    click = click / (float(np.max(np.abs(click))) or 1.0)
    click *= 0.35 * np.exp(-np.linspace(0.0, 5.0, click_n))

    burst_n = int(sr * SQUELCH_SEC)
    burst = _bandpass(rng.normal(0.0, 1.0, max(burst_n, 32)), sr, np)[:burst_n]
    burst = burst / (float(np.std(burst)) or 1.0)
    burst *= SQUELCH_LEVEL * np.linspace(1.0, 0.25, burst_n)
    return click, burst


def _to_target_sr(x, sr, np):
    """7) 8kHz로 리샘플 — 대역 제한(≤2.7kHz)이라 손실 없음, 파일 1/3."""
    if sr > TARGET_SR:
        src_t = np.arange(len(x)) / sr
        dst_t = np.arange(int(len(x) * TARGET_SR / sr)) / TARGET_SR
        x = np.interp(dst_t, src_t, x)
        sr = TARGET_SR
    return x, sr


def _imports():
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
    return np, sf


def process(src_path: str, dst_path: str, noise: float = 0.004) -> Optional[str]:
    """src(mp3/wav) → 무전 효과 wav. 실패 시 None (호출부는 원본으로 폴백)."""
    mods = _imports()
    if mods is None:
        return None
    np, sf = mods
    loaded = _load_mono(src_path, np)
    if loaded is None:
        return None
    x, sr = loaded

    # 난수 소비 순서(노이즈 베드 → 클릭 → 버스트)가 배포 캐시 출력을 고정한다
    rng = np.random.default_rng(56)
    x = _body(x, sr, noise, rng, np)
    click, burst = _edge_clips(sr, rng, np)
    x = np.concatenate([click, x, np.zeros(int(sr * 0.03)), burst])
    x, sr = _to_target_sr(x, sr, np)

    # 8) 정규화 후 저장
    x = 0.9 * x / max(float(np.max(np.abs(x))), 1e-6)
    try:
        sf.write(dst_path, x.astype(np.float32), sr, subtype="PCM_16")
    except Exception as e:
        log.warning("무전 효과 저장 실패: %s", e)
        return None
    return dst_path


# -- 문장 세그먼트 (radio_assemble.py / RadioAssembler.cs가 조립) -------------
# 통문장 클립은 위 process()로 클릭·스켈치까지 한 파일에 굽지만, 세그먼트는
# 목소리만 담고 클릭/간격/스켈치는 공용 자산 4개로 따로 둔다. 런타임은 연산
# 없이 이어 붙이기만 하므로 페이드는 여기서 미리 넣는다.
# 값을 바꿔 배포 팩을 다시 만들면 radio_assemble.SEG_VERSION(= C# SegSuffix)을 올릴 것.
SEG_SPEED = 1.10                 # 문장 단독 렌더는 통문장보다 느려서 보정
SEG_GAP_SEC = {"casual": 0.20, "urgent": 0.15}   # 문장 사이 무음(노이즈 베드)
SEG_FADE_SEC = 0.005
SEG_TRIM_DB = -40                # 피크 대비 이 아래 프레임은 앞뒤 무음으로 잘라냄
SEG_LEAD_SEC = 0.02
SEG_TRAIL_SEC = 0.05
SEG_PEAK = 0.9
# 자산(클릭/간격/스켈치)에 곱하는 고정 게인 — 통문장 정규화 게인의 대표값.
# pregen_audio.py --segments가 세그먼트 게인 중앙값과 비교해 경고한다.
ASSET_GAIN = 0.63

_SEG_FRAME_SEC = 0.005
# 자산 원천 샘플레이트 — Kokoro 출력과 같게 해 통문장 클릭/스켈치와 같은 소리
_ASSET_SRC_SR = 24000


def _trim(x, sr, np):
    """5ms 프레임 RMS가 피크 대비 SEG_TRIM_DB 이상인 구간 ± 여유만 남긴다."""
    fn = max(int(sr * _SEG_FRAME_SEC), 1)
    m = len(x) // fn
    peak = float(np.max(np.abs(x))) if len(x) else 0.0
    if m == 0 or peak <= 0.0:
        return None
    rms = np.sqrt(np.mean(x[:m * fn].reshape(m, fn) ** 2, axis=1) + 1e-12)
    idx = np.where(20.0 * np.log10(rms / peak) >= SEG_TRIM_DB)[0]
    if len(idx) == 0:
        return None
    s = max(int(idx[0]) * fn - int(SEG_LEAD_SEC * sr), 0)
    e = min((int(idx[-1]) + 1) * fn + int(SEG_TRAIL_SEC * sr), len(x))
    return x[s:e]


def _fade(x, sr, np):
    """앞뒤 SEG_FADE_SEC 선형 페이드 — 첫/끝 샘플이 정확히 0 (단순 연결 시 무클릭)."""
    k = min(int(sr * SEG_FADE_SEC), len(x) // 2)
    if k > 1:
        ramp = np.linspace(0.0, 1.0, k)
        x = x.copy()
        x[:k] *= ramp
        x[-k:] *= ramp[::-1]
    return x


def _write_atomic(sf, dst, x, sr, subtype):
    tmp = dst + ".part"
    sf.write(tmp, x, sr, subtype=subtype, format="WAV")
    os.replace(tmp, dst)


def process_segment(samples, sr: int, dst: str,
                    noise: float = 0.004) -> Optional[float]:
    """
    Kokoro 문장 렌더(float 샘플) → 무전 효과 세그먼트 (8kHz μ-law WAV).
    반환: 마지막 피크 정규화 게인 (자산 ASSET_GAIN과 비교용). 실패 시 None.
    """
    mods = _imports()
    if mods is None:
        return None
    np, sf = mods
    x = np.asarray(samples, dtype=np.float64).reshape(-1)
    x = _trim(x, sr, np)
    if x is None or len(x) < int(sr * 0.05):
        return None
    x = x / (float(np.max(np.abs(x))) or 1.0)
    x = _body(x, sr, noise, np.random.default_rng(56), np)
    x, out_sr = _to_target_sr(x, sr, np)
    # 페이드는 최종 샘플레이트에서 — 리샘플 뒤에도 끝 샘플이 0으로 남도록
    x = _fade(x, out_sr, np)
    peak = float(np.max(np.abs(x)))
    if peak <= 1e-6:
        return None
    gain = SEG_PEAK / peak
    try:
        _write_atomic(sf, dst, (x * gain).astype(np.float32), out_sr, "ULAW")
    except Exception as e:
        log.warning("세그먼트 저장 실패(%s): %s", dst, e)
        return None
    return gain


def write_segment_assets(cache_dir: str, noise: float = 0.004) -> bool:
    """
    조립 자산 4개 (8kHz PCM16): 클릭 / 간격(casual, urgent) / 꼬리(30ms 무음 + 스켈치).
    파일명은 radio_assemble.ASSET_NAMES. 시드 고정이라 다시 만들어도 같은 바이트.
    """
    mods = _imports()
    if mods is None:
        return False
    np, sf = mods
    from radio_assemble import asset_name

    sr = _ASSET_SRC_SR
    rng = np.random.default_rng(57)
    click, burst = _edge_clips(sr, rng, np)
    clips = {
        "click": click,
        "tail": np.concatenate([np.zeros(int(sr * 0.03)), burst]),
    }
    for tone in ("casual", "urgent"):
        # 통문장에서 목소리가 빈 구간의 베드와 같은 레벨 (swell 최대 1.6)
        bed = _bandpass(rng.normal(0.0, 1.0, int(sr * SEG_GAP_SEC[tone])), sr, np)
        bed = bed / (float(np.std(bed)) or 1.0)
        clips[f"gap_{tone}"] = bed * 1.6 * noise * 2.2
    try:
        os.makedirs(cache_dir, exist_ok=True)
        for kind, x in clips.items():
            x, out_sr = _to_target_sr(x, sr, np)
            if kind.startswith("gap_"):
                x = _fade(x, out_sr, np)
            x = np.clip(x * ASSET_GAIN, -1.0, 1.0).astype(np.float32)
            _write_atomic(sf, os.path.join(cache_dir, asset_name(kind)),
                          x, out_sr, "PCM_16")
    except Exception as e:
        log.warning("세그먼트 자산 저장 실패: %s", e)
        return False
    return True
