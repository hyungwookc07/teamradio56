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

echo
echo "▶ 멘트 풀 / 슬롯 포매팅 대조 (사전 캐시 텍스트 전체 집합)"
python3 tools/dump_pregen.py --out "$OUT/py_pregen.txt"
"$RUNNER" --dump-pregen --out "$OUT/cs_pregen.txt"
if diff -q "$OUT/py_pregen.txt" "$OUT/cs_pregen.txt" > /dev/null; then
    echo "✅ 일치 — $(wc -l < "$OUT/py_pregen.txt")개 텍스트 동일"
else
    echo "❌ 멘트 풀 불일치:"
    diff "$OUT/py_pregen.txt" "$OUT/cs_pregen.txt" | head -20
    fail=1
fi

for replay in data/replays/*.jsonl.gz; do
    name=$(basename "$replay" .jsonl.gz)
    echo
    echo "▶ 리플레이: $name"
    python3 tools/replay_calls.py --replay "$replay" --out "$OUT/py_$name.jsonl"
    "$RUNNER" --replay "$replay" --out "$OUT/cs_$name.jsonl"
    if ! python3 tools/compare_calls.py "$OUT/py_$name.jsonl" "$OUT/cs_$name.jsonl"; then
        fail=1
    fi
done

echo
if [ "$fail" -ne 0 ]; then
    echo "❌ 리플레이 회귀 실패 — 포팅이 파이썬과 다른 판단을 냈습니다"
    exit 1
fi
echo "✅ 리플레이 회귀 전체 통과"
