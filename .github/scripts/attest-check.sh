#!/usr/bin/env bash
# attest 잡 — 게시될 파일(플러그인 zip + 매니페스트)의 빌드 출처 증명 앞뒤 확인.
#
#   attest-check.sh files  <dist/ci>   증명 전: assets = zip + 매니페스트, 매니페스트 sha256 = zip,
#                                      Defender 검사 보고서가 이 바이트를 깨끗하다고 함 (verify-artifact·
#                                      publish와 같은 규칙 — 검사하지 않은 바이트에 증명을 붙이지 않는다)
#   attest-check.sh verify <dist/ci>   증명 후: 릴리스 본문이 안내하는 명령 그대로
#                                      (gh attestation verify <파일> -R <저장소>) 각 파일이 확인되는지.
#                                      증명 API가 바로 안 보일 수 있어 몇 번 다시 본다
#
# env: REPO (verify: GH_TOKEN, 증명은 실행한 저장소에 남으므로 GITHUB_REPOSITORY가 있으면 그것)
set -euo pipefail

MODE=${1:?files|verify}
DIST=${2:?dist dir}
: "${REPO:?}"
export REPO
SUMMARY=${GITHUB_STEP_SUMMARY:-/dev/null}
# shellcheck source=release-lib.sh
. "$(dirname "$0")/release-lib.sh"

err() { echo "::error::$*"; exit 1; }
kv() { sed -n "s/^$1=//p" "$2" | head -n1; }

MAN="$DIST/assets/teamradio56-release.txt"
[ -f "$MAN" ] || err "매니페스트가 없습니다: $MAN"
V=$(kv version "$MAN")
ZIP="teamradio56-plugin-$V.zip"
mapfile -t files < <(find "$DIST/assets" -mindepth 1 -maxdepth 1 -printf "%f\n" | LC_ALL=C sort)
[ "${files[*]}" = "$ZIP teamradio56-release.txt" ] \
    || err "assets에는 $ZIP 과 teamradio56-release.txt만 있어야 합니다: ${files[*]}"

case "$MODE" in
    files)
        sha=$(sha256sum "$DIST/assets/$ZIP" | cut -d' ' -f1)
        [ "$sha" = "$(kv plugin_sha256 "$MAN")" ] || err "plugin_sha256 ≠ zip 실제 해시 $sha"
        why=$(av_report_problem "$DIST/meta/av-scan.txt" "$DIST/assets")
        [ -z "$why" ] || err "백신 검사 보고서(av-scan.txt): $why — 증명하지 않습니다"
        echo "증명할 파일 (Defender 보고서와 바이트 일치):"
        for f in "${files[@]}"; do
            echo "  $f  $(sha256sum "$DIST/assets/$f" | cut -d' ' -f1)"
        done
        ;;
    verify)
        R=${GITHUB_REPOSITORY:-$REPO}
        for f in "${files[@]}"; do
            ok=false
            for i in 1 2 3 4 5 6; do
                if gh attestation verify "$DIST/assets/$f" -R "$R"; then
                    ok=true
                    break
                fi
                echo "  ($i/6) $f 증명이 아직 확인되지 않음 — 10초 뒤 다시"
                sleep 10
            done
            [ "$ok" = true ] || err "$f 의 빌드 출처 증명을 확인하지 못했습니다 (gh attestation verify -R $R)"
        done
        {
            echo "### 빌드 출처 증명 — $(kv tag "$MAN")"
            echo
            for f in "${files[@]}"; do
                echo "- \`$f\` sha256 \`$(sha256sum "$DIST/assets/$f" | cut -d' ' -f1)\` — \`gh attestation verify $f -R $R\` 통과"
            done
        } >> "$SUMMARY"
        echo "✅ 빌드 출처 증명 확인: ${files[*]}"
        ;;
    *) err "모르는 모드: $MODE (files|verify)" ;;
esac
