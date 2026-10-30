#!/usr/bin/env bash
# Windows 빌드 아티팩트(게시될 바로 그 파일)로 사용자 업데이트 흐름을 재현한다:
# 매니페스트 → 플러그인 zip 교체 → audio-<pack> 릴리스에서 음성 팩 설치.
#
#   verify-release-artifact.sh <dist/ci> <음성 팩 내려받을 폴더>
#
# env PUBLISH=true면 음성 팩을 못 받는 것도 실패 (dry run은 경고 후 팩 설치 흐름만 뺀다).
set -euo pipefail

DIST=${1:?dist dir}
AUDIO=${2:?audio dir}
REPO=${REPO:?}
SUMMARY=${GITHUB_STEP_SUMMARY:-/dev/null}

err() { echo "::error::$*"; exit 1; }
kv() { sed -n "s/^$1=//p" "$2" | head -n1; }

MAN="$DIST/assets/teamradio56-release.txt"
INFO="$DIST/meta/release-info.txt"
[ -f "$MAN" ] || err "아티팩트에 매니페스트가 없습니다: $MAN"
[ -f "$INFO" ] || err "아티팩트에 release-info.txt가 없습니다: $INFO"
[ -f "$DIST/meta/release-notes.md" ] || err "아티팩트에 release-notes.md가 없습니다"

V=$(kv version "$MAN")
ZIP="teamradio56-plugin-$V.zip"
mapfile -t files < <(find "$DIST/assets" -mindepth 1 -maxdepth 1 -printf "%f\n" | LC_ALL=C sort)
[ "${files[*]}" = "$ZIP teamradio56-release.txt" ] \
    || err "assets에는 $ZIP 과 teamradio56-release.txt만 있어야 합니다: ${files[*]}"

for k in tag version plugin_sha256 plugin_asset audio_pack audio_tag; do
    [ "$(kv "$k" "$MAN")" = "$(kv "$k" "$INFO")" ] \
        || err "release-info.txt의 $k($(kv "$k" "$INFO")) ≠ 매니페스트($(kv "$k" "$MAN"))"
done
[ "$(kv tag "$MAN")" = "v$V" ] || err "매니페스트 tag ≠ v$V"
[ "$(kv plugin_asset "$MAN")" = "$ZIP" ] || err "plugin_asset ≠ $ZIP"
sha=$(sha256sum "$DIST/assets/$ZIP" | cut -d' ' -f1)
[ "$sha" = "$(kv plugin_sha256 "$MAN")" ] || err "plugin_sha256 ≠ zip 실제 해시 $sha"

AUDIO_TAG=$(kv audio_tag "$MAN")
IFS=, read -r -a names <<< "$(kv audio_assets "$MAN")"
AUDIO_ARGS=(--audio-dir "$AUDIO")
mkdir -p "$AUDIO"
for n in "${names[@]}"; do
    [ -f "$AUDIO/$n" ] && continue
    if ! curl -fsSL --retry 5 --retry-all-errors -o "$AUDIO/$n" \
        "https://github.com/$REPO/releases/download/$AUDIO_TAG/$n"; then
        rm -f "$AUDIO/$n"
        if [ "${PUBLISH:-false}" = true ]; then
            err "음성 팩 $AUDIO_TAG/$n 을(를) 받지 못했습니다"
        fi
        echo "::warning::음성 팩 $AUDIO_TAG/$n 없음 — dry run이라 팩 설치 흐름은 건너뜁니다"
        AUDIO_ARGS=()
        break
    fi
done

dotnet build simhub/tests/TeamRadio56.Replay/TeamRadio56.Replay.csproj -c Release -v q --nologo
simhub/tests/TeamRadio56.Replay/bin/Release/net8.0/teamradio56-replay \
    --release-check voice_lines/audio_pack.txt --manifest "$MAN" ${AUDIO_ARGS[@]+"${AUDIO_ARGS[@]}"}

{
    echo "### 아티팩트 검증 — $(kv tag "$MAN")"
    echo
    echo "- $ZIP sha256 \`$sha\`"
    echo "- 음성 팩 $(kv audio_pack "$MAN") ($AUDIO_TAG): $([ ${#AUDIO_ARGS[@]} -gt 0 ] && echo '설치 흐름 확인' || echo '건너뜀')"
    echo "- SimHub: $(kv simhub_wpf_version "$INFO") (SimHub.Plugins $(kv simhub_plugins_version "$INFO"))"
} >> "$SUMMARY"
