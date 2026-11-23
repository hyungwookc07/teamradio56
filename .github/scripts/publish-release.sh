#!/usr/bin/env bash
# 검증된 아티팩트를 그대로 플러그인 릴리스(latest)로 게시한다. 빌드하지 않는다.
#
# 태그 push 실행(MODE=tag)에서 같은 태그 릴리스가 이미 있으면 GitHub 화면에서 만든 빈
# 릴리스(초안 아님, 자산 0개, 태그 = 이 커밋)일 때만 이어받는다: zip → 매니페스트 순으로
# 올리고(업데이터는 매니페스트를 보므로 마지막에) 제목·본문·latest를 고친다. 중간에 멈추면
# 올린 자산을 지워 빈 릴리스로 되돌린다 — 'Re-run failed jobs'로 다시 이어받을 수 있게.
# 판단은 preflight가 아니라 게시하는 지금의 릴리스 상태로 한다 (그사이 만들어졌을 수 있다).
#
# 게시 전에 Windows 빌드의 Defender 검사 보고서(meta/av-scan.txt)가 이 zip·매니페스트 바이트를
# 깨끗하다고 했는지 본다 (아니면 게시하지 않는다). 본문 끝에는 '파일 검증' 절(SHA-256, 검사 결과,
# 확인 방법)을 붙인다 — 이어받기에서 화면에 쓴 본문은 그 아래에 남는다.
#
# env: TAG SHA MODE(tag|dispatch) GH_TOKEN REPO   — 작업 폴더에 dist/ci (아티팩트)
set -euo pipefail

: "${TAG:?}" "${SHA:?}" "${MODE:?}" "${REPO:?}"
export REPO
SUMMARY=${GITHUB_STEP_SUMMARY:-/dev/null}
DIST=dist/ci
# shellcheck source=release-lib.sh
. "$(dirname "$0")/release-lib.sh"

err() { echo "::error::$*"; exit 1; }
kv() { sed -n "s/^$1=//p" "$2" | head -n1; }

MAN="$DIST/assets/teamradio56-release.txt"
INFO="$DIST/meta/release-info.txt"
NOTES="$DIST/meta/release-notes.md"
AV="$DIST/meta/av-scan.txt"
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
# 백신 검사 — 보고서의 asset sha256 = 지금 게시할 파일 (자산 구성 확인은 위에서 끝났다)
why=$(av_report_problem "$AV" "$DIST/assets")
[ -z "$why" ] || err "백신 검사 보고서(av-scan.txt): $why — 게시하지 않습니다"

# 2. 같은 태그 릴리스 — 없거나, 태그 push 실행이면 화면에서 만든 빈 릴리스
ADOPT=false
rc=0
rel=$(gh_published_release "$TAG") || rc=$?
case "$rc" in
    0)
        [ "$MODE" = tag ] || err "릴리스 $TAG 가 이미 있습니다"
        why=$(adopt_blocker "$rel" "$TAG" "$SHA")
        [ -z "$why" ] || err "릴리스 $TAG 가 이미 있습니다 — $why"
        ADOPT=true
        OLD_BODY=$(jq -r '.body // ""' <<< "$rel")
        ;;
    2) ;;
    *) err "릴리스 $TAG 를 확인하지 못했습니다 (gh api)" ;;
esac

# 3. 게시된 최신보다 높은 버전 — releases/latest가 아니라 공개된 플러그인 릴리스 전체의 최고 버전
#    (이어받는 빈 릴리스가 latest면 latest 매니페스트는 404라 비교를 건너뛰게 된다)
latest=$(published_plugin_max "$TAG") || err "게시된 플러그인 릴리스 목록 확인 실패 (gh api releases)"
if [ -n "$latest" ]; then
    top=$(printf '%s\n%s\n' "$latest" "$V" | sort -V | tail -n1)
    if [ "$latest" = "$V" ] || [ "$top" != "$V" ]; then
        err "이번 $V 이(가) 게시된 최신 $latest 보다 높지 않습니다"
    fi
fi

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

# 6. 게시 — 본문 = 생성한 본문 + '파일 검증' 절 (+ 이어받기면 화면에서 쓴 본문)
body=$(mktemp)
{
    cat "$NOTES"
    echo
    release_verify_section "$AV" "$DIST/assets" "$V"
} > "$body"
if [ "$ADOPT" = true ]; then
    # 화면에서 쓴 본문이 있으면 생성한 본문 아래에 남긴다
    if [ -n "$(tr -d '[:space:]' <<< "$OLD_BODY")" ]; then
        printf '\n%s\n' "$OLD_BODY" >> "$body"
    fi
    uploaded=()
    undo() {
        [ "${#uploaded[@]}" -gt 0 ] || return 0
        echo "::warning::이어받기 중에 멈춤 — 올린 자산(${uploaded[*]})을 지워 빈 릴리스로 되돌립니다. 원인을 푼 뒤 Re-run failed jobs"
        for n in "${uploaded[@]}"; do
            gh release delete-asset "$TAG" "$n" --repo "$REPO" --yes || true
        done
    }
    trap undo EXIT
    # 매니페스트는 마지막 — 업데이터가 매니페스트를 받았는데 zip이 없는 순간이 없게
    for f in "$ZIP" "$MAN"; do
        uploaded+=("$(basename "$f")")    # 업로드 도중 실패해 반쯤 남은 자산도 지우게 먼저 적는다
        gh release upload "$TAG" "$f" --repo "$REPO"
    done
    gh release edit "$TAG" --repo "$REPO" --title "$TITLE" --notes-file "$body" \
        --prerelease=false --latest
    uploaded=()
    trap - EXIT
else
    # 자산을 다 올린 뒤 공개되고, 실패하면 gh가 초안을 지운다
    gh release create "$TAG" "$ZIP" "$MAN" --repo "$REPO" --title "$TITLE" \
        --notes-file "$body" --latest "${flags[@]}"
fi
rm -f "$body"

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
    echo "- https://github.com/$REPO/releases/tag/$TAG$([ "$ADOPT" = true ] && echo ' (화면에서 만든 빈 릴리스를 이어받음)')"
    echo "- 업데이터 확인 주소: https://github.com/$REPO/releases/latest/download/teamradio56-release.txt (version=$V, audio_tag=$AUDIO_TAG)"
    echo "- Defender 검사: 엔진 $(kv engine_version "$AV") · 서명 $(kv antivirus_signature_version "$AV") · 자체 시험 $(kv selftest "$AV") · $(kv result "$AV") — 본문에 '파일 검증' 절"
} >> "$SUMMARY"
echo "✅ 릴리스 $TAG 게시 완료"
