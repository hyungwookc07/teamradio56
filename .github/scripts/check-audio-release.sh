#!/usr/bin/env bash
# 플러그인 릴리스 전 — voice_lines/audio_pack.txt가 가리키는 audio-<pack> 릴리스가 정말 있고
# 자산이 기술자와 같은지(이름·크기·sha256), 지금 코드가 찾는 멘트 파일이 팩에 다 있는지.
# 그리고 이 릴리스가 음성 팩을 새로 렌더해야 하는지 정한다.
#
#   check-audio-release.sh <repo> <audio_pack.txt> <내려받을 폴더>
#
# 출력($GITHUB_OUTPUT): need_pack(true|false) pack(이번 릴리스가 쓸 팩 ID)
#
# env PUBLISH=true (게시 실행):
#   - 팩이 낡음 — 기술자 tag가 audio-<pack>이 아님 / 멘트 집합이 다름
#     → need_pack=true, pack=새 ID (숫자 꼬리 +1, 자릿수 유지, 이미 공개된 audio-<id>와
#     다른 집합으로 배포된 ID는 건너뜀). release 워크플로가 audio-pack을 불러 렌더·게시하고
#     그 기술자로 빌드한다
#   - 망가진 상태 — 기술자가 가리키는 audio-<pack> 릴리스가 안 보임(없음 또는 초안: preflight의
#     읽기 토큰엔 초안이 안 보인다 — 기술자 push 뒤 공개에서 멈춘 팩이면 공개만 하면 된다),
#     공개된 팩의 자산 크기·sha256이 기술자와 다름, 공개 주소에서 못 받음, 확인 실패
#     → 실패 (새로 렌더해도 고쳐지지 않거나, 약 2시간 렌더가 헛돈다)
# 아니면(dry run) 문제는 전부 경고 후 need_pack=false — 렌더하지 않고 지금 기술자로
# 빌드한다 (팩 릴리스가 아직 없어도 Windows 빌드 경로를 시험할 수 있게).
#
# env GITHUB_SHA BRANCH(기본 main): 새 팩이 필요한데 이 커밋 뒤 BRANCH의 기술자가 바뀌었으면
# 실패 — publish-audio.sh가 어차피 기술자 커밋을 거부할 것을 렌더(약 2시간) 전에 알린다.
set -euo pipefail

REPO=${1:?repo}
DESC=${2:?descriptor}
DL=${3:?download dir}
export REPO
PUBLISH=${PUBLISH:-false}
BRANCH=${BRANCH:-main}
OUTPUT=${GITHUB_OUTPUT:-/dev/stdout}
SUMMARY=${GITHUB_STEP_SUMMARY:-/dev/null}
# shellcheck source=release-lib.sh
. "$(dirname "$0")/release-lib.sh"

PACK=""
EXPECTED="$DL/expected.txt"
mkdir -p "$DL"

err() {
    echo "::error::$*"
    exit 1
}

emit() {
    {
        echo "need_pack=$1"
        echo "pack=$2"
    } >> "$OUTPUT"
}

# 망가진 상태 — 게시 실행은 실패, dry run은 경고
problem() {
    if [ "$PUBLISH" = true ]; then
        err "$*"
    fi
    echo "::warning::$* (dry run — 음성 팩 확인 나머지 건너뜀)"
    echo "- ⚠ 음성 팩: $*" >> "$SUMMARY"
    emit false "$PACK"
    exit 0
}

HAVE_EXPECTED=false
expected_names() {
    [ "$HAVE_EXPECTED" = false ] || return 0
    python3 tools/pack_audio.py expected --tiers 1,2,3 --tracks voice_lines/track_names_en.txt \
        --out "$EXPECTED" || err "지금 코드의 멘트 파일 집합을 만들지 못했습니다 (pack_audio.py expected)"
    HAVE_EXPECTED=true
}

# 새 팩 ID → NEW_PACK (명령 치환으로 부르면 err의 ::error::가 값에 섞인다)
NEW_PACK=""
pick_new_pack() {
    local cand=$PACK json rc
    for _ in $(seq 1 20); do
        cand=$(python3 -I tools/pack_audio.py next-id "$cand" --expect-names "$EXPECTED") \
            || err "pack=$PACK 다음 팩 ID를 정하지 못했습니다 — Actions → audio-pack을 새 pack ID로 직접 실행한 뒤 다시 릴리스하세요"
        rc=0
        json=$(gh_release_view "audio-$cand" isDraft) || rc=$?
        case "$rc" in
            2) ;;    # 없음 — 읽기 토큰엔 앞선 실행이 남긴 초안도 여기 (audio-pack 게시 단계가 지우고 다시 올린다)
            0)
                if [ "$(jq -r .isDraft <<< "$json")" != true ]; then
                    echo "audio-$cand 는 이미 공개돼 있어 건너뜁니다"
                    continue
                fi
                echo "앞선 실행이 남긴 초안 audio-$cand 는 audio-pack 게시 단계가 지우고 다시 올립니다"
                ;;
            *) err "audio-$cand 릴리스를 확인하지 못했습니다" ;;
        esac
        NEW_PACK=$cand
        return 0
    done
    err "pack=$PACK 뒤로 쓸 수 있는 팩 ID를 찾지 못했습니다 (20개 모두 공개됨)"
}

# 낡은 팩 — 게시 실행은 새 ID로 렌더하게(need_pack), dry run은 경고만 (렌더하지 않는다)
stale() {
    if [ "$PUBLISH" != true ]; then
        echo "::warning::$* (dry run — 음성 팩을 렌더하지 않고 지금 기술자로 빌드합니다)"
        echo "- ⚠ 음성 팩: $*" >> "$SUMMARY"
        emit false "$PACK"
        exit 0
    fi
    if [ -n "${GITHUB_SHA:-}" ]; then
        files=$(gh api "repos/$REPO/compare/$GITHUB_SHA...$BRANCH" --jq '.files[]?.filename') \
            || err "$BRANCH 와 이 커밋 비교 실패 (gh api compare)"
        if grep -qx 'voice_lines/audio_pack.txt' <<< "$files"; then
            err "$* — 그런데 이 커밋 뒤 $BRANCH 의 voice_lines/audio_pack.txt가 바뀌었습니다 (새 팩이 이미 들어갔을 수 있다). 태그를 지우고 $BRANCH 최신 커밋에 다시 태그해 push하세요 (화면에서 만든 릴리스면 그 릴리스와 태그를 지우고 다시 만든다, 버전을 올려도 된다). 같은 실행을 이어갈 때는 Re-run failed jobs"
        fi
    fi
    expected_names
    pick_new_pack
    echo "::notice::$* → 이 실행이 새 음성 팩 $NEW_PACK 를 렌더·게시하고(약 2시간) 그 기술자로 빌드합니다"
    echo "- 🔁 음성 팩: $* → 새 팩 \`$NEW_PACK\` 렌더·게시 후 빌드 (audio-pack 호출)" >> "$SUMMARY"
    emit true "$NEW_PACK"
    exit 0
}

parsed=$(python3 -I tools/pack_audio.py descriptor --print "$DESC") || problem "audio_pack.txt를 읽지 못했습니다"
PACK=$(sed -n 's/^PACK=//p' <<< "$parsed")
TAG=$(sed -n 's/^TAG=//p' <<< "$parsed")
read -r -a ASSETS <<< "$(sed -n 's/^ASSETS=//p' <<< "$parsed")"

if [ "$TAG" != "audio-$PACK" ]; then
    stale "audio_pack.txt의 tag=$TAG 는 audio-<pack> 릴리스가 아닙니다 (CI 릴리스는 audio-<pack> 릴리스만 참조)"
fi

rc=0
json=$(gh_release_view "$TAG" isDraft,isPrerelease,assets) || rc=$?
case "$rc" in
    0) ;;
    # 새 ID로 렌더하지 않는다 — 초안이면 공개만 하면 되고, 정말 없으면 누가 지운 것이다
    2) problem "음성 팩 릴리스 $TAG 가 없거나 초안입니다 (읽기 토큰엔 초안이 안 보인다). 초안이면 렌더한 실행의 Re-run failed jobs 또는 gh release edit $TAG --repo $REPO --draft=false --prerelease --latest=false 뒤 이 실행을 다시 (Re-run failed jobs). 정말 없으면 기술자 커밋을 되돌리거나 Actions → audio-pack을 새 ID로" ;;
    *) problem "음성 팩 릴리스 $TAG 를 확인하지 못했습니다" ;;
esac
# 쓰기 토큰으로 돌릴 때만 초안이 보인다 (위 2와 같은 처리)
if [ "$(jq -r .isDraft <<< "$json")" != false ]; then
    problem "음성 팩 릴리스 $TAG 가 초안입니다 — 사용자가 받을 수 없습니다. gh release edit $TAG --repo $REPO --draft=false --prerelease --latest=false 로 공개하거나, 기술자 커밋을 되돌린 커밋에서 다시 릴리스하세요 (같은 ID로 다시 렌더하고 초안은 지우고 올린다)"
fi
if [ "$(jq -r .isPrerelease <<< "$json")" != true ]; then
    echo "::warning::$TAG 가 프리릴리스가 아닙니다 — latest가 될 수 있습니다 (gh release edit $TAG --prerelease --latest=false)"
fi

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
expected_names
if ! python3 -I tools/pack_audio.py verify --dir "$DL" --descriptor "$DESC" --expect-names "$EXPECTED"; then
    stale "멘트 집합이 팩 $PACK 과 다릅니다 (그대로 내면 빠진 멘트는 자막으로만 나간다)"
fi

emit false "$PACK"
echo "- ✅ 음성 팩 $PACK ($TAG): zip ${#ASSETS[@]}개 크기·sha256·멘트 집합 확인" >> "$SUMMARY"
echo "✅ 음성 팩 릴리스 $TAG 확인"
