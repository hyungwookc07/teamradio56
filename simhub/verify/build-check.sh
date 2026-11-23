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
# 호버 설명 — hint_*/*_hint 키는 툴팁 전용이라 늘 보이는 글씨로 그리면 실패한다.
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
activities = enum("Update/UpdateController.cs", "UpdateActivity")
errors = enum("Update/UpdateException.cs", "UpdateErrorKind")
if not (notes and chatter and activities and errors):
    bad.append("동적 키의 원천(VoicePolicy.Note*, ChatterChoices, UpdateActivity, UpdateErrorKind)을 못 읽음")
families = (  # (조립 코드, 키들, 전부 있어야 하나 — 없으면 화면에 키 이름이 그대로 보인다)
    ('L("voice_note_" +', ["voice_note_" + n for n in notes], True),
    ('L("upd_failed_" +', ["upd_failed_" + a for a in activities if a != "Idle"], True),
    ('L("upd_err_" +', ["upd_err_" + x for x in errors], True),
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
# 거슬러 올라가 그 글이 들어가는 호출과 인자 위치를 본다: Tip(el, ●) / Row(…, …, …, ●) /
# Section(…, …, ●) / UpdateTip(…, …, ref …, ●)만 허용 (string.Format·+ 이어 붙이기는 통과).
# TextBlock.Text·SetText·Content 등 늘 보이는 글씨로 가면 실패한다.
def blank(m):
    return re.sub(r"[^\n]", " ", m.group(0))
# 문자열·문자·주석 속 괄호에 속지 않게 같은 길이 공백으로 지운 사본에서 괄호를 센다
flat = re.sub(r'@"(?:[^"]|"")*"|"(?:[^"\\\n]|\\.)*"|\'(?:[^\'\\\n]|\\.)+\'|//[^\n]*|/\*.*?\*/',
              blank, ui, flags=re.S)
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
def body(name):
    m = re.search(r"\b" + name + r"\s*\([^)]*\)\s*\{", flat)
    if not m:
        return None
    depth, i = 0, m.end() - 1
    while i < len(flat):
        depth += {"{": 1, "}": -1}.get(flat[i], 0)
        if depth == 0:
            return flat[m.end():i]
        i += 1
    return None
for sink, param in (("Row", "hint"), ("Section", "hint"), ("UpdateTip", "text")):
    b = body(r"(?:StackPanel|void)\s+" + sink)
    if b is None or not re.search(r"\bTip\(\s*\w+\s*,\s*" + param + r"\s*\)", b):
        bad.append(sink + "(): " + param + "를 Tip(…)으로 넘기지 않음")
    elif re.search(r"\b(?:Text|Content)\s*=\s*" + param + r"\b", b):
        bad.append(sink + "(): " + param + "를 늘 보이는 글씨로 그림")
tip = body(r"void\s+Tip")
if tip is None or "ToolTip" not in tip or "SetShowDuration" not in tip:
    bad.append("Tip(): 툴팁을 달지 않음 (ToolTip + ShowDuration)")
elif re.search(r"\b(?:Foreground|Background)\b", tip):
    bad.append("Tip(): 툴팁 글자색·배경을 박음 — SimHub 테마 ToolTip 스타일에 맡긴다")
if re.search(r"\bHint\s*\(", flat):
    bad.append("SettingsControl.cs: 예전 회색 설명 헬퍼 Hint()가 돌아옴 — Tip / Row·Section의 hint로")
if bad:
    print("❌ 설정 화면 문자열(Loc.cs / SettingsControl.cs):")
    for b in bad:
        print("   " + b)
    sys.exit(1)
tips = len(re.findall(HINT_KEY, ui))
print(f"✅ 설정 화면 문자열 {len(k)}개 (ko/en 일치, 안 쓰는 키 없음, 음성 팩 안내 확인, 호버 설명 {tips}곳은 툴팁으로만)")
PY

# LMU 내장 공유 메모리 명세 — pack(4) 오프셋·크기 재계산, rF2 공통 필드가 rf2data.py와 같은지.
# C# 리더와의 대조(합성 덤프)는 러너가 필요해 replay-check.sh에서 한다.
python3 -I ../tools/lmu_native_blob.py verify-spec

echo
echo "✅ 컴파일 검증 통과 — 실제 SimHub API 일치는 사용자 PC 빌드에서 확인"
