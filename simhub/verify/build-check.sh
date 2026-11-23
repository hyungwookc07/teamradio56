#!/usr/bin/env bash
# C# 전체 컴파일 검증 — SimHub이 설치되지 않은 환경(리눅스/CI 포함)에서도 돈다.
#
# SimHub 어셈블리 대신 verify/*.Stub 의 최소 스텁을 만들어 그걸 참조해 빌드한다.
# 잡아주는 것: 문법 오류, 타입 오류, 오탈자, 누락된 using, 시그니처 불일치.
# 못 잡는 것: 실제 SimHub API와의 차이 (스텁이 곧 가정이므로).
#
# 사용법:  bash simhub/verify/build-check.sh
set -euo pipefail

cd "$(dirname "$0")/.."
STUBOUT="$PWD/verify/stubout"

echo "▶ SimHub 스텁 빌드"
dotnet build verify/SimHub.Plugins.Stub/SimHub.Plugins.Stub.csproj -c Release -v q --nologo

mkdir -p "$STUBOUT"
cp verify/SimHub.Plugins.Stub/bin/Release/net48/SimHub.Plugins.dll "$STUBOUT/"
cp verify/GameReaderCommon.Stub/bin/Release/net48/GameReaderCommon.dll "$STUBOUT/"

echo "▶ Core 빌드 (SimHub 무관)"
dotnet build src/TeamRadio56.Core/TeamRadio56.Core.csproj -c Release -v q --nologo

echo "▶ 플러그인 빌드 (스텁 참조)"
dotnet build src/TeamRadio56.SimHub/TeamRadio56.SimHub.csproj -c Release -v q --nologo \
    -p:SimHubPath="$STUBOUT"

# 경량 플러그인 — 무전은 내장 엔진 + 영어 + kokoro 음성 팩 한 목소리뿐이다. 빠진 기능
# (파이썬 엔진 모드, edge 온라인 TTS·mp3 재생, Windows 음성, 목소리 섞임, 한국어 무전)이
# 되살아나면 여기서 멈춘다. 되살리려면 이 목록과 문서를 함께 고친다.
REMOVED='System\.Speech|System\.Management|EdgeTts|ClientWebSocket|MediaPlayer|TeamRadio56\.Core\.Engine|VoiceKind|IFallbackSpeaker|Messages\.IsKo|SetLanguage|PhraseLines\.Ko\b|TrackNamesEn|KnownTrackEn|BriefingPackOnly'
LEFTOVERS=$(grep -rnE "$REMOVED" src --include=*.cs --include=*.csproj | grep -v '/bin/\|/obj/' || true)
if [ -n "$LEFTOVERS" ]; then
    echo "❌ 경량 플러그인에서 뺀 기능이 다시 들어왔습니다:"
    echo "$LEFTOVERS"
    exit 1
fi

# 설정 화면 문자열 — ko/en 키가 같고, 코드가 쓰는 키가 다 있고, 코드가 안 쓰는 키가 없고,
# 음성 팩이 없을 때의 안내는 [음성 팩 업데이트]를 가리킨다 (다른 목소리로 대신 말한다고 하지 않는다).
# 호버 설명 — hint_*/*_hint 키는 툴팁 전용이라 늘 보이는 글씨로 그리면 실패한다. 툴팁을 단 패널은
# 투명 배경(틈에서 툴팁이 닫혔다 다시 뜨지 않게)과 스크린 리더용 reader가 있어야 하고, 콤보 줄은 드랍다운이
# 열린 동안 줄 툴팁을 끄며(선택지를 덮지 않게), 엔진이 시작할 때만 읽는 설정은 전부 [엔진 재시작] 안내의
# 지문(EngineInputs)에 들어가야 한다. 접두사 + Core 값으로 조립하는 키는 조립 코드를 감싼 if가 거르는 값
# (!= Enum.X)을 빼고 센다 — 화면에 못 나오는 키를 "쓰는 키"로 치지 않게.
python3 - <<'PY'
import re, sys
def read(p):
    return open(p, encoding="utf-8").read()
loc = read("src/TeamRadio56.SimHub/Loc.cs")
ko = loc[loc.index("Dictionary<string, string> Ko"):loc.index("Dictionary<string, string> En")]
en = loc[loc.index("Dictionary<string, string> En"):]
def table(t):
    return dict(re.findall(r'^\s*\{ "([^"]+)", "((?:[^"\\]|\\.)*)"', t, re.M))
k, e = table(ko), table(en)
bad = []
if set(k) != set(e):
    bad.append("ko/en 키가 다름: " + ", ".join(sorted(set(k) ^ set(e))))
ui = read("src/TeamRadio56.SimHub/SettingsControl.cs")
code = ui + read("src/TeamRadio56.SimHub/TeamRadio56Plugin.cs")
used = set(re.findall(r'\bL\("([^"]+)"\)', code))
# 데이터 원천 줄의 키는 Core가 고른다 (TelemetrySource.StatusLineKeys → SettingsControl이 L(key))
used |= set(re.findall(r'"(src_[a-z0-9_]+)"', read("src/TeamRadio56.Core/Telemetry/TelemetrySource.cs")))
# 코드가 접두사 + Core 값으로 이어 붙이는 키. 그 조립 코드가 있을 때만 쓰는 키로 친다
core = "src/TeamRadio56.Core/"
def enum(path, name):
    body = re.search(r"enum " + name + r"\s*\{([^}]*)\}", read(core + path)).group(1)
    return [m.strip() for m in re.sub(r"//[^\n]*", "", body).split(",") if m.strip()]
notes = [n.replace("-", "_") for n in
         re.findall(r'const string Note\w+ = "([^"]+)"', read(core + "Logic/VoicePolicy.cs"))]
chatter = re.findall(r'"([^"]+)"', re.search(r"ChatterChoices = \{([^}]*)\}",
                                             read(core + "Config/PluginSettings.cs")).group(1))
def blank(m):
    return re.sub(r"[^\n]", " ", m.group(0))
# 문자열·문자·주석 속 괄호에 속지 않게 같은 길이 공백으로 지운 사본에서 괄호를 센다
STRIP = r'@"(?:[^"]|"")*"|"(?:[^"\\\n]|\\.)*"|\'(?:[^\'\\\n]|\\.)+\'|//[^\n]*|/\*.*?\*/'
def flatten(text):
    return re.sub(STRIP, blank, text, flags=re.S)
flat = flatten(ui)
def guarded_out(marker, enum_name):
    # 조립 코드(marker)를 감싼 if 조건에 && 로만 이어진 "!= Enum.X"가 있으면 X는 그 키로 안 간다
    # (예: RollbackFailed는 upd_err_ 줄 대신 수동 복구 안내로). 조립 코드가 여러 곳이면 모든 곳에서
    # 빠진 값만 뺀다. if가 아닌 블록(메서드·반복문)에서 멈춘다 — 모르면 빼지 않는 쪽(키를 요구)으로
    out = None
    for m in re.finditer(re.escape(marker), ui):
        excl, i = set(), m.start()
        while True:
            depth, i = 0, i - 1
            while i >= 0 and not (flat[i] == "{" and depth == 0):
                depth += {"}": 1, "{": -1}.get(flat[i], 0)
                i -= 1
            head = flat[:max(i, 0)].rstrip()
            if i < 0 or not head.endswith(")"):
                break
            depth, j = 0, len(head) - 1
            while j >= 0:
                depth += {")": 1, "(": -1}.get(head[j], 0)
                if depth == 0:
                    break
                j -= 1
            if j < 0 or not re.search(r"\bif\s*$", head[:j]):
                break
            cond = head[j + 1:-1]
            if "||" not in cond:
                excl |= set(re.findall(r"!=\s*" + enum_name + r"\.(\w+)\b", cond))
        out = excl if out is None else out & excl
    return out or set()
activities = enum("Update/UpdateController.cs", "UpdateActivity")
errors = enum("Update/UpdateException.cs", "UpdateErrorKind")
if not (notes and chatter and activities and errors):
    bad.append("동적 키의 원천(VoicePolicy.Note*, ChatterChoices, UpdateActivity, UpdateErrorKind)을 못 읽음")
families = (  # (조립 코드, 키들, 전부 있어야 하나 — 없으면 화면에 키 이름이 그대로 보인다)
    ('L("voice_note_" +', ["voice_note_" + n for n in notes], True),
    ('L("upd_failed_" +', ["upd_failed_" + a for a in activities if a != "Idle"], True),
    ('L("upd_err_" +', ["upd_err_" + x for x in errors
                        if x not in guarded_out('L("upd_err_" +', "UpdateErrorKind")], True),
    ('Choice("choice_")', ["choice_" + c for c in chatter], False),  # 없으면 값 그대로 보인다
)
dynamic = set()
for marker, keys, required in families:
    if marker in ui:
        dynamic |= set(keys)
        if required:
            used |= set(keys)
missing = sorted(used - set(k))
if missing:
    bad.append("Loc에 없는 키: " + ", ".join(missing))
unused = sorted(set(k) - used - dynamic)
if unused:
    bad.append("코드가 쓰지 않는 키 (지우거나 쓰세요): " + ", ".join(unused))
for key in ("pack_missing_banner", "voice_note_no_kokoro", "voice_note_pack_incomplete",
            "test_speak_no_pack"):
    if "[음성 팩 업데이트]" not in k.get(key, "") or "[Update voice pack]" not in e.get(key, ""):
        bad.append(key + ": [음성 팩 업데이트] / [Update voice pack] 안내가 없음")
for key, text in list(k.items()) + list(e.items()):
    if re.search(r"\bedge\b|python|파이썬|Windows 음성|Windows speech", text, re.I):
        bad.append(key + ": 빠진 기능을 가리킴 — " + text[:60])

# 호버 설명 가드 — hint_*/*_hint(와 예전 subtitle) 키는 툴팁 전용이다. L("…")을 감싼 괄호를
# 거슬러 올라가 그 글이 들어가는 호출과 인자 위치를 본다: Tip(el, ●[, reader]) / Row(…, …, …, ●) /
# Section(…, …, ●) / UpdateTip(…, …, ref …, ●, reader)만 허용 (string.Format·+ 이어 붙이기는 통과).
# TextBlock.Text·SetText·Content 등 늘 보이는 글씨로 가면 실패한다.
TIP_SINKS = {"Tip": 1, "Row": 3, "Section": 2, "UpdateTip": 3}   # 호출 → 설명 인자 위치(0부터)
def sink_of(pos):
    while True:
        depth, commas, i = 0, 0, pos - 1
        while i >= 0:
            c = flat[i]
            if c == ")":
                depth += 1
            elif c == "(":
                if depth == 0:
                    break
                depth -= 1
            elif depth == 0 and c in "{};":
                return None, -1          # 호출 인자가 아님 (대입·초기화 식)
            elif depth == 0 and c == ",":
                commas += 1
            i -= 1
        if i < 0:
            return None, -1
        m = re.search(r"(\w+)\s*$", flat[:i])
        name = m.group(1) if m else ""
        if name != "Format":
            return name, commas
        pos = m.start()                  # string.Format(●, …)은 그 결과가 들어가는 곳을 본다
HINT_KEY = r'\bL\("(hint_[a-z0-9_]+|[a-z0-9_]+_hint|subtitle)"\)'
for m in re.finditer(HINT_KEY, ui):
    name, arg = sink_of(m.start())
    if TIP_SINKS.get(name) != arg:
        line = ui.count("\n", 0, m.start()) + 1
        where = "%s(%d번째 인자)" % (name, arg + 1) if name else "대입/초기화"
        bad.append("SettingsControl.cs:%d: 설명 %s를 늘 보이는 글씨로 씀 (%s) — Tip / Row·Section의 hint로"
                   % (line, m.group(1), where))
def block(text, start):
    # text[start] == "{" → 짝이 맞는 "}"까지의 안쪽 (못 찾으면 None)
    depth, i = 0, start
    while i < len(text):
        depth += {"{": 1, "}": -1}.get(text[i], 0)
        if depth == 0:
            return text[start + 1:i]
        i += 1
    return None
def body(name, text=None):
    text = flat if text is None else text
    m = re.search(r"\b" + name + r"\s*\([^)]*\)\s*\{", text)
    return block(text, m.end() - 1) if m else None
for sink, param in (("Row", "hint"), ("Section", "hint"), ("UpdateTip", "text")):
    b = body(r"(?:StackPanel|void)\s+" + sink)
    if b is None or not re.search(r"\bTip\(\s*\w+\s*,\s*" + param + r"\s*[,)]", b):
        bad.append(sink + "(): " + param + "를 Tip(…)으로 넘기지 않음")
    elif re.search(r"\b(?:Text|Content)\s*=\s*" + param + r"\b", b):
        bad.append(sink + "(): " + param + "를 늘 보이는 글씨로 그림")
# 콤보 줄 — 드랍다운 항목에서 툴팁 찾기가 콤보를 거쳐 줄까지 올라오므로, 열린 동안 줄 툴팁을 꺼야
# 줄 설명이 선택지(choice_*) 이름을 덮지 않는다
row_b, mute = body(r"(?:StackPanel|void)\s+Row"), body(r"void\s+MuteTipWhileOpen")
if (row_b is None or not re.search(r"\bMuteTipWhileOpen\(\s*\w+\s*,\s*row\s*\)", row_b)
        or mute is None or "DropDownOpened" not in mute
        or not re.search(r"ToolTipService\.SetIsEnabled\(\s*\w+\s*,\s*false\s*\)", mute)):
    bad.append("Row(): 콤보 드랍다운이 열린 동안 줄 툴팁을 끄지 않음 (MuteTipWhileOpen) — 선택지를 덮는다")
tip = body(r"void\s+Tip")
if tip is None or "ToolTip" not in tip or "SetShowDuration" not in tip:
    bad.append("Tip(): 툴팁을 달지 않음 (ToolTip + ShowDuration)")
elif re.search(r"\b(?:Foreground|Background)\b", tip):
    bad.append("Tip(): 툴팁 글자색·배경을 박음 — SimHub 테마 ToolTip 스타일에 맡긴다")
if re.search(r"\bHint\s*\(", flat):
    bad.append("SettingsControl.cs: 예전 회색 설명 헬퍼 Hint()가 돌아옴 — Tip / Row·Section의 hint로")
# 툴팁을 단 패널 — (1) 배경이 null인 패널은 자식 위에서만 마우스가 걸려, 글자와 ⓘ 사이 여백·라벨 칸의
# 빈 곳·라벨과 컨트롤 사이를 지날 때 툴팁이 닫혔다가 다시 뜬다 → 초기화에 Background(투명이라도)를.
# (2) 패널은 AutomationPeer가 없어 거기 단 HelpText는 스크린 리더가 못 읽는다 → Tip(패널, 글, reader).
# 툴팁 주인이 이 파일에서 new 패널로 만든 변수일 때만 본다 (버튼·매개변수는 해당 없음).
PANEL = r"(?:StackPanel|DockPanel|WrapPanel|Grid|Canvas|UniformGrid)"
def top_args(text, open_paren):
    # text[open_paren] == "(" → 최상위 인자 수
    depth, commas, i = 0, 0, open_paren
    while i < len(text):
        c = text[i]
        if c in "({[":
            depth += 1
        elif c in ")}]":
            depth -= 1
            if depth == 0:
                return commas + 1
        elif c == "," and depth == 1:
            commas += 1
        i += 1
    return -1
for m in re.finditer(r"\b(Tip|UpdateTip)\(\s*(\w+)\s*,", flat):
    call, owner = m.group(1), m.group(2)
    decls = [d for d in re.finditer(r"\b" + owner + r"\s*=\s*new\s+" + PANEL + r"\b(\s*\(\s*\))?\s*(\{)?", flat)
             if not re.search(r"\bprivate\b[^\n]*$", flat[:d.start()])]   # 필드 자리 표시자는 Build가 갈아끼운다
    if not decls:
        continue
    line = ui.count("\n", 0, m.start()) + 1
    assigned = re.search(r"\b" + owner + r"\.Background\s*=", flat)
    for d in decls:
        init = block(flat, d.start(2)) if d.group(2) else None
        if not assigned and (init is None or not re.search(r"\bBackground\s*=", init)):
            bad.append("SettingsControl.cs:%d: 툴팁을 단 패널 %s에 배경이 없음 — 틈에서 툴팁이 닫혔다 다시 뜬다"
                       " (Background = HitArea)" % (ui.count("\n", 0, d.start()) + 1, owner))
    if call == "Tip" and top_args(flat, m.end(1)) < 3:
        bad.append("SettingsControl.cs:%d: Tip(%s, …)의 HelpText가 패널에 — 스크린 리더가 못 읽는다"
                   " (Tip(패널, 글, 컨트롤·TextBlock))" % (line, owner))
if not re.search(r"Background\s*=\s*HitArea", flat) or not re.search(r"HitArea\s*=\s*Brushes\.Transparent", flat):
    bad.append("SettingsControl.cs: 툴팁 패널의 투명 배경(HitArea)이 없음")

# [엔진 재시작] 옆 "설정이 바뀌었습니다" — 엔진이 시작할 때만 읽는 설정(InitBuiltin, 음성 팩 폴더는
# ResolveCacheDir)은 전부 EngineInputs 지문에 있어야 바꿨을 때 안내가 뜬다. 매 틱 읽는 VoiceEnabled만 뺀다.
pflat = flatten(read("src/TeamRadio56.SimHub/TeamRadio56Plugin.cs"))
init_b = body(r"void\s+InitBuiltin", pflat)
cache_b = body(r"string\s+ResolveCacheDir", pflat)
fp_b = body(r"string\s+EngineInputs", pflat)
if init_b is None or cache_b is None or fp_b is None:
    bad.append("TeamRadio56Plugin.cs: InitBuiltin / ResolveCacheDir / EngineInputs를 못 찾음")
else:
    reads = set(re.findall(r"\bSettings\.(\w+)", init_b)) | set(re.findall(r"\b[Ss]ettings\.(\w+)", cache_b))
    lost = sorted(reads - {"VoiceEnabled"} - set(re.findall(r"\bs\.(\w+)", fp_b)))
    if lost:
        bad.append("TeamRadio56Plugin.EngineInputs(): 엔진이 시작할 때 읽는 설정이 빠짐 — " + ", ".join(lost)
                   + " (바꿔도 [엔진 재시작] 안내가 안 뜬다)")
    if not re.search(r"_engineInputs\s*=\s*EngineInputs\(\s*Settings\s*\)", init_b):
        bad.append("TeamRadio56Plugin.InitBuiltin(): 읽은 설정의 지문(_engineInputs)을 남기지 않음")
if "EngineSettingsPending" not in flat or 'L("engine_apply_note")' not in ui:
    bad.append("SettingsControl.cs: [엔진 재시작] 옆 '설정이 바뀌었습니다' 안내(EngineSettingsPending)가 없음")
if bad:
    print("❌ 설정 화면 문자열(Loc.cs / SettingsControl.cs):")
    for b in bad:
        print("   " + b)
    sys.exit(1)
tips = len(re.findall(HINT_KEY, ui))
print(f"✅ 설정 화면 문자열 {len(k)}개 (ko/en 일치, 안 쓰는 키 없음, 음성 팩 안내 확인, 호버 설명 {tips}곳은 툴팁으로만,"
      " 툴팁 패널 배경·reader, 콤보 줄 툴팁, 엔진 재시작 안내 지문)")
PY

# LMU 내장 공유 메모리 명세 — pack(4) 오프셋·크기 재계산, rF2 공통 필드가 rf2data.py와 같은지.
# C# 리더와의 대조(합성 덤프)는 러너가 필요해 replay-check.sh에서 한다.
python3 -I ../tools/lmu_native_blob.py verify-spec

echo
echo "✅ 컴파일 검증 통과 — 실제 SimHub API 일치는 사용자 PC 빌드에서 확인"
