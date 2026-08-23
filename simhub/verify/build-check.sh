#!/usr/bin/env bash
# C# 전체 컴파일 검증 — SimHub이 설치되지 않은 환경(리눅스/CI 포함)에서도 돈다.
#
# SimHub 어셈블리 대신 verify/*.Stub 의 최소 스텁을 만들어 그걸 참조해 빌드한다.
# 잡아주는 것: 문법 오류, 타입 오류, 오탈자, 누락된 using, 시그니처 불일치.
# 못 잡는 것: 실제 SimHub API와의 차이 (스텁이 곧 가정이므로).
#
# 사용법:  bash simhub/verify/build-check.sh
set -euo pipefail

cd "$(dirname "$0")/.."
STUBOUT="$PWD/verify/stubout"

echo "▶ SimHub 스텁 빌드"
dotnet build verify/SimHub.Plugins.Stub/SimHub.Plugins.Stub.csproj -c Release -v q --nologo

mkdir -p "$STUBOUT"
cp verify/SimHub.Plugins.Stub/bin/Release/net48/SimHub.Plugins.dll "$STUBOUT/"
cp verify/GameReaderCommon.Stub/bin/Release/net48/GameReaderCommon.dll "$STUBOUT/"

echo "▶ Core 빌드 (SimHub 무관)"
dotnet build src/TeamRadio56.Core/TeamRadio56.Core.csproj -c Release -v q --nologo

echo "▶ 플러그인 빌드 (스텁 참조)"
dotnet build src/TeamRadio56.SimHub/TeamRadio56.SimHub.csproj -c Release -v q --nologo \
    -p:SimHubPath="$STUBOUT"

echo
echo "✅ 컴파일 검증 통과 — 실제 SimHub API 일치는 사용자 PC 빌드에서 확인"
