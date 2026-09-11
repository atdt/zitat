#!/bin/sh
set -eu

repository=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
output="$repository/artifacts/linux-arm64"

dotnet publish "$repository/src/Zitat/Zitat.fsproj" \
  --configuration Release \
  --runtime linux-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  --output "$output"

tar -C "$output" -czf "$repository/artifacts/zitat-linux-arm64.tar.gz" .
printf '%s\n' "$repository/artifacts/zitat-linux-arm64.tar.gz"
