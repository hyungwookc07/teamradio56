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

# 이 실행의 태그를 뺀, 공개된(초안·프리릴리스 아님) 플러그인 릴리스 중 가장 높은 버전.
# 플러그인 릴리스 = teamradio56-release.txt가 있는 vX.Y.Z 릴리스 (게시 스크립트가 태그 = v<매니페스트
# version>을 강제하므로 태그 이름이 곧 버전이다 — 매니페스트를 받지 않는다).
# releases/latest는 보지 않는다: 화면에서 만든 빈 릴리스가 latest가 되면 매니페스트가 404라
# '첫 릴리스'로 보고 비교를 건너뛰게 된다. 어느 릴리스가 latest 표시인지와도 상관없다.
#   published_plugin_max <이 실행의 태그> — 0: stdout에 x.y.z (빈 출력 = 첫 릴리스) / 1: 확인 실패
published_plugin_max() {
    local json
    json=$(gh api --paginate "repos/$REPO/releases?per_page=100") || return 1
    jq -r --arg t "$1" '.[] | select((.draft | not) and (.prerelease | not) and .tag_name != $t)
        | select(any(.assets[]?; .name == "teamradio56-release.txt")) | .tag_name' <<< "$json" \
        | sed -n 's/^v\([0-9]*\.[0-9]*\.[0-9]*\)$/\1/p' | sort -V | tail -n1
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
