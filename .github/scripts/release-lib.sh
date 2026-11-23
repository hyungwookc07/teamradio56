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

# ── 백신 검사 보고서 ─────────────────────────────────────────────────────
# simhub/ci/scan-defender.ps1이 Windows 러너에서 Microsoft Defender로 검사하고 key=value(LF) 보고서를 남긴다.
#   - release build 잡: dist/ci/meta/av-scan.txt — 게시 파일(asset=<이름>,<sha256>,<크기>)과 그 매니페스트가
#     가리키는 음성 팩 zip(audio_file=<이름>,<sha256>,<크기>). 아티팩트에 함께 실려 verify-artifact·attest·
#     publish가 '게시할 바로 그 바이트'와 '매니페스트가 가리키는 팩 바이트'를 깨끗하다고 했는지 본다
#   - audio-pack scan 잡: 새 음성 팩만(audio_file 줄) — publish-audio.sh가 올릴 zip(기술자 asset 줄)과 대조
AV_MAX_SIG_AGE_SEC=$((3 * 86400))

_av_kv() { sed -n "s/^$1=//p" "$2" | head -n1; }

# 보고서 공통 — 형식, 검사기, 자체 시험, result, 서명 나이. 출력이 비면 통과, 아니면 이유 한 줄
_av_header_problem() {
    local rep=$1 k v dup sig scan
    if [ ! -s "$rep" ]; then
        echo "보고서가 없습니다 ($(basename "$rep") — Windows 러너의 Defender 검사 단계가 돌지 않았다)"
        return
    fi
    if grep -q $'\r' "$rep"; then echo "줄 끝이 LF가 아닙니다"; return; fi
    if [ -n "$(tail -c1 "$rep")" ]; then echo "마지막 줄이 끝나지 않았습니다 (잘린 보고서?)"; return; fi
    if grep -qvE '^[a-z][a-z0-9_]*=' "$rep"; then echo "key=value가 아닌 줄이 있습니다"; return; fi
    dup=$(grep -vE '^(asset|audio_file)=' "$rep" | cut -d= -f1 | LC_ALL=C sort | uniq -d | head -n1)
    if [ -n "$dup" ]; then echo "키 $dup 이(가) 두 번 나옵니다"; return; fi
    if [ "$(_av_kv format "$rep")" != 1 ]; then echo "format=1이 아닙니다"; return; fi
    v=$(_av_kv scanner "$rep")
    if [ "$v" != "Microsoft Defender" ]; then echo "scanner=${v:-없음} (Microsoft Defender여야 한다)"; return; fi
    if [ "$(_av_kv running_mode "$rep")" = sample ]; then
        echo "검증 게이트용 견본 보고서(-SampleReport)입니다 — 실제 검사가 아닙니다"
        return
    fi
    for k in engine_version antivirus_signature_version signature_updated scan_time; do
        if [ -z "$(_av_kv "$k" "$rep")" ]; then echo "$k 가 비었습니다"; return; fi
    done
    v=$(_av_kv selftest "$rep")
    if [ "$v" != detected ]; then
        echo "탐지 자체 시험(EICAR)을 통과하지 않았습니다 (selftest=${v:-없음}) — 검사기가 동작했는지 알 수 없다"
        return
    fi
    v=$(_av_kv result "$rep")
    if [ "$v" != clean ]; then echo "검사 결과가 clean이 아닙니다 (result=${v:-없음})"; return; fi
    # 서명이 검사 시각 기준 3일 이내 (스크립트도 막지만 보고서만으로도 확인한다)
    sig=$(_av_kv signature_updated "$rep")
    scan=$(_av_kv scan_time "$rep")
    for v in "$sig" "$scan"; do
        if ! [[ "$v" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]]; then
            echo "시각 형식이 ISO-8601 UTC가 아닙니다: $v"
            return
        fi
    done
    sig=$(date -u -d "$sig" +%s) && scan=$(date -u -d "$scan" +%s) || { echo "시각을 읽지 못했습니다"; return; }
    if [ $((scan - sig)) -gt "$AV_MAX_SIG_AGE_SEC" ] || [ $((sig - scan)) -gt 86400 ]; then
        echo "서명($(_av_kv signature_updated "$rep"))이 검사 시각($(_av_kv scan_time "$rep"))보다 3일 넘게 오래됐습니다"
        return
    fi
}

# 매니페스트의 음성 팩 zip → "이름,sha256,크기" 줄 (audio_assets·audio_sha256·audio_size 개수가 다르면 1)
_av_manifest_audio() {
    local man=$1 i
    local -a n s z
    IFS=, read -r -a n <<< "$(_av_kv audio_assets "$man")"
    IFS=, read -r -a s <<< "$(_av_kv audio_sha256 "$man")"
    IFS=, read -r -a z <<< "$(_av_kv audio_size "$man")"
    [ "${#n[@]}" -gt 0 ] && [ "${#n[@]}" = "${#s[@]}" ] && [ "${#n[@]}" = "${#z[@]}" ] || return 1
    for i in "${!n[@]}"; do
        printf '%s,%s,%s\n' "${n[$i]}" "${s[$i]}" "${z[$i]}"
    done
}

# 보고서의 audio_file 줄 집합 = 기대 줄 집합 (이름·sha256·크기). 출력이 비면 통과
#   _av_audio_problem <보고서> <기대 줄 "이름,sha256,크기" (줄마다)> <기대 출처 이름>
_av_audio_problem() {
    local rep=$1 want=$2 src=$3 got gn wn w g name
    want=$(LC_ALL=C sort <<< "$want")
    got=$(sed -n 's/^audio_file=//p' "$rep" | LC_ALL=C sort)
    if [ -z "$want" ]; then echo "$src 에 음성 팩 zip이 없습니다"; return; fi
    while IFS= read -r w; do
        if ! [[ "$w" =~ ^[^,]+,[0-9a-f]{64},[0-9]+$ ]]; then echo "$src 의 음성 팩 줄 형식이 잘못됐습니다: $w"; return; fi
    done <<< "$want"
    if [ -z "$got" ]; then
        echo "음성 팩을 검사하지 않았습니다 (audio_file 줄 없음) — $src 이(가) 가리키는 팩 zip도 검사해야 한다"
        return
    fi
    gn=$(cut -d, -f1 <<< "$got")
    wn=$(cut -d, -f1 <<< "$want")
    if [ "$gn" != "$wn" ]; then
        echo "검사한 음성 팩 zip($(tr '\n' ' ' <<< "$gn" | sed 's/ $//')) ≠ $src 의 팩 zip($(tr '\n' ' ' <<< "$wn" | sed 's/ $//'))"
        return
    fi
    while IFS= read -r w; do
        name=${w%%,*}
        g=$(awk -v n="$name," 'index($0, n) == 1' <<< "$got" | head -n1)
        if [ "$g" != "$w" ]; then
            echo "음성 팩 $name 이(가) 검사한 바이트와 다릅니다 (보고서 ${g#"$name,"}, $src ${w#"$name,"})"
            return
        fi
    done <<< "$want"
}

# 플러그인 릴리스 — 출력이 비면 통과, 아니면 이유 한 줄.
#   av_report_problem <보고서> <assets 폴더>
# assets의 파일(zip + 매니페스트)이 asset 줄과 같은 바이트이고, 매니페스트가 가리키는 음성 팩 zip
# (audio_assets·audio_sha256·audio_size)이 audio_file 줄과 같아야 한다 — 팩은 audio-pack이 공개 전에
# 검사했어도 매 릴리스가 지금 서명으로 다시 본다 (다음 릴리스는 팩을 새로 렌더하지 않는다).
# env AV_AUDIO_OPTIONAL=1 (dry run만): audio_file 줄이 하나도 없으면 팩 대조를 건너뛴다 — 팩 릴리스를
# 확인하지 못한 dry run도 빌드 경로를 시험할 수 있게. 게시(publish-release.sh)는 이것을 켜지 않는다
av_report_problem() {
    local rep=$1 dir=$2 why want got name line sha size rest v man
    why=$(_av_header_problem "$rep")
    if [ -n "$why" ]; then echo "$why"; return; fi
    # 검사한 자산 = 게시할 파일 (집합이 같고 바이트가 같다)
    want=$(find "$dir" -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort)
    got=$(sed -n 's/^asset=\([^,]*\),.*/\1/p' "$rep" | LC_ALL=C sort)
    if [ -z "$want" ]; then echo "게시할 파일이 없습니다 ($dir)"; return; fi
    if [ "$want" != "$got" ]; then
        echo "검사한 파일($(tr '\n' ' ' <<< "$got" | sed 's/ $//')) ≠ 게시할 파일($(tr '\n' ' ' <<< "$want" | sed 's/ $//'))"
        return
    fi
    while IFS= read -r name; do
        line=$(awk -v n="asset=$name," 'index($0, n) == 1' "$rep")
        rest=${line#"asset=$name,"}
        sha=${rest%%,*}
        size=${rest#*,}
        if ! [[ "$sha" =~ ^[0-9a-f]{64}$ ]] || ! [[ "$size" =~ ^[0-9]+$ ]]; then
            echo "asset 줄 형식이 잘못됐습니다: $line"
            return
        fi
        v=$(sha256sum "$dir/$name" | cut -d' ' -f1)
        if [ "$sha" != "$v" ]; then
            echo "$name 이(가) 검사한 바이트와 다릅니다 (보고서 sha256 $sha, 게시할 파일 $v)"
            return
        fi
        if [ "$size" != "$(stat -c %s "$dir/$name")" ]; then
            echo "$name 크기가 검사한 파일과 다릅니다"
            return
        fi
    done <<< "$want"
    # 매니페스트가 가리키는 음성 팩 = 검사한 팩 zip
    man="$dir/teamradio56-release.txt"
    if [ ! -f "$man" ]; then echo "매니페스트(teamradio56-release.txt)가 없습니다"; return; fi
    if [ "${AV_AUDIO_OPTIONAL:-}" = 1 ] && ! grep -q '^audio_file=' "$rep"; then return; fi
    if ! want=$(_av_manifest_audio "$man"); then
        echo "매니페스트의 audio_assets·audio_sha256·audio_size 개수가 맞지 않습니다"
        return
    fi
    _av_audio_problem "$rep" "$want" "매니페스트"
}

# 음성 팩 게시(publish-audio.sh) — audio-pack scan 잡의 보고서가 올릴 팩 zip(기술자 asset 줄)을 깨끗하다고
# 했는지. 출력이 비면 통과, 아니면 이유 한 줄. 기술자의 sha256은 pack_audio.py verify가 실제 zip과 대조한다
#   av_pack_report_problem <보고서> <기술자 audio_pack.txt>
av_pack_report_problem() {
    local rep=$1 desc=$2 why
    why=$(_av_header_problem "$rep")
    if [ -n "$why" ]; then echo "$why"; return; fi
    if [ ! -f "$desc" ]; then echo "기술자가 없습니다: $desc"; return; fi
    _av_audio_problem "$rep" "$(sed -n 's/^asset=//p' "$desc" | tr -d '\r')" "기술자"
}

# 릴리스 본문 끝에 붙이는 '파일 검증' 절 — 게시 파일의 SHA-256, Defender 검사 결과, 사용자가 확인하는 법.
# 보고서는 av_report_problem을 통과한 것이어야 한다. env REPO.
#   release_verify_section <보고서> <assets 폴더> <버전>
release_verify_section() {
    local rep=$1 dir=$2 v=$3 f when n man
    when=$(_av_kv signature_updated "$rep" | sed 's/^\([0-9-]*\)T\([0-9]*:[0-9]*\).*/\1 \2 UTC/')
    man="$dir/teamradio56-release.txt"
    n=$(grep -c '^audio_file=' "$rep" || true)
    printf '%s\n' "### 파일 검증" "" "| 파일 | SHA-256 |" "| --- | --- |"
    while IFS= read -r f; do
        printf '| `%s` | `%s` |\n' "$f" "$(sha256sum "$dir/$f" | cut -d' ' -f1)"
    done < <(find "$dir" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | LC_ALL=C sort)
    printf '%s\n' "" \
        "- **백신 검사**: Microsoft Defender (엔진 $(_av_kv engine_version "$rep"), 서명 $(_av_kv antivirus_signature_version "$rep"), 서명 갱신 $when)로 게시 직전에 위 두 파일과 zip을 푼 DLL까지 검사했고 위협이 없었습니다. 검사 전에 EICAR 시험 파일로 검사기가 실제로 잡는지 먼저 확인했습니다."
    if [ "$n" -gt 0 ]; then
        printf '%s\n' "- **음성 팩**: 플러그인이 설정 화면에서 받는 음성 팩 $(_av_kv audio_pack "$man") (\`$(_av_kv audio_tag "$man")\` 릴리스의 zip ${n}개 — 매니페스트에 SHA-256이 고정돼 다른 파일은 설치되지 않습니다)도 같은 검사에서 zip과 그 안의 파일까지 위협이 없었습니다."
    fi
    printf '%s\n' "- **빌드 출처 증명**: 이 저장소의 GitHub Actions 릴리스 워크플로가 위 두 파일을 만들었다는 서명된 증명(attestation)을 남겼습니다." \
        "" \
        "받은 zip이 게시된 파일과 같은지 확인하려면 (선택) zip이 있는 폴더의 PowerShell에서:" \
        "" \
        '```powershell' \
        "Get-FileHash .\\teamradio56-plugin-$v.zip -Algorithm SHA256" \
        '```' \
        "" \
        "나온 Hash가 위 표의 값과 같으면 됩니다 (대소문자는 상관없습니다). 로그인한 [GitHub CLI](https://cli.github.com/)가 있으면 이 저장소의 워크플로가 만든 파일인지도 확인할 수 있습니다:" \
        "" \
        '```' \
        "gh attestation verify teamradio56-plugin-$v.zip -R $REPO" \
        '```'
}
