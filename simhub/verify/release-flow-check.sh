#!/usr/bin/env bash
# 릴리스 흐름 오프라인 검사 — release 워크플로 스크립트를 가짜 gh/curl/git(PATH 앞에 둔다)으로
# 돌려 판단을 확인한다. 네트워크·GitHub 없이 돈다 (replay-check.sh가 부른다).
#
#   release-preflight.sh   태그 push / 화면에서 만든 빈 릴리스 이어받기 / 자산 있는 릴리스 거부 /
#                          release-dry/** dry run / 태그·main·버전 규칙
#   check-audio-release.sh 새 음성 팩이 필요한지(need_pack)와 새 팩 ID, 망가진 팩은 실패
#   publish-audio.sh       태그 실행에서 main에 기술자 커밋, 공개에서 멈춘 뒤 재실행, main 기술자 충돌
#   publish-release.sh     새로 만들기 vs 이어받기 (zip → 매니페스트 → edit --latest 순서, 실패 시 되돌림)
#
# 사용법:  bash simhub/verify/release-flow-check.sh
set -euo pipefail

cd "$(dirname "$0")/../.."      # 저장소 루트
ROOT=$PWD
SCRIPTS=$ROOT/.github/scripts
for t in jq python3 git sha256sum; do
    command -v "$t" > /dev/null || { echo "❌ $t 없음"; exit 1; }
done

W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT
ST=$W/state
REAL_GIT=$(command -v git)
mkdir -p "$W/bin"

# ── 가짜 gh ──────────────────────────────────────────────────────────────
# 상태: $FAKE_STATE/releases/<태그>.json (REST 모양), assets/<태그>/<이름>, commits/<태그>,
# compare.json, latest(releases/latest 태그), fail_match(맞는 호출은 실패), calls.log(호출 기록)
cat > "$W/bin/gh" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
S=${FAKE_STATE:?}
printf '%s\n' "$*" >> "$S/calls.log"
if [ -s "$S/fail_match" ] && printf '%s\n' "$*" | grep -qEf "$S/fail_match"; then
    echo "fake gh: 주입된 실패 ($*)" >&2
    exit 1
fi
jqx=""
args=()
while [ $# -gt 0 ]; do
    case "$1" in
        --repo | -R) shift 2 ;;
        --jq | -q) jqx=$2; shift 2 ;;
        *) args+=("$1"); shift ;;
    esac
done
set -- "${args[@]}"
emit() { if [ -n "$jqx" ]; then jq -r "$jqx"; else cat; fi; }
relf() { echo "$S/releases/$1.json"; }
upd() { local r=$1; shift; jq "$@" "$r" > "$r.tmp" && mv "$r.tmp" "$r"; }
add_asset() {
    local tag=$1 f=$2 a
    mkdir -p "$S/assets/$tag"
    cp "$f" "$S/assets/$tag/"
    a=$(jq -n --arg n "$(basename "$f")" --argjson s "$(stat -c %s "$f")" \
        --arg d "sha256:$(sha256sum "$f" | cut -d' ' -f1)" '{name: $n, size: $s, state: "uploaded", digest: $d}')
    upd "$(relf "$tag")" --argjson a "$a" '.assets = ([.assets[] | select(.name != $a.name)] + [$a])'
}
case "${1:-} ${2:-}" in
    "release view")
        f=$(relf "$3")
        [ -f "$f" ] || { echo "release not found" >&2; exit 1; }
        jq '{isDraft: .draft, isPrerelease: .prerelease, assets, body, tagName: .tag_name}' "$f" | emit
        ;;
    "release create")
        tag=$3
        shift 3
        files=()
        latest=false draft=false pre=false
        while [ $# -gt 0 ]; do
            case "$1" in
                --title | --notes-file | --target) shift 2 ;;
                --latest) latest=true; shift ;;
                --draft) draft=true; shift ;;
                --prerelease) pre=true; shift ;;
                --verify-tag) [ -f "$S/commits/$tag" ] || { echo "tag $tag doesn't exist" >&2; exit 1; }; shift ;;
                -*) shift ;;
                *) files+=("$1"); shift ;;
            esac
        done
        [ ! -f "$(relf "$tag")" ] || { echo "a release with the same tag name already exists" >&2; exit 1; }
        jq -n --arg t "$tag" --argjson d "$draft" --argjson p "$pre" \
            '{tag_name: $t, draft: $d, prerelease: $p, immutable: false, body: "", assets: []}' > "$(relf "$tag")"
        for f in "${files[@]}"; do add_asset "$tag" "$f"; done
        [ "$latest" != true ] || echo "$tag" > "$S/latest"
        ;;
    "release upload")
        tag=$3
        shift 3
        [ -f "$(relf "$tag")" ] || { echo "release not found" >&2; exit 1; }
        for f in "$@"; do add_asset "$tag" "$f"; done
        ;;
    "release edit")
        tag=$3
        shift 3
        r=$(relf "$tag")
        [ -f "$r" ] || { echo "release not found" >&2; exit 1; }
        while [ $# -gt 0 ]; do
            case "$1" in
                --latest) echo "$tag" > "$S/latest"; shift ;;
                --prerelease=false) upd "$r" '.prerelease = false'; shift ;;
                --draft=false) upd "$r" '.draft = false'; shift ;;
                --notes-file) upd "$r" --rawfile b "$2" '.body = $b'; shift 2 ;;
                --title) shift 2 ;;
                *) shift ;;
            esac
        done
        ;;
    "release delete-asset")
        r=$(relf "$3")
        upd "$r" --arg n "$4" '.assets = [.assets[] | select(.name != $n)]'
        rm -f "$S/assets/$3/$4"
        ;;
    "release delete")
        rm -rf "$(relf "$3")" "$S/assets/$3"
        ;;
    api\ *)
        path=$2
        case "$path" in
            repos/*/releases/tags/*)
                f=$(relf "${path##*/releases/tags/}")
                if [ ! -f "$f" ] || [ "$(jq -r .draft "$f")" = true ]; then
                    echo "gh: Not Found (HTTP 404)" >&2
                    exit 1
                fi
                emit < "$f"
                ;;
            repos/*/releases/latest)
                [ -s "$S/latest" ] || { echo "gh: Not Found (HTTP 404)" >&2; exit 1; }
                jq -n --arg t "$(cat "$S/latest")" '{tag_name: $t}' | emit
                ;;
            repos/*/commits/*)
                tag=${path##*/commits/}
                [ -f "$S/commits/$tag" ] || { echo "gh: No commit found for SHA: $tag (HTTP 422)" >&2; exit 1; }
                jq -n --arg s "$(cat "$S/commits/$tag")" '{sha: $s}' | emit
                ;;
            repos/*/compare/*) emit < "$S/compare.json" ;;
            repos/*/git/matching-refs/tags/*)
                tag=${path##*/matching-refs/tags/}
                if [ -f "$S/commits/$tag" ]; then
                    jq -n --arg r "refs/tags/$tag" '[{ref: $r}]'
                else
                    echo '[]'
                fi | emit
                ;;
            *) echo "fake gh: 모르는 api $path" >&2; exit 1 ;;
        esac
        ;;
    *) echo "fake gh: 모르는 명령 $*" >&2; exit 1 ;;
esac
FAKE

# ── 가짜 curl — github.com 릴리스 다운로드 주소를 상태 폴더에서 낸다 ─────
cat > "$W/bin/curl" <<'FAKE'
#!/usr/bin/env bash
set -uo pipefail
S=${FAKE_STATE:?}
printf '%s\n' "$*" >> "$S/curl.log"
out="" fmt="" url="" failflag=false
while [ $# -gt 0 ]; do
    case "$1" in
        -o) out=$2; shift 2 ;;
        -w) fmt=$2; shift 2 ;;
        --retry) shift 2 ;;
        --*) shift ;;
        -*) [[ "$1" != *f* ]] || failflag=true; shift ;;
        *) url=$1; shift ;;
    esac
done
src=""
case "$url" in
    https://github.com/*/releases/latest/download/*)
        tag=$(cat "$S/latest" 2> /dev/null || true)
        [ -z "$tag" ] || src="$S/assets/$tag/${url##*/}"
        ;;
    https://github.com/*/releases/download/*)
        rest=${url#*/releases/download/}
        tag=${rest%%/*}
        r="$S/releases/$tag.json"
        if [ -f "$r" ] && [ "$(jq -r .draft "$r")" = false ]; then src="$S/assets/$tag/${rest#*/}"; fi
        ;;
esac
if [ -n "$src" ] && [ -f "$src" ]; then
    if [ -n "$out" ]; then cp "$src" "$out"; else cat "$src"; fi
    code=200
else
    code=404
    [ "$failflag" = false ] || exit 22
    if [ -n "$out" ]; then echo "Not Found" > "$out"; else echo "Not Found"; fi
fi
[ -z "$fmt" ] || printf '%s' "${fmt//%\{http_code\}/$code}"
exit 0
FAKE

# ── 가짜 git — GitHub 원격(fetch·ls-remote)만 가로채고 나머지는 진짜 git ─
# (publish-audio 검사의 origin은 로컬 bare 저장소라 그대로 통과)
cat > "$W/bin/git" <<'FAKE'
#!/usr/bin/env bash
S=${FAKE_STATE:?}
case "${1:-}" in
    fetch)
        url=$("$FAKE_REAL_GIT" remote get-url origin 2> /dev/null || true)
        [ "${url#/}" != "$url" ] || { echo "fake git: 네트워크 없음" >&2; exit 1; }
        ;;
    ls-remote)
        ref=${!#}
        tag=${ref#refs/tags/}
        if [ -f "$S/commits/$tag" ]; then printf '%s\t%s\n' "$(cat "$S/commits/$tag")" "$ref"; fi
        exit 0
        ;;
esac
exec "$FAKE_REAL_GIT" "$@"
FAKE
printf '#!/bin/sh\nexit 0\n' > "$W/bin/sleep"
chmod +x "$W/bin/"*
export PATH="$W/bin:$PATH" FAKE_STATE=$ST FAKE_REAL_GIT=$REAL_GIT GH_TOKEN=fake-token
# 러너처럼 전역 git 설정 없이 (개발 PC의 서명·push 설정이 섞이지 않게)
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1
unset GITHUB_SHA GITHUB_OUTPUT GITHUB_STEP_SUMMARY

REPO=hyungwookc07/teamradio56
FULL=$(sed -n 's/.*public const string Version = "\([^"]*\)".*/\1/p' \
    simhub/src/TeamRadio56.SimHub/TeamRadio56Plugin.cs | head -n1)
NUM=$(grep -oE '^[0-9]+\.[0-9]+\.[0-9]+' <<< "$FULL")
TAG=v$NUM
SHA=1111111111111111111111111111111111111111
OTHER=2222222222222222222222222222222222222222

pass=0
fails=()
RC=0
# 판정 조건이 실패해도 멈추지 않게 (expect가 $?로 센다)
set +e

reset_state() {
    rm -rf "$ST"
    mkdir -p "$ST/releases" "$ST/assets" "$ST/commits"
    echo '{"status": "identical", "files": []}' > "$ST/compare.json"
    : > "$ST/calls.log"
}

# mk_release <태그> <draft> <prerelease> [자산 파일...]
mk_release() {
    local tag=$1 draft=$2 pre=$3 f a assets='[]'
    shift 3
    mkdir -p "$ST/assets/$tag"
    for f in "$@"; do
        cp "$f" "$ST/assets/$tag/"
        a=$(jq -n --arg n "$(basename "$f")" --argjson s "$(stat -c %s "$f")" \
            --arg d "sha256:$(sha256sum "$f" | cut -d' ' -f1)" '{name: $n, size: $s, state: "uploaded", digest: $d}')
        assets=$(jq --argjson a "$a" '. + [$a]' <<< "$assets")
    done
    jq -n --arg t "$tag" --argjson d "$draft" --argjson p "$pre" --argjson as "$assets" \
        '{tag_name: $t, draft: $d, prerelease: $p, immutable: false, body: "", assets: $as}' \
        > "$ST/releases/$tag.json"
}

set_release() {    # <태그> <jq 식> — 릴리스 json 고치기
    local r="$ST/releases/$1.json"
    jq "$2" "$r" > "$r.tmp" && mv "$r.tmp" "$r"
}

out_val() { sed -n "s/^$1=//p" "$W/gho" | tail -n1; }

# 바로 앞 조건(명령·&& 목록)의 종료 상태로 판정한다:  cond && cond; expect "설명"
expect() {
    local st=$? what=$1
    if [ "$st" = 0 ]; then
        pass=$((pass + 1))
    else
        fails+=("$what")
        echo "   ✗ $what (종료 코드 $RC)"
        sed 's/^/     | /' "$W/out" | tail -n 12
    fi
}

has() { grep -q -- "$1" "$W/out"; }
called() { grep -qE -- "$1" "$ST/calls.log"; }
line_of() { grep -nE -- "$1" "$ST/calls.log" | head -n1 | cut -d: -f1; }

# ── release-preflight.sh ─────────────────────────────────────────────────
preflight() {      # <EVENT> <REF_TYPE> <REF_NAME> [DRY_RUN]
    : > "$W/gho"
    RC=0
    env EVENT="$1" REF_TYPE="$2" REF_NAME="$3" DRY_RUN="${4:-true}" GITHUB_SHA=$SHA REPO=$REPO \
        GITHUB_OUTPUT="$W/gho" GITHUB_STEP_SUMMARY="$W/sum" SIMHUB_VERSION=9.13.3 NOTES_INPUT= \
        bash "$SCRIPTS/release-preflight.sh" > "$W/out" 2>&1 || RC=$?
}

echo "  · release-preflight.sh"
reset_state
echo "$SHA" > "$ST/commits/$TAG"
preflight push tag "$TAG"
[ "$RC" = 0 ] && [ "$(out_val publish)" = true ] \
    && [ "$(out_val adopt)" = false ] && [ "$(out_val tag)" = "$TAG" ]
expect "태그 push: 게시"

mk_release "$TAG" false false
echo "$TAG" > "$ST/latest"
preflight push tag "$TAG"
[ "$RC" = 0 ] && [ "$(out_val publish)" = true ] \
    && [ "$(out_val adopt)" = true ]
expect "화면에서 만든 빈 릴리스 → 이어받기"

set_release "$TAG" '.prerelease = true'
preflight push tag "$TAG"
[ "$RC" = 0 ] && [ "$(out_val adopt)" = true ] && has '프리릴리스'
expect "빈 프리릴리스도 이어받기 (경고)"

echo "x" > "$W/notes.txt"
mk_release "$TAG" false false "$W/notes.txt"
preflight push tag "$TAG"
[ "$RC" = 1 ] && has '자산이 1개' && [ ! -s "$W/gho" ]
expect "자산 있는 릴리스 → 실패"

mk_release "$TAG" false false
set_release "$TAG" '.immutable = true'
preflight push tag "$TAG"
[ "$RC" = 1 ] && has 'immutable'
expect "immutable 빈 릴리스 → 실패"

mk_release "$TAG" false false
echo "$OTHER" > "$ST/commits/$TAG"
preflight push tag "$TAG"
[ "$RC" = 1 ] && has "$OTHER"
expect "빈 릴리스지만 태그가 다른 커밋 → 실패"
echo "$SHA" > "$ST/commits/$TAG"

mk_release "$TAG" false false "$W/notes.txt"
preflight push branch release-dry/windows
[ "$RC" = 0 ] \
    && [ "$(out_val publish)" = false ] && [ "$(out_val adopt)" = false ] && has '::warning::릴리스'
expect "release-dry/** push: dry run (자산 있는 릴리스도 경고만)"

preflight push branch feature/x
[ "$RC" = 1 ]
expect "release-dry 아닌 브랜치 push → 실패"

reset_state
preflight push tag v0.0.1
[ "$RC" = 1 ] && has '플러그인 버전 태그'
expect "태그 ≠ 플러그인 버전 → 실패"

echo '{"status": "diverged", "files": []}' > "$ST/compare.json"
preflight push tag "$TAG"
[ "$RC" = 1 ] && has 'main에 없습니다'
expect "main에 없는 태그 커밋 → 실패"

reset_state
echo "$SHA" > "$ST/commits/$TAG"
mk_release "$TAG" false false
preflight workflow_dispatch branch main false
[ "$RC" = 1 ] && has '이미 있습니다'
expect "수동 게시는 기존 태그를 거부 (이어받지 않음)"

reset_state
printf 'format=1\nversion=99.0.0\ntag=v99.0.0\n' > "$W/teamradio56-release.txt"
mk_release v99.0.0 false false "$W/teamradio56-release.txt"
echo v99.0.0 > "$ST/latest"
preflight push tag "$TAG"
[ "$RC" = 1 ] && has '높지 않습니다'
expect "게시된 최신보다 낮은 버전 → 실패"

# ── check-audio-release.sh ───────────────────────────────────────────────
echo "  · check-audio-release.sh"
python3 tools/pack_audio.py expected --tiers 1,2,3 --tracks voice_lines/track_names_en.txt \
    --out "$W/expected.txt" 2> /dev/null
# 가짜 팩: 지금 코드가 찾는 이름 전부(full) / 하나 빠짐(short) — 내용은 작은 가짜 wav
python3 - "$W/expected.txt" "$W" <<'PY'
import os, sys, zipfile
names = [n.strip() for n in open(sys.argv[1], encoding="utf-8") if n.strip()]
for label, members in (("full", names), ("short", names[1:])):
    d = os.path.join(sys.argv[2], "pack-" + label)
    os.makedirs(d)
    with zipfile.ZipFile(os.path.join(d, "teamradio56_kokoro_v016_1of1.zip"), "w") as zf:
        for n in members:
            zf.writestr(zipfile.ZipInfo(n, (2026, 1, 1, 0, 0, 0)), b"RIFF" + n.encode())
PY
desc_for() {       # <팩 zip 폴더> → 기술자 경로
    local z="$1/teamradio56_kokoro_v016_1of1.zip" d="$1/audio_pack.txt"
    printf '# 시험용\npack=kokoro-v016\ntag=audio-kokoro-v016\nasset=%s,%s,%s\n' "$(basename "$z")" \
        "$(sha256sum "$z" | cut -d' ' -f1)" "$(stat -c %s "$z")" > "$d"
    echo "$d"
}
FULL_DESC=$(desc_for "$W/pack-full")
SHORT_DESC=$(desc_for "$W/pack-short")
V015_DESC=$ROOT/voice_lines/audio_pack.txt
grep -qx 'tag=v0.16.0' "$V015_DESC" && grep -qx 'pack=kokoro-v015' "$V015_DESC" \
    || V015_DESC=$(printf 'pack=kokoro-v015\ntag=v0.16.0\nasset=a.zip,%s,1\n' "$(printf 'a%.0s' {1..64})" \
        > "$W/v015.txt" && echo "$W/v015.txt")

audio_check() {    # <PUBLISH> <기술자>
    : > "$W/gho"
    rm -rf "$W/dl"
    RC=0
    env PUBLISH="$1" GITHUB_SHA=$SHA GITHUB_OUTPUT="$W/gho" GITHUB_STEP_SUMMARY="$W/sum" \
        bash "$SCRIPTS/check-audio-release.sh" "$REPO" "$2" "$W/dl" > "$W/out" 2>&1 || RC=$?
}
need() { [ "$RC" = 0 ] && [ "$(out_val need_pack)" = "$1" ] && [ "$(out_val pack)" = "$2" ]; }

reset_state
audio_check true "$V015_DESC"
need true kokoro-v016
expect "kokoro-v015 (tag=v0.16.0) 게시 → 새 팩 kokoro-v016"

mk_release audio-kokoro-v016 false true "$W/pack-full/teamradio56_kokoro_v016_1of1.zip"
audio_check true "$V015_DESC"
need true kokoro-v017
expect "공개된 audio-kokoro-v016은 건너뜀 → kokoro-v017"

set_release audio-kokoro-v016 '.draft = true'
audio_check true "$V015_DESC"
need true kokoro-v016
expect "남은 초안 audio-kokoro-v016은 다시 씀 → kokoro-v016"

reset_state
audio_check false "$V015_DESC"
need false kokoro-v015 && has '::warning::'
expect "dry run: 렌더 안 함 (경고, 지금 기술자)"

echo '{"status": "ahead", "files": [{"filename": "voice_lines/audio_pack.txt"}]}' > "$ST/compare.json"
audio_check true "$V015_DESC"
[ "$RC" = 1 ] && has '바뀌었습니다' \
    && [ ! -s "$W/gho" ]
expect "이 커밋 뒤 main 기술자가 바뀜 → 렌더 전에 실패"

reset_state
mk_release audio-kokoro-v016 false true "$W/pack-full/teamradio56_kokoro_v016_1of1.zip"
audio_check true "$FULL_DESC"
need false kokoro-v016
expect "맞는 팩(공개·자산·멘트 집합) → need_pack=false"

reset_state
mk_release audio-kokoro-v016 false true "$W/pack-short/teamradio56_kokoro_v016_1of1.zip"
audio_check true "$SHORT_DESC"
need true kokoro-v017
expect "멘트 집합이 다름 → 새 팩 kokoro-v017"

reset_state
audio_check true "$FULL_DESC"
need true kokoro-v017
expect "기술자의 audio-<pack> 릴리스가 없음 → 새 팩 kokoro-v017"

audio_check false "$FULL_DESC"
need false kokoro-v016
expect "dry run: 팩 릴리스 없음도 경고만"

mk_release audio-kokoro-v016 false true "$W/pack-full/teamradio56_kokoro_v016_1of1.zip"
set_release audio-kokoro-v016 '.assets[0].size += 1'
audio_check true "$FULL_DESC"
[ "$RC" = 1 ] && has '크기' && [ ! -s "$W/gho" ]
expect "공개된 팩의 자산 크기 ≠ 기술자 → 실패"

mk_release audio-kokoro-v016 false true "$W/pack-full/teamradio56_kokoro_v016_1of1.zip"
set_release audio-kokoro-v016 '.assets[0].digest = "sha256:" + ("0" * 64)'
audio_check true "$FULL_DESC"
[ "$RC" = 1 ] && has 'digest'
expect "공개된 팩의 digest ≠ 기술자 → 실패"

mk_release audio-kokoro-v016 true true "$W/pack-full/teamradio56_kokoro_v016_1of1.zip"
audio_check true "$FULL_DESC"
[ "$RC" = 1 ] && has '초안'
expect "기술자가 가리키는 초안 → 실패"

# ── publish-audio.sh (release가 부른 태그 실행: BRANCH=main, SHA=태그 커밋) ─
echo "  · publish-audio.sh"
AZ=$W/pack-full/teamradio56_kokoro_v016_1of1.zip
O=$W/origin.git
WK=$W/work
pa_origin() {      # 기술자가 옛 팩인 main 하나짜리 원격 → PSHA
    rm -rf "$O" "$W/seed"
    git init -q --bare "$O"
    git -C "$O" symbolic-ref HEAD refs/heads/main
    git clone -q "$O" "$W/seed" 2> /dev/null
    mkdir -p "$W/seed/voice_lines" "$W/seed/tools"
    cp tools/pack_audio.py "$W/seed/tools/"
    cp "$V015_DESC" "$W/seed/voice_lines/audio_pack.txt"
    git -C "$W/seed" add -A
    git -C "$W/seed" -c user.name=t -c user.email=t@t.invalid commit -qm seed
    git -C "$W/seed" push -q origin HEAD:refs/heads/main
    PSHA=$(git -C "$W/seed" rev-parse HEAD)
}
pa_work() {        # 태그 커밋을 체크아웃한 작업 폴더 + pack 잡 아티팩트
    rm -rf "$WK"
    git clone -q "$O" "$WK"
    git -C "$WK" checkout -q --detach "$PSHA"
    mkdir -p "$WK/dist/audio/kokoro-v016"
    cp "$AZ" "$WK/dist/audio/kokoro-v016/"
    cp "$FULL_DESC" "$WK/dist/audio/audio_pack.txt"
}
publish_audio() {
    RC=0
    (cd "$WK" && env PACK=kokoro-v016 SHA="$PSHA" BRANCH=main RUN_URL=https://run.invalid/1 REPO=$REPO \
        GITHUB_STEP_SUMMARY="$W/sum" bash "$SCRIPTS/publish-audio.sh") > "$W/out" 2>&1 || RC=$?
}
main_desc_is() { git -C "$O" show main:voice_lines/audio_pack.txt | cmp -s - "$1"; }
rel_field() { jq -r "$2" "$ST/releases/$1.json"; }

reset_state
pa_origin
pa_work
publish_audio
[ "$RC" = 0 ] && main_desc_is "$FULL_DESC" \
    && [ "$(git -C "$O" rev-parse main~1)" = "$PSHA" ] \
    && git -C "$O" log -1 --format=%s main | grep -q '음성 팩 기술자 갱신: kokoro-v016' \
    && [ "$(rel_field audio-kokoro-v016 .draft)" = false ] && [ "$(rel_field audio-kokoro-v016 .prerelease)" = true ] \
    && [ "$(line_of '^release create audio-kokoro-v016 .*--draft')" -lt "$(line_of '^release edit audio-kokoro-v016 .*--draft=false')" ]
expect "팩 게시: 초안 → 기술자 커밋을 main에 push → 공개 (프리릴리스)"

# 앞선 시도가 기술자를 push하고 공개에서 멈춤 → publish 잡 재실행은 초안을 다시 올리고 공개만
pushed=$(git -C "$O" rev-parse main)
set_release audio-kokoro-v016 '.draft = true'
: > "$ST/calls.log"
pa_work
publish_audio
[ "$RC" = 0 ] && [ "$(git -C "$O" rev-parse main)" = "$pushed" ] \
    && called '^release delete audio-kokoro-v016' && [ "$(rel_field audio-kokoro-v016 .draft)" = false ]
expect "재실행: main에 같은 기술자가 있으면 커밋 없이 공개만"

reset_state
pa_origin
printf '# 다른 사람이 바꿈\n' >> "$W/seed/voice_lines/audio_pack.txt"
git -C "$W/seed" -c user.name=t -c user.email=t@t.invalid commit -qam other
git -C "$W/seed" push -q origin HEAD:refs/heads/main
pa_work
publish_audio
[ "$RC" = 1 ] && has '바뀌었습니다' && [ "$(rel_field audio-kokoro-v016 .draft)" = true ] \
    && [ "$(git -C "$O" rev-parse main)" = "$(git -C "$W/seed" rev-parse HEAD)" ]
expect "태그 뒤 main 기술자가 바뀜 → 커밋 안 함, 초안은 남김"

# ── publish-release.sh ───────────────────────────────────────────────────
echo "  · publish-release.sh"
P=$W/pub
mkdir -p "$P/dist/ci/assets" "$P/dist/ci/meta"
ZIPNAME=teamradio56-plugin-$NUM.zip
printf 'PK fake plugin zip' > "$P/dist/ci/assets/$ZIPNAME"
{
    echo "format=1"
    echo "version=$NUM"
    echo "tag=$TAG"
    echo "plugin_asset=$ZIPNAME"
    echo "plugin_sha256=$(sha256sum "$P/dist/ci/assets/$ZIPNAME" | cut -d' ' -f1)"
    echo "audio_pack=kokoro-v016"
    echo "audio_tag=audio-kokoro-v016"
    echo "audio_assets=$(basename "$AZ")"
    echo "audio_sha256=$(sha256sum "$AZ" | cut -d' ' -f1)"
    echo "audio_size=$(stat -c %s "$AZ")"
} > "$P/dist/ci/assets/teamradio56-release.txt"
printf 'format=1\ntag=%s\ntitle=teamradio56 %s\ncommit=%s\n' "$TAG" "$FULL" "$SHA" > "$P/dist/ci/meta/release-info.txt"
printf 'teamradio56 %s\n\n생성한 본문\n' "$FULL" > "$P/dist/ci/meta/release-notes.md"

publish() {        # <MODE>
    RC=0
    (cd "$P" && env TAG=$TAG SHA=$SHA MODE="$1" REPO=$REPO GITHUB_STEP_SUMMARY="$W/sum" \
        bash "$SCRIPTS/publish-release.sh") > "$W/out" 2>&1 || RC=$?
}
pub_reset() {
    reset_state
    echo "$SHA" > "$ST/commits/$TAG"
    mk_release audio-kokoro-v016 false true "$AZ"
}
UP_ZIP="^release upload $TAG dist/ci/assets/$ZIPNAME"
UP_MAN="^release upload $TAG dist/ci/assets/teamradio56-release.txt"

pub_reset
publish tag
[ "$RC" = 0 ] \
    && called "^release create $TAG dist/ci/assets/$ZIPNAME dist/ci/assets/teamradio56-release.txt .*--latest --verify-tag" \
    && ! called '^release (upload|edit)' && [ "$(cat "$ST/latest")" = "$TAG" ]
expect "새로 만들기: create --latest --verify-tag (zip·매니페스트 함께)"

pub_reset
mk_release "$TAG" false true
set_release "$TAG" '.body = "화면에서 쓴 설명"'
echo "$TAG" > "$ST/latest"
publish tag
zl=$(line_of "$UP_ZIP")
ml=$(line_of "$UP_MAN")
el=$(line_of "^release edit $TAG ")
[ "$RC" = 0 ] \
    && [ -n "$zl" ] && [ -n "$ml" ] && [ -n "$el" ] && [ "$zl" -lt "$ml" ] && [ "$ml" -lt "$el" ] \
    && called "^release edit $TAG .*--prerelease=false --latest" && ! called '^release create'
expect "이어받기: zip → 매니페스트 → edit --latest, create 없음"
[ "$(jq '.assets | length' "$ST/releases/$TAG.json")" = 2 ] \
    && [ "$(jq -r .prerelease "$ST/releases/$TAG.json")" = false ] \
    && jq -r .body "$ST/releases/$TAG.json" | grep -q '생성한 본문' \
    && jq -r .body "$ST/releases/$TAG.json" | grep -q '화면에서 쓴 설명'
expect "이어받기: 자산 2개, 프리릴리스 해제, 화면 본문 보존"

pub_reset
mk_release "$TAG" false false "$W/notes.txt"
publish tag
[ "$RC" = 1 ] && has '자산이 1개' \
    && ! called '^release (create|upload|edit)'
expect "자산 있는 릴리스는 이어받지 않음 → 실패"

pub_reset
mk_release "$TAG" false false
echo "$UP_MAN" > "$ST/fail_match"
publish tag
[ "$RC" != 0 ] \
    && called "^release delete-asset $TAG $ZIPNAME" && ! called "^release edit" \
    && [ "$(jq '.assets | length' "$ST/releases/$TAG.json")" = 0 ]
expect "이어받기 중 매니페스트 업로드 실패 → 올린 zip 지워 빈 릴리스로"

pub_reset
mk_release "$TAG" false false
publish dispatch
[ "$RC" = 1 ] && has '이미 있습니다' \
    && ! called '^release (create|upload|edit)'
expect "수동 게시는 기존 릴리스를 이어받지 않음 → 실패"

if [ "${#fails[@]}" -gt 0 ]; then
    echo "❌ 릴리스 흐름 검사 실패 ${#fails[@]}건 (통과 $pass건)"
    for f in "${fails[@]}"; do echo "   - $f"; done
    exit 1
fi
echo "✅ 릴리스 흐름 검사 $pass건 통과 (가짜 gh/curl/git, 네트워크 없음)"
