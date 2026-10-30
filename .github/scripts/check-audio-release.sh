#!/usr/bin/env bash
# 플러그인 릴리스 전 — voice_lines/audio_pack.txt가 가리키는 audio-<pack> 릴리스가 정말 있고
# 자산이 기술자와 같은지(이름·크기·sha256), 지금 코드가 찾는 멘트 파일이 팩에 다 있는지.
#
#   check-audio-release.sh <repo> <audio_pack.txt> <내려받을 폴더>
#
# env PUBLISH=true면 문제는 실패, 아니면(dry run) 경고 후 나머지를 건너뛴다 — 팩 릴리스가
# 아직 없을 때도 Windows 빌드 경로를 시험할 수 있게.
set -euo pipefail

REPO=${1:?repo}
DESC=${2:?descriptor}
DL=${3:?download dir}
SUMMARY=${GITHUB_STEP_SUMMARY:-/dev/null}

problem() {
    if [ "${PUBLISH:-false}" = true ]; then
        echo "::error::$*"
        exit 1
    fi
    echo "::warning::$* (dry run — 음성 팩 확인 나머지 건너뜀)"
    echo "- ⚠ 음성 팩: $*" >> "$SUMMARY"
    exit 0
}

parsed=$(python3 -I tools/pack_audio.py descriptor --print "$DESC") || problem "audio_pack.txt를 읽지 못했습니다"
PACK=$(sed -n 's/^PACK=//p' <<< "$parsed")
TAG=$(sed -n 's/^TAG=//p' <<< "$parsed")
read -r -a ASSETS <<< "$(sed -n 's/^ASSETS=//p' <<< "$parsed")"

if [ "$TAG" != "audio-$PACK" ]; then
    problem "audio_pack.txt의 tag=$TAG — CI 릴리스는 audio-<pack> 릴리스만 참조합니다. Actions → audio-pack 워크플로를 먼저 실행하세요"
fi

json=$(gh release view "$TAG" --repo "$REPO" --json isDraft,isPrerelease,assets 2> /dev/null) \
    || problem "음성 팩 릴리스 $TAG 가 없습니다 (또는 초안) — audio-pack 워크플로가 끝까지 돌았는지 확인하세요"
[ "$(jq -r .isDraft <<< "$json")" = false ] || problem "음성 팩 릴리스 $TAG 가 초안입니다 — 사용자가 받을 수 없습니다"
if [ "$(jq -r .isPrerelease <<< "$json")" != true ]; then
    echo "::warning::$TAG 가 프리릴리스가 아닙니다 — latest가 될 수 있습니다 (gh release edit $TAG --prerelease --latest=false)"
fi

mkdir -p "$DL"
for a in "${ASSETS[@]}"; do
    IFS=: read -r name sha size <<< "$a"
    row=$(jq -c --arg n "$name" '.assets[] | select(.name == $n)' <<< "$json")
    [ -n "$row" ] || problem "$TAG 릴리스에 $name 이(가) 없습니다"
    [ "$(jq -r .state <<< "$row")" = uploaded ] || problem "$name 업로드가 끝나지 않았습니다"
    [ "$(jq -r .size <<< "$row")" = "$size" ] || problem "$name 크기 $(jq -r .size <<< "$row") ≠ 기술자 $size"
    digest=$(jq -r '.digest // ""' <<< "$row")
    if [ -n "$digest" ] && [ "$digest" != "sha256:$sha" ]; then
        problem "$name digest $digest ≠ 기술자 sha256:$sha"
    fi
    # 사용자 PC와 같은 공개 경로로 받는다 (초안·비공개 자산이면 여기서 걸린다)
    curl -fsSL --retry 5 --retry-all-errors -o "$DL/$name" \
        "https://github.com/$REPO/releases/download/$TAG/$name" \
        || problem "$name 을(를) 공개 주소에서 받지 못했습니다"
done

python3 -I tools/pack_audio.py verify --dir "$DL" --descriptor "$DESC" \
    || problem "음성 팩 zip이 기술자와 다릅니다"
python3 tools/pack_audio.py expected --tiers 1,2,3 --tracks voice_lines/track_names_en.txt \
    --out "$DL/expected.txt"
python3 -I tools/pack_audio.py verify --dir "$DL" --descriptor "$DESC" --expect-names "$DL/expected.txt" \
    || problem "멘트 집합이 팩과 다릅니다 — 새 pack ID로 audio-pack을 실행하세요 (그대로 내면 빠진 멘트는 자막으로만 나갑니다)"

echo "- ✅ 음성 팩 $PACK ($TAG): zip ${#ASSETS[@]}개 크기·sha256·멘트 집합 확인" >> "$SUMMARY"
echo "✅ 음성 팩 릴리스 $TAG 확인"
