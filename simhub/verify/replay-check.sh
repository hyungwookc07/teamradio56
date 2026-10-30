#!/usr/bin/env bash
# 리플레이 회귀 — C# 포팅이 파이썬과 같은 판단을 내는지 검증.
#
# data/replays/*.jsonl.gz 각각에 대해:
#   1) 파이썬 기준값 생성 (tools/replay_calls.py)
#   2) C# 러너 실행 (tests/TeamRadio56.Replay)
#   3) 수락 이벤트 시퀀스 비교 (순서·타이밍·데이터·결정적 멘트 'say' 완전 일치 요구)
#   4) 레이스 종료 오라클 — 남은 랩 트레이스(en) 파이썬 ↔ C# 일치, race_end_* 리플레이는
#      체커까지 가고 정답(.truth.json)과 마지막 랩·체커 콜까지 대조
#      (tools/make_race_end_replays.py가 만들고 --check로 재생성 대조)
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

for lang in en ko; do
    echo
    echo "▶ 멘트 풀 / 슬롯 포매팅 대조 ($lang, 사전 캐시 텍스트 전체 집합)"
    python3 tools/dump_pregen.py --lang "$lang" --out "$OUT/py_pregen_$lang.txt"
    "$RUNNER" --dump-pregen --lang "$lang" --out "$OUT/cs_pregen_$lang.txt"
    if diff -q "$OUT/py_pregen_$lang.txt" "$OUT/cs_pregen_$lang.txt" > /dev/null; then
        echo "✅ 일치 — $(wc -l < "$OUT/py_pregen_$lang.txt")개 텍스트 동일"
    else
        echo "❌ 멘트 풀 불일치 ($lang):"
        diff "$OUT/py_pregen_$lang.txt" "$OUT/cs_pregen_$lang.txt" | head -20
        fail=1
    fi
done

echo
echo "▶ 오디오 캐시 파일명 규약 대조 (md5 앞 20자 등)"
python3 tools/dump_pregen.py --cache-names --out "$OUT/py_names.txt"
"$RUNNER" --dump-cache-names --out "$OUT/cs_names.txt"
if diff -q "$OUT/py_names.txt" "$OUT/cs_names.txt" > /dev/null; then
    echo "✅ 일치 — $(wc -l < "$OUT/py_names.txt")개 파일명 동일"
else
    echo "❌ 캐시 파일명 불일치 — builtin이 배포 캐시를 못 찾게 됩니다:"
    diff "$OUT/py_names.txt" "$OUT/cs_names.txt" | head -10
    fail=1
fi

# 문장 단위 사전 생성 (en 전용) — 문장 집합과 세그먼트 파일명 대조.
# 트랙 이름 목록은 2단계 브리핑용, 파일이 생기면 양쪽에 같이 넘긴다.
TRACKS_ARGS=()
if [ -f voice_lines/track_names_en.txt ]; then
    TRACKS_ARGS=(--tracks voice_lines/track_names_en.txt)
fi

echo
echo "▶ 문장 단위 사전 생성 대상 대조 (en, 단계 1~2)"
python3 tools/dump_pregen.py --sentences --tiers 1,2 ${TRACKS_ARGS[@]+"${TRACKS_ARGS[@]}"} \
    --out "$OUT/py_sent.txt"
"$RUNNER" --dump-sentences --tier 2 ${TRACKS_ARGS[@]+"${TRACKS_ARGS[@]}"} --out "$OUT/cs_sent.txt"
if diff -q "$OUT/py_sent.txt" "$OUT/cs_sent.txt" > /dev/null; then
    echo "✅ 일치 — $(wc -l < "$OUT/py_sent.txt")개 문장 동일"
else
    echo "❌ 문장 집합 불일치 — 조립 멘트가 세그먼트 캐시를 못 찾게 됩니다:"
    diff "$OUT/py_sent.txt" "$OUT/cs_sent.txt" | head -20
    fail=1
fi

echo
echo "▶ 세그먼트 파일명 규약 대조 (_seg1.wav)"
python3 tools/dump_pregen.py --seg-names --tiers 1,2 ${TRACKS_ARGS[@]+"${TRACKS_ARGS[@]}"} \
    --out "$OUT/py_seg_names.txt"
"$RUNNER" --dump-seg-names --tier 2 ${TRACKS_ARGS[@]+"${TRACKS_ARGS[@]}"} --out "$OUT/cs_seg_names.txt"
if diff -q "$OUT/py_seg_names.txt" "$OUT/cs_seg_names.txt" > /dev/null; then
    echo "✅ 일치 — $(wc -l < "$OUT/py_seg_names.txt")개 파일명 동일"
else
    echo "❌ 세그먼트 파일명 불일치:"
    diff "$OUT/py_seg_names.txt" "$OUT/cs_seg_names.txt" | head -10
    fail=1
fi

# 생성된 오디오 캐시가 로컬에 있으면 실제 Resolve까지 확인 (선택 게이트)
if [ -d audio_cache ] && ls audio_cache/*_rfx3.wav > /dev/null 2>&1; then
    echo
    echo "▶ 실캐시 Resolve 확인 (audio_cache/)"
    if ! "$RUNNER" --check-cache audio_cache --tier 2 ${TRACKS_ARGS[@]+"${TRACKS_ARGS[@]}"}; then
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

# 업데이터 Core — 매니페스트·버전 비교·DLL 교체/롤백·zip-slip·sha·음성 팩 표식.
# 로드된 DLL 이름 바꾸기 자체는 Windows 전용이라 가짜 파일 조작으로 흉내 낸다.
echo
echo "▶ 업데이터 셀프테스트 (임시 폴더, 네트워크 없음 — HTTP는 로컬 서버)"
if ! "$RUNNER" --update-selftest; then
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
for ps in update.ps1 release.ps1; do
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
        update.ps1 release.ps1 simhub/verify/ps-check.ps1; then
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
else
    skip_or_fail "release.ps1 실행 검사 건너뜀 — pwsh 없음 (dotnet tool install -g PowerShell)"
fi

# 음성 팩 zip 도구 — 분할·이름·기술자 갱신·verify 거부 규칙 (합성 wav, 캐시 불필요)
echo
echo "▶ 음성 팩 도구 셀프테스트 (tools/pack_audio.py)"
if ! python3 tools/pack_audio.py selftest; then
    fail=1
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

for replay in data/replays/*.jsonl.gz; do
    name=$(basename "$replay" .jsonl.gz)
    truth="data/replays/$name.truth.json"
    for lang in en ko; do
        echo
        echo "▶ 리플레이: $name ($lang)"
        PY_TRACE=()
        CS_TRACE=()
        if [ "$lang" = en ]; then
            PY_TRACE=(--race-end-trace "$OUT/py_${name}_trace.jsonl")
            CS_TRACE=(--race-end-trace "$OUT/cs_${name}_trace.jsonl")
        fi
        python3 tools/replay_calls.py --replay "$replay" --lang "$lang" \
            --out "$OUT/py_${name}_$lang.jsonl" ${PY_TRACE[@]+"${PY_TRACE[@]}"}
        "$RUNNER" --replay "$replay" --lang "$lang" --out "$OUT/cs_${name}_$lang.jsonl" \
            ${CS_TRACE[@]+"${CS_TRACE[@]}"}
        if ! python3 tools/compare_calls.py \
            "$OUT/py_${name}_$lang.jsonl" "$OUT/cs_${name}_$lang.jsonl"; then
            fail=1
        fi
    done

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
    # 템플릿·톤이 사전 생성 도메인을 벗어나면 런타임에선 조용히 edge로 빠지므로 여기서 막는다.
    echo
    echo "▶ 코코로 커버리지: $name (en, 통문장 + 문장 세그먼트, 기준 100%)"
    cov="$OUT/cov_$name.txt"
    if "$RUNNER" --replay "$replay" --lang en --coverage --tier 2 \
        ${TRACKS_ARGS[@]+"${TRACKS_ARGS[@]}"} --min 100 > "$cov"; then
        echo "✅ $(grep '^발화 ' "$cov")"
    else
        echo "❌ 커버리지 미달 — 미스 멘트:"
        grep -E '^  miss |빠진 문장' "$cov" | head -40 || true
        grep -E '^발화 |^❌' "$cov" || true
        fail=1
    fi
    if grep -q '^트랙 이름 추가' "$cov"; then
        grep '^트랙 이름 추가' "$cov" | sed 's/^/⚠️  /'
    fi
done

echo
if [ "$fail" -ne 0 ]; then
    echo "❌ 리플레이 회귀 실패 — 포팅이 파이썬과 다른 판단을 냈습니다"
    exit 1
fi
echo "✅ 리플레이 회귀 전체 통과"
