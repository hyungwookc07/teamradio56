"""
멘트 오디오 사전 캐시 생성 (배포 팩).

기본(통문장) 패스: 멘트 풀 전체 + 소수 조합 멘트를 TTS로 합성해 audio_cache에
저장한다. 런타임과 같은 캐시 키(md5)를 쓰므로 실전에서는 파일 재생만 한다.
kokoro는 build_pregen_engine으로 항상 kokoro 렌더 (팩/edge로 건너뛰지 않음).

--segments: 문장 단위 세그먼트 패스 (en, kokoro 전용). 숫자 슬롯이 많은 멘트를
문장별 클립({md5}_seg1.wav)으로 만들어 두면 런타임이 이어 붙여 재생한다
(radio_assemble.py / RadioAssembler.cs). 끝나면 조립 자산 4개(_seg1_*.wav)도 쓴다.

--ab: 한 멘트를 통문장 렌더와 세그먼트 조립본으로 각각 만들어 귀로 비교.

--shard K/N: 대상 목록(정렬된 통문장 / 단계별 세그먼트)을 N등분해 K번째만 렌더
(i % N == K-1). CI가 N개 잡으로 나눠 렌더하고 캐시를 합친다 — 합집합은 샤드 없는
실행과 같다 (replay-check가 --dry-run으로 대조). 조립 자산은 결정적이라 샤드마다 같다.

설정(config.yaml)의 tts 엔진/보이스를 그대로 사용한다. 보이스나 말 속도를
바꾸면 다시 실행할 것. 이미 캐시된 파일은 건너뛴다.

사용법: python tools/pregen_audio.py [--config config.yaml] [--engine kokoro]
        python tools/pregen_audio.py --segments [--tiers 1,2,3] [--tracks voice_lines/track_names_en.txt]
        python tools/pregen_audio.py --engine kokoro [--segments ...] --shard 2/6
        python tools/pregen_audio.py --ab "Gap behind 6.3 seconds. Closing 1.3 a lap." --tone casual --ab-out ab
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
import math
import os
import shutil
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from config import load_config                                # noqa: E402
from tts import KokoroEngine, build_pregen_engine             # noqa: E402
from voice import (PhrasePool, iter_pregen_sentences,         # noqa: E402
                   iter_pregen_texts, lines_file, load_track_names)


def _parse_shard(spec):
    """'K/N' → (K, N). 1 <= K <= N."""
    try:
        k, n = (int(v) for v in spec.split("/"))
    except ValueError:
        raise SystemExit(f"--shard 형식은 K/N (예: 2/6): {spec}")
    if not 1 <= k <= n:
        raise SystemExit(f"--shard는 1 <= K <= N: {spec}")
    return k, n


def _take_shard(items: list, shard) -> list:
    if shard is None:
        return items
    k, n = shard
    return items[k - 1::n]


def _whole_pass(cfg, dry_run: bool, shard=None) -> int:
    # 멘트 언어에 맞는 슬롯/풀로 열거 (en/ko)
    from messages import set_language
    set_language(cfg.get("voice.language", "en"))
    pool = PhrasePool(lines_file(cfg.get("voice.language", "en")))
    texts = sorted(set(iter_pregen_texts(pool)))
    print(f"사전 캐시 대상: {len(texts)}개 멘트 (톤별)")
    if shard is not None:
        texts = _take_shard(texts, shard)
        print(f"샤드 {shard[0]}/{shard[1]}: {len(texts)}개")

    if dry_run:
        for tone, t in texts:
            print(f"  [{tone}] {t}")
        return 0

    engine = build_pregen_engine(cfg)
    ok = skip = fail = 0
    t0 = time.time()
    for i, (tone, text) in enumerate(texts, 1):
        # 엔진의 synth가 캐시를 확인하므로 여기선 존재 여부만 미리 보고용으로 체크
        path = engine.synth(text, tone)
        if path is None:
            fail += 1
            print(f"[{i}/{len(texts)}] 실패: [{tone}] {text}")
        elif os.path.getmtime(path) < t0:
            skip += 1
        else:
            ok += 1
            print(f"[{i}/{len(texts)}] [{tone}] {text}")
    print(f"완료: 신규 {ok}, 기존 {skip}, 실패 {fail}")
    return 1 if fail else 0


def _kokoro(cfg, cache_dir: str):
    """세그먼트/AB 렌더용 kokoro. 모델이 없으면 None."""
    engine = KokoroEngine(cache_dir, cfg.get("tts.kokoro_voice", "bm_george"))
    if engine._load() is None:
        print("❌ kokoro-onnx를 불러올 수 없음 — pip install kokoro-onnx soundfile")
        return None
    return engine


def _render_segment(kokoro, sentence: str, tone: str, dst: str,
                    noise: float):
    """문장 하나 → 세그먼트 파일. 게인(성공) 또는 None."""
    import radiofx
    rendered = kokoro.render(sentence, tone, radiofx.SEG_SPEED)
    if rendered is None:
        return None
    samples, sr = rendered
    return radiofx.process_segment(samples, sr, dst, noise=noise)


def _wav_seconds(path: str) -> float:
    import soundfile as sf
    try:
        info = sf.info(path)
        return info.frames / float(info.samplerate)
    except Exception:
        return 0.0


def _percentile(values: list, pct: float) -> float:
    v = sorted(values)
    k = (len(v) - 1) * pct / 100.0
    lo, hi = math.floor(k), math.ceil(k)
    return v[lo] + (v[hi] - v[lo]) * (k - lo)


def _db(x: float) -> float:
    return 20.0 * math.log10(max(x, 1e-9))


def _segment_pass(cfg, tiers: tuple, tracks: tuple, dry_run: bool,
                  shard=None) -> int:
    import radiofx
    from radio_assemble import segment_file_name

    # 세그먼트 팩은 영어 전용 (ko는 edge 합성)
    from messages import set_language
    set_language("en")
    cache_dir = cfg.get("tts.cache_dir", "audio_cache")
    voice = cfg.get("tts.kokoro_voice", "bm_george")
    noise = cfg.get("tts.radio_noise", 0.004)

    # 단계별로 나눠 집계 — 앞 단계에 이미 있는 문장은 뒤 단계에서 뺀다
    seen: set = set()
    per_tier = []
    for tier in sorted(tiers):
        items = sorted(set(iter_pregen_sentences((tier,), tracks)) - seen)
        seen.update(items)
        print(f"{tier}단계 문장: {len(items)}개")
        # 단계 중복 제거는 전체 목록으로 한 뒤 나눈다 — 샤드 합집합 = 전체
        if shard is not None:
            items = _take_shard(items, shard)
            print(f"{tier}단계 샤드 {shard[0]}/{shard[1]}: {len(items)}개")
        per_tier.append((tier, items))
    if dry_run:
        for tier, items in per_tier:
            for tone, s in items:
                print(f"  {tier} [{tone}] {s}")
        return 0

    os.makedirs(cache_dir, exist_ok=True)
    kokoro = None
    gains: list = []
    fail = 0
    for tier, items in per_tier:
        new = 0
        secs = 0.0
        size = 0
        for i, (tone, s) in enumerate(items, 1):
            dst = os.path.join(cache_dir, segment_file_name(s, tone, voice))
            if not os.path.exists(dst):
                if kokoro is None:
                    kokoro = _kokoro(cfg, cache_dir)
                    if kokoro is None:
                        return 1
                gain = _render_segment(kokoro, s, tone, dst, noise)
                if gain is None:
                    fail += 1
                    print(f"[{tier}단계 {i}/{len(items)}] 실패: [{tone}] {s}")
                    continue
                gains.append(gain)
                new += 1
                print(f"[{tier}단계 {i}/{len(items)}] [{tone}] {s}")
            secs += _wav_seconds(dst)
            size += os.path.getsize(dst)
        print(f"{tier}단계: 클립 {len(items)}개 (신규 {new}), "
              f"{secs:.0f}초, {size / 1048576:.1f} MB")

    if not radiofx.write_segment_assets(cache_dir, noise=noise):
        print("❌ 조립 자산(_seg1_*.wav) 저장 실패")
        return 1
    print("조립 자산 4개 저장")

    if gains:
        med = _percentile(gains, 50)
        p90 = _percentile(gains, 90)
        print(f"세그먼트 게인: 중앙값 {med:.3f} ({_db(med):+.1f} dB), "
              f"90퍼센타일 {p90:.3f} ({_db(p90):+.1f} dB) — 자산 게인 {radiofx.ASSET_GAIN}")
        # 자산(클릭/간격/스켈치) 레벨이 문장 레벨과 어긋나면 이음새가 들린다
        if abs(_db(med) - _db(radiofx.ASSET_GAIN)) > 2.0:
            print(f"⚠️  게인 중앙값이 ASSET_GAIN ±2 dB 밖 — radiofx.ASSET_GAIN/SEG_PEAK 조정 검토")
    print(f"완료: 실패 {fail}")
    return 1 if fail else 0


def _ab(cfg, message: str, tone: str, out_dir: str) -> int:
    """같은 멘트를 통문장(_rfx) / 세그먼트 조립본으로 — 청취 비교용."""
    import radiofx
    import radio_assemble as ra
    from messages import set_language, split_sentences
    set_language("en")
    noise = cfg.get("tts.radio_noise", 0.004)
    voice = cfg.get("tts.kokoro_voice", "bm_george")
    os.makedirs(out_dir, exist_ok=True)

    with tempfile.TemporaryDirectory() as tmp:
        whole_dir = os.path.join(tmp, "whole")
        seg_dir = os.path.join(tmp, "seg")
        os.makedirs(whole_dir)
        os.makedirs(seg_dir)

        tts_cfg = cfg._data.setdefault("tts", {})
        tts_cfg.update({"engine": "kokoro", "cache_dir": whole_dir,
                        "radio_fx": True})
        cfg._data.setdefault("voice", {})["language"] = "en"
        path = build_pregen_engine(cfg).synth(message, tone)
        if path is None or not path.endswith(f"_rfx{radiofx.VERSION}.wav"):
            print("❌ 통문장 렌더 실패 (kokoro-onnx/numpy/soundfile 확인)")
            return 1
        whole_out = os.path.join(out_dir, f"whole_rfx{radiofx.VERSION}.wav")
        shutil.copyfile(path, whole_out)

        kokoro = _kokoro(cfg, seg_dir)
        if kokoro is None:
            return 1
        paths = []
        for s in split_sentences(message):
            dst = os.path.join(seg_dir, ra.segment_file_name(s, tone, voice))
            if not os.path.exists(dst) and \
                    _render_segment(kokoro, s, tone, dst, noise) is None:
                print(f"❌ 세그먼트 렌더 실패: {s}")
                return 1
            paths.append(dst)
        if not radiofx.write_segment_assets(seg_dir, noise=noise):
            print("❌ 조립 자산 저장 실패")
            return 1
        data = ra.build(seg_dir, tone, paths)
        if data is None:
            print("❌ 조립 실패")
            return 1
        asm_out = os.path.join(out_dir, "assembled.wav")
        with open(asm_out, "wb") as f:
            f.write(data)

    print(f"통문장: {whole_out} ({_wav_seconds(whole_out):.2f}초)")
    print(f"조립본: {asm_out} ({_wav_seconds(asm_out):.2f}초, 문장 {len(paths)}개)")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default="config.yaml")
    parser.add_argument("--engine", choices=("edge", "elevenlabs", "kokoro"),
                        help="config.yaml의 tts.engine을 이 실행에서만 덮어씀 "
                             "(배포용 캐시는 --engine kokoro)")
    parser.add_argument("--dry-run", action="store_true", help="합성 없이 대상 텍스트만 출력")
    parser.add_argument("--segments", action="store_true",
                        help="문장 세그먼트 패스 (en, kokoro) + 조립 자산")
    parser.add_argument("--tiers", default="1,2,3",
                        help="--segments 문장 단계 목록 (예: 1 또는 1,2)")
    parser.add_argument("--tracks", default=None,
                        help="--segments 트랙 이름 목록 파일 (2단계 세션 브리핑용)")
    parser.add_argument("--ab", metavar="MESSAGE", default=None,
                        help="이 멘트를 통문장/조립본 두 가지로 렌더해 비교")
    parser.add_argument("--tone", default="casual", choices=("casual", "urgent"),
                        help="--ab 톤")
    parser.add_argument("--ab-out", default="ab_out", help="--ab 출력 폴더")
    parser.add_argument("--shard", metavar="K/N", default=None,
                        help="대상 목록을 N등분해 K번째만 (CI 병렬 렌더)")
    args = parser.parse_args()
    shard = _parse_shard(args.shard) if args.shard else None

    cfg = load_config(args.config)
    if args.engine:
        cfg._data.setdefault("tts", {})["engine"] = args.engine
        print(f"TTS 엔진 덮어씀: {args.engine}")

    if args.ab is not None:
        return _ab(cfg, args.ab, args.tone, args.ab_out)
    if args.segments:
        tiers = tuple(int(t) for t in args.tiers.split(",") if t.strip())
        tracks: tuple = ()
        if args.tracks and os.path.exists(args.tracks):
            tracks = tuple(load_track_names(args.tracks))
        elif args.tracks:
            print(f"⚠️  트랙 목록 없음: {args.tracks} — 트랙 이름 문장 없이 진행")
        return _segment_pass(cfg, tiers, tracks, args.dry_run, shard)
    return _whole_pass(cfg, args.dry_run, shard)


if __name__ == "__main__":
    sys.exit(main())
