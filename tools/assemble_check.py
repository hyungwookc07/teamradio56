"""
문장 세그먼트 조립기 바이트 대조용 합성 팩 생성 + 파이썬 쪽 조립.

--dir D 아래에:
  D/pack/, D/noasset/   결정적 합성 세그먼트 + 조립 자산 (실제 Kokoro 렌더 아님)
  D/messages.tsv        "하위폴더\t톤\t멘트" 한 줄에 하나 (줄 번호 = 출력 번호)
  D/py/{i}.wav          radio_assemble로 조립한 결과 (미스면 파일 없음)
C# 러너 --assemble-check D가 같은 입력으로 D/cs/{i}.wav를 만들고,
simhub/verify/replay-check.sh가 쌍마다 cmp한다 (양쪽 다 없으면 통과).

세그먼트 형식을 섞는다: 자체 μ-law(fmt 18 + fact, libsndfile 모양),
청크 순서가 뒤섞인 PCM16(홀수 크기 LIST 청크 포함), soundfile이 있으면
sf.write(subtype='ULAW')로 쓴 실제 libsndfile 파일, 그리고 규약 밖 파일들.
soundfile이 있으면 read_samples가 libsndfile 복호(sf.read int16)와
같은지도 확인한다 (μ-law 256개 코드 전체 포함).

파이썬 전용 점검도 곁들인다 (D/engine/, C# 대조와 무관): KokoroPackEngine
조립본 재사용·팩 교체 감지·임시 폴더 정리, speech_text 소수 읽기,
load_track_names의 BOM 처리.

사용법: python tools/assemble_check.py --dir <폴더>
종료 코드: 0 = 통과, 1 = 파이썬 쪽 기대와 다름.
"""

from __future__ import annotations

import argparse
import math
import os
import random
import shutil
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import radio_assemble as ra                                   # noqa: E402

VOICE = "bm_george"

# (하위폴더, 톤, 멘트, 조립 성공 기대)
CASES = [
    ("pack", "casual", "Gap behind 6.3 seconds. Closing 1.3 a lap. Just no mistakes.", True),
    ("pack", "casual", "Lost 2.4 on that lap. Checking why.", True),
    ("pack", "urgent", "one Hypercar, one GT3 behind. two LMP2s closing. Ahead is clear.", True),
    ("pack", "casual", "Check done. Front left wheel damage. "
                       "Heavy bodywork damage, front and front right. Prepare to box.", True),
    ("pack", "urgent", "Yellow in sector 2! No overtaking, be ready to lift.", True),
    ("pack", "urgent", "Copy.", True),
    # 문장 하나가 빠짐 → 전체 미스 (부분 조립 금지)
    ("pack", "casual", "Gap ahead 7.8 seconds. Opening up. Don't overdo it.", False),
    # 같은 문장이라도 톤이 다르면 다른 세그먼트
    ("pack", "urgent", "Lost 2.4 on that lap. Checking why.", False),
    # 자산(tail) 없음 → 미스
    ("noasset", "casual", "Lost 2.4 on that lap. Checking why.", False),
    # 규약 밖 세그먼트 → 파싱 실패 → 미스
    ("pack", "casual", "Box this lap. Rate is wrong.", False),
    ("pack", "casual", "Box this lap. Two channels.", False),
    ("pack", "casual", "Box this lap. Float samples.", False),
    ("pack", "casual", "Box this lap. Cut short.", False),
    ("pack", "casual", "Box this lap. Fmt too long.", False),
]

# 쓰지 않는 세그먼트 — 톤 구분 케이스는 urgent 쪽이 통째로 없다
MISSING = {("casual", "Don't overdo it."),
           ("urgent", "Lost 2.4 on that lap."), ("urgent", "Checking why.")}
PCM_SENTENCE = ("casual", "Checking why.")
SF_SENTENCE = ("casual", "Closing 1.3 a lap.")
BAD = {
    "Rate is wrong.": "rate",
    "Two channels.": "stereo",
    "Float samples.": "float",
    "Cut short.": "truncated",
    "Fmt too long.": "fmt20",
}


def synth(seed: int, n: int, amp: float = 9000.0) -> list[int]:
    """결정적 합성 신호 (파이썬 안에서만 결정적이면 된다 — C#은 파일을 읽기만 한다)."""
    rnd = random.Random(seed)
    f = 180.0 + 40.0 * (seed % 7)
    out = []
    for i in range(n):
        env = math.sin(math.pi * i / n)
        v = amp * env * math.sin(2 * math.pi * f * i / ra.SAMPLE_RATE) + rnd.gauss(0, 600)
        out.append(max(-32768, min(32767, int(round(v)))))
    return out


def chunk(cid: bytes, body: bytes) -> bytes:
    return cid + struct.pack("<I", len(body)) + body + (b"\x00" if len(body) & 1 else b"")


def write_wav(path: str, chunks: list[bytes]) -> None:
    body = b"WAVE" + b"".join(chunks)
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", len(body)) + body)


def fmt_chunk(tag: int, channels: int, rate: int, bits: int, extra: bytes = b"") -> bytes:
    align = channels * bits // 8
    return chunk(b"fmt ", struct.pack("<HHIIHH", tag, channels, rate, rate * align,
                                      align, bits) + extra)


def pcm_bytes(samples: list[int]) -> bytes:
    return struct.pack(f"<{len(samples)}h", *samples)


def write_pcm16(path: str, samples: list[int]) -> None:
    write_wav(path, [fmt_chunk(1, 1, ra.SAMPLE_RATE, 16), chunk(b"data", pcm_bytes(samples))])


def write_bad(path: str, kind: str, samples: list[int]) -> None:
    data = pcm_bytes(samples)
    if kind == "rate":
        write_wav(path, [fmt_chunk(1, 1, 22050, 16), chunk(b"data", data)])
    elif kind == "stereo":
        write_wav(path, [fmt_chunk(1, 2, ra.SAMPLE_RATE, 16), chunk(b"data", data)])
    elif kind == "float":
        fl = struct.pack(f"<{len(samples)}f", *(s / 32768.0 for s in samples))
        write_wav(path, [fmt_chunk(3, 1, ra.SAMPLE_RATE, 32), chunk(b"data", fl)])
    elif kind == "fmt20":
        write_wav(path, [fmt_chunk(1, 1, ra.SAMPLE_RATE, 16, b"\x00" * 4),
                         chunk(b"data", data)])
    elif kind == "truncated":
        write_pcm16(path, samples)
        with open(path, "r+b") as f:
            f.truncate(os.path.getsize(path) - 100)
    else:
        raise ValueError(kind)


def build_pack(root: str, sf) -> dict:
    """세그먼트·자산을 쓰고 {경로: 기대 샘플}을 돌려준다 (규약 밖 파일 제외)."""
    expected = {}
    pack = os.path.join(root, "pack")
    noasset = os.path.join(root, "noasset")
    os.makedirs(pack, exist_ok=True)
    os.makedirs(noasset, exist_ok=True)

    sentences = set()
    for sub, tone, text, _ in CASES:
        for s in ra.split_sentences(text):
            sentences.add((tone, s))

    for seed, (tone, s) in enumerate(sorted(sentences)):
        if (tone, s) in MISSING:
            continue
        name = ra.segment_file_name(s, tone, VOICE)
        n = 400 + 37 * len(s)
        samples = synth(seed, n)
        for d in (pack, noasset):
            path = os.path.join(d, name)
            if s in BAD:
                write_bad(path, BAD[s], samples)
            elif (tone, s) == PCM_SENTENCE:
                # 청크 순서 뒤섞기 + 홀수 크기 LIST(패딩) + fmt 앞의 알 수 없는 청크
                write_wav(path, [chunk(b"LIST", b"INFOabc"),
                                 fmt_chunk(1, 1, ra.SAMPLE_RATE, 16),
                                 chunk(b"junk", b"\x01\x02\x03"),
                                 chunk(b"data", pcm_bytes(samples))])
                expected[path] = samples
            elif (tone, s) == SF_SENTENCE and sf is not None:
                import numpy as np
                sf.write(path, np.asarray(samples, dtype=np.int16), ra.SAMPLE_RATE,
                         subtype="ULAW", format="WAV")
                expected[path] = None        # libsndfile 부호화 — sf.read로 확인
            else:
                ra.write_ulaw_wav(path, samples)
                expected[path] = [ra.ULAW_TABLE[ra._ulaw_encode(v)] for v in samples]

    assets = {
        "click": synth(101, 240, 12000),
        "gap_casual": synth(102, 1600, 1500),
        "gap_urgent": synth(103, 1200, 1500),
        "tail": synth(104, 800, 9000),
    }
    for kind, samples in assets.items():
        write_pcm16(os.path.join(pack, ra.asset_name(kind)), samples)
        if kind != "tail":
            write_pcm16(os.path.join(noasset, ra.asset_name(kind)), samples)
    return expected


def soundfile_checks(root: str, sf, expected: dict) -> list[str]:
    errors = []
    # μ-law 256개 코드 전체 — libsndfile 복호와 같은 표인지
    table = os.path.join(root, "ulaw_table.wav")
    write_wav(table, [fmt_chunk(7, 1, ra.SAMPLE_RATE, 8, b"\x00\x00"),
                      chunk(b"fact", struct.pack("<I", 256)),
                      chunk(b"data", bytes(range(256)))])
    got = sf.read(table, dtype="int16")[0].tolist()
    if got != list(ra.ULAW_TABLE):
        errors.append("ULAW_TABLE이 libsndfile 복호와 다름")
    for path in expected:
        mine = ra.read_samples(path)
        ref = sf.read(path, dtype="int16")[0].tolist()
        if mine != ref:
            errors.append(f"read_samples ≠ sf.read: {os.path.basename(path)}")
    return errors


def engine_checks(root: str) -> list[str]:
    """파이썬 엔진 쪽 점검 — 팩 복사본(D/engine)에서만 돌려 C# 대조 입력은 그대로 둔다."""
    from messages import speech_text
    from tts import KokoroPackEngine
    from voice import load_track_names

    errors = []
    cache = os.path.join(root, "engine")
    shutil.copytree(os.path.join(root, "pack"), cache)
    _, tone, text, _ = CASES[1]
    paths = ra.segment_paths(cache, VOICE, tone, text)

    def read(p):
        with open(p, "rb") as f:
            return f.read()

    eng = KokoroPackEngine(cache, VOICE)
    eng._tmp_dir = os.path.join(root, "engine_tmp")
    first = eng.synth(text, tone)
    if first is None or read(first) != ra.build(cache, tone, paths):
        errors.append("KokoroPackEngine 조립 결과가 radio_assemble.build와 다름")
        return errors
    if eng.synth(text, tone) != first:
        errors.append("KokoroPackEngine이 같은 팩의 조립본을 재사용하지 않음")

    # 팩 교체 — 새 세그먼트의 mtime이 조립본보다 옛날이어도(압축 해제) 다시 조립
    old_t = os.path.getmtime(first) - 86400
    write_pcm16(paths[0], synth(999, 900))
    os.utime(paths[0], (old_t, old_t))
    second = eng.synth(text, tone)
    if second is None or second == first or read(second) != ra.build(cache, tone, paths):
        errors.append("팩 교체 후에도 옛 조립본을 재생함")

    # 캐시 폴더에 못 쓰면 임시 폴더로, 거기도 MAX_ASSEMBLED개만 (C# assembled.wav는 보존)
    eng._asm_dir = os.path.join(first, "x")       # 파일 아래 폴더 — makedirs 실패
    eng.MAX_ASSEMBLED = 2
    os.makedirs(eng._tmp_dir, exist_ok=True)
    keep = os.path.join(eng._tmp_dir, "assembled.wav")
    write_pcm16(keep, [0])
    for k in range(4):
        os.utime(paths[0], (old_t - k, old_t - k))
        if eng.synth(text, tone) is None:
            errors.append("임시 폴더 조립 실패")
    asm = [n for n in os.listdir(eng._tmp_dir) if "_asm" in n]
    if len(asm) != 2 or not os.path.exists(keep):
        errors.append(f"임시 폴더 정리 이상: 조립본 {len(asm)}개")

    for src, want in [("Gap 1.25 seconds.", "Gap 1 point 2 5 seconds."),
                      ("0.15", "0 point 1 5"),
                      ("Closing 1.3 a lap.", "Closing 1 point 3 a lap."),
                      ("-1.3 on two LMP2s", "-1 point 3 on two LMP twos")]:
        if speech_text(src) != want:
            errors.append(f"speech_text({src!r}) = {speech_text(src)!r}, 기대 {want!r}")

    # 윈도 편집기의 UTF-8 BOM — C# File.ReadAllLines(UTF8)처럼 떼어야 한다
    tracks = os.path.join(root, "tracks_bom.txt")
    with open(tracks, "wb") as f:
        f.write("\ufeff# 주석\nFuji Speedway\n".encode("utf-8"))
    if load_track_names(tracks) != ["Fuji Speedway"]:
        errors.append(f"load_track_names BOM 처리 이상: {load_track_names(tracks)!r}")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dir", required=True)
    args = parser.parse_args()
    root = args.dir

    try:
        import numpy  # noqa: F401
        import soundfile as sf
    except ImportError:
        sf = None

    expected = build_pack(root, sf)
    errors = []

    for path, want in expected.items():
        if want is not None and ra.read_samples(path) != want:
            errors.append(f"read_samples 결과 다름: {os.path.basename(path)}")

    with open(os.path.join(root, "messages.tsv"), "w", encoding="utf-8", newline="\n") as f:
        for sub, tone, text, _ in CASES:
            f.write(f"{sub}\t{tone}\t{text}\n")

    out_dir = os.path.join(root, "py")
    os.makedirs(out_dir, exist_ok=True)
    hits = 0
    for i, (sub, tone, text, want_hit) in enumerate(CASES):
        cache = os.path.join(root, sub)
        paths = ra.segment_paths(cache, VOICE, tone, text)
        wav = ra.build(cache, tone, paths) if paths else None
        if (wav is not None) != want_hit:
            errors.append(f"#{i} 기대 {'조립' if want_hit else '미스'}인데 반대: {text}")
            continue
        if wav is None:
            continue
        hits += 1
        out = os.path.join(out_dir, f"{i}.wav")
        with open(out, "wb") as f:
            f.write(wav)
        # 조립 결과 = click + seg + gap + ... + seg + tail
        gap = ra.read_samples(os.path.join(cache, ra.asset_name(f"gap_{tone}")))
        want = list(ra.read_samples(os.path.join(cache, ra.asset_name("click"))))
        for k, p in enumerate(paths):
            if k:
                want += gap
            want += ra.read_samples(p)
        want += ra.read_samples(os.path.join(cache, ra.asset_name("tail")))
        if ra.read_samples(out) != want or len(wav) != 44 + 2 * len(want):
            errors.append(f"#{i} 조립 결과 순서/길이 다름")

    errors += engine_checks(root)

    if sf is not None:
        errors += soundfile_checks(root, sf, expected)
        sf_note = "libsndfile 복호 대조 포함"
    else:
        sf_note = "soundfile 없음 — libsndfile 대조 건너뜀"

    for e in errors:
        print(f"❌ {e}")
    if errors:
        return 1
    print(f"✅ 파이썬 조립 {hits}/{len(CASES)}건 (나머지는 의도된 미스), {sf_note}, "
          "팩 엔진·speech_text·트랙 BOM 점검 통과")
    return 0


if __name__ == "__main__":
    sys.exit(main())
