"""
한 목소리 정책 셀프테스트 (파이썬) — C# --voice-selftest와 짝.

세션 목소리로 낼 수 없는 콜이 다른 목소리(edge)로 새지 않고 무음(자막만)이
되는지, LLM 멘트가 임의 문장을 말할 수 없는 목소리에서 호출되지 않는지 확인한다.
네트워크·pygame·실제 edge_tts/kokoro 없이 돈다 — 가짜 edge_tts.Communicate와
kokoro_onnx.Kokoro를 sys.modules에 끼우고, 임시 폴더(가짜 models/ 포함)에서 실행.

사용법:
  python tools/voice_policy_selftest.py                  # 셀프테스트
  python tools/voice_policy_selftest.py --dump-table --out f
      세션 목소리 결정표 (engine lang pack kind note) — C# --dump-voice-table과 diff
  python tools/voice_policy_selftest.py --pack-fixtures DIR
      팩 상태 픽스처 폴더들 + expected.tsv — C# --pack-state-check DIR로 대조

numpy/soundfile이 필요한 케이스는 없으면 건너뛰고, TEAMRADIO56_STRICT=1이면 실패.
종료 코드: 0 = 통과, 1 = 실패.
"""

from __future__ import annotations

import argparse
import copy
import logging
import os
import struct
import sys
import tempfile
import types

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)

import config            # noqa: E402
import radio_assemble    # noqa: E402
import radiofx           # noqa: E402
import tts               # noqa: E402
from events import Event, EventType, Priority   # noqa: E402
from messages import set_language, split_sentences   # noqa: E402

EDGE_VOICE = "en-GB-RyanNeural"
VOICE = "bm_george"
TEXT_A = "Box box, box box."
TEXT_B = "Alpha one check. Bravo two check."
TEXT_C = "Charlie raw only."
TEXT_D = "Delta edge only."
TEXT_E = "Echo nothing here."

ENGINES = ("kokoro", "edge")
LANGS = ("en", "ko")
PACKS = ("ok", "partial", "incomplete", "none")


# -- 가짜 모듈 ----------------------------------------------------------------

class FakeEdgeState:
    calls = 0
    fail = False


class FakeCommunicate:
    def __init__(self, text, voice, rate=None, pitch=None):
        self.text = text

    async def save(self, path):
        FakeEdgeState.calls += 1
        if FakeEdgeState.fail:
            raise OSError("offline (fake)")
        with open(path, "wb") as f:
            f.write(b"ID3fake")


class FakeKokoro:
    created = 0

    def __init__(self, model, voices):
        pass

    def create(self, text, voice, speed, lang):
        import numpy as np
        FakeKokoro.created += 1
        return np.zeros(2400, dtype=np.float32), 24000


def install_fakes() -> None:
    edge = types.ModuleType("edge_tts")
    edge.Communicate = FakeCommunicate
    sys.modules["edge_tts"] = edge
    kk = types.ModuleType("kokoro_onnx")
    kk.Kokoro = FakeKokoro
    sys.modules["kokoro_onnx"] = kk


def set_runtime(available: bool) -> None:
    tts.KokoroEngine._module_available = staticmethod(lambda: available)


# -- 픽스처 -------------------------------------------------------------------

def write_wav(path: str, samples: int) -> None:
    data = b"".join(struct.pack("<h", (i % 40) * 100 - 2000) for i in range(samples))
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVE")
        f.write(b"fmt " + struct.pack("<IHHIIHH", 16, 1, 1, 8000, 16000, 2, 16))
        f.write(b"data" + struct.pack("<I", len(data)) + data)


def kokoro_raw(cache: str, text: str, tone: str = "casual") -> str:
    return tts._cache_path(cache, f"kokoro|{VOICE}|{tone}|{text}", ext="wav")


def edge_path(cache: str, text: str, tone: str = "casual", rate: int = 10) -> str:
    extra, pitch = tts.EdgeTTSEngine.TONE_DELIVERY[tone]
    return tts._cache_path(cache, f"edge|{EDGE_VOICE}|{rate + extra:+d}%|{pitch}|{text}")


def rfx(path: str) -> str:
    return os.path.splitext(path)[0] + f"_rfx{radiofx.VERSION}.wav"


def make_fixture(root: str, name: str) -> str:
    d = os.path.join(root, name)
    os.makedirs(d, exist_ok=True)
    write_wav(rfx(kokoro_raw(d, TEXT_A)), 800)
    for s in split_sentences(TEXT_B):
        write_wav(os.path.join(d, radio_assemble.segment_file_name(s, "casual", VOICE)), 400)
    for n in radio_assemble.ASSET_NAMES:
        write_wav(os.path.join(d, n), 80)
    write_wav(kokoro_raw(d, TEXT_C), 600)
    with open(edge_path(d, TEXT_D), "wb") as f:
        f.write(b"ID3fake")
    return d


def make_cfg(cache: str, **tts_over) -> config.Config:
    data = copy.deepcopy(config.DEFAULTS)
    data["tts"]["cache_dir"] = cache
    data["tts"]["edge_rate"] = "+10%"
    data["voice"]["language"] = tts_over.pop("lang", "en")
    data["llm"]["enabled"] = False
    data["tts"].update(tts_over)
    return config.Config(data)


def write_pack_fixtures(root: str) -> list:
    """(이름, 기대 상태) — 파이썬 kokoro_pack_state로 확인한 뒤 C#과 대조한다."""
    os.makedirs(root, exist_ok=True)
    probe = tts.probe_text()
    cases = []

    def case(name, expect, files):
        d = os.path.join(root, name)
        os.makedirs(d, exist_ok=True)
        for fname, content in files.items():
            mode = "wb" if isinstance(content, bytes) else "w"
            kw = {} if mode == "wb" else {"encoding": "utf-8", "newline": ""}
            with open(os.path.join(d, fname), mode, **kw) as f:
                f.write(content)
        cases.append((name, expect))

    marker = tts.PACK_MARKER
    case("marker_v015", "ok", {marker: "# teamradio56 음성 팩\npack=kokoro-v015\n"})
    case("marker_incomplete", "incomplete", {marker: "pack=incomplete:kokoro-v016\n"})
    case("marker_empty", "incomplete", {marker: ""})
    case("marker_comment_only", "incomplete", {marker: "# 주석만\n\n"})
    case("marker_bom", "ok", {marker: "﻿pack=kokoro-v016\r\n"})
    case("marker_bare_id", "ok", {marker: "kokoro-v016\n"})
    case("assets_only", "ok", {n: b"RIFF" for n in radio_assemble.ASSET_NAMES})
    case("assets_partial", "none", {radio_assemble.ASSET_NAMES[0]: b"RIFF"})
    raw_probe = kokoro_raw(root, probe)
    # 설치가 끊겨도 확인용 통문장이 남아 있으면 partial (있는 클립은 kokoro로)
    case("marker_incomplete_probe", "partial", {marker: "pack=incomplete:kokoro-v016\n",
                                                os.path.basename(rfx(raw_probe)): b"RIFF"})
    case("marker_empty_raw_probe", "partial", {marker: "",
                                               os.path.basename(raw_probe): b"RIFF"})
    case("marker_incomplete_assets", "incomplete",
         {marker: "pack=incomplete:kokoro-v016\n",
          **{n: b"RIFF" for n in radio_assemble.ASSET_NAMES}})
    case("rfx_probe_only", "ok", {os.path.basename(rfx(raw_probe)): b"RIFF"})
    case("raw_probe_only", "ok", {os.path.basename(raw_probe): b"RIFF"})
    case("edge_only", "none", {os.path.basename(edge_path(root, probe)): b"ID3"})
    case("empty", "none", {})
    with open(os.path.join(root, "expected.tsv"), "w", encoding="utf-8", newline="\n") as f:
        for name, expect in cases:
            f.write(f"{name}\t{expect}\n")
    return cases


def dump_table() -> str:
    rows = []
    for engine in ENGINES:
        for lang in LANGS:
            for pack in PACKS:
                kind, note = tts.resolve_voice_kind(engine, lang, pack)
                rows.append(f"{engine}\t{lang}\t{pack}\t{kind}\t{note or ''}\n")
    return "".join(rows)


# -- 셀프테스트 ---------------------------------------------------------------

class Runner:
    def __init__(self):
        self.passed = 0
        self.failures: list[str] = []
        self.skipped: list[str] = []

    def check(self, cond, what):
        if cond:
            self.passed += 1
        else:
            self.failures.append(what)

    def section(self, name, fn, *args):
        before = len(self.failures)
        try:
            fn(self, *args)
        except Exception as e:      # noqa: BLE001 — 실패로 기록
            import traceback
            self.failures.append(f"{name}: 예외 {type(e).__name__} — {e}\n"
                                 + traceback.format_exc(limit=3))
        print(("  ✓ " if len(self.failures) == before else "  ✗ ") + name)


class LogCapture(logging.Handler):
    def __init__(self):
        super().__init__(logging.DEBUG)
        self.lines: list[tuple[int, str]] = []

    def emit(self, record):
        self.lines.append((record.levelno, record.getMessage()))


def t_table(r: Runner, _root):
    rows = dump_table().splitlines()
    r.check(len(rows) == 16, "결정표 16행")
    r.check(tts.resolve_voice_kind("kokoro", "en", "none") == ("edge", "no-kokoro"), "팩 없음 → edge")
    r.check(tts.resolve_voice_kind("kokoro", "en", "incomplete") == ("edge", "pack-incomplete"),
            "설치 끊김 → edge")
    r.check(tts.resolve_voice_kind("kokoro", "en", "partial") == ("kokoro", "pack-partial"),
            "설치 끊김이지만 클립 있음 → kokoro (pack-partial)")
    r.check(tts.resolve_voice_kind("kokoro", "ko-KR", "ok") == ("edge", "ko"), "ko-KR → edge (ko)")
    r.check(tts.resolve_voice_kind("kokoro", "en", "ok") == ("kokoro", None), "팩 있음 → kokoro")
    r.check(tts.resolve_voice_kind("kokoro", "en", "none", runtime=True) == ("kokoro", None),
            "런타임 → kokoro")
    r.check(tts.resolve_voice_kind("kokoro", "ko", "ok") == ("edge", "ko"), "ko → edge (ko)")
    r.check(tts.resolve_voice_kind("elevenlabs", "en", "none", eleven_voice_id="v")
            == ("elevenlabs", None), "elevenlabs voice_id 있음")
    r.check(tts.resolve_voice_kind("elevenlabs", "ko", "none") == ("edge", "no-elevenlabs-voice"),
            "elevenlabs voice_id 없음 → edge")
    r.check(tts.probe_text() == TEXT_A, "팩 확인 문장 = pit_call 첫 줄")


def t_markers(r: Runner, root):
    r.check(tts.read_marker("# c\n\npack=kokoro-v015\n") == "kokoro-v015", "표식 pack=")
    r.check(tts.read_marker("﻿kokoro-v016") == "kokoro-v016", "BOM + 맨 ID")
    r.check(tts.read_marker("pack=\n") is None, "빈 ID")
    r.check(tts.read_marker("") is None, "빈 파일")
    d = os.path.join(root, "packfx")
    for name, expect in write_pack_fixtures(d):
        got = tts.kokoro_pack_state(os.path.join(d, name), VOICE)
        r.check(got == expect, f"팩 상태 {name}: 기대 {expect}, 실제 {got}")
    r.check(tts.kokoro_pack_state(os.path.join(root, "nope"), VOICE) == "none", "폴더 없음 → none")


def t_kokoro_only(r: Runner, root):
    set_runtime(False)
    d = make_fixture(root, "kokoro_only")
    eng = tts.build_engine(make_cfg(d, engine="kokoro"))
    r.check(eng.kind == "kokoro" and eng.note is None, "세션 목소리 kokoro")
    r.check(not eng.free_text, "런타임 없는 kokoro는 임의 문장 불가")
    FakeEdgeState.calls = 0
    a = eng.synth(TEXT_A, "casual")
    r.check(a == rfx(kokoro_raw(d, TEXT_A)), "A → _rfx3 통문장")
    b = eng.synth(TEXT_B, "casual")
    r.check(b is not None and "_asm" in os.path.basename(b), "B → 세그먼트 조립")
    r.check(eng.synth(TEXT_D, "casual") is None, "D(edge mp3만) → 무음")
    r.check(eng.synth(TEXT_E, "casual") is None and eng.miss_reason == "kokoro-miss", "E → 무음 kokoro-miss")
    r.check(FakeEdgeState.calls == 0, "edge 합성 0회")
    plan = tts.kokoro_plan(d, VOICE, "casual", TEXT_E)
    r.check(plan == (None, TEXT_E), "kokoro_plan 미스 → 빠진 문장")
    # 효과 설정 꺼도 kokoro는 팩(효과본)을 쓴다
    eng2 = tts.build_engine(make_cfg(d, engine="kokoro", radio_fx=False))
    r.check(eng2.synth(TEXT_A, "casual") == rfx(kokoro_raw(d, TEXT_A)), "무전 효과 꺼짐이어도 _rfx3")


def t_kokoro_raw(r: Runner, root):
    set_runtime(False)
    d = make_fixture(root, "kokoro_raw")
    eng = tts.build_engine(make_cfg(d, engine="kokoro"))
    c = eng.synth(TEXT_C, "casual")
    base = os.path.splitext(kokoro_raw(d, TEXT_C))[0]
    r.check(c is not None and c.startswith(base), "C → 원본 kokoro (+효과)")


def t_kokoro_runtime(r: Runner, root):
    set_runtime(True)
    d = make_fixture(root, "kokoro_rt")
    os.makedirs("models", exist_ok=True)
    for n in tts.KokoroEngine.MODEL_FILES:
        with open(os.path.join("models", n), "wb") as f:
            f.write(b"x")
    try:
        eng = tts.build_engine(make_cfg(d, engine="kokoro"))
        r.check(eng.kind == "kokoro" and eng.free_text, "런타임 준비 → kokoro, 임의 문장 가능")
        FakeKokoro.created = 0
        FakeEdgeState.calls = 0
        e = eng.synth(TEXT_E, "casual")
        r.check(e is not None and FakeKokoro.created == 1 and FakeEdgeState.calls == 0,
                "E → 런타임 kokoro 합성 (edge 0회)")
    finally:
        for n in tts.KokoroEngine.MODEL_FILES:
            os.remove(os.path.join("models", n))
        set_runtime(False)


def t_no_download(r: Runner, root):
    called = []
    import urllib.request
    orig = urllib.request.urlretrieve
    urllib.request.urlretrieve = lambda *a, **k: called.append(a)
    try:
        set_runtime(True)
        kk = tts.KokoroEngine(os.path.join(root, "nodl"), VOICE, model_dir=os.path.join(root, "nomodels"),
                              allow_download=False)
        r.check(not kk.runtime_ready(), "모델 없으면 런타임 준비 아님")
        r.check(kk._load() is None and not called, "세션 중 모델 다운로드 없음")
        r.check(not kk.runtime_ready(), "초기화 실패 뒤 준비 아님")
    finally:
        urllib.request.urlretrieve = orig
        set_runtime(False)


def t_edge_only(r: Runner, root):
    set_runtime(False)
    d = make_fixture(root, "edge_only")
    eng = tts.build_engine(make_cfg(d, engine="edge", radio_fx=False))
    r.check(eng.kind == "edge" and eng.free_text, "edge 세션, 임의 문장 가능")
    FakeEdgeState.calls = 0
    FakeEdgeState.fail = False
    for t in (TEXT_A, TEXT_B, TEXT_C):
        p = eng.synth(t, "casual")
        r.check(p == edge_path(d, t), f"edge 세션: {t} → edge 합성 (kokoro 파일 아님)")
    r.check(FakeEdgeState.calls == 3, "합성 3회")
    r.check(eng.synth(TEXT_D, "casual") == edge_path(d, TEXT_D) and FakeEdgeState.calls == 3,
            "D → 캐시된 edge, 합성 없음")
    FakeEdgeState.fail = True
    try:
        r.check(eng.synth(TEXT_E, "casual") is None and eng.miss_reason == "edge-failed",
                "합성 실패 → 무음 edge-failed")
    finally:
        FakeEdgeState.fail = False


def t_korean(r: Runner, root):
    d = make_fixture(root, "ko")
    eng = tts.build_engine(make_cfg(d, engine="kokoro", lang="ko", radio_fx=False))
    r.check(eng.kind == "edge" and eng.note == "ko", "ko → edge (ko)")


def t_incomplete(r: Runner, root):
    set_runtime(False)
    d = make_fixture(root, "incomplete")
    with open(os.path.join(d, tts.PACK_MARKER), "w", encoding="utf-8") as f:
        f.write("pack=incomplete:kokoro-v016\n")
    eng = tts.build_engine(make_cfg(d, engine="kokoro", radio_fx=False))
    r.check(eng.kind == "kokoro" and eng.note == "pack-partial",
            "설치 끊김이지만 클립 남음 → kokoro (pack-partial)")
    r.check(eng.synth(TEXT_A, "casual") is not None and eng.synth(TEXT_E, "casual") is None,
            "남은 클립은 kokoro로, 없는 멘트는 자막만")
    bare = os.path.join(root, "incomplete_bare")
    os.makedirs(bare, exist_ok=True)
    with open(os.path.join(bare, tts.PACK_MARKER), "w", encoding="utf-8") as f:
        f.write("pack=incomplete:kokoro-v016\n")
    eng = tts.build_engine(make_cfg(bare, engine="kokoro", radio_fx=False))
    r.check(eng.kind == "edge" and eng.note == "pack-incomplete", "설치 끊김·클립 없음 → 세션 전체 edge")
    empty = os.path.join(root, "empty_cache")
    os.makedirs(empty, exist_ok=True)
    eng = tts.build_engine(make_cfg(empty, engine="kokoro", radio_fx=False))
    r.check(eng.kind == "edge" and eng.note == "no-kokoro", "팩 없음 → 세션 전체 edge")


def t_allow(r: Runner, root):
    set_runtime(False)
    d = make_fixture(root, "allow")
    eng = tts.build_engine(make_cfg(d, engine="kokoro", allow_fallback=True))
    r.check(eng.kind == "kokoro" and eng.free_text, "허용 → 임의 문장 가능")
    r.check(eng.synth(TEXT_A, "casual") == rfx(kokoro_raw(d, TEXT_A)), "허용이어도 팩 우선")
    p = eng.synth(TEXT_D, "casual")
    r.check(p is not None and p.startswith(os.path.splitext(edge_path(d, TEXT_D))[0]),
            "허용: D → edge")
    eleven = tts.build_engine(make_cfg(d, engine="elevenlabs", elevenlabs_voice_id="v"))
    r.check(eleven.kind == "elevenlabs" and not eleven.free_text, "elevenlabs 키 없음 → 임의 문장 불가")
    eleven2 = tts.build_engine(make_cfg(d, engine="elevenlabs", elevenlabs_voice_id="v",
                                        allow_fallback=True))
    r.check(eleven2.free_text, "elevenlabs + 허용 → 임의 문장 가능")


class FakeLLM:
    def __init__(self):
        self.available = True
        self.calls = 0

    def generate(self, prompt):
        self.calls += 1
        return "LLM line."


def t_llm_gate(r: Runner, root):
    from state import SessionState
    from voice import VoiceGenerator
    cfg = make_cfg(root, engine="kokoro")
    ev = Event(type=EventType.PACE_COMMENT, priority=Priority.NORMAL,
               data={"delta": 1.2, "direction": "slower"})
    bridge_ev = Event(type=EventType.FUEL_CRITICAL, priority=Priority.CRITICAL,
                      bridge={"topic": "연료"})
    vg = VoiceGenerator(cfg, SessionState(), speakable=lambda: False)
    vg.llm = FakeLLM()
    text, source = vg.text_for(ev)
    r.check(source == "template" and text and vg.llm.calls == 0, "말할 수 없는 목소리 → 템플릿, LLM 0회")
    r.check(vg.bridge_text(bridge_ev) is None and vg.llm.calls == 0, "브리지도 없음")
    vg2 = VoiceGenerator(cfg, SessionState(), speakable=lambda: True)
    vg2.llm = FakeLLM()
    text, source = vg2.text_for(ev)
    r.check(source == "llm" and vg2.llm.calls == 1, "말할 수 있는 목소리 → LLM")


class NullEngine(tts.TTSEngine):
    kind = "kokoro"
    miss_reason = "kokoro-miss"

    def synth(self, text, tone="casual"):
        return None


class FakeVoiceGen:
    llm_speakable = False

    def text_for(self, ev):
        return ev.message, "composed"


class FakePlayer:
    def __init__(self):
        self.calls = 0

    def play(self, *a, **k):
        self.calls += 1
        return True

    def stop(self):
        pass


def t_worker(r: Runner, root):
    from events import EventBus
    from state import SessionState
    cap = LogCapture()
    logging.getLogger("tts").addHandler(cap)
    try:
        player = FakePlayer()
        logp = os.path.join(root, "speech_log.jsonl")
        w = tts.VoiceWorker(EventBus({}), FakeVoiceGen(), NullEngine(), player, SessionState(),
                            speech_log=tts.SpeechLogger(logp))
        mk = lambda t, p=Priority.NORMAL: Event(type=EventType.SESSION_BRIEFING, priority=p, message=t)
        w._speak(mk(TEXT_E, Priority.CRITICAL))
        w._speak(mk(TEXT_E))
        r.check(w.recent[-1].dropped and player.calls == 0, "무음 콜: recent dropped, 재생 없음")
        r.check(w.drops.count == 2 and w.drops.distinct == 1, "건수 2, 텍스트 1종")
        drop_lines = [x for x in cap.lines if x[1].startswith("[무음] kokoro")]
        r.check(len(drop_lines) == 1 and drop_lines[0][0] == logging.WARNING,
                "텍스트별 첫 한 번만 기록 (긴급은 WARNING)")
        r.check(drop_lines and drop_lines[0][1]
                == f"[무음] kokoro 목소리로 낼 수 없어 말하지 않음 (kokoro-miss) [casual] {TEXT_E}",
                "무음 로그 문구 (C#과 같음)")
        for i in range(60):
            w._speak(mk(f"Line {i}."))
        cap_lines = [x for x in cap.lines if "기록 상한" in x[1]]
        r.check(w.drops.distinct == tts.DropLog.LIMIT and len(cap_lines) == 1, "상한 50종 + 상한 줄 1번")
        w.speech_log.close()
        with open(logp, encoding="utf-8") as f:
            first = f.readline()
        r.check('"spoken": false' in first and '"miss": "kokoro-miss"' in first, "발화 로그 spoken/miss")
        from status import recent_call_fields
        fields = recent_call_fields(w.recent)
        r.check(len([k for k in fields if not k.endswith("_dropped")]) == 5
                and all(fields.get(f"call{i}_dropped") is True for i in range(1, 6)),
                "recent_call_fields: call{i} + call{i}_dropped")
        ok = tts.RecentCall("12:00:00", "x")
        f2 = recent_call_fields([ok])
        r.check(f2 == {"call1": "12:00:00  x"}, "재생된 콜엔 _dropped 키 없음")
    finally:
        logging.getLogger("tts").removeHandler(cap)


def selftest() -> int:
    strict = os.environ.get("TEAMRADIO56_STRICT") == "1"
    try:
        import numpy  # noqa: F401
        import soundfile  # noqa: F401
        have_np = True
    except ImportError:
        have_np = False
    install_fakes()
    set_language("en")
    logging.getLogger().setLevel(logging.CRITICAL)
    logging.getLogger("tts").setLevel(logging.DEBUG)
    logging.getLogger("tts").propagate = False
    logging.getLogger("tts").addHandler(logging.NullHandler())   # 기대한 경고가 출력을 덮지 않게
    r = Runner()
    cwd = os.getcwd()
    with tempfile.TemporaryDirectory(prefix="teamradio56-voicepy-") as root:
        os.chdir(root)        # models/ 상대 경로 — 저장소의 실제 모델을 보지 않게
        try:
            r.section("세션 목소리 결정표", t_table, root)
            r.section("팩 표식·상태", t_markers, root)
            r.section("kokoro 세션 — 팩 소스만, 미스는 무음", t_kokoro_only, root)
            r.section("edge 세션 — kokoro 파일을 안 보고, 실패는 무음", t_edge_only, root)
            r.section("ko — edge", t_korean, root)
            r.section("팩 없음·설치 끊김 — 세션 전체 edge", t_incomplete, root)
            r.section("세션 중 모델 다운로드 없음", t_no_download, root)
            r.section("LLM — 말할 수 있는 목소리에서만", t_llm_gate, root)
            r.section("보이스 워커 — 무음 표시·기록 상한", t_worker, root)
            np_cases = [("kokoro 원본 통문장 (+효과)", t_kokoro_raw),
                        ("kokoro 런타임 합성", t_kokoro_runtime),
                        ("목소리 섞임 허용", t_allow)]
            for name, fn in np_cases:
                if have_np:
                    r.section(name, fn, root)
                else:
                    r.skipped.append(name)
                    print(f"  ⏭ {name} — numpy/soundfile 없음")
        finally:
            os.chdir(cwd)
    print()
    if r.skipped and strict:
        r.failures.append("numpy/soundfile 없음 (TEAMRADIO56_STRICT=1): " + ", ".join(r.skipped))
    if r.failures:
        print(f"❌ 한 목소리 정책 셀프테스트(파이썬) 실패 {len(r.failures)}건 (통과 {r.passed}건)")
        for f in r.failures:
            print("   - " + f)
        return 1
    print(f"✅ 한 목소리 정책 셀프테스트(파이썬) {r.passed}건 통과"
          + (f" (건너뜀 {len(r.skipped)})" if r.skipped else ""))
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    ap.add_argument("--dump-table", action="store_true")
    ap.add_argument("--pack-fixtures", metavar="DIR")
    ap.add_argument("--out")
    args = ap.parse_args(argv)
    if args.dump_table:
        text = dump_table()
        if args.out:
            with open(args.out, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)
        else:
            sys.stdout.write(text)
        return 0
    if args.pack_fixtures:
        set_language("en")
        logging.getLogger().setLevel(logging.CRITICAL)
        bad = 0
        for name, expect in write_pack_fixtures(args.pack_fixtures):
            got = tts.kokoro_pack_state(os.path.join(args.pack_fixtures, name), VOICE)
            if got != expect:
                print(f"❌ 파이썬 팩 상태 {name}: 기대 {expect}, 실제 {got}")
                bad += 1
        return 1 if bad else 0
    return selftest()


if __name__ == "__main__":
    sys.exit(main())
