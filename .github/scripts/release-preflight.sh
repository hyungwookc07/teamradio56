#!/usr/bin/env bash
# release 워크플로 1단계 — 태그·버전·게시 여부를 정하고 게시 전제 조건을 확인한다.
#
# env: GH_TOKEN EVENT REF_NAME REF_TYPE DRY_RUN SIMHUB_VERSION NOTES_INPUT REPO GITHUB_SHA
# 출력($GITHUB_OUTPUT): tag version publish simhub_version notes
set -euo pipefail

: "${REPO:?}" "${EVENT:?}" "${REF_NAME:?}" "${REF_TYPE:?}" "${GITHUB_SHA:?}"
OUTPUT=${GITHUB_OUTPUT:-/dev/stdout}
SUMMARY=${GITHUB_STEP_SUMMARY:-/dev/null}

err() { echo "::error::$*"; exit 1; }
warn() { echo "::warning::$*"; }

FULL=$(sed -n 's/.*public[[:space:]]\{1,\}const[[:space:]]\{1,\}string[[:space:]]\{1,\}Version[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' \
    simhub/src/TeamRadio56.SimHub/TeamRadio56Plugin.cs | head -n1)
NUM=$(printf '%s' "$FULL" | grep -oE '^[0-9]+\.[0-9]+\.[0-9]+' || true)
[ -n "$NUM" ] || err "TeamRadio56Plugin.Version이 x.y.z로 시작하지 않습니다: '$FULL'"
TAG="v$NUM"

case "$EVENT" in
    push)
        [ "$REF_TYPE" = tag ] && [ "$REF_NAME" = "$TAG" ] \
            || err "태그 $REF_NAME ≠ 플러그인 버전 태그 $TAG (Version=$FULL) — 태그를 지우고 맞는 버전으로 다시 push하세요"
        PUBLISH=true
        # 게시물은 main에 들어간 커밋에서만 (기능 브랜치의 태그로 사용자 업데이트가 나가지 않게)
        status=$(gh api "repos/$REPO/compare/main...$GITHUB_SHA" --jq .status) \
            || err "main과 태그 커밋 비교 실패 (gh api compare)"
        case "$status" in
            identical|behind) ;;
            *) err "태그 커밋 $GITHUB_SHA 가 main에 없습니다 (compare: $status) — main에 병합한 뒤 태그를 다시 만드세요" ;;
        esac
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

if gh release view "$TAG" --repo "$REPO" > /dev/null 2>&1; then
    if [ "$PUBLISH" = true ]; then
        err "릴리스 $TAG 가 이미 있습니다 — 버전을 올리세요"
    fi
    warn "릴리스 $TAG 가 이미 있습니다 (dry run이라 계속)"
fi

# 사용자 플러그인은 게시된 최신보다 높은 버전만 업데이트로 제안한다
tmp=$(mktemp)
code=$(curl -sSL --retry 3 -o "$tmp" -w '%{http_code}' \
    "https://github.com/$REPO/releases/latest/download/teamradio56-release.txt" || true)
LATEST=""
if [ "$code" = 200 ]; then
    LATEST=$(sed -n 's/^[[:space:]]*version[[:space:]]*=[[:space:]]*\([0-9]*\.[0-9]*\.[0-9]*\)[[:space:]]*$/\1/p' "$tmp" | head -n1)
    if [ -n "$LATEST" ]; then
        top=$(printf '%s\n%s\n' "$LATEST" "$NUM" | sort -V | tail -n1)
        if [ "$LATEST" = "$NUM" ] || [ "$top" != "$NUM" ]; then
            msg="이번 버전 $NUM 이(가) 게시된 최신 $LATEST 보다 높지 않습니다 — TeamRadio56Plugin.Version을 올리세요"
            if [ "$PUBLISH" = true ]; then err "$msg"; fi
            warn "$msg (dry run이라 계속)"
        fi
    fi
elif [ "$code" = 404 ]; then
    echo "게시된 매니페스트 없음 — 첫 릴리스입니다"
else
    msg="최신 릴리스 매니페스트를 확인하지 못했습니다 (HTTP $code)"
    if [ "$PUBLISH" = true ]; then err "$msg"; fi
    warn "$msg"
fi
rm -f "$tmp"

# 설정 화면 '변경 사항' 한 줄: 입력 > 주석 태그 제목
NOTES=${NOTES_INPUT:-}
if [ -z "$NOTES" ] && [ "$EVENT" = push ]; then
    # checkout은 태그를 커밋으로만 받아 주석 태그 객체가 없을 수 있다
    git fetch --no-tags --depth=1 origin "+refs/tags/$TAG:refs/tags/$TAG" > /dev/null 2>&1 || true
    if [ "$(git cat-file -t "refs/tags/$TAG" 2>/dev/null || true)" = tag ]; then
        NOTES=$(git tag -l --format='%(contents:subject)' "$TAG")
    fi
fi
NOTES=$(printf '%s' "$NOTES" | tr '\r\n\t' '   ' | sed 's/  */ /g; s/^ //; s/ $//')

SIMHUB_VERSION=${SIMHUB_VERSION:-9.13.3}
[[ "$SIMHUB_VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]] || err "SimHub 버전 형식이 잘못됨: $SIMHUB_VERSION"

{
    echo "tag=$TAG"
    echo "version=$NUM"
    echo "publish=$PUBLISH"
    echo "simhub_version=$SIMHUB_VERSION"
    echo "notes=$NOTES"
} >> "$OUTPUT"
{
    echo "### teamradio56 $FULL → $TAG"
    echo
    echo "- 게시: $PUBLISH (이벤트 $EVENT, $REF_TYPE $REF_NAME)"
    echo "- 게시된 최신: ${LATEST:-없음}"
    echo "- SimHub: $SIMHUB_VERSION"
    echo "- 변경 사항: ${NOTES:-(없음)}"
} >> "$SUMMARY"
echo "태그 $TAG · 게시 $PUBLISH · SimHub $SIMHUB_VERSION · 최신 ${LATEST:-없음}"
