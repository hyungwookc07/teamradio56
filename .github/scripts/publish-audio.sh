#!/usr/bin/env bash
# 렌더된 음성 팩을 audio-<pack> 릴리스(프리릴리스, latest 아님)로 내고, 새 기술자를
# 실행한 브랜치에 커밋한다. 순서: 초안 → 기술자 push → 공개.
# 업로드 중에 멈추면 초안을 지운다. 기술자 커밋·push에서 멈추면 초안은 남기고(사용자에겐 안
# 보인다) 손으로 마무리하는 방법을 출력한다 — 지우면 기술자가 없는 릴리스를 가리키게 된다.
# publish 잡만 다시 실행해도 된다: 남은 초안은 지우고 아티팩트에서 다시 올린다.
#
# env: PACK SHA BRANCH RUN_URL GH_TOKEN REPO   — 작업 폴더에 dist/audio (render 아티팩트)
set -euo pipefail

# commit뿐 아니라 push 거부 뒤 rebase도 커미터가 필요하다 (러너엔 전역 git 설정이 없다)
export GIT_AUTHOR_NAME='github-actions[bot]'
export GIT_AUTHOR_EMAIL='41898282+github-actions[bot]@users.noreply.github.com'
export GIT_COMMITTER_NAME="$GIT_AUTHOR_NAME"
export GIT_COMMITTER_EMAIL="$GIT_AUTHOR_EMAIL"

: "${PACK:?}" "${SHA:?}" "${BRANCH:?}" "${RUN_URL:?}" "${REPO:?}"
SUMMARY=${GITHUB_STEP_SUMMARY:-/dev/null}
TAG="audio-$PACK"
SRC="dist/audio/$PACK"
NEWDESC="dist/audio/audio_pack.txt"
DESC=voice_lines/audio_pack.txt

err() { echo "::error::$*"; exit 1; }

# 1. 아티팩트 확인
python3 -I tools/pack_audio.py verify --dir "$SRC" --descriptor "$NEWDESC"
parsed=$(python3 -I tools/pack_audio.py descriptor --print "$NEWDESC")
[ "$(sed -n 's/^PACK=//p' <<< "$parsed")" = "$PACK" ] || err "새 기술자의 pack ≠ $PACK"
[ "$(sed -n 's/^TAG=//p' <<< "$parsed")" = "$TAG" ] || err "새 기술자의 tag ≠ $TAG"
read -r -a ASSETS <<< "$(sed -n 's/^ASSETS=//p' <<< "$parsed")"
zips=()
for a in "${ASSETS[@]}"; do
    zips+=("$SRC/${a%%:*}")
done

# 2. 팩 릴리스는 한 번만 — 공개된 것만 막는다. 초안은 앞선 실행이 남긴 것이니 지우고 다시
if existing=$(gh release view "$TAG" --repo "$REPO" --json isDraft --jq .isDraft 2> /dev/null); then
    if [ "$existing" = true ]; then
        echo "::warning::앞선 실행이 남긴 초안 $TAG 를 지우고 아티팩트에서 다시 올립니다"
        gh release delete "$TAG" --repo "$REPO" --yes
    else
        err "릴리스 $TAG 가 이미 공개돼 있습니다 — 팩은 바꾸지 않습니다. 멘트 집합이 바뀌었으면 새 pack ID로 다시 실행하세요"
    fi
fi

# 3. 초안 (태그는 아직 없다)
notes=$(mktemp)
cat > "$notes" <<NOTES
설정 화면 [음성 팩 업데이트]가 받는 자산 전용 릴리스 — 지우거나 자산을 바꾸지 말 것(매니페스트가 sha256으로 고정).

- 팩: $PACK (zip ${#zips[@]}개)
- 렌더 실행: $RUN_URL
- 소스 커밋: $SHA
NOTES
# --draft를 직접 주면 gh는 업로드 실패 때 초안을 치우지 않는다 — 트랩을 먼저 건다
stage=upload
cleanup() {
    case "${stage:-}" in
        upload)
            echo "::warning::업로드 중에 멈춤 — 초안 $TAG 를 지웁니다. publish 잡만 다시 실행하세요"
            gh release delete "$TAG" --repo "$REPO" --yes || true
            ;;
        commit)
            echo "::error::기술자 커밋·push 전에 멈춤 — 초안 $TAG 는 남겨 둡니다 (자산 업로드 완료, 사용자에겐 안 보임)."
            echo "::error::손으로 마무리: (1) 아티팩트 teamradio56-audio-$PACK 의 audio_pack.txt를 $BRANCH 의 $DESC 로 커밋·push (2) gh release edit $TAG --repo $REPO --draft=false --prerelease --latest=false"
            echo "::error::또는 push를 막은 원인을 풀고 publish 잡만 다시 실행 (초안은 지우고 다시 올립니다)"
            ;;
    esac
}
trap cleanup EXIT
gh release create "$TAG" "${zips[@]}" --repo "$REPO" --draft --prerelease --latest=false \
    --target "$SHA" --title "teamradio56 음성 팩 $PACK" --notes-file "$notes"
rm -f "$notes"
stage=commit

# 4. 기술자 커밋 → push (브랜치가 그새 나갔으면 기술자가 그대로일 때만 그 위에)
descriptor_untouched() {
    git diff --quiet "$SHA" "origin/$BRANCH" -- "$DESC" \
        || err "실행 뒤 $BRANCH 의 $DESC 가 바뀌었습니다 — 손으로 맞추세요"
}
git fetch --no-tags origin "+refs/heads/$BRANCH:refs/remotes/origin/$BRANCH"
if [ "$(git rev-parse "origin/$BRANCH")" != "$SHA" ]; then
    descriptor_untouched
fi
git checkout -q -B "$BRANCH" "origin/$BRANCH"
cp "$NEWDESC" "$DESC"
changed=$(git diff --name-only)
[ "$changed" = "$DESC" ] || err "기술자 말고 바뀐 파일이 있습니다: $changed"
git commit -q -m "음성 팩 기술자 갱신: $PACK ($TAG)" -m "Actions 렌더: $RUN_URL" -- "$DESC"
if ! git push origin "HEAD:refs/heads/$BRANCH"; then
    echo "::warning::push 거부 — 한 번 다시 맞춰 봅니다"
    git fetch --no-tags origin "+refs/heads/$BRANCH:refs/remotes/origin/$BRANCH"
    descriptor_untouched
    git rebase -q "origin/$BRANCH"
    git push origin "HEAD:refs/heads/$BRANCH" \
        || err "$BRANCH 에 push하지 못했습니다 (보호 규칙?)"
fi
COMMIT=$(git rev-parse HEAD)
stage=done

# 5. 공개 — 프리릴리스 + latest 아님 (releases/latest는 플러그인 릴리스만 가리킨다)
gh release edit "$TAG" --repo "$REPO" --draft=false --prerelease --latest=false \
    || err "기술자는 push됐는데 공개에 실패 — gh release edit $TAG --repo $REPO --draft=false --prerelease --latest=false 로 마저 공개하세요 (그 전까지 플러그인 릴리스 게이트가 거부합니다)"

# 6. 사용자 경로로 확인
for a in "${ASSETS[@]}"; do
    IFS=: read -r name sha _ <<< "$a"
    got=$(curl -fsSL --retry 5 --retry-all-errors "https://github.com/$REPO/releases/download/$TAG/$name" \
        | sha256sum | cut -d' ' -f1)
    [ "$got" = "$sha" ] || err "공개 주소의 $name sha256 $got ≠ $sha"
done
latest=$(gh api "repos/$REPO/releases/latest" --jq .tag_name 2> /dev/null || true)
[ "$latest" != "$TAG" ] || err "$TAG 가 latest가 됐습니다 — 플러그인 릴리스를 latest로 되돌리세요 (gh release edit <v태그> --latest)"

{
    echo "### ✅ 음성 팩 $PACK 게시"
    echo
    echo "- 릴리스: https://github.com/$REPO/releases/tag/$TAG (프리릴리스, latest: ${latest:-없음})"
    echo "- 기술자 커밋: $COMMIT ($BRANCH)"
    echo "- 다음: 이 커밋을 받은 뒤 플러그인 버전 태그를 push하면 release 워크플로가 이 팩을 가리키는 릴리스를 냅니다"
} >> "$SUMMARY"
echo "✅ $TAG 게시, 기술자 $COMMIT"
