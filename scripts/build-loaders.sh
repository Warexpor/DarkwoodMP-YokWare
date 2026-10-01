#!/usr/bin/env bash
# Build both loader variants (BepInEx + MelonLoader) for DarkwoodMP.Mod.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

if [[ -z "${DOTNET_ROOT:-}" ]]; then
  # Linux hosts often have a runtime-only system dotnet. Point UNITY_DOTNET_SDK at a full SDK,
  # e.g. the one bundled with Unity Hub:
  #   export UNITY_DOTNET_SDK=~/Unity/Hub/Editor/<version>/Editor/Data/DotNetSdk
  # (or just export DOTNET_ROOT yourself).
  if [[ -n "${UNITY_DOTNET_SDK:-}" && -d "$UNITY_DOTNET_SDK" ]]; then
    export DOTNET_ROOT="$UNITY_DOTNET_SDK"
    export PATH="$DOTNET_ROOT:$PATH"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: dotnet not on PATH (set DOTNET_ROOT or UNITY_DOTNET_SDK)" >&2
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
