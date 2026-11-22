#!/usr/bin/env bash
# 리플레이 회귀 — C# 포팅이 파이썬과 같은 판단을 내는지 검증.
#
# data/replays/*.jsonl.gz 각각에 대해:
#   1) 파이썬 기준값 생성 (tools/replay_calls.py)
#   2) C# 러너 실행 (tests/TeamRadio56.Replay)
#   3) 수락 이벤트 시퀀스 비교 (순서·타이밍·데이터·결정적 멘트 'say' 완전 일치 요구)
#   4) 레이스 종료 오라클 — 남은 랩 트레이스 파이썬 ↔ C# 일치, race_end_* 리플레이는
#      체커 뒤까지 가고 정답(.truth.json)과 마지막 랩·체커 콜(등급)·체커 뒤 무전(쿨다운·
#      요약·피트인)·피니시 뒤 침묵까지 대조 (tools/make_race_end_replays.py가 만들고
#      --check로 재생성 대조; <이름>.config.json은 두 하니스가 읽는 설정 덮어쓰기)
#   5) 기대 콜 — <이름>.expect.json이 있는 리플레이(rival_pit, align*, wing*, session_*, traffic_class_*)는
#      수락 콜을 정답 목록과 파이썬·C# 각각 대조 (tools/expect_calls.py — 세션 브리핑은 문장까지,
#      트래픽 클래스는 변형 풀의 풀·슬롯(클래스 호칭)까지; 생성기 tools/make_rival_pit_replay.py ·
#      tools/make_align_replay.py · tools/make_wing_replay.py · tools/make_session_replays.py ·
#      tools/make_traffic_class_replay.py --check)
#
# C# 플러그인 무전은 영어 + kokoro 음성 팩 고정이라 파이썬 ↔ C# 대조는 en만 한다.
# 파이썬 단독 앱의 한국어·edge 목소리는 파이썬 자체 검사(셀프테스트, ko 스모크)로만 돈다.
#
# 사용법:  bash simhub/verify/replay-check.sh
#
# 환경 변수 (CI가 쓴다):
#   TEAMRADIO56_STRICT=1         도구가 없어 건너뛰던 검사(pwsh, numpy/soundfile)를 실패로
#   TEAMRADIO56_REQUIRE_CACHE=1  audio_cache/*_rfx3.wav가 없으면 실패 (실캐시 Resolve 필수)
#   TEAMRADIO56_AUDIO_DIR=폴더   음성 팩 zip 폴더 — release.ps1 -AudioDir 대조 + 팩 설치 흐름
set -euo pipefail

cd "$(dirname "$0")/../.."      # 저장소 루트

echo "▶ C# 리플레이 러너 빌드"
dotnet build simhub/tests/TeamRadio56.Replay/TeamRadio56.Replay.csproj \
    -c Release -v q --nologo

RUNNER=simhub/tests/TeamRadio56.Replay/bin/Release/net8.0/teamradio56-replay
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT

fail=0
STRICT=${TEAMRADIO56_STRICT:-0}

# 선택 도구가 없을 때 — 로컬은 건너뛰고, CI(STRICT)는 조용히 빠지지 않게 실패
skip_or_fail() {
    if [ "$STRICT" = 1 ]; then
        echo "❌ $1 (TEAMRADIO56_STRICT=1)"
        fail=1
    else
        echo "⏭  $1"
    fi
}

echo
echo "▶ C# 멘트 풀 생성물 최신 (tools/gen_csharp_phrases.py --check)"
if ! python3 tools/gen_csharp_phrases.py --check; then
    fail=1
fi

echo
echo "▶ 멘트 풀 / 슬롯 포매팅 대조 (en, 사전 캐시 텍스트 전체 집합)"
python3 tools/dump_pregen.py --lang en --out "$OUT/py_pregen.txt"
"$RUNNER" --dump-pregen --out "$OUT/cs_pregen.txt"
if [ -s "$OUT/py_pregen.txt" ] && diff -q "$OUT/py_pregen.txt" "$OUT/cs_pregen.txt" > /dev/null; then
    echo "✅ 일치 — $(wc -l < "$OUT/py_pregen.txt")개 텍스트 동일"
else
    echo "❌ 멘트 풀 불일치 (또는 빈 출력):"
    diff "$OUT/py_pregen.txt" "$OUT/cs_pregen.txt" | head -20
    fail=1
fi

# 파이썬 덤프는 "톤 kokoro edge 텍스트" — edge 열은 파이썬 단독 앱 전용이라 C#과는 kokoro 열만
echo
echo "▶ 음성 팩 파일명 규약 대조 (kokoro md5 앞 20자 등)"
python3 tools/dump_pregen.py --cache-names --out "$OUT/py_names_full.txt"
cut -f1,2,4 "$OUT/py_names_full.txt" > "$OUT/py_names.txt"
"$RUNNER" --dump-cache-names --out "$OUT/cs_names.txt"
if [ -s "$OUT/py_names.txt" ] && diff -q "$OUT/py_names.txt" "$OUT/cs_names.txt" > /dev/null; then
    echo "✅ 일치 — $(wc -l < "$OUT/py_names.txt")개 파일명 동일"
else
    echo "❌ 캐시 파일명 불일치 (또는 빈 출력) — 플러그인이 음성 팩을 못 찾게 됩니다:"
    diff "$OUT/py_names.txt" "$OUT/cs_names.txt" | head -10
    fail=1
fi

# 문장 단위 사전 생성 (en 전용) — 문장 집합과 세그먼트 파일명 대조. 3단계는 HUD 리포트.

echo
echo "▶ 문장 단위 사전 생성 대상 대조 (en, 단계 1~3)"
python3 tools/dump_pregen.py --sentences --tiers 1,2,3 \
    --out "$OUT/py_sent.txt"
"$RUNNER" --dump-sentences --tier 3 --out "$OUT/cs_sent.txt"
if diff -q "$OUT/py_sent.txt" "$OUT/cs_sent.txt" > /dev/null; then
    echo "✅ 일치 — $(wc -l < "$OUT/py_sent.txt")개 문장 동일"
else
    echo "❌ 문장 집합 불일치 — 조립 멘트가 세그먼트 캐시를 못 찾게 됩니다:"
    diff "$OUT/py_sent.txt" "$OUT/cs_sent.txt" | head -20
    fail=1
fi

echo
echo "▶ 세그먼트 파일명 규약 대조 (_seg1.wav)"
python3 tools/dump_pregen.py --seg-names --tiers 1,2,3 \
    --out "$OUT/py_seg_names.txt"
"$RUNNER" --dump-seg-names --tier 3 --out "$OUT/cs_seg_names.txt"
if diff -q "$OUT/py_seg_names.txt" "$OUT/cs_seg_names.txt" > /dev/null; then
    echo "✅ 일치 — $(wc -l < "$OUT/py_seg_names.txt")개 파일명 동일"
else
    echo "❌ 세그먼트 파일명 불일치:"
    diff "$OUT/py_seg_names.txt" "$OUT/cs_seg_names.txt" | head -10
    fail=1
fi

# CI 음성 팩 렌더는 N개 잡이 --shard K/N로 나눠 렌더하고 합친다 — 빠지거나 겹치면
# 팩에 구멍이 나거나(렌더 후 게이트에서야 발견) 시간이 낭비된다
echo
echo "▶ 음성 팩 렌더 샤드 분할 (--shard K/3 합집합 = 전체, 겹침 없음)"
shard_ok=1
for pass in whole segments; do
    SHARD_ARGS=(--config __defaults_only__ --engine kokoro --dry-run)
    if [ "$pass" = segments ]; then
        SHARD_ARGS+=(--segments --tiers 1,2,3)
    fi
    python3 tools/pregen_audio.py "${SHARD_ARGS[@]}" | grep '^  ' | sort > "$OUT/shard_all_$pass.txt"
    : > "$OUT/shard_union_$pass.txt"
    for k in 1 2 3; do
        python3 tools/pregen_audio.py "${SHARD_ARGS[@]}" --shard "$k/3" | grep '^  ' \
            >> "$OUT/shard_union_$pass.txt"
    done
    sort "$OUT/shard_union_$pass.txt" -o "$OUT/shard_union_$pass.txt"
    if [ -s "$OUT/shard_all_$pass.txt" ] && cmp -s "$OUT/shard_all_$pass.txt" "$OUT/shard_union_$pass.txt"; then
        echo "✅ $pass: $(wc -l < "$OUT/shard_all_$pass.txt")개 = 샤드 3개 합집합"
    else
        echo "❌ $pass: 샤드 합집합이 전체와 다름 (빠짐 '<' / 겹침·추가 '>'):"
        diff "$OUT/shard_all_$pass.txt" "$OUT/shard_union_$pass.txt" | head -10
        shard_ok=0
    fi
done
[ "$shard_ok" = 1 ] || fail=1

# 커버리지 퍼즈 — 순수 조립 함수에 도메인 안팎 값을 넣어 가상 음성 팩으로 판정.
# 도메인 안 값은 통문장/조립으로, 밖은 억제·클램프·허용된 무음으로만 나가야 한다.
echo
echo "▶ 커버리지 퍼즈 (가상 음성 팩, 파이썬 ↔ C#, en)"
python3 tools/coverage_fuzz.py gen --seed 56 --out "$OUT/fuzz_cases.jsonl"
python3 tools/coverage_fuzz.py eval --cases "$OUT/fuzz_cases.jsonl" --lang en \
    --out "$OUT/fuzz_py_en.tsv"
"$RUNNER" --fuzz-eval "$OUT/fuzz_cases.jsonl" \
    --out "$OUT/fuzz_cs_en.tsv"
if ! python3 tools/coverage_fuzz.py check --py "$OUT/fuzz_py_en.tsv" \
    --cs "$OUT/fuzz_cs_en.tsv" --lang en; then
    fail=1
fi

# 생성된 오디오 캐시가 로컬에 있으면 실제 Resolve까지 확인 (선택 게이트)
if [ -d audio_cache ] && ls audio_cache/*_rfx3.wav > /dev/null 2>&1; then
    echo
    echo "▶ 실캐시 Resolve 확인 (audio_cache/)"
    if ! "$RUNNER" --check-cache audio_cache --tier 3; then
        fail=1
    fi
elif [ "${TEAMRADIO56_REQUIRE_CACHE:-0}" = 1 ]; then
    echo
    echo "❌ audio_cache/*_rfx3.wav 없음 — 실캐시 Resolve 필수 (TEAMRADIO56_REQUIRE_CACHE=1)"
    fail=1
fi

# 숫자 표기 — C#이 파이썬과 같은 숫자를 말해야 사전 생성 문장과 맞는다
echo
echo "▶ 고정소수점 표기 대조 (Messages.PyFixed ↔ 파이썬 f-string)"
python3 tools/fixed_check.py --in "$OUT/fixed_in.tsv" --expect "$OUT/fixed_py.txt"
"$RUNNER" --fixed-check "$OUT/fixed_in.tsv" --out "$OUT/fixed_cs.txt"
if diff -q "$OUT/fixed_py.txt" "$OUT/fixed_cs.txt" > /dev/null; then
    echo "✅ 일치 — $(wc -l < "$OUT/fixed_py.txt")개 표기 동일 (반올림 경계 포함)"
else
    echo "❌ 숫자 표기 불일치:"
    paste "$OUT/fixed_in.tsv" "$OUT/fixed_py.txt" "$OUT/fixed_cs.txt" \
        | awk -F'\t' '$3 != $4' | head -10
    fail=1
fi

# 클래스 정규화 — 실제 LMU 클래스 문자열 후보를 계열·서열·호칭·빠른 클래스·진단 로그로
# 판정한 결과가 파이썬 vehicle_class와 C# VehicleClass에서 바이트 단위로 같아야 한다
echo
echo "▶ 클래스 정규화 대조 (vehicle_class ↔ VehicleClass)"
python3 tools/class_check.py --in "$OUT/class_in.tsv" --expect "$OUT/class_py.tsv"
"$RUNNER" --class-check "$OUT/class_in.tsv" --out "$OUT/class_cs.tsv"
if [ -s "$OUT/class_py.tsv" ] && cmp -s "$OUT/class_py.tsv" "$OUT/class_cs.tsv"; then
    echo "✅ 일치 — $(wc -l < "$OUT/class_py.tsv")줄 동일 (계열·서열·호칭·같은 클래스·빠른 클래스·진단·페이스)"
else
    echo "❌ 클래스 정규화 불일치 (또는 빈 출력):"
    diff "$OUT/class_py.tsv" "$OUT/class_cs.tsv" | head -10
    fail=1
fi

# 업데이터 Core — 매니페스트·버전 비교·DLL 교체/롤백·zip-slip·sha·음성 팩 표식.
# 로드된 DLL 이름 바꾸기 자체는 Windows 전용이라 가짜 파일 조작으로 흉내 낸다.
echo
echo "▶ 업데이터 셀프테스트 (임시 폴더, 네트워크 없음 — HTTP는 로컬 서버)"
if ! "$RUNNER" --update-selftest; then
    fail=1
fi

# 한 목소리 정책 — 플러그인 목소리는 kokoro 음성 팩 하나. 팩에 없는 콜, 팩이 없거나
# 설치가 끊긴 세션의 콜은 다른 목소리로 새지 않고 무음(자막만)
echo
echo "▶ 한 목소리 정책 셀프테스트 (C#, kokoro 음성 팩 전용)"
if ! "$RUNNER" --voice-selftest; then
    fail=1
fi

echo
echo "▶ 한 목소리 정책 셀프테스트 (파이썬 — edge·ko·섞임 허용 포함, 단독 앱 정책)"
if ! python3 tools/voice_policy_selftest.py; then
    fail=1
fi

# 파이썬 결정표(engine × lang × pack) 중 플러그인에 해당하는 kokoro·en 4행만 대조한다.
# 파이썬 단독 앱이 팩 없이 edge로 대신 말하는 자리가 플러그인에선 무음(none)이어야 하고,
# 사유(note)는 같은 문자열이어야 한다.
echo
echo "▶ 음성 팩 상태 결정표 대조 (파이썬 kokoro·en 행 ↔ C#)"
python3 tools/voice_policy_selftest.py --dump-table --out "$OUT/py_voice_table_all.tsv"
awk -F'\t' -v OFS='\t' '$1 == "kokoro" && $2 == "en" { v = ($4 == "edge") ? "none" : $4; print $3, v, $5 }' \
    "$OUT/py_voice_table_all.tsv" > "$OUT/py_voice_table.tsv"
"$RUNNER" --dump-voice-table --out "$OUT/cs_voice_table.tsv"
if [ "$(wc -l < "$OUT/py_voice_table.tsv")" != 4 ]; then
    echo "❌ 파이썬 결정표에서 kokoro·en 4행을 찾지 못함 (형식이 바뀌었나?):"
    head -5 "$OUT/py_voice_table_all.tsv"
    fail=1
elif diff -q "$OUT/py_voice_table.tsv" "$OUT/cs_voice_table.tsv" > /dev/null; then
    echo "✅ 일치 — 팩 상태 4종 (ok/partial → kokoro, incomplete/none → 무음) 사유까지 동일"
else
    echo "❌ 음성 팩 상태 결정표 불일치 (팩 상태 / 목소리 / 사유):"
    diff "$OUT/py_voice_table.tsv" "$OUT/cs_voice_table.tsv" | head -10
    fail=1
fi
if ! python3 tools/voice_policy_selftest.py --pack-fixtures "$OUT/packstate" \
    || ! "$RUNNER" --pack-state-check "$OUT/packstate"; then
    fail=1
fi

# 설정 파일 호환 — 예전 버전의 키(EngineMode=python, EdgeVoice, VoiceLanguage=ko …)가
# 남은 teamradio56.settings.txt를 오류 없이 읽고, 다음 저장 때 지운다
echo
echo "▶ 설정 파일 호환 셀프테스트 (옛 키 무시)"
if ! "$RUNNER" --settings-selftest; then
    fail=1
fi

# 릴리스 도구 — 음성 팩 기술자와 매니페스트 형식을 Core 파서로 대조하고, pwsh가 있으면
# release.ps1을 검증 모드(빌드·게시 없음)로 돌려 그 출력으로 사용자 업데이트 흐름을 재현한다.
# TEAMRADIO56_AUDIO_DIR에 팩 zip 폴더를 주면 음성 팩 설치 흐름까지 돈다 (선택).
echo
echo "▶ 릴리스 도구 (voice_lines/audio_pack.txt · release.ps1 · release.bat)"
if ! "$RUNNER" --release-check voice_lines/audio_pack.txt; then
    fail=1
fi
# bat은 CRLF여야 cmd가 줄을 제대로 읽고, ps1은 BOM이 있어야 Windows PowerShell 5가 한글을 읽는다
for bat in update.bat release.bat; do
    if [ "$(grep -c $'\r$' "$bat")" != "$(wc -l < "$bat")" ] || [ -n "$(tail -c1 "$bat")" ]; then
        echo "❌ $bat 줄 끝이 CRLF가 아닙니다"
        fail=1
    fi
done
for ps in update.ps1 release.ps1 simhub/ci/install-simhub.ps1; do
    if [ "$(head -c3 "$ps" | od -An -tx1 | tr -d ' ')" != "efbbbf" ]; then
        echo "❌ $ps 에 UTF-8 BOM이 없습니다"
        fail=1
    fi
done
PWSH=$(command -v pwsh || true)
if [ -z "$PWSH" ] && [ -x "$HOME/.dotnet/tools/pwsh" ]; then
    PWSH="$HOME/.dotnet/tools/pwsh"
fi
if [ -n "$PWSH" ]; then
    if ! "$PWSH" -NoProfile -NonInteractive -File simhub/verify/ps-check.ps1 \
        update.ps1 release.ps1 simhub/ci/install-simhub.ps1 simhub/verify/ps-check.ps1; then
        fail=1
    fi
    mkdir -p "$OUT/rel/dlls"
    printf 'MZ release-check core' > "$OUT/rel/dlls/TeamRadio56.Core.dll"
    printf 'MZ release-check simhub' > "$OUT/rel/dlls/TeamRadio56.SimHub.dll"
    REL_AUDIO=()
    if [ -n "${TEAMRADIO56_AUDIO_DIR:-}" ]; then
        REL_AUDIO=(-AudioDir "$TEAMRADIO56_AUDIO_DIR")
    fi
    if "$PWSH" -NoProfile -NonInteractive -File release.ps1 -TestDllDir "$OUT/rel/dlls" \
        -OutDir "$OUT/rel/out" -Notes "릴리스 도구 검증" ${REL_AUDIO[@]+"${REL_AUDIO[@]}"} \
        > "$OUT/rel/log.txt" 2>&1; then
        echo "✅ release.ps1 검증 모드 실행 — $(ls "$OUT/rel/out" | wc -l)개 파일"
        REL_CHECK_AUDIO=()
        if [ -n "${TEAMRADIO56_AUDIO_DIR:-}" ]; then
            REL_CHECK_AUDIO=(--audio-dir "$TEAMRADIO56_AUDIO_DIR")
        fi
        if ! "$RUNNER" --release-check voice_lines/audio_pack.txt \
            --manifest "$OUT/rel/out/teamradio56-release.txt" \
            ${REL_CHECK_AUDIO[@]+"${REL_CHECK_AUDIO[@]}"}; then
            fail=1
        fi
    else
        echo "❌ release.ps1 검증 모드 실패:"
        cat "$OUT/rel/log.txt"
        fail=1
    fi

    # Actions가 쓰는 -Ci 경로 — 메타 파일, 태그 대조, 한글 요약은 UTF-8 파일로.
    # 실패 경로는 멈춤(Read-Host) 없이 바로 끝나야 한다 (러너가 무한 대기하지 않게).
    REL_NUM=$(sed -n 's/.*public const string Version = "\([0-9]*\.[0-9]*\.[0-9]*\).*/\1/p' \
        simhub/src/TeamRadio56.SimHub/TeamRadio56Plugin.cs)
    printf '릴리스 도구 검증 (CI)\n둘째 줄\n' > "$OUT/rel/notes.txt"
    if "$PWSH" -NoProfile -NonInteractive -File release.ps1 -Ci -TestDllDir "$OUT/rel/dlls" \
        -OutDir "$OUT/rel/ci/assets" -MetaDir "$OUT/rel/ci/meta" -ExpectTag "v$REL_NUM" \
        -NotesFile "$OUT/rel/notes.txt" > "$OUT/rel/ci.log" 2>&1 \
        && grep -qx "tag=v$REL_NUM" "$OUT/rel/ci/meta/release-info.txt" \
        && grep -q '^title=teamradio56 ' "$OUT/rel/ci/meta/release-info.txt" \
        && grep -qx "plugin_sha256=$(sha256sum "$OUT/rel/ci/assets/teamradio56-plugin-$REL_NUM.zip" | cut -d' ' -f1)" \
            "$OUT/rel/ci/meta/release-info.txt" \
        && grep -q '^릴리스 도구 검증 (CI) 둘째 줄$' "$OUT/rel/ci/meta/release-notes.md" \
        && grep -qx 'notes=릴리스 도구 검증 (CI) 둘째 줄' "$OUT/rel/ci/assets/teamradio56-release.txt"; then
        echo "✅ release.ps1 -Ci 검증 모드 — 메타·태그·한글 요약 파일"
    else
        echo "❌ release.ps1 -Ci 검증 모드 실패:"
        cat "$OUT/rel/ci.log"
        fail=1
    fi
    # 같은 실행이 새 음성 팩을 렌더한 릴리스 — 아티팩트의 기술자(-AudioPackFile)로 매니페스트를 만든다
    sed -e 's/^pack=.*/pack=kokoro-v999/' -e 's/^tag=.*/tag=audio-kokoro-v999/' \
        voice_lines/audio_pack.txt > "$OUT/rel/new_pack.txt"
    if "$PWSH" -NoProfile -NonInteractive -File release.ps1 -Ci -TestDllDir "$OUT/rel/dlls" \
        -OutDir "$OUT/rel/ci3/assets" -MetaDir "$OUT/rel/ci3/meta" -ExpectTag "v$REL_NUM" \
        -AudioPackFile "$OUT/rel/new_pack.txt" > "$OUT/rel/ci3.log" 2>&1 \
        && grep -qx 'audio_pack=kokoro-v999' "$OUT/rel/ci3/assets/teamradio56-release.txt" \
        && grep -qx 'audio_tag=audio-kokoro-v999' "$OUT/rel/ci3/meta/release-info.txt" \
        && "$RUNNER" --release-check "$OUT/rel/new_pack.txt" \
            --manifest "$OUT/rel/ci3/assets/teamradio56-release.txt" > "$OUT/rel/ci3-check.log" 2>&1; then
        echo "✅ release.ps1 -Ci -AudioPackFile — 새 팩 기술자로 매니페스트"
    else
        echo "❌ release.ps1 -Ci -AudioPackFile 실패:"
        cat "$OUT/rel/ci3.log" "$OUT/rel/ci3-check.log" 2> /dev/null || true
        fail=1
    fi
    if timeout 60 "$PWSH" -NoProfile -NonInteractive -File release.ps1 -Ci -TestDllDir "$OUT/rel/dlls" \
        -OutDir "$OUT/rel/ci2/assets" -MetaDir "$OUT/rel/ci2/meta" -ExpectTag v0.0.0 \
        > "$OUT/rel/ci2.log" 2>&1; then
        echo "❌ release.ps1 -Ci가 다른 태그(v0.0.0)를 거부하지 않음"
        fail=1
    elif [ "$?" -ne 1 ]; then
        echo "❌ release.ps1 -Ci 태그 거부가 종료 코드 1이 아님 (멈춤/시간 초과?)"
        cat "$OUT/rel/ci2.log"
        fail=1
    else
        echo "✅ release.ps1 -Ci 태그 불일치 거부 (멈춤 없음)"
    fi
else
    skip_or_fail "release.ps1 실행 검사 건너뜀 — pwsh 없음 (dotnet tool install -g PowerShell)"
fi

# 음성 팩 zip 도구 — 분할·이름·기술자 갱신·verify 거부 규칙 (합성 wav, 캐시 불필요)
echo
echo "▶ 음성 팩 도구 셀프테스트 (tools/pack_audio.py)"
if ! python3 tools/pack_audio.py selftest; then
    fail=1
fi

# release 워크플로 판단 — 게시/이어받기/dry run, 새 음성 팩 필요 여부와 ID, 게시 호출 순서.
# 가짜 gh/curl/git으로 돌린다 (GitHub Actions는 로컬에서 못 돌리므로 스크립트만이라도)
echo
echo "▶ 릴리스 흐름 (preflight · 음성 팩 판단 · 게시 — 가짜 gh/curl/git, 네트워크 없음)"
if command -v jq > /dev/null; then
    if ! bash simhub/verify/release-flow-check.sh; then
        fail=1
    fi
else
    skip_or_fail "건너뜀 — jq 없음"
fi

# 무전 효과 출력 고정 — 배포 캐시(_rfx3)를 만드는 체인의 리팩터 가드
echo
echo "▶ 무전 효과(radiofx) 출력 md5 대조"
if python3 -c "import numpy, soundfile" > /dev/null 2>&1; then
    if ! python3 tools/radiofx_selftest.py; then
        fail=1
    fi
else
    skip_or_fail "건너뜀 — numpy/soundfile 미설치"
fi

# 문장 세그먼트 조립 — 파이썬 radio_assemble과 C# RadioAssembler 출력 바이트 대조
echo
echo "▶ 세그먼트 조립 바이트 대조 (radio_assemble ↔ RadioAssembler)"
mkdir -p "$OUT/asm"
if python3 tools/assemble_check.py --dir "$OUT/asm" \
    && "$RUNNER" --assemble-check "$OUT/asm"; then
    asm_n=$(wc -l < "$OUT/asm/messages.tsv")
    asm_bad=0
    for ((i = 0; i < asm_n; i++)); do
        py="$OUT/asm/py/$i.wav"
        cs="$OUT/asm/cs/$i.wav"
        if [ ! -e "$py" ] && [ ! -e "$cs" ]; then
            continue                    # 양쪽 다 미스 — 통과
        fi
        if [ ! -e "$py" ] || [ ! -e "$cs" ] || ! cmp -s "$py" "$cs"; then
            echo "❌ 조립 결과 다름 — #$i: $(sed -n "$((i + 1))p" "$OUT/asm/messages.tsv")"
            asm_bad=1
        fi
    done
    if [ "$asm_bad" -eq 0 ]; then
        echo "✅ 일치 — $asm_n건 바이트 동일 (미스 포함)"
    else
        fail=1
    fi
else
    echo "❌ 조립 대조 실행 실패"
    fail=1
fi

echo
echo "▶ 레이스 종료 리플레이 재생성 대조"
python3 tools/make_race_end_replays.py --check || fail=1

echo
echo "▶ 경쟁자 피트 리플레이 재생성 대조"
python3 tools/make_rival_pit_replay.py --check || fail=1

echo
echo "▶ 얼라인 리플레이 재생성 대조 (스위퍼 직전 접촉 · 기준 없음 · 펑크 · 경미 · 심각 · 게이트 하나 어김 · 기준 재수집 · 리어 토 게걸음 · 대기 콜 중 다음 충격 리포트)"
python3 tools/make_align_replay.py --check || fail=1

echo
echo "▶ 윙 리플레이 재생성 대조 (정적 필드 · 펑크 · 중속 접촉 뒤 긴 직선 · 스플리터 손상 · 기준 재수집 · 대기 콜 중 다음 충격 리포트)"
python3 tools/make_wing_replay.py --check || fail=1

echo
echo "▶ 세션 브리핑 리플레이 재생성 대조 (연습·퀄리·웜업, 시작·중간 합류)"
python3 tools/make_session_replays.py --check || fail=1

echo
echo "▶ 트래픽 클래스 리플레이 재생성 대조 (Hyper·LMP3·LMP2 계열·모르는 클래스 페이스)"
python3 tools/make_traffic_class_replay.py --check || fail=1

# 브리핑 변형(FNV-1a 시드)·체커 콜 등급·체커 뒤 무전 규칙·피니시 뒤 침묵 (파이썬 단위 검사 —
# C#은 퍼즈·리플레이 대조가 같은 결과를 요구한다)
echo
echo "▶ 무전 흐름 셀프테스트 (브리핑 변형 · 체커 등급 · 피니시 뒤 침묵)"
python3 tools/radio_flow_selftest.py || fail=1

for replay in data/replays/*.jsonl.gz; do
    name=$(basename "$replay" .jsonl.gz)
    truth="data/replays/$name.truth.json"
    echo
    echo "▶ 리플레이: $name (en)"
    python3 tools/replay_calls.py --replay "$replay" --lang en \
        --out "$OUT/py_${name}_en.jsonl" --race-end-trace "$OUT/py_${name}_trace.jsonl"
    "$RUNNER" --replay "$replay" --out "$OUT/cs_${name}_en.jsonl" \
        --race-end-trace "$OUT/cs_${name}_trace.jsonl"
    if ! python3 tools/compare_calls.py \
        "$OUT/py_${name}_en.jsonl" "$OUT/cs_${name}_en.jsonl"; then
        fail=1
    fi

    # 기대 콜 — 두 엔진이 같이 틀려도 잡히게 생성기가 적은 정답과 직접 대조
    expect="data/replays/$name.expect.json"
    if [ -f "$expect" ]; then
        echo
        echo "▶ 기대 콜 대조: $name"
        if ! python3 tools/expect_calls.py --expect "$expect" \
            --calls "$OUT/py_${name}_en.jsonl" --calls "$OUT/cs_${name}_en.jsonl"; then
            fail=1
        fi
    fi

    # 남은 랩 트레이스 파이썬 ↔ C#, 정답이 있으면 마지막 랩·체커 콜까지
    echo
    echo "▶ 레이스 종료 오라클: $name"
    ORACLE_ARGS=(--calls "$OUT/py_${name}_en.jsonl" --calls "$OUT/cs_${name}_en.jsonl"
                 --trace "$OUT/py_${name}_trace.jsonl" --trace "$OUT/cs_${name}_trace.jsonl")
    if [ -f "$truth" ]; then
        ORACLE_ARGS+=(--truth "$truth")
    fi
    if ! python3 tools/race_end_oracle.py "${ORACLE_ARGS[@]}"; then
        fail=1
    fi

    # 코코로 커버리지 (en) — 발화가 전부 통문장 클립이나 문장 세그먼트로 나가야 한다.
    # 템플릿·톤이 사전 생성 도메인을 벗어나면 런타임에선 말하지 않고 자막만 나가므로 여기서 막는다.
    echo
    echo "▶ 코코로 커버리지: $name (en, 통문장 + 문장 세그먼트, 기준 100%)"
    cov="$OUT/cov_$name.txt"
    if "$RUNNER" --replay "$replay" --coverage --tier 3 \
        --min 100 > "$cov"; then
        echo "✅ $(grep '^발화 ' "$cov")"
    else
        echo "❌ 커버리지 미달 — 미스 멘트:"
        grep -E '^  miss |빠진 문장' "$cov" | head -40 || true
        grep -E '^발화 |^❌' "$cov" || true
        fail=1
    fi
done

# 파이썬 단독 앱의 한국어 무전 — C#은 더 이상 대조하지 않지만, 파이썬 쪽이 깨지지 않게
# 멘트 풀 덤프·퍼즈 규칙(band none → NONE)·리플레이 실행만 확인한다
echo
echo "▶ 파이썬 전용 ko 스모크 (멘트 풀 · 퍼즈 규칙 · 리플레이 실행, C# 대조 없음)"
ko_ok=1
python3 tools/dump_pregen.py --lang ko --out "$OUT/py_pregen_ko.txt" || ko_ok=0
[ -s "$OUT/py_pregen_ko.txt" ] || ko_ok=0
if python3 tools/coverage_fuzz.py eval --cases "$OUT/fuzz_cases.jsonl" --lang ko \
    --out "$OUT/fuzz_py_ko.tsv" > "$OUT/fuzz_ko.log" 2>&1; then
    python3 tools/coverage_fuzz.py check --py "$OUT/fuzz_py_ko.tsv" --lang ko \
        >> "$OUT/fuzz_ko.log" 2>&1 || ko_ok=0
else
    ko_ok=0
fi
for replay in data/replays/*.jsonl.gz; do
    name=$(basename "$replay" .jsonl.gz)
    if ! python3 tools/replay_calls.py --replay "$replay" --lang ko \
        --out "$OUT/py_${name}_ko.jsonl" 2> "$OUT/py_${name}_ko.log" \
        || [ ! -s "$OUT/py_${name}_ko.jsonl" ]; then
        echo "   ko 리플레이 실패: $name"
        cat "$OUT/py_${name}_ko.log"
        ko_ok=0
    fi
done
if [ "$ko_ok" = 1 ]; then
    echo "✅ 파이썬 ko — 멘트 $(wc -l < "$OUT/py_pregen_ko.txt")개, 퍼즈 규칙 통과, 리플레이 $(ls data/replays/*.jsonl.gz | wc -l)개 실행"
else
    echo "❌ 파이썬 ko 스모크 실패:"
    cat "$OUT/fuzz_ko.log" 2> /dev/null | tail -20 || true
    fail=1
fi

echo
if [ "$fail" -ne 0 ]; then
    echo "❌ 리플레이 회귀 실패 — 포팅이 파이썬과 다른 판단을 냈습니다"
    exit 1
fi
echo "✅ 리플레이 회귀 전체 통과"
