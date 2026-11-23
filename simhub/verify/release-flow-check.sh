#!/usr/bin/env bash
# 릴리스 흐름 오프라인 검사 — release 워크플로 스크립트를 가짜 gh/curl/git(PATH 앞에 둔다)으로
# 돌려 판단을 확인한다. 네트워크·GitHub 없이 돈다 (replay-check.sh가 부른다).
#
#   release-preflight.sh   태그 push / 화면에서 만든 빈 릴리스 이어받기 / 자산 있는 릴리스 거부 /
#                          release-dry/** dry run / 태그·main 규칙 / 공개된 플러그인 릴리스 최고 버전과
#                          비교 (빈 릴리스가 latest여도)
#   check-audio-release.sh 새 음성 팩이 필요한지(need_pack)와 새 팩 ID, 망가진 팩·안 보이는 팩은 실패
#                          (팩 ID는 KNOWN_NAME_SETS에 없는 test-v* — 표가 늘어나도 기대값이 그대로)
#   publish-audio.sh       태그 실행에서 main에 기술자 커밋, 공개에서 멈춘 뒤 재실행, main 기술자 충돌
#   publish-release.sh     새로 만들기 vs 이어받기 (zip → 매니페스트 → edit --latest 순서, 실패 시 되돌림),
#                          이어받기도 버전 비교, Defender 검사 보고서가 없거나 깨끗하지 않거나 게시할
#                          바이트와 다르면 거부, 본문 끝 '파일 검증' 절 (SHA-256 · 확인 명령)
#   verify-release-artifact.sh  같은 보고서 검사 (가짜 dotnet·리플레이 러너로 앞부분만)
#   attest-check.sh        증명 전 파일 재확인, 증명 후 gh attestation verify (사용자 안내 명령)
#   scan-defender.ps1      -SampleReport 보고서 ↔ bash 검사기 형식 계약 (pwsh가 있으면)
#   release.yml 정적 검사  uses: 전부 40자리 SHA 고정, 빌드 잡의 Defender 단계가 업로드 앞,
#                          attest 잡 권한이 정확히 셋, publish가 attest를 기다림 — 검사기가 무는지
#                          변형한 사본으로도 확인. 저장소 파일에 완성된 EICAR 문자열 없음
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
# GH_TOKEN=fake-read(preflight 잡의 contents: read)면 GitHub처럼 초안이 안 보인다 —
# release view는 'release not found', 릴리스 목록에서는 빠진다. fake-write면 보인다.
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
repo_arg=""
args=()
while [ $# -gt 0 ]; do
    case "$1" in
        --repo | -R) repo_arg=$2; shift 2 ;;
        --jq | -q) jqx=$2; shift 2 ;;
        --paginate) shift ;;
        *) args+=("$1"); shift ;;
    esac
done
set -- "${args[@]}"
emit() { if [ -n "$jqx" ]; then jq -r "$jqx"; else cat; fi; }
relf() { echo "$S/releases/$1.json"; }
read_token() { [ "${GH_TOKEN:-}" = fake-read ]; }
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
        if [ ! -f "$f" ] || { read_token && [ "$(jq -r .draft "$f")" = true ]; }; then
            echo "release not found" >&2
            exit 1
        fi
        jq '{isDraft: .draft, isPrerelease: .prerelease, assets, body, tagName: .tag_name}' "$f" | emit
        ;;
    "release create")
        tag=$3
        shift 3
        files=()
        latest=false draft=false pre=false notes=""
        while [ $# -gt 0 ]; do
            case "$1" in
                --notes-file) notes=$2; shift 2 ;;
                --title | --target) shift 2 ;;
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
        [ -z "$notes" ] || upd "$(relf "$tag")" --rawfile b "$notes" '.body = $b'
        for f in "${files[@]}"; do add_asset "$tag" "$f"; done
        [ "$latest" != true ] || echo "$tag" > "$S/latest"
        ;;
    "attestation verify")
        # 증명 상태: $FAKE_STATE/attested/<sha256> = 증명을 남긴 저장소
        sha=$(sha256sum "$3" | cut -d' ' -f1)
        if [ -f "$S/attested/$sha" ] && [ "$(cat "$S/attested/$sha")" = "$repo_arg" ]; then
            echo "Verification succeeded! ($3)"
        else
            echo "Error: no attestations found for $3 in $repo_arg" >&2
            exit 1
        fi
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
            repos/*/releases\?*)
                shopt -s nullglob
                rels=("$S"/releases/*.json)
                if [ "${#rels[@]}" = 0 ]; then echo '[]'; else jq -s . "${rels[@]}"; fi \
                    | if read_token; then jq '[.[] | select(.draft | not)]'; else cat; fi | emit
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
export PATH="$W/bin:$PATH" FAKE_STATE=$ST FAKE_REAL_GIT=$REAL_GIT GH_TOKEN=fake-write
# 러너처럼 전역 git 설정 없이 (개발 PC의 서명·push 설정이 섞이지 않게)
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1
unset GITHUB_SHA GITHUB_OUTPUT GITHUB_STEP_SUMMARY GITHUB_REPOSITORY

REPO=hyungwookc07/teamradio56
FULL=$(sed -n 's/.*public const string Version = "\([^"]*\)".*/\1/p' \
    simhub/src/TeamRadio56.SimHub/TeamRadio56Plugin.cs | head -n1)
NUM=$(grep -oE '^[0-9]+\.[0-9]+\.[0-9]+' <<< "$FULL")
TAG=v$NUM
# 이번 버전보다 높은 버전 (부 번호 +1) — 저장소 버전이 올라가도 그대로 맞게
HI=$(awk -F. '{ print $1 "." $2 + 1 ".0" }' <<< "$NUM")
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
        GH_TOKEN=fake-read bash "$SCRIPTS/release-preflight.sh" > "$W/out" 2>&1 || RC=$?
}

# mk_plugin_release <버전> [draft] [prerelease] — 매니페스트가 있는 플러그인 릴리스 v<버전>
mk_plugin_release() {
    mkdir -p "$W/man-$1"
    printf 'format=1\nversion=%s\ntag=v%s\n' "$1" "$1" > "$W/man-$1/teamradio56-release.txt"
    mk_release "v$1" "${2:-false}" "${3:-false}" "$W/man-$1/teamradio56-release.txt"
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
    && [ "$(out_val adopt)" = true ] && has '첫 릴리스'
expect "화면에서 만든 빈 릴리스(latest) → 이어받기"

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

# release/v<버전> 브랜치 push — 태그를 push할 수 없을 때의 게시 (수동 게시처럼 태그를 만든다)
reset_state
preflight push branch "release/$TAG"
[ "$RC" = 0 ] && [ "$(out_val publish)" = true ] && [ "$(out_val adopt)" = false ] \
    && [ "$(out_val tag)" = "$TAG" ]
expect "release/$TAG 브랜치 push: 게시 (이어받기 없음)"

preflight push branch release/v0.0.1
[ "$RC" = 1 ] && has "release/$TAG" && [ ! -s "$W/gho" ]
expect "release/<다른 버전> 브랜치 push → 실패"

echo '{"status": "ahead", "files": []}' > "$ST/compare.json"
preflight push branch "release/$TAG"
[ "$RC" = 1 ] && has 'main에 없습니다'
expect "release/$TAG 브랜치 커밋이 main에 없음 → 실패"

reset_state
echo "$SHA" > "$ST/commits/$TAG"
preflight push branch "release/$TAG"
[ "$RC" = 1 ] && has "태그 $TAG 가 이미 있습니다"
expect "release/$TAG 브랜치 push인데 태그가 이미 있음 → 실패"

# 태그 없이 릴리스만 보이는 경합이어도 이어받지 않는다 (이어받기는 태그 push만)
reset_state
mk_release "$TAG" false false
preflight push branch "release/$TAG"
[ "$RC" = 1 ] && has '이미 있습니다' && ! has '이어받습니다'
expect "release/$TAG 브랜치 push는 빈 릴리스를 이어받지 않음 → 실패"

# 변경 사항 한 줄 = 브랜치 커밋의 Release-Notes: 트레일러
reset_state
NR="$W/notes-repo"
rm -rf "$NR"
"$REAL_GIT" init -q "$NR"
"$REAL_GIT" -C "$NR" -c user.name=t -c user.email=t@t commit -q --allow-empty \
    -m "제목" -m "본문" -m "Release-Notes: 한 줄 변경 사항"
NSHA=$("$REAL_GIT" -C "$NR" rev-parse HEAD)
: > "$W/gho"
RC=0
env EVENT=push REF_TYPE=branch REF_NAME="release/$TAG" DRY_RUN=true GITHUB_SHA="$NSHA" REPO=$REPO \
    GIT_DIR="$NR/.git" GITHUB_OUTPUT="$W/gho" GITHUB_STEP_SUMMARY="$W/sum" SIMHUB_VERSION=9.13.3 NOTES_INPUT= \
    GH_TOKEN=fake-read bash "$SCRIPTS/release-preflight.sh" > "$W/out" 2>&1 || RC=$?
[ "$RC" = 0 ] && [ "$(out_val notes)" = "한 줄 변경 사항" ]
expect "release/$TAG 브랜치 push: 변경 사항 = 커밋의 Release-Notes: 트레일러"

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
mk_plugin_release 99.0.0
echo v99.0.0 > "$ST/latest"
preflight push tag "$TAG"
[ "$RC" = 1 ] && has '높지 않습니다'
expect "게시된 최신보다 낮은 버전 → 실패"

# 화면에서 만든 빈 릴리스가 latest여도(latest 매니페스트 404) 공개된 플러그인 릴리스와 비교한다
reset_state
echo "$SHA" > "$ST/commits/$TAG"
mk_plugin_release "$HI"
mk_release "$TAG" false false
echo "$TAG" > "$ST/latest"
preflight push tag "$TAG"
[ "$RC" = 1 ] && has "게시된 최신 $HI 보다 높지 않습니다" && [ ! -s "$W/gho" ]
expect "빈 릴리스가 latest, 더 높은 v$HI 플러그인 릴리스 있음 → 이어받기 전에 실패"

reset_state
echo "$SHA" > "$ST/commits/$TAG"
mk_plugin_release 0.0.1
mk_plugin_release "$HI" false true
mk_release v99.0.0 false false "$W/notes.txt"
preflight push tag "$TAG"
[ "$RC" = 0 ] && [ "$(out_val publish)" = true ] && has '최신 0.0.1'
expect "프리릴리스·매니페스트 없는 릴리스는 비교에서 뺌 (최신 0.0.1)"

mk_plugin_release "$HI" true false
preflight push tag "$TAG"
[ "$RC" = 0 ] && has '최신 0.0.1'
expect "초안 플러그인 릴리스는 비교에서 뺌 (읽기 토큰엔 안 보임)"

echo '/releases\?per_page' > "$ST/fail_match"
preflight push tag "$TAG"
[ "$RC" = 1 ] && has '목록을 확인하지 못했습니다'
expect "릴리스 목록 확인 실패 → 게시 실행은 실패"
preflight push branch release-dry/x
[ "$RC" = 0 ] && [ "$(out_val publish)" = false ] && has '::warning::게시된 플러그인 릴리스 목록'
expect "릴리스 목록 확인 실패 → dry run은 경고만"

# ── check-audio-release.sh ───────────────────────────────────────────────
echo "  · check-audio-release.sh"
python3 tools/pack_audio.py expected --tiers 1,2,3 \
    --out "$W/expected.txt" 2> /dev/null
# 가짜 팩: 지금 코드가 찾는 이름 전부(full) / 하나 빠짐(short) — 내용은 작은 가짜 wav.
# 팩 ID는 KNOWN_NAME_SETS에 없는 test-v* — 실제 표가 팩마다 늘어나도 기대 ID가 그대로다
python3 - "$W/expected.txt" "$W" <<'PY'
import os, sys, zipfile
names = [n.strip() for n in open(sys.argv[1], encoding="utf-8") if n.strip()]
for label, members in (("full", names), ("short", names[1:])):
    d = os.path.join(sys.argv[2], "pack-" + label)
    os.makedirs(d)
    with zipfile.ZipFile(os.path.join(d, "teamradio56_test_v016_1of1.zip"), "w") as zf:
        for n in members:
            zf.writestr(zipfile.ZipInfo(n, (2026, 1, 1, 0, 0, 0)), b"RIFF" + n.encode())
PY
desc_for() {       # <팩 zip 폴더> → 기술자 경로
    local z="$1/teamradio56_test_v016_1of1.zip" d="$1/audio_pack.txt"
    printf '# 시험용\npack=test-v016\ntag=audio-test-v016\nasset=%s,%s,%s\n' "$(basename "$z")" \
        "$(sha256sum "$z" | cut -d' ' -f1)" "$(stat -c %s "$z")" > "$d"
    echo "$d"
}
FULL_DESC=$(desc_for "$W/pack-full")
SHORT_DESC=$(desc_for "$W/pack-short")
# 손으로 배포한 kokoro-v015 모양 — tag가 audio-<pack>이 아닌 옛 기술자
V015_DESC=$W/v015.txt
printf 'pack=test-v015\ntag=v0.16.0\nasset=teamradio56_test_v015_1of1.zip,%s,1\n' \
    "$(printf 'a%.0s' {1..64})" > "$V015_DESC"

audio_check() {    # <PUBLISH> <기술자>
    : > "$W/gho"
    rm -rf "$W/dl"
    RC=0
    env PUBLISH="$1" GITHUB_SHA=$SHA GITHUB_OUTPUT="$W/gho" GITHUB_STEP_SUMMARY="$W/sum" GH_TOKEN=fake-read \
        bash "$SCRIPTS/check-audio-release.sh" "$REPO" "$2" "$W/dl" > "$W/out" 2>&1 || RC=$?
}
need() { [ "$RC" = 0 ] && [ "$(out_val need_pack)" = "$1" ] && [ "$(out_val pack)" = "$2" ]; }

reset_state
audio_check true "$V015_DESC"
need true test-v016
expect "test-v015 (tag=v0.16.0, 손 배포 모양) 게시 → 새 팩 test-v016"

mk_release audio-test-v016 false true "$W/pack-full/teamradio56_test_v016_1of1.zip"
audio_check true "$V015_DESC"
need true test-v017
expect "공개된 audio-test-v016은 건너뜀 → test-v017"

set_release audio-test-v016 '.draft = true'
audio_check true "$V015_DESC"
need true test-v016
expect "남은 초안 audio-test-v016은 다시 씀 → test-v016"

# 저장소 기술자가 아직 audio-<pack> 릴리스가 아니면(손 배포 kokoro-v015) v0.17.1 실행의 실제 경로 —
# 기대 ID는 운영 경로와 같은 CLI로 정한다 (KNOWN_NAME_SETS 내용에 기대지 않게)
REAL_DESC=$ROOT/voice_lines/audio_pack.txt
real=$(python3 -I tools/pack_audio.py descriptor --print "$REAL_DESC")
real_pack=$(sed -n 's/^PACK=//p' <<< "$real")
if [ "$(sed -n 's/^TAG=//p' <<< "$real")" != "audio-$real_pack" ]; then
    reset_state
    N1=$(python3 -I tools/pack_audio.py next-id "$real_pack" --expect-names "$W/expected.txt")
    audio_check true "$REAL_DESC"
    need true "$N1"
    expect "저장소 기술자 $real_pack (audio-<pack> 아님) 게시 → 새 팩 $N1"
fi

reset_state
audio_check false "$V015_DESC"
need false test-v015 && has '::warning::'
expect "dry run: 렌더 안 함 (경고, 지금 기술자)"

echo '{"status": "ahead", "files": [{"filename": "voice_lines/audio_pack.txt"}]}' > "$ST/compare.json"
audio_check true "$V015_DESC"
[ "$RC" = 1 ] && has '바뀌었습니다' \
    && [ ! -s "$W/gho" ]
expect "이 커밋 뒤 main 기술자가 바뀜 → 렌더 전에 실패"

reset_state
mk_release audio-test-v016 false true "$W/pack-full/teamradio56_test_v016_1of1.zip"
audio_check true "$FULL_DESC"
need false test-v016
expect "맞는 팩(공개·자산·멘트 집합) → need_pack=false"

reset_state
mk_release audio-test-v016 false true "$W/pack-short/teamradio56_test_v016_1of1.zip"
audio_check true "$SHORT_DESC"
need true test-v017
expect "멘트 집합이 다름 → 새 팩 test-v017"

reset_state
audio_check true "$FULL_DESC"
[ "$RC" = 1 ] && has '없거나 초안' && [ ! -s "$W/gho" ]
expect "기술자의 audio-<pack> 릴리스가 안 보임 → 새 ID로 렌더하지 않고 실패"

audio_check false "$FULL_DESC"
need false test-v016 && has '::warning::'
expect "dry run: 팩 릴리스 없음도 경고만"

mk_release audio-test-v016 false true "$W/pack-full/teamradio56_test_v016_1of1.zip"
set_release audio-test-v016 '.assets[0].size += 1'
audio_check true "$FULL_DESC"
[ "$RC" = 1 ] && has '크기' && [ ! -s "$W/gho" ]
expect "공개된 팩의 자산 크기 ≠ 기술자 → 실패"

mk_release audio-test-v016 false true "$W/pack-full/teamradio56_test_v016_1of1.zip"
set_release audio-test-v016 '.assets[0].digest = "sha256:" + ("0" * 64)'
audio_check true "$FULL_DESC"
[ "$RC" = 1 ] && has 'digest'
expect "공개된 팩의 digest ≠ 기술자 → 실패"

# 기술자 push 뒤 공개에서 멈춘 팩 — 읽기 토큰엔 초안이 '없음'으로 보이지만 새 팩(약 2시간)을 렌더하지 않는다
mk_release audio-test-v016 true true "$W/pack-full/teamradio56_test_v016_1of1.zip"
audio_check true "$FULL_DESC"
[ "$RC" = 1 ] && has '없거나 초안' && has 'draft=false' && [ ! -s "$W/gho" ]
expect "기술자가 가리키는 초안 → 새 ID로 렌더하지 않고 실패 (공개 방법 안내)"

# ── publish-audio.sh (release가 부른 태그 실행: BRANCH=main, SHA=태그 커밋) ─
echo "  · publish-audio.sh"
AZ=$W/pack-full/teamradio56_test_v016_1of1.zip
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
    mkdir -p "$WK/dist/audio/test-v016"
    cp "$AZ" "$WK/dist/audio/test-v016/"
    cp "$FULL_DESC" "$WK/dist/audio/audio_pack.txt"
}
publish_audio() {
    RC=0
    (cd "$WK" && env PACK=test-v016 SHA="$PSHA" BRANCH=main RUN_URL=https://run.invalid/1 REPO=$REPO GH_TOKEN=fake-write \
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
    && git -C "$O" log -1 --format=%s main | grep -q '음성 팩 기술자 갱신: test-v016' \
    && [ "$(rel_field audio-test-v016 .draft)" = false ] && [ "$(rel_field audio-test-v016 .prerelease)" = true ] \
    && [ "$(line_of '^release create audio-test-v016 .*--draft')" -lt "$(line_of '^release edit audio-test-v016 .*--draft=false')" ]
expect "팩 게시: 초안 → 기술자 커밋을 main에 push → 공개 (프리릴리스)"

# 앞선 시도가 기술자를 push하고 공개에서 멈춤 → publish 잡 재실행은 초안을 다시 올리고 공개만
pushed=$(git -C "$O" rev-parse main)
set_release audio-test-v016 '.draft = true'
: > "$ST/calls.log"
pa_work
publish_audio
[ "$RC" = 0 ] && [ "$(git -C "$O" rev-parse main)" = "$pushed" ] \
    && called '^release delete audio-test-v016' && [ "$(rel_field audio-test-v016 .draft)" = false ]
expect "재실행: main에 같은 기술자가 있으면 커밋 없이 공개만"

reset_state
pa_origin
printf '# 다른 사람이 바꿈\n' >> "$W/seed/voice_lines/audio_pack.txt"
git -C "$W/seed" -c user.name=t -c user.email=t@t.invalid commit -qam other
git -C "$W/seed" push -q origin HEAD:refs/heads/main
pa_work
publish_audio
[ "$RC" = 1 ] && has '바뀌었습니다' && [ "$(rel_field audio-test-v016 .draft)" = true ] \
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
    echo "audio_pack=test-v016"
    echo "audio_tag=audio-test-v016"
    echo "audio_assets=$(basename "$AZ")"
    echo "audio_sha256=$(sha256sum "$AZ" | cut -d' ' -f1)"
    echo "audio_size=$(stat -c %s "$AZ")"
} > "$P/dist/ci/assets/teamradio56-release.txt"
{
    printf 'format=1\nversion=%s\ntag=%s\ntitle=teamradio56 %s\ncommit=%s\n' "$NUM" "$TAG" "$FULL" "$SHA"
    grep -E '^(plugin_asset|plugin_sha256|audio_pack|audio_tag)=' "$P/dist/ci/assets/teamradio56-release.txt"
    printf 'simhub_wpf_version=9.13.3.0\nsimhub_plugins_version=1.0.0.0\n'
} > "$P/dist/ci/meta/release-info.txt"
printf 'teamradio56 %s\n\n생성한 본문\n' "$FULL" > "$P/dist/ci/meta/release-notes.md"
ZIPSHA=$(sha256sum "$P/dist/ci/assets/$ZIPNAME" | cut -d' ' -f1)
MANSHA=$(sha256sum "$P/dist/ci/assets/teamradio56-release.txt" | cut -d' ' -f1)

# Windows 빌드의 Defender 검사 보고서 (scan-defender.ps1 형식 — 형식 계약은 아래 -SampleReport 검사가 본다)
#   mk_av <dist/ci> — 지금 assets와 맞는 깨끗한 보고서
mk_av() {
    local d=$1 f
    {
        printf '%s\n' format=1 'scanner=Microsoft Defender' product_version=4.18.25100.9 \
            engine_version=1.1.25100.9 antivirus_signature_version=1.441.123.0 \
            signature_updated=2026-11-23T06:00:00Z signature_update=Update-MpSignature running_mode=Normal \
            scan_time=2026-11-23T07:00:00Z exclusions_removed=2 archive_scanning=enabled selftest=detected \
            selftest_threat=Virus:DOS/EICAR_Test_File selftest_archive=detected scanned_files=2 \
            extracted_files=2 result=clean
        while IFS= read -r f; do
            echo "asset=$f,$(sha256sum "$d/assets/$f" | cut -d' ' -f1),$(stat -c %s "$d/assets/$f")"
        done < <(find "$d/assets" -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort)
    } > "$d/meta/av-scan.txt"
}
AVR=$P/dist/ci/meta/av-scan.txt
av_edit() { python3 -I -c 'import sys; p, a, b = sys.argv[1:]; s = open(p).read(); assert a in s, a; open(p, "w").write(s.replace(a, b))' "$AVR" "$1" "$2"; }
mk_av "$P/dist/ci"

publish() {        # <MODE>
    RC=0
    (cd "$P" && env TAG=$TAG SHA=$SHA MODE="$1" REPO=$REPO GITHUB_STEP_SUMMARY="$W/sum" GH_TOKEN=fake-write \
        bash "$SCRIPTS/publish-release.sh") > "$W/out" 2>&1 || RC=$?
}
pub_reset() {
    reset_state
    echo "$SHA" > "$ST/commits/$TAG"
    mk_release audio-test-v016 false true "$AZ"
}
UP_ZIP="^release upload $TAG dist/ci/assets/$ZIPNAME"
UP_MAN="^release upload $TAG dist/ci/assets/teamradio56-release.txt"

pub_reset
publish tag
[ "$RC" = 0 ] \
    && called "^release create $TAG dist/ci/assets/$ZIPNAME dist/ci/assets/teamradio56-release.txt .*--latest --verify-tag" \
    && ! called '^release (upload|edit)' && [ "$(cat "$ST/latest")" = "$TAG" ]
expect "새로 만들기: create --latest --verify-tag (zip·매니페스트 함께)"
body_of() { jq -r .body "$ST/releases/$1.json"; }
body_has() { body_of "$TAG" | grep -qF -- "$1"; }
body_line() { body_of "$TAG" | grep -nF -- "$1" | head -n1 | cut -d: -f1; }
body_has '생성한 본문' && body_has '### 파일 검증' \
    && body_has "| \`$ZIPNAME\` | \`$ZIPSHA\` |" \
    && body_has "| \`teamradio56-release.txt\` | \`$MANSHA\` |" \
    && body_has "Get-FileHash .\\$ZIPNAME -Algorithm SHA256" \
    && body_has "gh attestation verify $ZIPNAME -R $REPO" \
    && body_has '엔진 1.1.25100.9, 서명 1.441.123.0, 서명 갱신 2026-11-23 06:00 UTC' \
    && [ "$(body_line '생성한 본문')" -lt "$(body_line '### 파일 검증')" ]
expect "새로 만들기: 본문 = 생성한 본문 + 파일 검증 절 (SHA-256 두 줄 · Get-FileHash · gh attestation verify · Defender 버전)"

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
body_has "| \`$ZIPNAME\` | \`$ZIPSHA\` |" && body_has "| \`teamradio56-release.txt\` | \`$MANSHA\` |" \
    && body_has "gh attestation verify $ZIPNAME -R $REPO" \
    && [ "$(body_line '생성한 본문')" -lt "$(body_line '### 파일 검증')" ] \
    && [ "$(body_line '### 파일 검증')" -lt "$(body_line '화면에서 쓴 설명')" ]
expect "이어받기: 본문 = 생성한 본문 → 파일 검증 절 → 화면에서 쓴 설명"

# 이어받는 빈 릴리스가 latest(latest 매니페스트 404)여도 공개된 플러그인 릴리스와 비교 — 올리기 전에 멈춘다
pub_reset
mk_plugin_release "$HI"
mk_release "$TAG" false false
echo "$TAG" > "$ST/latest"
publish tag
[ "$RC" = 1 ] && has "게시된 최신 $HI 보다 높지 않습니다" \
    && ! called '^release (create|upload|edit|delete-asset)' \
    && [ "$(jq '.assets | length' "$ST/releases/$TAG.json")" = 0 ]
expect "이어받기: 더 높은 v$HI 플러그인 릴리스 있음 → 올리기 전에 실패"

# 쓰기 토큰엔 초안이 목록에 보인다 — 초안·프리릴리스는 비교에서 뺀다
pub_reset
mk_plugin_release 0.0.1
mk_plugin_release "$HI" true false
mk_plugin_release 99.0.0 false true
publish tag
[ "$RC" = 0 ] && called "^release create $TAG "
expect "새로 만들기: 낮은 v0.0.1만 비교 (초안 v$HI·프리릴리스 v99.0.0은 뺌)"

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

# 수동 게시·release/v* push: 태그를 만들며 게시 (--target 이 커밋, --verify-tag 없음)
pub_reset
rm -f "$ST/commits/$TAG"
publish dispatch
[ "$RC" = 0 ] \
    && called "^release create $TAG dist/ci/assets/$ZIPNAME dist/ci/assets/teamradio56-release.txt .*--latest --target $SHA" \
    && ! called 'verify-tag' && [ "$(cat "$ST/latest")" = "$TAG" ]
expect "태그 없이 게시(dispatch): create --latest --target <커밋>"

pub_reset
publish dispatch
[ "$RC" = 1 ] && has "태그 $TAG 가 이미 있습니다" && ! called '^release create'
expect "태그 없이 게시(dispatch)인데 태그가 이미 있음 → 실패"

pub_reset
mk_release "$TAG" false false
publish dispatch
[ "$RC" = 1 ] && has '이미 있습니다' \
    && ! called '^release (create|upload|edit)'
expect "수동 게시는 기존 릴리스를 이어받지 않음 → 실패"

# 게시 전 Defender 검사 보고서 — 없거나, 깨끗하지 않거나, 게시할 바이트와 다르면 아무것도 올리지 않는다
nothing_published() { ! called '^release (create|upload|edit)' && [ ! -f "$ST/releases/$TAG.json" ]; }
av_refuses() {     # <설명> <출력에 있어야 할 문구>
    pub_reset
    publish tag
    [ "$RC" = 1 ] && has '백신 검사 보고서' && has "$2" && nothing_published
    expect "$1"
    mk_av "$P/dist/ci"
}
rm -f "$AVR"
av_refuses "Defender 보고서 없음 → 게시 거부" '보고서가 없습니다'
av_edit 'result=clean' 'result=threat'
av_refuses "Defender 보고서 result=threat → 게시 거부" 'result=threat'
av_edit $'selftest=detected\n' ''
av_refuses "Defender 보고서에 selftest 줄 없음 → 게시 거부" 'selftest=없음'
av_edit 'selftest=detected' 'selftest=not_detected'
av_refuses "Defender 자체 시험(EICAR) 실패 보고서 → 게시 거부" 'selftest=not_detected'
av_edit "asset=$ZIPNAME,$ZIPSHA" "asset=$ZIPNAME,$(printf '0%.0s' {1..64})"
av_refuses "보고서의 zip sha256 ≠ 게시할 zip → 게시 거부" "$ZIPNAME 이(가) 검사한 바이트와 다릅니다"
av_edit "asset=teamradio56-release.txt,$MANSHA" "asset=teamradio56-release.txt,$(printf 'f%.0s' {1..64})"
av_refuses "보고서의 매니페스트 sha256 ≠ 게시할 매니페스트 → 게시 거부" 'teamradio56-release.txt 이(가) 검사한 바이트와 다릅니다'
python3 -I -c 'import sys; p = sys.argv[1]; s = [l for l in open(p) if not l.startswith("asset=teamradio56-release.txt,")]; open(p, "w").writelines(s)' "$AVR"
av_refuses "보고서에 매니페스트 줄 없음 (zip만 검사) → 게시 거부" '검사한 파일'
echo "asset=other.zip,$ZIPSHA,1" >> "$AVR"
av_refuses "보고서에 게시하지 않는 파일 줄 → 게시 거부 (집합이 같아야)" '검사한 파일'
av_edit 'signature_updated=2026-11-23T06:00:00Z' 'signature_updated=2026-11-19T06:00:00Z'
av_refuses "서명이 검사 시각보다 4일 오래됨 → 게시 거부" '3일 넘게'
av_edit 'result=clean' $'result=clean\nresult=threat'
av_refuses "보고서에 result가 두 번 → 게시 거부" '두 번'
av_edit 'running_mode=Normal' 'running_mode=sample'
av_refuses "검증 게이트용 견본 보고서 → 게시 거부" '견본 보고서'
sed -i 's/$/\r/' "$AVR"
av_refuses "CRLF 보고서 → 게시 거부" 'LF가 아닙니다'
# 보고서가 맞는 이어받기·수동 게시도 파일 검증 절이 붙는다
pub_reset
rm -f "$ST/commits/$TAG"
publish dispatch
[ "$RC" = 0 ] && body_has '### 파일 검증' && body_has "| \`$ZIPNAME\` | \`$ZIPSHA\` |"
expect "수동 게시(dispatch)도 본문에 파일 검증 절"

# ── verify-release-artifact.sh (같은 보고서 검사 — dotnet·리플레이 러너는 가짜) ─
echo "  · verify-release-artifact.sh"
VR=$W/vr
mkdir -p "$W/bin-vr" "$VR/simhub/tests/TeamRadio56.Replay/bin/Release/net8.0" "$W/vr-audio"
printf '#!/bin/sh\necho "dotnet $*" >> "$FAKE_STATE/dotnet.log"\n' > "$W/bin-vr/dotnet"
printf '#!/bin/sh\necho "replay $*" >> "$FAKE_STATE/dotnet.log"\n' \
    > "$VR/simhub/tests/TeamRadio56.Replay/bin/Release/net8.0/teamradio56-replay"
chmod +x "$W/bin-vr/dotnet" "$VR/simhub/tests/TeamRadio56.Replay/bin/Release/net8.0/teamradio56-replay"
cp "$AZ" "$W/vr-audio/"
printf 'pack=test-v016\n' > "$VR/desc.txt"
verify_art() {
    RC=0
    rm -rf "$VR/dist"
    cp -r "$P/dist" "$VR/dist"
    : > "$W/vsum"
    : > "$ST/dotnet.log"
    (cd "$VR" && env PATH="$W/bin-vr:$PATH" REPO=$REPO PUBLISH=false GITHUB_STEP_SUMMARY="$W/vsum" \
        bash "$SCRIPTS/verify-release-artifact.sh" dist/ci "$W/vr-audio" "$VR/desc.txt") > "$W/out" 2>&1 || RC=$?
}
reset_state
verify_art
[ "$RC" = 0 ] && grep -q '^replay --release-check' "$ST/dotnet.log" \
    && grep -qF "| \`$ZIPNAME\` | \`$ZIPSHA\` |" "$W/vsum" && grep -qF "gh attestation verify $ZIPNAME -R $REPO" "$W/vsum" \
    && grep -q 'Defender 검사: 엔진 1.1.25100.9' "$W/vsum"
expect "아티팩트 검증: 보고서 통과 → 리플레이 흐름, 요약에 파일 검증 절 미리 보기"
rm -f "$AVR"
verify_art
[ "$RC" = 1 ] && has '백신 검사 보고서' && has '보고서가 없습니다' && [ ! -s "$ST/dotnet.log" ]
expect "아티팩트 검증: 보고서 없음 → 빌드 전에 실패"
mk_av "$P/dist/ci"
av_edit "asset=$ZIPNAME,$ZIPSHA" "asset=$ZIPNAME,$(printf '0%.0s' {1..64})"
verify_art
[ "$RC" = 1 ] && has "$ZIPNAME 이(가) 검사한 바이트와 다릅니다" && [ ! -s "$ST/dotnet.log" ]
expect "아티팩트 검증: 보고서 zip sha256 불일치 → 실패"
mk_av "$P/dist/ci"
av_edit 'result=clean' 'result=threat'
verify_art
[ "$RC" = 1 ] && has 'result=threat'
expect "아티팩트 검증: result=threat → 실패"
mk_av "$P/dist/ci"

# ── attest-check.sh (증명 전 파일 재확인 · 증명 후 사용자 안내 명령) ─────────
echo "  · attest-check.sh"
attest_check() {   # <files|verify> [GITHUB_REPOSITORY]
    RC=0
    : > "$W/asum"
    (cd "$P" && env REPO=$REPO GITHUB_REPOSITORY="${2:-$REPO}" GITHUB_STEP_SUMMARY="$W/asum" GH_TOKEN=fake-read \
        bash "$SCRIPTS/attest-check.sh" "$1" dist/ci) > "$W/out" 2>&1 || RC=$?
}
attest_mark() { mkdir -p "$ST/attested"; echo "$2" > "$ST/attested/$1"; }
reset_state
attest_check files
[ "$RC" = 0 ] && has "$ZIPNAME  $ZIPSHA"
expect "증명 전 확인: zip + 매니페스트, 보고서와 바이트 일치 → 통과"
rm -f "$AVR"
attest_check files
[ "$RC" = 1 ] && has '증명하지 않습니다'
expect "증명 전 확인: 보고서 없음 → 증명하지 않음"
mk_av "$P/dist/ci"
echo x > "$P/dist/ci/assets/extra.txt"
attest_check files
[ "$RC" = 1 ] && has 'assets에는'
expect "증명 전 확인: assets에 다른 파일 → 실패"
rm -f "$P/dist/ci/assets/extra.txt"
cp "$P/dist/ci/assets/$ZIPNAME" "$W/zip.bak"
printf 'tampered' >> "$P/dist/ci/assets/$ZIPNAME"
attest_check files
[ "$RC" = 1 ] && has 'plugin_sha256'
expect "증명 전 확인: zip이 매니페스트 sha256과 다름 → 실패"
cp "$W/zip.bak" "$P/dist/ci/assets/$ZIPNAME"
reset_state
attest_mark "$ZIPSHA" "$REPO"
attest_mark "$MANSHA" "$REPO"
attest_check verify
[ "$RC" = 0 ] && called "^attestation verify dist/ci/assets/$ZIPNAME -R $REPO\$" \
    && called "^attestation verify dist/ci/assets/teamradio56-release.txt -R $REPO\$" \
    && grep -qF "gh attestation verify $ZIPNAME -R $REPO" "$W/asum"
expect "증명 후 확인: 두 파일 다 gh attestation verify -R <저장소> 통과"
reset_state
attest_mark "$ZIPSHA" "$REPO"
attest_check verify
[ "$RC" = 1 ] && has 'teamradio56-release.txt 의 빌드 출처 증명을 확인하지 못했습니다' \
    && [ "$(grep -c '^attestation verify dist/ci/assets/teamradio56-release.txt' "$ST/calls.log")" = 6 ]
expect "증명 후 확인: 매니페스트 증명 없음 → 6번 다시 본 뒤 실패"
reset_state
attest_mark "$ZIPSHA" someone/fork
attest_mark "$MANSHA" someone/fork
attest_check verify
[ "$RC" = 1 ] && has "$ZIPNAME 의 빌드 출처 증명"
expect "증명 후 확인: 다른 저장소의 증명 → 실패"
attest_check verify someone/fork
[ "$RC" = 0 ]
expect "증명 후 확인: 포크의 dry run은 그 저장소(GITHUB_REPOSITORY)에서 확인"

# ── scan-defender.ps1 -SampleReport ↔ bash 검사기 (보고서 형식 계약) ────────
echo "  · scan-defender.ps1 보고서 형식 ↔ release-lib.sh"
PWSH=$(command -v pwsh || true)
if [ -z "$PWSH" ] && [ -x "$HOME/.dotnet/tools/pwsh" ]; then PWSH="$HOME/.dotnet/tools/pwsh"; fi
if [ -n "$PWSH" ]; then
    SR=$W/sample
    mkdir -p "$SR/meta"
    cp -r "$P/dist/ci/assets" "$SR/assets"
    RC=0
    "$PWSH" -NoProfile -NonInteractive -File simhub/ci/scan-defender.ps1 -SampleReport \
        -AssetsDir "$SR/assets" -ReportPath "$SR/meta/av-scan.txt" > "$W/out" 2>&1 || RC=$?
    why=$(REPO=$REPO bash -c '. "$1"; av_report_problem "$2" "$3"' _ "$SCRIPTS/release-lib.sh" \
        "$SR/meta/av-scan.txt" "$SR/assets")
    [ "$RC" = 0 ] && [[ "$why" == *'견본 보고서'* ]]
    expect "견본 보고서(-SampleReport)는 그대로는 거부 (실제 검사가 아님)"
    sed -i 's/^running_mode=sample$/running_mode=Normal/' "$SR/meta/av-scan.txt"
    why=$(REPO=$REPO bash -c '. "$1"; av_report_problem "$2" "$3"' _ "$SCRIPTS/release-lib.sh" \
        "$SR/meta/av-scan.txt" "$SR/assets")
    echo "$why" > "$W/out"
    [ -z "$why" ] && [ "$(grep -c '^asset=' "$SR/meta/av-scan.txt")" = 2 ] \
        && ! LC_ALL=C grep -q $'\r' "$SR/meta/av-scan.txt" && [ "$(head -c3 "$SR/meta/av-scan.txt")" = "for" ]
    expect "scan-defender.ps1이 쓰는 보고서 형식을 bash 검사기가 그대로 읽음 (LF, BOM 없음, asset 줄 2개)"
elif [ "${TEAMRADIO56_STRICT:-0}" = 1 ]; then
    fails+=("scan-defender.ps1 보고서 형식 계약 — pwsh 없음 (TEAMRADIO56_STRICT=1)")
else
    echo "   ⏭  scan-defender.ps1 보고서 형식 계약 건너뜀 — pwsh 없음"
fi

# ── release.yml 정적 검사 ────────────────────────────────────────────────
echo "  · release.yml 정적 검사"
cat > "$W/wfcheck.py" <<'PY'
import os, re, sys
import yaml

ATTEST = "actions/attest-build-provenance@4d101475d8b20a2381f78447822ac1eab6504dd8"
check, wf = sys.argv[1], sys.argv[2]


def fail(msg):
    print(msg)
    sys.exit(1)


def as_list(v):
    return [v] if isinstance(v, str) else list(v or [])


if check == "pins":
    bad = []
    for f in sys.argv[2:]:
        for i, line in enumerate(open(f, encoding="utf-8"), 1):
            m = re.match(r"^\s*(?:-\s+)?uses:\s*(\S+)(.*)$", line)
            if not m or m.group(1).startswith("./"):
                continue
            ref, rest = m.group(1), m.group(2)
            if not re.fullmatch(r"[\w.-]+/[\w.-]+(?:/[\w./-]+)?@[0-9a-f]{40}", ref) or not re.search(r"#\s*v\d", rest):
                bad.append(f"{f}:{i}: {line.strip()}")
    if bad:
        fail("40자리 SHA + # v<버전> 주석으로 고정되지 않은 uses:\n" + "\n".join(bad))
    sys.exit(0)

text = open(wf, encoding="utf-8").read()
d = yaml.safe_load(text)
jobs = d["jobs"]


def steps(job):
    return jobs[job].get("steps", [])


def find(st, pred):
    return [i for i, s in enumerate(st) if pred(s)]


if check == "scan":
    st = steps("build")
    rel = find(st, lambda s: "release.bat -Ci" in str(s.get("run", "")))
    scan = find(st, lambda s: "scan-defender.ps1" in str(s.get("run", "")))
    up = find(st, lambda s: str(s.get("uses", "")).startswith("actions/upload-artifact@")
              and str((s.get("with") or {}).get("name", "")).startswith("teamradio56-release-"))
    if len(rel) != 1 or len(scan) != 1 or len(up) != 1:
        fail(f"build 잡: release.bat {len(rel)}개, Defender 검사 {len(scan)}개, 릴리스 업로드 {len(up)}개 (각 1개여야)")
    if not rel[0] < scan[0] < up[0]:
        fail("build 잡: Defender 검사가 release.bat 뒤, 아티팩트 업로드 앞이 아니다")
    s = st[scan[0]]
    if "if" in s or s.get("continue-on-error"):
        fail("Defender 검사 단계에 if/continue-on-error — 항상 돌고 실패하면 멈춰야 한다")
    if s.get("shell") != "powershell":
        fail("Defender 검사는 Windows PowerShell 5 (shell: powershell)")
    run = s["run"]
    for need in ["-AssetsDir dist\\ci\\assets", "-ReportPath dist\\ci\\meta\\av-scan.txt",
                 "-WorkDir dist\\av-scan", "exit $LASTEXITCODE"]:
        if need not in run:
            fail(f"Defender 검사 단계에 '{need}' 없음")
    for bad in ["-SelfTest", "-SampleReport"]:
        if bad in run:
            fail(f"Defender 검사 단계가 {bad}를 쓴다 (실제 검사가 아님)")
    u = st[up[0]]
    if (u.get("with") or {}).get("path") != "dist/ci/" or "if" in u:
        fail("릴리스 아티팩트 업로드는 dist/ci/ 전체(보고서 포함), 조건 없이")
    if "publish" in str(jobs["build"].get("if", "")):
        fail("build 잡이 게시 여부에 묶였다 — dry run에서도 검사해야 한다")
elif check == "attest":
    a = jobs.get("attest") or fail("attest 잡 없음")
    if a.get("permissions") != {"contents": "read", "id-token": "write", "attestations": "write"}:
        fail(f"attest 잡 권한이 contents: read · id-token: write · attestations: write가 아니다: {a.get('permissions')}")
    if "verify-artifact" not in as_list(a.get("needs")):
        fail("attest 잡이 verify-artifact를 기다리지 않는다")
    cond = str(a.get("if", ""))
    if "needs.verify-artifact.result == 'success'" not in cond:
        fail("attest 잡 조건에 verify-artifact 성공이 없다")
    if "publish" in cond:
        fail("attest 잡이 게시 여부에 묶였다 — dry run에서도 돌아야 한다")
    st = a.get("steps", [])
    at = find(st, lambda s: str(s.get("uses", "")).startswith("actions/attest-build-provenance@"))
    if len(at) != 1:
        fail("attest 잡에 actions/attest-build-provenance 단계가 하나가 아니다")
    s = st[at[0]]
    if s.get("uses") != ATTEST or f"uses: {ATTEST} # v4.2.2" not in text:
        fail(f"attest-build-provenance 고정이 다르다: {s.get('uses')}")
    if "if" in s or s.get("continue-on-error"):
        fail("증명 단계에 if/continue-on-error")
    sp = [l.strip() for l in str((s.get("with") or {}).get("subject-path", "")).splitlines() if l.strip()]
    if sp != ["dist/ci/assets/teamradio56-plugin-${{ needs.preflight.outputs.version }}.zip",
              "dist/ci/assets/teamradio56-release.txt"]:
        fail(f"증명 대상이 게시 파일 둘(zip, 매니페스트)이 아니다: {sp}")
    dl = find(st, lambda s: str(s.get("uses", "")).startswith("actions/download-artifact@")
              and (s.get("with") or {}).get("name") == "teamradio56-release-${{ needs.preflight.outputs.version }}")
    pre = find(st, lambda s: "attest-check.sh files" in str(s.get("run", "")))
    post = find(st, lambda s: "attest-check.sh verify" in str(s.get("run", "")))
    if not dl or not pre or not post or not dl[0] < pre[0] < at[0] < post[0]:
        fail("attest 잡 순서: 아티팩트 내려받기 → attest-check.sh files → 증명 → attest-check.sh verify")
    for i in pre + post:
        if "if" in st[i] or st[i].get("continue-on-error"):
            fail("attest-check 단계에 if/continue-on-error")
elif check == "perms":
    if d.get("permissions") != {}:
        fail("워크플로 최상위 permissions가 {}가 아니다")
    for name, j in jobs.items():
        p = j.get("permissions")
        if not isinstance(p, dict):
            fail(f"{name} 잡에 권한 목록(permissions: 이름: 수준)이 없다")
        if name != "attest" and ("id-token" in p or "attestations" in p):
            fail(f"{name} 잡에 id-token/attestations 권한 — 증명 권한은 attest 잡에만")
elif check == "publish":
    p = jobs["publish"]
    needs = as_list(p.get("needs"))
    if "attest" not in needs or "verify-artifact" not in needs:
        fail(f"publish가 attest·verify-artifact를 기다리지 않는다: {needs}")
    cond = str(p.get("if", ""))
    for need in ["needs.attest.result == 'success'", "needs.verify-artifact.result == 'success'",
                 "needs.preflight.outputs.publish == 'true'"]:
        if need not in cond:
            fail(f"publish 조건에 {need} 없음")
else:
    fail(f"모르는 검사 {check}")
PY
WF=$ROOT/.github/workflows/release.yml
mapfile -t WF_ALL < <(ls "$ROOT"/.github/workflows/*.yml "$ROOT"/.github/actions/*/action.yml)
wfcheck() { RC=0; python3 -I "$W/wfcheck.py" "$@" > "$W/out" 2>&1 || RC=$?; }
wfcheck pins "${WF_ALL[@]}"
[ "$RC" = 0 ]
expect "워크플로·액션의 uses: 전부 40자리 SHA + # v<버전> 주석"
wfcheck scan "$WF"
[ "$RC" = 0 ]
expect "build 잡: Defender 검사가 release.bat 뒤·업로드 앞, 조건·continue-on-error 없음"
wfcheck attest "$WF"
[ "$RC" = 0 ]
expect "attest 잡: 권한 정확히 셋, verify-artifact 뒤, dry run에서도, 게시 파일 둘을 증명 → gh로 확인"
wfcheck perms "$WF"
[ "$RC" = 0 ]
expect "최상위 permissions: {}, 증명 권한은 attest 잡에만"
wfcheck publish "$WF"
[ "$RC" = 0 ]
expect "publish가 verify-artifact·attest 성공을 기다림"
# 검사기가 무는지 — 변형한 사본은 걸려야 한다
mutant() {         # <검사> <설명> <바꿀 문자열> <새 문자열>
    python3 -I -c 'import sys; p, o, a, b = sys.argv[1:]; s = open(p, encoding="utf-8").read(); assert a in s, a; open(o, "w", encoding="utf-8").write(s.replace(a, b, 1))' \
        "$WF" "$W/mutant.yml" "$3" "$4"
    wfcheck "$1" "$W/mutant.yml"
    [ "$RC" = 1 ]
    expect "정적 검사가 문다: $2"
}
mutant pins "attest 액션을 태그(@v4)로" '@4d101475d8b20a2381f78447822ac1eab6504dd8' '@v4'
mutant attest "attest 액션을 다른 SHA로" '@4d101475d8b20a2381f78447822ac1eab6504dd8' "@$(printf 'a%.0s' {1..40})"
mutant scan "Defender 검사 단계 이름만 남기고 스크립트 제거" 'simhub\ci\scan-defender.ps1' 'simhub\ci\other.ps1'
mutant scan "Defender 검사에 continue-on-error" '        timeout-minutes: 30
        env:
          TR56_AUDIO_DIR' '        timeout-minutes: 30
        continue-on-error: true
        env:
          TR56_AUDIO_DIR'
mutant scan "Defender 검사를 견본 보고서로" '-WorkDir dist\av-scan @extra' '-WorkDir dist\av-scan -SampleReport @extra'
mutant attest "attest 잡에 권한 하나 더" '      id-token: write
      attestations: write' '      id-token: write
      attestations: write
      packages: write'
mutant attest "attest 잡을 게시 실행에만" "if: \${{ !cancelled() && needs.verify-artifact.result == 'success' }}" \
    "if: \${{ !cancelled() && needs.verify-artifact.result == 'success' && needs.preflight.outputs.publish == 'true' }}"
mutant attest "증명 대상에서 매니페스트 빠짐" '            dist/ci/assets/teamradio56-release.txt
' ''
mutant perms "build 잡에 id-token 권한" '    runs-on: windows-2025
    timeout-minutes: 60
    permissions:
      contents: read' '    runs-on: windows-2025
    timeout-minutes: 60
    permissions:
      contents: read
      id-token: write'
mutant publish "publish가 attest를 기다리지 않음" 'needs: [preflight, verify-artifact, attest]' 'needs: [preflight, verify-artifact]'
mutant publish "publish 조건에서 attest 성공 빠짐" " && needs.attest.result == 'success'" ''

# 저장소 파일에 완성된 EICAR 문자열이 있으면 저장소·체크아웃 자체가 백신에 걸린다 (조각으로만 둔다)
EICAR=$(printf '%s' 'X5O!P%@AP' '[4\PZX54(P^)' '7CC)7}$' 'EICAR-STAN' 'DARD-ANTIVI' 'RUS-TEST-F' 'ILE!$H+H*')
git -C "$ROOT" ls-files -co --exclude-standard -z | (cd "$ROOT" && xargs -0 grep -slF -- "$EICAR") > "$W/out" || true
[ "$(printf '%s' "$EICAR" | sha256sum | cut -d' ' -f1)" = 275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f ] \
    && [ ! -s "$W/out" ]
expect "저장소 파일에 완성된 EICAR 문자열 없음 (조각 조립 sha256 = 표준)"

if [ "${#fails[@]}" -gt 0 ]; then
    echo "❌ 릴리스 흐름 검사 실패 ${#fails[@]}건 (통과 $pass건)"
    for f in "${fails[@]}"; do echo "   - $f"; done
    exit 1
fi
echo "✅ 릴리스 흐름 검사 $pass건 통과 (가짜 gh/curl/git, 네트워크 없음)"
