#!/usr/bin/env bash
# release 워크플로 스크립트 공용 함수 — source해서 쓴다. env REPO.
#
# gh는 '없음'과 '확인 실패'(네트워크·권한)를 둘 다 종료 코드 1로 낸다. 없음만 2로 나눠
# 돌려준다 — 없음을 실패로 보거나 확인 실패를 없음으로 보면 게시 판단이 틀어진다.

# gh release view — 0: 있음(stdout json) / 2: 없음 / 1: 확인 실패.
# 초안은 쓰기 권한 토큰에만 보인다 (읽기 토큰에는 '없음')
gh_release_view() {
    local err out rc=0
    err=$(mktemp)
    out=$(gh release view "$1" --repo "$REPO" --json "$2" 2> "$err") || rc=$?
    if [ "$rc" = 0 ]; then
        rm -f "$err"
        printf '%s\n' "$out"
        return 0
    fi
    if grep -q 'release not found' "$err"; then
        rc=2
    else
        sed 's/^/  gh: /' "$err" >&2
        rc=1
    fi
    rm -f "$err"
    return "$rc"
}

# 공개된 릴리스(REST 원본 json, 초안은 안 나온다) — 0: 있음 / 2: 없음 / 1: 확인 실패.
# immutable 같은 새 필드는 없으면 그냥 빠진다 (gh --json은 모르는 필드에서 실패)
gh_published_release() {
    local err out rc=0
    err=$(mktemp)
    out=$(gh api "repos/$REPO/releases/tags/$1" 2> "$err") || rc=$?
    if [ "$rc" = 0 ]; then
        rm -f "$err"
        printf '%s\n' "$out"
        return 0
    fi
    if grep -q 'HTTP 404' "$err"; then
        rc=2
    else
        sed 's/^/  gh: /' "$err" >&2
        rc=1
    fi
    rm -f "$err"
    return "$rc"
}

# GitHub 화면에서 만든 빈 릴리스를 이 태그 실행이 이어받을 수 있는지.
# 출력이 비면 가능, 아니면 이유 한 줄 (preflight와 게시 잡이 같은 규칙을 쓴다).
#   adopt_blocker <릴리스 json(gh_published_release)> <태그> <이 실행의 커밋>
adopt_blocker() {
    local json=$1 tag=$2 sha=$3 n remote
    if [ "$(jq -r '.draft // false' <<< "$json")" = true ]; then
        echo "초안입니다"
        return
    fi
    n=$(jq '.assets | length' <<< "$json")
    if [ "$n" != 0 ]; then
        echo "자산이 ${n}개 있습니다 (빈 릴리스만 이어받는다)"
        return
    fi
    if [ "$(jq -r '.immutable // false' <<< "$json")" = true ]; then
        echo "변경 불가(immutable) 릴리스라 자산을 올릴 수 없습니다"
        return
    fi
    if ! remote=$(gh api "repos/$REPO/commits/$tag" --jq .sha 2> /dev/null); then
        echo "태그 $tag 의 커밋을 확인하지 못했습니다"
        return
    fi
    if [ "$remote" != "$sha" ]; then
        echo "태그 $tag 가 이 실행의 커밋이 아닌 $remote 를 가리킵니다"
        return
    fi
}
