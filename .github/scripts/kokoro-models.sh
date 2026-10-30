#!/usr/bin/env bash
# 코코로 모델 파일을 sha256으로 고정해 받는다 (tts.KokoroEngine._ensure_file은 무결성 확인이 없다).
#
#   kokoro-models.sh <models 폴더>      env: KOKORO_MODEL_SHA256 KOKORO_VOICES_SHA256
set -euo pipefail

DIR=${1:?models dir}
BASE=https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0
mkdir -p "$DIR"

fetch() {
    local name=$1 sha=$2 f="$DIR/$1"
    [ -n "$sha" ] || { echo "::error::$name sha256이 비었습니다"; exit 1; }
    if [ -f "$f" ] && echo "$sha  $f" | sha256sum -c --status; then
        echo "✅ $name (캐시)"
        return
    fi
    rm -f "$f" "$f.part"
    curl -fL --retry 5 --retry-all-errors -sS -o "$f.part" "$BASE/$name"
    if ! echo "$sha  $f.part" | sha256sum -c --status; then
        echo "::error::$name sha256 불일치: $(sha256sum "$f.part" | cut -d' ' -f1) (기대 $sha)"
        rm -f "$f.part"
        exit 1
    fi
    mv "$f.part" "$f"
    echo "✅ $name (내려받음, $(stat -c %s "$f") 바이트)"
}

fetch kokoro-v1.0.onnx "${KOKORO_MODEL_SHA256:-}"
fetch voices-v1.0.bin "${KOKORO_VOICES_SHA256:-}"
