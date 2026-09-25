#!/usr/bin/env bash
# Build both loader variants (BepInEx + MelonLoader) for DarkwoodMP.Mod.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

if [[ -z "${DOTNET_ROOT:-}" ]]; then
  # Linux host: Unity-bundled SDK (system dotnet is often runtime-only).
  CANDIDATE="/home/warexpor/Unity/Hub/Editor/6000.6.0f1/Editor/Data/DotNetSdk"
  if [[ -d "$CANDIDATE" ]]; then
    export DOTNET_ROOT="$CANDIDATE"
    export PATH="$DOTNET_ROOT:$PATH"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: dotnet not on PATH" >&2
  exit 1
fi

"$ROOT/scripts/fetch-melonloader-refs.sh"

echo "=== BepInEx ==="
dotnet build DarkwoodMP.Mod -c Release -p:Loader=BepInEx
echo "=== MelonLoader ==="
dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader

echo
echo "Outputs:"
echo "  $ROOT/DarkwoodMP.Mod/bin/Release/BepInEx/DarkwoodMP.Mod.dll"
echo "  $ROOT/DarkwoodMP.Mod/bin/Release/MelonLoader/DarkwoodMP.Mod.dll"
