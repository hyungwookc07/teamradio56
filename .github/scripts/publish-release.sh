#!/usr/bin/env bash
# 검증된 아티팩트를 그대로 플러그인 릴리스(latest)로 게시한다. 빌드하지 않는다.
#
# env: TAG SHA MODE(tag|dispatch) GH_TOKEN REPO   — 작업 폴더에 dist/ci (아티팩트)
set -euo pipefail

: "${TAG:?}" "${SHA:?}" "${MODE:?}" "${REPO:?}"
SUMMARY=${GITHUB_STEP_SUMMARY:-/dev/null}
DIST=dist/ci

err() { echo "::error::$*"; exit 1; }
kv() { sed -n "s/^$1=//p" "$2" | head -n1; }

MAN="$DIST/assets/teamradio56-release.txt"
INFO="$DIST/meta/release-info.txt"
NOTES="$DIST/meta/release-notes.md"
V=$(kv version "$MAN")
ZIP="$DIST/assets/teamradio56-plugin-$V.zip"

# 1. 아티팩트 재확인 (verify-artifact 잡이 본 파일과 같은지)
mapfile -t files < <(find "$DIST/assets" -mindepth 1 -maxdepth 1 -printf "%f\n" | LC_ALL=C sort)
[ "${files[*]}" = "teamradio56-plugin-$V.zip teamradio56-release.txt" ] || err "assets 구성이 다릅니다: ${files[*]}"
[ "$(kv tag "$MAN")" = "$TAG" ] || err "매니페스트 tag $(kv tag "$MAN") ≠ $TAG"
[ "$TAG" = "v$V" ] || err "태그 $TAG ≠ v$V"
[ "$(sha256sum "$ZIP" | cut -d' ' -f1)" = "$(kv plugin_sha256 "$MAN")" ] || err "plugin_sha256 불일치"
[ "$(kv commit "$INFO")" = "$SHA" ] || err "빌드 커밋 $(kv commit "$INFO") ≠ 이 실행의 커밋 $SHA"
TITLE=$(kv title "$INFO")
[ -n "$TITLE" ] && [ -s "$NOTES" ] || err "release-info.txt title 또는 release-notes.md가 비었습니다"

# 2. 같은 태그 릴리스 없음
if gh release view "$TAG" --repo "$REPO" > /dev/null 2>&1; then
    err "릴리스 $TAG 가 이미 있습니다"
fi

# 3. 게시된 최신보다 높은 버전
tmp=$(mktemp)
code=$(curl -sSL --retry 3 -o "$tmp" -w '%{http_code}' \
    "https://github.com/$REPO/releases/latest/download/teamradio56-release.txt" || true)
if [ "$code" = 200 ]; then
    latest=$(kv version "$tmp")
    top=$(printf '%s\n%s\n' "$latest" "$V" | sort -V | tail -n1)
    if [ "$latest" = "$V" ] || [ "$top" != "$V" ]; then
        err "이번 $V 이(가) 게시된 최신 $latest 보다 높지 않습니다"
    fi
elif [ "$code" != 404 ]; then
    err "최신 릴리스 매니페스트 확인 실패 (HTTP $code)"
fi
rm -f "$tmp"

# 4. 음성 팩 릴리스 (가벼운 재확인 — 이름·크기·digest, 초안 아님)
AUDIO_TAG=$(kv audio_tag "$MAN")
json=$(gh release view "$AUDIO_TAG" --repo "$REPO" --json isDraft,assets) || err "음성 팩 릴리스 $AUDIO_TAG 없음"
[ "$(jq -r .isDraft <<< "$json")" = false ] || err "음성 팩 릴리스 $AUDIO_TAG 가 초안입니다"
IFS=, read -r -a names <<< "$(kv audio_assets "$MAN")"
IFS=, read -r -a shas <<< "$(kv audio_sha256 "$MAN")"
IFS=, read -r -a sizes <<< "$(kv audio_size "$MAN")"
for i in "${!names[@]}"; do
    row=$(jq -c --arg n "${names[$i]}" '.assets[] | select(.name == $n)' <<< "$json")
    [ -n "$row" ] || err "$AUDIO_TAG 에 ${names[$i]} 없음"
    [ "$(jq -r .size <<< "$row")" = "${sizes[$i]}" ] || err "${names[$i]} 크기 불일치"
    d=$(jq -r '.digest // ""' <<< "$row")
    [ -z "$d" ] || [ "$d" = "sha256:${shas[$i]}" ] || err "${names[$i]} digest 불일치"
done

# 5. 태그
flags=()
if [ "$MODE" = tag ]; then
    remote=$(gh api "repos/$REPO/commits/$TAG" --jq .sha) || err "원격 태그 $TAG 를 찾지 못했습니다"
    [ "$remote" = "$SHA" ] || err "원격 태그 $TAG ($remote) ≠ 빌드 커밋 $SHA — 태그가 옮겨졌습니다"
    flags=(--verify-tag)
else
    if gh api "repos/$REPO/git/matching-refs/tags/$TAG" --jq '.[].ref' | grep -qx "refs/tags/$TAG"; then
        err "태그 $TAG 가 이미 있습니다"
    fi
    # 게시할 때 GitHub이 이 커밋에 태그를 만든다 (GITHUB_TOKEN 태그는 워크플로를 다시 띄우지 않는다)
    flags=(--target "$SHA")
fi

# 6. 게시 — 자산을 다 올린 뒤 공개되고, 실패하면 gh가 초안을 지운다
gh release create "$TAG" "$ZIP" "$MAN" --repo "$REPO" --title "$TITLE" \
    --notes-file "$NOTES" --latest "${flags[@]}"

# 7. 사용자 업데이터가 보는 주소 확인
ok=false
for _ in 1 2 3 4 5 6 7 8 9 10; do
    lt=$(gh api "repos/$REPO/releases/latest" --jq .tag_name 2> /dev/null || true)
    mv=$(curl -fsSL "https://github.com/$REPO/releases/latest/download/teamradio56-release.txt" 2> /dev/null \
        | sed -n 's/^version=//p' || true)
    if [ "$lt" = "$TAG" ] && [ "$mv" = "$V" ]; then
        ok=true
        break
    fi
    sleep 6
done
[ "$ok" = true ] || err "게시 후 releases/latest가 $TAG 를 가리키지 않습니다 (latest=$lt, manifest version=$mv)"

{
    echo "### ✅ 게시: $TITLE"
    echo
    echo "- https://github.com/$REPO/releases/tag/$TAG"
    echo "- 업데이터 확인 주소: https://github.com/$REPO/releases/latest/download/teamradio56-release.txt (version=$V, audio_tag=$AUDIO_TAG)"
} >> "$SUMMARY"
echo "✅ 릴리스 $TAG 게시 완료"
