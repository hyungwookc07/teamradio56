#!/usr/bin/env bash
# 리플레이 회귀 — C# 포팅이 파이썬과 같은 판단을 내는지 검증.
#
# data/replays/*.jsonl.gz 각각에 대해:
#   1) 파이썬 기준값 생성 (tools/replay_calls.py)
#   2) C# 러너 실행 (tests/TeamRadio56.Replay)
#   3) 수락 이벤트 시퀀스 비교 (순서·타이밍·데이터 완전 일치 요구)
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

# 생성된 오디오 캐시가 로컬에 있으면 실제 Resolve까지 확인 (선택 게이트)
if [ -d audio_cache ] && ls audio_cache/*_rfx3.wav > /dev/null 2>&1; then
    echo
    echo "▶ 실캐시 Resolve 확인 (audio_cache/)"
    if ! "$RUNNER" --check-cache audio_cache; then
        fail=1
    fi
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
