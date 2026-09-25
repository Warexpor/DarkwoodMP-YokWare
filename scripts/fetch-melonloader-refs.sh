#!/usr/bin/env bash
# Fetches MelonLoader v0.7.0 net35 reference DLLs into libs/MelonLoader (build-only).
# GPLv3 project; MelonLoader is third-party — not redistributed in git.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/libs/MelonLoader"
mkdir -p "$DEST"

ZIP="${TMPDIR:-/tmp}/MelonLoader.x64.v0.7.0.zip"
EXTRACT="${TMPDIR:-/tmp}/ML_v0.7.0_extract"
URI="https://github.com/LavaGang/MelonLoader/releases/download/v0.7.0/MelonLoader.x64.zip"

if [[ ! -f "$DEST/MelonLoader.dll" || ! -f "$DEST/0Harmony.dll" ]]; then
  echo "Downloading $URI ..."
  curl -fsSL -o "$ZIP" "$URI"
  rm -rf "$EXTRACT"
  mkdir -p "$EXTRACT"
  unzip -qo "$ZIP" -d "$EXTRACT"
  NET35="$EXTRACT/MelonLoader/net35"
  if [[ ! -f "$NET35/MelonLoader.dll" ]]; then
    echo "error: MelonLoader/net35 not found in zip" >&2
    exit 1
  fi
  cp -f "$NET35/MelonLoader.dll" "$NET35/0Harmony.dll" "$DEST/"
fi

echo "OK: $DEST"
ls -la "$DEST"/*.dll
