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

# 설정 화면 문자열 — ko/en 키가 같고, 코드가 쓰는 키가 다 있고, 음성 팩이 없을 때의
# 안내는 [음성 팩 업데이트]를 가리킨다 (다른 목소리로 대신 말한다고 하지 않는다)
python3 - <<'PY'
import re, sys
loc = open("src/TeamRadio56.SimHub/Loc.cs", encoding="utf-8").read()
ko = loc[loc.index("Dictionary<string, string> Ko"):loc.index("Dictionary<string, string> En")]
en = loc[loc.index("Dictionary<string, string> En"):]
def table(t):
    return dict(re.findall(r'^\s*\{ "([^"]+)", "((?:[^"\\]|\\.)*)"', t, re.M))
k, e = table(ko), table(en)
bad = []
if set(k) != set(e):
    bad.append("ko/en 키가 다름: " + ", ".join(sorted(set(k) ^ set(e))))
code = "".join(open("src/TeamRadio56.SimHub/" + f, encoding="utf-8").read()
               for f in ("SettingsControl.cs", "TeamRadio56Plugin.cs"))
used = set(re.findall(r'\bL\("([^"]+)"\)', code))
for note in ("no_kokoro", "pack_incomplete", "pack_partial"):
    used.add("voice_note_" + note)          # "voice_note_" + VoicePolicy.Note*
missing = sorted(used - set(k))
if missing:
    bad.append("Loc에 없는 키: " + ", ".join(missing))
for key in ("pack_missing_banner", "voice_note_no_kokoro", "voice_note_pack_incomplete",
            "test_speak_no_pack"):
    if "[음성 팩 업데이트]" not in k.get(key, "") or "[Update voice pack]" not in e.get(key, ""):
        bad.append(key + ": [음성 팩 업데이트] / [Update voice pack] 안내가 없음")
for key, text in list(k.items()) + list(e.items()):
    if re.search(r"\bedge\b|python|파이썬|Windows 음성|Windows speech", text, re.I):
        bad.append(key + ": 빠진 기능을 가리킴 — " + text[:60])
if bad:
    print("❌ 설정 화면 문자열(Loc.cs):")
    for b in bad:
        print("   " + b)
    sys.exit(1)
print(f"✅ 설정 화면 문자열 {len(k)}개 (ko/en 일치, 음성 팩 안내 확인)")
PY

echo
echo "✅ 컴파일 검증 통과 — 실제 SimHub API 일치는 사용자 PC 빌드에서 확인"
