#!/usr/bin/env bash
# release 워크플로 1단계 — 태그·버전·게시 여부를 정하고 게시 전제 조건을 확인한다.
#
#   v* 태그 push            게시 (태그 = v<Version 숫자부>, 태그 커밋이 main에 있어야)
#   release/v<버전> 브랜치 push  게시 (태그를 push할 수 없을 때 — 커밋이 main에 있고 태그가 아직
#                           없어야. 태그는 게시할 때 GitHub이 만든다. 변경 사항 한 줄은 커밋의
#                           'Release-Notes:' 트레일러)
#   release-dry/** 브랜치 push  dry run — 게시·음성 팩 렌더 없이 빌드·검증만
#   workflow_dispatch       dry_run 입력대로 (게시는 main에서만, 태그가 없을 때만)
#
# 태그 push 실행에서 같은 태그 릴리스가 이미 있으면, GitHub 화면에서 만든 빈 릴리스
# (초안 아님, 자산 0개, 태그가 이 커밋)일 때만 이어받는다(adopt) — 게시 잡이 자산을 채운다.
#
# env: GH_TOKEN EVENT REF_NAME REF_TYPE DRY_RUN SIMHUB_VERSION NOTES_INPUT REPO GITHUB_SHA
# 출력($GITHUB_OUTPUT): tag version publish adopt simhub_version notes
set -euo pipefail

: "${REPO:?}" "${EVENT:?}" "${REF_NAME:?}" "${REF_TYPE:?}" "${GITHUB_SHA:?}"
export REPO
OUTPUT=${GITHUB_OUTPUT:-/dev/stdout}
SUMMARY=${GITHUB_STEP_SUMMARY:-/dev/null}
# shellcheck source=release-lib.sh
. "$(dirname "$0")/release-lib.sh"

err() { echo "::error::$*"; exit 1; }
warn() { echo "::warning::$*"; }

FULL=$(sed -n 's/.*public[[:space:]]\{1,\}const[[:space:]]\{1,\}string[[:space:]]\{1,\}Version[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' \
    simhub/src/TeamRadio56.SimHub/TeamRadio56Plugin.cs | head -n1)
NUM=$(printf '%s' "$FULL" | grep -oE '^[0-9]+\.[0-9]+\.[0-9]+' || true)
[ -n "$NUM" ] || err "TeamRadio56Plugin.Version이 x.y.z로 시작하지 않습니다: '$FULL'"
TAG="v$NUM"

# 게시물은 main에 들어간 커밋에서만 (기능 브랜치의 태그·브랜치로 사용자 업데이트가 나가지 않게)
require_on_main() {   # <무엇> <다시 할 일>
    local status
    status=$(gh api "repos/$REPO/compare/main...$GITHUB_SHA" --jq .status) \
        || err "main과 $1 비교 실패 (gh api compare)"
    case "$status" in
        identical|behind) ;;
        *) err "$1 $GITHUB_SHA 가 main에 없습니다 (compare: $status) — main에 병합한 뒤 $2" ;;
    esac
}

case "$EVENT" in
    push)
        if [ "$REF_TYPE" = branch ] && [[ "$REF_NAME" == release-dry/* ]]; then
            # workflow_dispatch 없이(push 권한만으로) Windows SimHub 설치·빌드·검증 경로를 시험한다
            PUBLISH=false
        elif [ "$REF_TYPE" = branch ] && [[ "$REF_NAME" == release/* ]]; then
            # 태그를 push할 수 없는 곳(브랜치 push만 되는 자동화 등)의 게시: release/v<버전> 브랜치 push.
            # 수동 게시와 같은 길 — 태그는 게시할 때 GitHub이 이 커밋에 만든다 (GITHUB_TOKEN이 만든
            # 태그는 워크플로를 다시 띄우지 않는다). 이어받기는 없다 (태그가 없으면 릴리스도 없다)
            [ "$REF_NAME" = "release/$TAG" ] \
                || err "브랜치 $REF_NAME ≠ release/$TAG (Version=$FULL) — 맞는 버전으로 release/$TAG 를 push하세요"
            PUBLISH=true
            require_on_main "브랜치 커밋" "release/$TAG 를 main 커밋으로 다시 push하세요"
            if [ -n "$(git ls-remote --tags "https://github.com/$REPO.git" "refs/tags/$TAG")" ]; then
                err "태그 $TAG 가 이미 있습니다 — 그 태그의 실행으로 게시하거나 버전을 올리세요"
            fi
        else
            [ "$REF_TYPE" = tag ] && [ "$REF_NAME" = "$TAG" ] \
                || err "태그 $REF_NAME ≠ 플러그인 버전 태그 $TAG (Version=$FULL) — 태그를 지우고 맞는 버전으로 다시 push하세요"
            PUBLISH=true
            require_on_main "태그 커밋" "태그를 다시 만드세요"
        fi
        ;;
    workflow_dispatch)
        [ "$REF_TYPE" = branch ] || err "수동 실행은 브랜치에서만 합니다 (지금 $REF_TYPE $REF_NAME)"
        if [ "${DRY_RUN:-true}" = true ]; then PUBLISH=false; else PUBLISH=true; fi
        if [ "$PUBLISH" = true ]; then
            [ "$REF_NAME" = main ] || err "게시(dry_run 끔)는 main에서만 — 지금 $REF_NAME"
            if [ -n "$(git ls-remote --tags "https://github.com/$REPO.git" "refs/tags/$TAG")" ]; then
                err "태그 $TAG 가 이미 있습니다 — 태그 push로 게시하거나 버전을 올리세요"
            fi
        fi
        ;;
    *) err "모르는 이벤트: $EVENT" ;;
esac

ADOPT=false
rc=0
rel=$(gh_published_release "$TAG") || rc=$?
case "$rc" in
    0)
        if [ "$PUBLISH" != true ]; then
            warn "릴리스 $TAG 가 이미 있습니다 (dry run이라 계속)"
        elif [ "$EVENT" = push ] && [ "$REF_TYPE" = tag ]; then
            why=$(adopt_blocker "$rel" "$TAG" "$GITHUB_SHA")
            [ -z "$why" ] || err "릴리스 $TAG 가 이미 있습니다 — $why. 버전을 올리세요"
            ADOPT=true
            if [ "$(jq -r '.prerelease // false' <<< "$rel")" = true ]; then
                warn "릴리스 $TAG 가 프리릴리스입니다 — 게시할 때 프리릴리스 표시를 끄고 latest로 만듭니다"
            fi
            echo "GitHub 화면에서 만든 빈 릴리스 $TAG 를 이어받습니다 — 게시 잡이 zip과 매니페스트를 올립니다"
        else
            err "릴리스 $TAG 가 이미 있습니다 — 버전을 올리세요"
        fi
        ;;
    2) ;;
    *)
        if [ "$PUBLISH" = true ]; then err "릴리스 $TAG 를 확인하지 못했습니다 (gh api)"; fi
        warn "릴리스 $TAG 를 확인하지 못했습니다 (dry run이라 계속)"
        ;;
esac

# 사용자 플러그인은 게시된 최신보다 높은 버전만 업데이트로 제안한다. releases/latest가 아니라
# 공개된 플러그인 릴리스 전체의 최고 버전과 비교한다 (화면에서 만든 빈 릴리스가 latest여도 같게)
LATEST=""
if LATEST=$(published_plugin_max "$TAG"); then
    if [ -z "$LATEST" ]; then
        echo "공개된 플러그인 릴리스(매니페스트 있는 vX.Y.Z) 없음 — 첫 릴리스입니다"
    else
        top=$(printf '%s\n%s\n' "$LATEST" "$NUM" | sort -V | tail -n1)
        if [ "$LATEST" = "$NUM" ] || [ "$top" != "$NUM" ]; then
            msg="이번 버전 $NUM 이(가) 게시된 최신 $LATEST 보다 높지 않습니다 — TeamRadio56Plugin.Version을 올리세요"
            if [ "$PUBLISH" = true ]; then err "$msg"; fi
            warn "$msg (dry run이라 계속)"
        fi
    fi
else
    LATEST=""
    msg="게시된 플러그인 릴리스 목록을 확인하지 못했습니다 (gh api releases)"
    if [ "$PUBLISH" = true ]; then err "$msg"; fi
    warn "$msg"
fi

# 설정 화면 '변경 사항' 한 줄: 입력 > 주석 태그 제목 (태그 push) / 커밋의 Release-Notes: 트레일러 (release/v* push)
NOTES=${NOTES_INPUT:-}
if [ -z "$NOTES" ] && [ "$EVENT" = push ] && [ "$REF_TYPE" = tag ]; then
    # checkout은 태그를 커밋으로만 받아 주석 태그 객체가 없을 수 있다
    git fetch --no-tags --depth=1 origin "+refs/tags/$TAG:refs/tags/$TAG" > /dev/null 2>&1 || true
    if [ "$(git cat-file -t "refs/tags/$TAG" 2>/dev/null || true)" = tag ]; then
        NOTES=$(git tag -l --format='%(contents:subject)' "$TAG")
    fi
elif [ -z "$NOTES" ] && [ "$EVENT" = push ] && [ "$REF_TYPE" = branch ] && [ "$PUBLISH" = true ]; then
    NOTES=$(git log -1 --format='%(trailers:key=Release-Notes,valueonly,separator=%x20)' "$GITHUB_SHA" 2> /dev/null || true)
fi
NOTES=$(printf '%s' "$NOTES" | tr '\r\n\t' '   ' | sed 's/  */ /g; s/^ //; s/ $//')

SIMHUB_VERSION=${SIMHUB_VERSION:-9.13.3}
[[ "$SIMHUB_VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]] || err "SimHub 버전 형식이 잘못됨: $SIMHUB_VERSION"

{
    echo "tag=$TAG"
    echo "version=$NUM"
    echo "publish=$PUBLISH"
    echo "adopt=$ADOPT"
    echo "simhub_version=$SIMHUB_VERSION"
    echo "notes=$NOTES"
} >> "$OUTPUT"
{
    echo "### teamradio56 $FULL → $TAG"
    echo
    echo "- 게시: $PUBLISH (이벤트 $EVENT, $REF_TYPE $REF_NAME)"
    echo "- 화면에서 만든 빈 릴리스 이어받기: $ADOPT"
    echo "- 게시된 최신: ${LATEST:-없음}"
    echo "- SimHub: $SIMHUB_VERSION"
    echo "- 변경 사항: ${NOTES:-(없음)}"
} >> "$SUMMARY"
echo "태그 $TAG · 게시 $PUBLISH · 이어받기 $ADOPT · SimHub $SIMHUB_VERSION · 최신 ${LATEST:-없음}"
