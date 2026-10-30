"""
en 코코로 음성 팩 도구 — audio_cache → 독립 zip 여러 개 → voice_lines/audio_pack.txt.

설정 화면 [음성 팩 업데이트]가 받는 zip을 만들고 검증한다 (audio-pack 워크플로,
릴리스 게이트, replay-check 셀프테스트가 쓴다).

  build      캐시에서 팩에 들어갈 파일만 골라 크기 한도로 나눠 zip, 기술자 갱신
  verify     zip 폴더를 기술자(이름·sha256·크기)와 대조, 루트 *.wav만인지 확인
  expected   팩에 들어가야 할 파일 이름 집합 (게이트와 같은 열거기)
  descriptor 기술자 해석 결과를 셸에서 읽기 좋게 출력
  selftest   합성 wav로 분할·이름·기술자 갱신·verify 거부 규칙 확인

verify / descriptor / selftest는 표준 라이브러리만 쓴다 (python3 -I로 돌릴 수 있게
저장소 모듈은 expected/build에서만 불러온다).

사용법: python tools/pack_audio.py build --cache audio_cache --pack kokoro-v016 --out dist/audio/kokoro-v016
                                       [--descriptor voice_lines/audio_pack.txt --tag audio-kokoro-v016]
        python tools/pack_audio.py verify --dir DIR --descriptor FILE [--expect-names FILE]
        python tools/pack_audio.py expected --out FILE
        python tools/pack_audio.py descriptor --print FILE
        python tools/pack_audio.py selftest
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import sys
import tempfile
import zipfile

# zip 하나의 비압축 합계 한도 — 수동 배포본(v015)과 같은 분할을 재현한다
MAX_BYTES = 24 * 1024 * 1024
# Core ReleaseManifest.IsSafeName과 같은 규칙
SAFE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
# 이미 배포된 팩 ID → 그 팩의 파일 이름 집합 sha256 (정렬, LF 연결, 끝 LF).
# 같은 ID로 다른 집합을 내면 표식이 같은 사용자는 새 파일을 영영 못 받는다.
KNOWN_NAME_SETS = {
    "kokoro-v015": "2062e3431c563f16b10425be7d3473401a1628096d97057e3ed9dbf24ebd5fda",
}
DEFAULT_TRACKS = "voice_lines/track_names_en.txt"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


class PackError(Exception):
    pass


def is_safe_name(s: str) -> bool:
    return bool(s) and SAFE_NAME.match(s) is not None and ".." not in s


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def name_set_sha(names) -> str:
    return hashlib.sha256(("\n".join(sorted(names)) + "\n").encode("utf-8")).hexdigest()


def zip_names(pack: str, n: int) -> list:
    stem = pack.replace("-", "_").replace(".", "_")
    return [f"teamradio56_{stem}_{i}of{n}.zip" for i in range(1, n + 1)]


# ── 기대 집합 ────────────────────────────────────────────────────────────

def _repo_imports():
    if ROOT not in sys.path:
        sys.path.insert(0, ROOT)


def asset_names() -> tuple:
    _repo_imports()
    from radio_assemble import ASSET_NAMES
    return tuple(ASSET_NAMES)


def expected_names(tiers=(1, 2, 3), tracks: str = DEFAULT_TRACKS, voice: str = "bm_george") -> set:
    """런타임이 팩에서 찾는 파일 전체 — 통문장 _rfx + 문장 세그먼트 + 조립 자산."""
    _repo_imports()
    import radiofx
    from messages import set_language
    from radio_assemble import ASSET_NAMES, segment_file_name
    from tts import _cache_path
    from voice import (PhrasePool, iter_pregen_sentences, iter_pregen_texts,
                       lines_file, load_track_names)

    set_language("en")
    names = set()
    for tone, text in set(iter_pregen_texts(PhrasePool(lines_file("en")))):
        raw = os.path.basename(_cache_path("", f"kokoro|{voice}|{tone}|{text}", ext="wav"))
        names.add(os.path.splitext(raw)[0] + f"_rfx{radiofx.VERSION}.wav")
    track_names = tuple(load_track_names(tracks)) if tracks else ()
    for tone, s in set(iter_pregen_sentences(tuple(tiers), track_names)):
        names.add(segment_file_name(s, tone, voice))
    names.update(ASSET_NAMES)
    return names


# ── 분할 ─────────────────────────────────────────────────────────────────

def order_names(names, first) -> list:
    """조립 자산을 맨 앞(정렬), 나머지는 서수 정렬 — 수동 배포본과 같은 순서."""
    head = sorted(n for n in names if n in first)
    return head + sorted(n for n in names if n not in first)


def split_sizes(items, max_bytes: int) -> list:
    """(이름, 크기) 순서대로 탐욕 분할 — 다음 파일이 한도를 넘기면 새 zip."""
    parts, cur, total = [], [], 0
    for name, size in items:
        if size > max_bytes:
            raise PackError(f"파일 하나가 한도({max_bytes}B)보다 큼: {name} ({size}B)")
        if cur and total + size > max_bytes:
            parts.append(cur)
            cur, total = [], 0
        cur.append(name)
        total += size
    if cur:
        parts.append(cur)
    return parts


def _check_zip_entries(path: str, label: str) -> list:
    """루트 *.wav만 있는지 — 플러그인은 zip 루트의 *.wav만 푼다."""
    out = []
    with zipfile.ZipFile(path) as zf:
        bad = zf.testzip()
        if bad is not None:
            raise PackError(f"{label}: CRC 오류 {bad}")
        for info in zf.infolist():
            n = info.filename
            if info.is_dir() or "/" in n or "\\" in n or not n.lower().endswith(".wav"):
                raise PackError(f"{label}: 루트 *.wav가 아닌 항목 {n}")
            out.append(n)
    return out


def build_pack(cache: str, pack: str, out: str, expected, max_bytes: int = MAX_BYTES,
               first=()) -> list:
    """zip을 쓰고 [(zip 이름, sha256, 크기)]를 돌려준다."""
    if not is_safe_name(pack):
        raise PackError(f"팩 ID 형식이 잘못됨: {pack!r}")
    expected = set(expected)
    if not expected:
        raise PackError("팩에 넣을 파일이 없음")
    known = KNOWN_NAME_SETS.get(pack)
    if known is not None and name_set_sha(expected) != known:
        raise PackError(f"이 팩 ID({pack})는 다른 멘트 집합으로 이미 배포됨 — 새 팩 ID를 쓰세요")
    if os.path.isdir(out) and os.listdir(out):
        raise PackError(f"출력 폴더가 비어 있지 않음: {out}")

    missing = sorted(n for n in expected if not os.path.isfile(os.path.join(cache, n)))
    if missing:
        shown = "\n".join("  " + n for n in missing[:20])
        raise PackError(f"캐시에 없는 팩 파일 {len(missing)}개 (렌더를 먼저):\n{shown}")
    try:
        others = sum(1 for n in os.listdir(cache) if n not in expected)
    except OSError:
        others = 0

    ordered = order_names(expected, set(first))
    sizes = [(n, os.path.getsize(os.path.join(cache, n))) for n in ordered]
    parts = split_sizes(sizes, max_bytes)
    names = zip_names(pack, len(parts))
    os.makedirs(out, exist_ok=True)

    assets = []
    seen = set()
    for zname, members in zip(names, parts):
        zpath = os.path.join(out, zname)
        with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as zf:
            for n in members:
                # 항목 시각 = 원본 mtime — 손으로 푼 파이썬 사용자의 조립 캐시 갱신 판정에 쓰인다
                zf.write(os.path.join(cache, n), n)
        entries = _check_zip_entries(zpath, zname)
        if entries != members:
            raise PackError(f"{zname}: 항목이 기대와 다름")
        dup = seen.intersection(entries)
        if dup:
            raise PackError(f"zip 사이 중복 이름: {sorted(dup)[:5]}")
        seen.update(entries)
        size = os.path.getsize(zpath)
        if size > max_bytes:
            raise PackError(f"{zname}: zip 크기 {size}B가 한도 {max_bytes}B를 넘음")
        assets.append((zname, sha256_file(zpath), size))
    if seen != expected:
        raise PackError("zip 합집합이 기대 집합과 다름")
    print(f"팩 {pack}: 파일 {len(expected)}개 → zip {len(assets)}개 "
          f"({sum(a[2] for a in assets) / 1048576:.1f} MB), 팩 밖 캐시 파일 {others}개 무시",
          file=sys.stderr)
    return assets


# ── 기술자 ───────────────────────────────────────────────────────────────

def _read_text(path: str) -> str:
    with open(path, "rb") as f:
        data = f.read()
    text = data.decode("utf-8")
    return text[1:] if text.startswith("﻿") else text


def parse_descriptor(text: str) -> dict:
    """release.ps1·ReleaseCheck와 같은 규칙."""
    d = {"pack": "", "tag": "", "assets": []}
    seen = set()
    for raw in text.lstrip("﻿").split("\n"):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        eq = line.find("=")
        if eq <= 0:
            raise PackError(f"audio_pack.txt 줄 형식이 잘못됨: {line}")
        key, val = line[:eq].strip(), line[eq + 1:].strip()
        if key == "pack":
            d["pack"] = val
        elif key == "tag":
            d["tag"] = val
        elif key == "asset":
            parts = val.split(",")
            if len(parts) != 3:
                raise PackError(f"asset 줄은 이름,sha256,크기: {line}")
            name, sha, size = parts[0].strip(), parts[1].strip().lower(), parts[2].strip()
            if not is_safe_name(name) or not name.lower().endswith(".zip"):
                raise PackError(f"asset 이름이 잘못됨: {name}")
            if not re.fullmatch(r"[0-9a-f]{64}", sha):
                raise PackError(f"asset sha256이 잘못됨: {name}")
            if not re.fullmatch(r"[0-9]+", size) or int(size) <= 0:
                raise PackError(f"asset 크기가 잘못됨: {name}")
            if name in seen:
                raise PackError(f"asset 이름 중복: {name}")
            seen.add(name)
            d["assets"].append((name, sha, int(size)))
        else:
            raise PackError(f"audio_pack.txt에 모르는 키: {key}")
    if not is_safe_name(d["pack"]):
        raise PackError(f"pack이 비었거나 잘못됨: {d['pack']!r}")
    if not is_safe_name(d["tag"]):
        raise PackError(f"tag가 비었거나 잘못됨: {d['tag']!r}")
    if not d["assets"]:
        raise PackError("asset 줄이 없음")
    return d


def rewrite_descriptor(text: str, pack: str, tag: str, assets) -> str:
    """pack/tag/asset 줄만 바꾸고 주석·빈 줄은 그대로 둔다 (asset 묶음은 첫 asset 자리)."""
    parse_descriptor(text)  # 모르는 키 등은 여기서 거부
    out = []
    placed = False
    for raw in text.lstrip("﻿").replace("\r\n", "\n").split("\n"):
        line = raw.strip()
        key = line.split("=", 1)[0].strip() if "=" in line and not line.startswith("#") else None
        if key == "pack":
            out.append(f"pack={pack}")
        elif key == "tag":
            out.append(f"tag={tag}")
        elif key == "asset":
            if not placed:
                out.extend(f"asset={n},{s},{z}" for n, s, z in assets)
                placed = True
        else:
            out.append(raw)
    result = "\n".join(out)
    if not result.endswith("\n"):
        result += "\n"
    return result


def write_atomic(path: str, text: str) -> None:
    d = os.path.dirname(os.path.abspath(path))
    fd, tmp = tempfile.mkstemp(dir=d, prefix=".pack_audio-")
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


# ── 검증 ─────────────────────────────────────────────────────────────────

def verify_dir(folder: str, desc: dict, expect=None) -> list:
    """오류 목록을 던지고, 경고 목록을 돌려준다."""
    seen = set()
    for name, sha, size in desc["assets"]:
        p = os.path.join(folder, name)
        if not os.path.isfile(p):
            raise PackError(f"zip이 없음: {name}")
        if os.path.getsize(p) != size:
            raise PackError(f"크기가 기술자와 다름: {name} ({os.path.getsize(p)} ≠ {size})")
        if sha256_file(p) != sha:
            raise PackError(f"sha256이 기술자와 다름: {name}")
        entries = _check_zip_entries(p, name)
        dup = seen.intersection(entries)
        if dup or len(set(entries)) != len(entries):
            raise PackError(f"{name}: 중복 이름 {sorted(dup)[:5]}")
        seen.update(entries)
    warnings = []
    if expect is not None:
        missing = sorted(set(expect) - seen)
        if missing:
            shown = "\n".join("  " + n for n in missing[:20])
            raise PackError(f"멘트 집합이 팩과 다름 — 팩에 없는 파일 {len(missing)}개 "
                            f"(새 pack ID로 audio-pack 실행):\n{shown}")
        extra = len(seen - set(expect))
        if extra:
            warnings.append(f"팩에만 있는 파일 {extra}개 (지금 코드는 쓰지 않음)")
    print(f"팩 {desc['pack']} ({desc['tag']}): zip {len(desc['assets'])}개, wav {len(seen)}개 확인",
          file=sys.stderr)
    return warnings


def _read_names(path: str) -> set:
    return {ln.strip() for ln in _read_text(path).split("\n") if ln.strip()}


# ── 셀프테스트 ───────────────────────────────────────────────────────────

def _wav(path: str, size: int) -> None:
    """크기가 정확히 size인 가짜 wav (내용만 다르게)."""
    head = b"RIFF" + os.path.basename(path).encode()
    with open(path, "wb") as f:
        f.write((head * (size // len(head) + 1))[:size])


def selftest() -> int:
    fails = []
    passed = 0

    def check(cond, what):
        nonlocal passed
        if cond:
            passed += 1
        else:
            fails.append(what)

    def raises(fn, what):
        nonlocal passed
        try:
            fn()
        except PackError:
            passed += 1
        else:
            fails.append("거부 기대: " + what)

    # 분할 경계: 한도에 딱 맞으면 한 zip, 1바이트 넘으면 새 zip
    check(split_sizes([("a", 60), ("b", 40)], 100) == [["a", "b"]], "합계 = 한도면 한 zip")
    check(split_sizes([("a", 60), ("b", 41)], 100) == [["a"], ["b"]], "한도 + 1이면 새 zip")
    check(split_sizes([("a", 100), ("b", 1)], 100) == [["a"], ["b"]], "한도 크기 파일 하나")
    raises(lambda: split_sizes([("a", 101)], 100), "한도보다 큰 파일")
    check(order_names({"b.wav", "_seg1_tail.wav", "a.wav", "_seg1_click.wav"},
                      {"_seg1_click.wav", "_seg1_tail.wav"})
          == ["_seg1_click.wav", "_seg1_tail.wav", "a.wav", "b.wav"], "조립 자산 먼저, 나머지 정렬")
    check(zip_names("kokoro-v015", 4)[0] == "teamradio56_kokoro_v015_1of4.zip", "zip 이름 규칙 (v015)")
    check(zip_names("a.b-c", 2) == ["teamradio56_a_b_c_1of2.zip", "teamradio56_a_b_c_2of2.zip"],
          "zip 이름 규칙 (. - → _)")

    with tempfile.TemporaryDirectory() as tmp:
        cache = os.path.join(tmp, "cache")
        os.makedirs(cache)
        first = ("_seg1_click.wav", "_seg1_tail.wav")
        expected = {"_seg1_click.wav": 10, "_seg1_tail.wav": 10, "aa_rfx3.wav": 300,
                    "bb_seg1.wav": 400, "cc_seg1.wav": 500}
        for n, s in expected.items():
            _wav(os.path.join(cache, n), s)
        _wav(os.path.join(cache, "aa.wav"), 50)        # 팩 밖 (raw) — 무시
        out = os.path.join(tmp, "out")

        raises(lambda: build_pack(cache, "bad id", out, expected, 2048, first), "공백 든 팩 ID")
        raises(lambda: build_pack(cache, "x..y", out, expected, 2048, first), "'..' 든 팩 ID")
        raises(lambda: build_pack(cache, "kokoro-v015", out, expected, 2048, first),
               "배포된 팩 ID에 다른 집합")
        raises(lambda: build_pack(cache, "p1", out, set(expected) | {"zz_seg1.wav"}, 2048, first),
               "캐시에 없는 파일")

        assets = build_pack(cache, "p1", out, expected, 720, first)
        check([a[0] for a in assets] == zip_names("p1", 2), "한도 720 → zip 2개")
        with zipfile.ZipFile(os.path.join(out, assets[0][0])) as zf:
            check(zf.namelist() == ["_seg1_click.wav", "_seg1_tail.wav", "aa_rfx3.wav", "bb_seg1.wav"],
                  "첫 zip 항목·순서")
            mt = os.path.getmtime(os.path.join(cache, "aa_rfx3.wav"))
            import time as _t
            check(zf.getinfo("aa_rfx3.wav").date_time[:5] == _t.localtime(mt)[:5],
                  "항목 시각 = 원본 mtime")
        raises(lambda: build_pack(cache, "p1", out, expected, 720, first), "비어 있지 않은 출력 폴더")

        desc_text = ("﻿# 머리 주석\r\n\r\npack=old\r\ntag=v0.1.0\r\n# 자산\r\n"
                     "asset=x_1of2.zip," + "a" * 64 + ",5\r\nasset=x_2of2.zip," + "b" * 64 + ",6\r\n"
                     "# 끝 주석\r\n")
        new_text = rewrite_descriptor(desc_text, "p1", "audio-p1", assets)
        check(not new_text.startswith("﻿") and "\r" not in new_text, "기술자: BOM·CRLF 제거")
        check(new_text.splitlines()[:5] == ["# 머리 주석", "", "pack=p1", "tag=audio-p1", "# 자산"],
              "기술자: 주석·순서 유지")
        check(new_text.rstrip("\n").splitlines()[-1] == "# 끝 주석", "기술자: 끝 주석 유지")
        d = parse_descriptor(new_text)
        check(d["pack"] == "p1" and d["tag"] == "audio-p1" and d["assets"] == assets,
              "기술자: 새 asset 줄")
        raises(lambda: rewrite_descriptor("pack=a\ntag=b\nasset=x.zip," + "a" * 64 + ",1\nfoo=1\n",
                                          "p", "t", assets), "모르는 키")
        dpath = os.path.join(tmp, "audio_pack.txt")
        write_atomic(dpath, new_text)
        check(_read_text(dpath) == new_text, "기술자 원자적 쓰기")

        check(verify_dir(out, d, set(expected)) == [], "verify 통과")
        check(len(verify_dir(out, d, set(expected) - {"cc_seg1.wav"})) == 1, "verify 남는 이름은 경고")
        raises(lambda: verify_dir(out, d, set(expected) | {"zz_seg1.wav"}), "verify 빠진 이름")

        def variant(label, entries=None, desc_fix=None):
            vdir = os.path.join(tmp, "v_" + label)
            os.makedirs(vdir)
            name = "teamradio56_v_1of1.zip"
            zpath = os.path.join(vdir, name)
            with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as zf:
                for n in entries or ["a.wav"]:
                    if n.endswith("/"):
                        zf.writestr(zipfile.ZipInfo(n), b"")
                    else:
                        zf.writestr(n, b"RIFF" + n.encode())
            vd = {"pack": "v", "tag": "audio-v",
                  "assets": [(name, sha256_file(zpath), os.path.getsize(zpath))]}
            if desc_fix:
                desc_fix(vd)
            return vdir, vd

        for label, entries in (("dir", ["a.wav", "sub/"]), ("nonwav", ["a.wav", "b.txt"]),
                               ("nested", ["a.wav", "sub/b.wav"])):
            vdir, vd = variant(label, entries)
            raises(lambda vdir=vdir, vd=vd: verify_dir(vdir, vd), "verify 거부: " + label)
        vdir, vd = variant("sha", desc_fix=lambda x: x.__setitem__(
            "assets", [(x["assets"][0][0], "0" * 64, x["assets"][0][2])]))
        raises(lambda: verify_dir(vdir, vd), "verify 거부: sha 불일치")
        vdir, vd = variant("size", desc_fix=lambda x: x.__setitem__(
            "assets", [(x["assets"][0][0], x["assets"][0][1], x["assets"][0][2] + 1)]))
        raises(lambda: verify_dir(vdir, vd), "verify 거부: 크기 불일치")
        dup_dir = os.path.join(tmp, "dup")
        os.makedirs(dup_dir)
        dup_assets = []
        for i in (1, 2):
            zp = os.path.join(dup_dir, f"d_{i}of2.zip")
            with zipfile.ZipFile(zp, "w") as zf:
                zf.writestr("same.wav", b"RIFF" + bytes([i]))
            dup_assets.append((os.path.basename(zp), sha256_file(zp), os.path.getsize(zp)))
        raises(lambda: verify_dir(dup_dir, {"pack": "d", "tag": "audio-d", "assets": dup_assets}),
               "verify 거부: zip 사이 중복")

    if fails:
        print(f"❌ 음성 팩 도구 셀프테스트 실패 {len(fails)}건 (통과 {passed}건)")
        for f in fails:
            print("   - " + f)
        return 1
    print(f"✅ 음성 팩 도구 셀프테스트 {passed}건 통과")
    return 0


# ── CLI ──────────────────────────────────────────────────────────────────

def _tiers(s: str) -> tuple:
    return tuple(int(t) for t in s.split(",") if t.strip())


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description="en 코코로 음성 팩 도구")
    sub = p.add_subparsers(dest="cmd", required=True)

    b = sub.add_parser("build")
    b.add_argument("--cache", required=True)
    b.add_argument("--pack", required=True)
    b.add_argument("--out", required=True)
    b.add_argument("--max-bytes", type=int, default=MAX_BYTES)
    b.add_argument("--tiers", default="1,2,3")
    b.add_argument("--tracks", default=DEFAULT_TRACKS)
    b.add_argument("--descriptor", default=None)
    b.add_argument("--tag", default=None)

    v = sub.add_parser("verify")
    v.add_argument("--dir", required=True)
    v.add_argument("--descriptor", required=True)
    v.add_argument("--expect-names", default=None)

    e = sub.add_parser("expected")
    e.add_argument("--tiers", default="1,2,3")
    e.add_argument("--tracks", default=DEFAULT_TRACKS)
    e.add_argument("--out", required=True)

    d = sub.add_parser("descriptor")
    d.add_argument("--print", dest="path", required=True, metavar="FILE")

    sub.add_parser("selftest")
    args = p.parse_args(argv)

    try:
        if args.cmd == "selftest":
            return selftest()
        if args.cmd == "descriptor":
            desc = parse_descriptor(_read_text(args.path))
            print(f"PACK={desc['pack']}")
            print(f"TAG={desc['tag']}")
            print("ASSETS=" + " ".join(f"{n}:{s}:{z}" for n, s, z in desc["assets"]))
            return 0
        if args.cmd == "verify":
            desc = parse_descriptor(_read_text(args.descriptor))
            expect = _read_names(args.expect_names) if args.expect_names else None
            for w in verify_dir(args.dir, desc, expect):
                print("⚠️  " + w)
            print("✅ 음성 팩 zip 확인 통과")
            return 0
        if args.cmd == "expected":
            names = expected_names(_tiers(args.tiers), args.tracks)
            with open(args.out, "w", encoding="utf-8", newline="\n") as f:
                f.write("\n".join(sorted(names)) + "\n")
            print(f"팩 파일 이름 {len(names)}개 → {args.out} (집합 sha256 {name_set_sha(names)})",
                  file=sys.stderr)
            return 0
        # build
        if (args.descriptor is None) != (args.tag is None):
            raise PackError("--descriptor와 --tag는 함께 준다")
        if args.tag is not None and not is_safe_name(args.tag):
            raise PackError(f"태그 형식이 잘못됨: {args.tag!r}")
        old = _read_text(args.descriptor) if args.descriptor else None
        if old is not None:
            parse_descriptor(old)  # 렌더 결과를 쓰기 전에 기술자부터 거부
        names = expected_names(_tiers(args.tiers), args.tracks)
        assets = build_pack(args.cache, args.pack, args.out, names, args.max_bytes, asset_names())
        for n, s, z in assets:
            print(f"asset={n},{s},{z}")
        if old is not None:
            write_atomic(args.descriptor, rewrite_descriptor(old, args.pack, args.tag, assets))
            print(f"기술자 갱신: {args.descriptor} (pack={args.pack}, tag={args.tag})", file=sys.stderr)
        return 0
    except PackError as ex:
        print(f"❌ {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
