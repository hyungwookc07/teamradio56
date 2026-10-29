#!/usr/bin/env bash
# 리플레이 회귀 — C# 포팅이 파이썬과 같은 판단을 내는지 검증.
#
# data/replays/*.jsonl.gz 각각에 대해:
#   1) 파이썬 기준값 생성 (tools/replay_calls.py)
#   2) C# 러너 실행 (tests/TeamRadio56.Replay)
#   3) 수락 이벤트 시퀀스 비교 (순서·타이밍·데이터·결정적 멘트 'say' 완전 일치 요구)
#
# 사용법:  bash simhub/verify/replay-check.sh
set -euo pipefail

cd "$(dirname "$0")/../.."      # 저장소 루트

echo "▶ C# 리플레이 러너 빌드"
dotnet build simhub/tests/TeamRadio56.Replay/TeamRadio56.Replay.csproj \
    -c Release -v q --nologo

RUNNER=simhub/tests/TeamRadio56.Replay/bin/Release/net8.0/teamradio56-replay
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT

fail=0

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
    if ! "$RUNNER" --check-cache audio_cache; then
        fail=1
    fi
fi

# 무전 효과 출력 고정 — 배포 캐시(_rfx3)를 만드는 체인의 리팩터 가드
echo
echo "▶ 무전 효과(radiofx) 출력 md5 대조"
if python3 -c "import numpy, soundfile" > /dev/null 2>&1; then
    if ! python3 tools/radiofx_selftest.py; then
        fail=1
    fi
else
    echo "⏭  건너뜀 — numpy/soundfile 미설치"
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

for replay in data/replays/*.jsonl.gz; do
    name=$(basename "$replay" .jsonl.gz)
    for lang in en ko; do
        echo
        echo "▶ 리플레이: $name ($lang)"
        python3 tools/replay_calls.py --replay "$replay" --lang "$lang" \
            --out "$OUT/py_${name}_$lang.jsonl"
        "$RUNNER" --replay "$replay" --lang "$lang" --out "$OUT/cs_${name}_$lang.jsonl"
        if ! python3 tools/compare_calls.py \
            "$OUT/py_${name}_$lang.jsonl" "$OUT/cs_${name}_$lang.jsonl"; then
            fail=1
        fi
    done
done

echo
if [ "$fail" -ne 0 ]; then
    echo "❌ 리플레이 회귀 실패 — 포팅이 파이썬과 다른 판단을 냈습니다"
    exit 1
fi
echo "✅ 리플레이 회귀 전체 통과"
