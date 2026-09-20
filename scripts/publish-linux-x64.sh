#!/bin/sh
set -eu

repository=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
output="$repository/artifacts/linux-x64"

dotnet publish "$repository/src/Zitat/Zitat.fsproj" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  --output "$output" >&2

tar -C "$output" -czf "$repository/artifacts/zitat-linux-x64.tar.gz" .
printf '%s\n' "$repository/artifacts/zitat-linux-x64.tar.gz"
