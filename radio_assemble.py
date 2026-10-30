"""
문장 세그먼트 조립 — 사전 생성된 문장별 클립을 무전 한 번으로 이어 붙인다.

C# simhub/src/TeamRadio56.Core/Logic/RadioAssembler.cs와 바이트 단위로
같은 결과를 내야 한다 (simhub/verify/replay-check.sh가 cmp로 대조).
표준 라이브러리만 쓴다 — numpy 없이 파이썬 엔진 런타임에서 돌아야 한다.

출력: 8 kHz 모노 PCM16 WAV (44바이트 헤더)
  click + seg1 + gap(tone) + seg2 + ... + gap(tone) + segN + tail
런타임에는 크로스페이드·연산이 없다 — 페이드는 사전 생성 때 세그먼트와
간격 클립에 이미 들어 있으므로 단순 연결로 끝난다.

입력 클립 규약: RIFF/WAVE, 모노 8000 Hz, PCM16(태그 1) 또는 μ-law(태그 7, 8비트).
fmt 크기는 16 또는 18, 알 수 없는 청크(fact, LIST 등)는 건너뛴다.
그 밖의 형식이나 파싱 실패는 None — 호출부는 캐시 미스로 취급한다.
"""

from __future__ import annotations

import hashlib
import os
import struct
import sys
import threading
from array import array
from typing import Optional

from messages import split_sentences

# 세그먼트 렌더 규약(속도/효과/간격)이 바뀌면 올린다 — C# VoiceCache.SegSuffix와 일치
SEG_VERSION = 1
SEG_SUFFIX = f"_seg{SEG_VERSION}.wav"

SAMPLE_RATE = 8000


def asset_name(kind: str) -> str:
    """조립 자산 파일명 — click / tail / gap_casual / gap_urgent."""
    return f"_seg{SEG_VERSION}_{kind}.wav"


ASSET_NAMES = tuple(asset_name(k) for k in
                    ("click", "gap_casual", "gap_urgent", "tail"))


def _ulaw_decode(u: int) -> int:
    # G.711 표준 복호 — libsndfile과 같은 값 (assemble_check가 대조)
    u = ~u & 0xFF
    t = ((u & 0x0F) << 3) + 0x84
    t <<= (u & 0x70) >> 4
    return 0x84 - t if (u & 0x80) else t - 0x84


ULAW_TABLE = tuple(_ulaw_decode(u) for u in range(256))


def _ulaw_encode(s: int) -> int:
    """G.711 μ-law 부호화 (테스트 클립 작성용)."""
    sign = 0
    if s < 0:
        s = -s
        sign = 0x80
    s = min(s, 32635) + 0x84
    exp = min((s >> 7).bit_length() - 1, 7)
    mant = (s >> (exp + 3)) & 0x0F
    return ~(sign | (exp << 4) | mant) & 0xFF


def segment_file_name(sentence: str, tone: str, voice: str = "bm_george") -> str:
    """통문장과 같은 kokoro 키(tts._cache_path)에 세그먼트 접미사만 다르다."""
    h = hashlib.md5(f"kokoro|{voice}|{tone}|{sentence}".encode("utf-8")).hexdigest()[:20]
    return h + SEG_SUFFIX


def segment_paths(cache_dir: str, voice: str, tone: str,
                  text: str) -> Optional[list[str]]:
    """문장마다 세그먼트 경로 — 하나라도 없으면 None (부분 조립은 하지 않는다)."""
    return segment_lookup(cache_dir, voice, tone, text)[0]


def segment_lookup(cache_dir: str, voice: str, tone: str, text: str,
                   exists=os.path.isfile
                   ) -> tuple[Optional[list[str]], Optional[str]]:
    """(경로 목록 또는 None, 처음 빠진 문장) — C# SegmentPaths(out firstMissing).
    exists는 파일 확인 함수 (가상 팩 대조용으로 바꿔 끼운다)."""
    sents = split_sentences(text)
    if not sents:
        return None, None
    paths = []
    for s in sents:
        p = os.path.join(cache_dir, segment_file_name(s, tone, voice))
        if not exists(p):
            return None, s
        paths.append(p)
    return paths, None


def read_samples(path: str) -> Optional[list[int]]:
    """WAV → int16 샘플 목록. 규약 밖이거나 깨진 파일이면 None."""
    try:
        with open(path, "rb") as f:
            b = f.read()
    except OSError:
        return None
    if len(b) < 12 or b[0:4] != b"RIFF" or b[8:12] != b"WAVE":
        return None
    fmt = data = None
    pos = 12
    while pos + 8 <= len(b):
        cid = b[pos:pos + 4]
        size = struct.unpack_from("<I", b, pos + 4)[0]
        start = pos + 8
        end = start + size
        if end > len(b):
            return None
        if cid == b"fmt " and fmt is None:
            fmt = b[start:end]
        elif cid == b"data" and data is None:
            data = b[start:end]
        pos = end + (size & 1)      # 홀수 크기 청크는 패딩 1바이트
    if fmt is None or data is None or len(fmt) not in (16, 18):
        return None
    tag, channels, rate = struct.unpack_from("<HHI", fmt, 0)
    bits = struct.unpack_from("<H", fmt, 14)[0]
    if channels != 1 or rate != SAMPLE_RATE:
        return None
    if tag == 1 and bits == 16:
        n = len(data) // 2
        return list(struct.unpack_from(f"<{n}h", data, 0))
    if tag == 7 and bits == 8:
        return [ULAW_TABLE[u] for u in data]
    return None


_asset_lock = threading.Lock()
_asset_cache: dict[str, list[int]] = {}


def _asset(path: str) -> Optional[list[int]]:
    with _asset_lock:
        cached = _asset_cache.get(path)
        if cached is not None:
            return cached
        s = read_samples(path)
        if s is not None:
            _asset_cache[path] = s
        return s


def _wav_bytes(samples: list[int]) -> bytes:
    pcm = array("h", samples)
    if sys.byteorder != "little":
        pcm.byteswap()
    n = len(samples)
    header = struct.pack("<4sI4s4sIHHIIHH4sI",
                         b"RIFF", 36 + 2 * n, b"WAVE",
                         b"fmt ", 16, 1, 1, SAMPLE_RATE, SAMPLE_RATE * 2, 2, 16,
                         b"data", 2 * n)
    return header + pcm.tobytes()


def build(cache_dir: str, tone: str, seg_paths: list[str]) -> Optional[bytes]:
    """세그먼트들을 무전 한 번(PCM16 WAV 바이트)으로 조립. 실패하면 None."""
    if not seg_paths:
        return None
    click = _asset(os.path.join(cache_dir, asset_name("click")))
    gap = _asset(os.path.join(cache_dir, asset_name(f"gap_{tone}")))
    tail = _asset(os.path.join(cache_dir, asset_name("tail")))
    if click is None or gap is None or tail is None:
        return None
    out = list(click)
    for i, p in enumerate(seg_paths):
        seg = read_samples(p)
        if seg is None:
            return None
        if i > 0:
            out.extend(gap)
        out.extend(seg)
    out.extend(tail)
    return _wav_bytes(out)


def write_ulaw_wav(path: str, samples: list[int]) -> None:
    """μ-law WAV 작성 (테스트용) — libsndfile처럼 fmt 크기 18 + fact 청크."""
    data = bytes(_ulaw_encode(int(s)) for s in samples)
    n = len(data)
    fmt = struct.pack("<HHIIHHH", 7, 1, SAMPLE_RATE, SAMPLE_RATE, 1, 8, 0)
    body = (b"WAVE"
            + b"fmt " + struct.pack("<I", len(fmt)) + fmt
            + b"fact" + struct.pack("<II", 4, n)
            + b"data" + struct.pack("<I", n) + data
            + (b"\x00" if n & 1 else b""))
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", len(body)) + body)
