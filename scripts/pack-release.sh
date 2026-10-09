#!/usr/bin/env bash
# Pack the two loader builds into versioned release zips under artifacts/:
#   DarkwoodMP-YokWare-<version>-BepInEx.zip
#   DarkwoodMP-YokWare-<version>-MelonLoader.zip
# plus the optional manual-saves add-on (F3) for each loader:
#   YokWare-ManualSaves-<addon version>-<loader>.zip
# Each holds DarkwoodMP.Mod.dll + LiteNetLib.dll (+ LICENSE and an INSTALL.txt generated from
# PluginInfo.cs, so the text can never drift from the shipped version/protocol). The BepInEx zip is
# flat (both DLLs go to BepInEx/plugins); the MelonLoader zip mirrors the game folder (Mods/ for the
# mod, UserLibs/ for LiteNetLib), so it can be extracted straight into Darkwood/.
#
# Usage: scripts/pack-release.sh [--no-build]
#   --no-build  pack whatever is already in DarkwoodMP.Mod/bin/Release/{BepInEx,MelonLoader}
#
# Needs a GamePath.local.props (Steam GameDir) for the BepInEx build, like any other build.
# Override the SDK location with DOTNET_ROOT (defaults to the Unity-bundled SDK if present).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

NO_BUILD=0
[[ "${1:-}" == "--no-build" ]] && NO_BUILD=1

# Linux hosts often have a runtime-only system dotnet; same hint as build-loaders.sh.
if [[ -z "${DOTNET_ROOT:-}" && -n "${UNITY_DOTNET_SDK:-}" && -d "$UNITY_DOTNET_SDK" ]]; then
  export DOTNET_ROOT="$UNITY_DOTNET_SDK"
  export PATH="$DOTNET_ROOT:$PATH"
fi

INFO="$ROOT/DarkwoodMP.Mod/Bootstrap/PluginInfo.cs"
VERSION="$(grep -E 'const string Version *=' "$INFO" | sed -E 's/.*"([^"]+)".*/\1/')"
PROTOCOL="$(grep -E 'const int ProtocolVersion *=' "$INFO" | sed -E 's/.*= *([0-9]+).*/\1/')"
if [[ -z "$VERSION" || -z "$PROTOCOL" ]]; then
  echo "error: could not read Version/ProtocolVersion from $INFO" >&2
  exit 1
fi
echo "== Packing YokWare Branch $VERSION (protocol $PROTOCOL) =="

if [[ "$NO_BUILD" -eq 0 ]]; then
  command -v dotnet >/dev/null 2>&1 || { echo "error: dotnet not on PATH (set DOTNET_ROOT)" >&2; exit 1; }
  [[ -f "$ROOT/libs/MelonLoader/MelonLoader.dll" ]] || "$ROOT/scripts/fetch-melonloader-refs.sh"
  echo "== Build BepInEx =="
  dotnet build DarkwoodMP.Mod -c Release -p:Loader=BepInEx -p:SkipDeploy=true --nologo
  echo "== Build MelonLoader =="
  dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader -p:SkipDeploy=true --nologo
  echo "== Build manual-saves add-on =="
  dotnet build DarkwoodMP.ManualSaves -c Release -p:Loader=BepInEx -p:SkipDeploy=true --nologo
  dotnet build DarkwoodMP.ManualSaves -c Release -p:Loader=MelonLoader -p:SkipDeploy=true --nologo
  echo "== Path B tests =="
  dotnet test DarkwoodMP.PathB.Tests -c Release --nologo
fi

ART="$ROOT/artifacts"
rm -rf "$ART"
mkdir -p "$ART"

make_zip() {
  local loader="$1"
  local src="$ROOT/DarkwoodMP.Mod/bin/Release/$loader"
  local stage="$ART/stage-$loader"
  local zip="$ART/DarkwoodMP-YokWare-$VERSION-$loader.zip"

  for f in DarkwoodMP.Mod.dll LiteNetLib.dll; do
    [[ -f "$src/$f" ]] || { echo "error: missing $src/$f (build $loader first)" >&2; exit 1; }
  done

  mkdir -p "$stage"
  local step2
  if [[ "$loader" == "BepInEx" ]]; then
    cp "$src/DarkwoodMP.Mod.dll" "$src/LiteNetLib.dll" "$stage/"
    step2="Copy DarkwoodMP.Mod.dll and LiteNetLib.dll into Darkwood/BepInEx/plugins/"
  else
    mkdir -p "$stage/Mods" "$stage/UserLibs"
    cp "$src/DarkwoodMP.Mod.dll" "$stage/Mods/"
    cp "$src/LiteNetLib.dll" "$stage/UserLibs/"
    step2="Extract this zip into the Darkwood folder: Mods/DarkwoodMP.Mod.dll and
   UserLibs/LiteNetLib.dll (MelonLoader loads UserLibs before the mod)."
  fi
  [[ -f "$ROOT/LICENSE" ]] && cp "$ROOT/LICENSE" "$stage/"

  local cfg
  if [[ "$loader" == "BepInEx" ]]; then
    cfg="BepInEx/config/com.yokware.branch.cfg"
  else
    cfg="UserData/YokWare/com.yokware.branch.cfg"
  fi
  cat > "$stage/INSTALL.txt" <<TXT
YokWare Branch $VERSION - Path B (Horde base), $loader build
Wire protocol $PROTOCOL: every player in a session must run the same version.

1. Install $( [[ "$loader" == "BepInEx" ]] && echo "BepInEx 5.x" || echo "MelonLoader 0.7.x" ) for Darkwood (one loader per game install).
2. $step2
3. Launch Darkwood. MULTIPLAYER on the title screen and in the pause menu hosts, joins
   and holds the settings; its screen shows "YokWare Branch $VERSION" at the bottom.
   F4 spectate, Ctrl+C chat. Manual save slots (F3) are a separate optional add-on.
4. Config file (created on first launch): $cfg
License: GPLv3 (see LICENSE)
TXT

  if command -v zip >/dev/null 2>&1; then
    (cd "$stage" && zip -q -r "$zip" .)
  else
    (cd "$stage" && python3 -m zipfile -c "$zip" ./*)
  fi
  rm -rf "$stage"
  echo "  $zip"
}

make_addon_zip() {
  local loader="$1"
  local src="$ROOT/DarkwoodMP.ManualSaves/bin/Release/$loader/YokWare.ManualSaves.dll"
  local addon_version
  addon_version="$(grep -E 'const string Version *=' "$ROOT/DarkwoodMP.ManualSaves/PluginInfo.cs" | sed -E 's/.*"([^"]+)".*/\1/')"
  local stage="$ART/stage-saves-$loader"
  local zip="$ART/YokWare-ManualSaves-$addon_version-$loader.zip"
  [[ -f "$src" ]] || { echo "error: missing $src (build the add-on first)" >&2; exit 1; }
  mkdir -p "$stage"
  local where
  if [[ "$loader" == "BepInEx" ]]; then
    cp "$src" "$stage/"
    where="Darkwood/BepInEx/plugins/, next to DarkwoodMP.Mod.dll"
  else
    mkdir -p "$stage/Mods"
    cp "$src" "$stage/Mods/"
    where="Darkwood/Mods/, next to DarkwoodMP.Mod.dll"
  fi
  [[ -f "$ROOT/LICENSE" ]] && cp "$ROOT/LICENSE" "$stage/"
  cat > "$stage/INSTALL.txt" <<TXT
YokWare Manual Saves $addon_version - optional add-on to YokWare Branch $VERSION, $loader build

Ten extra save slots per profile. F3 in the world opens them in the pause menu.
Needs YokWare Branch installed. In co-op only the host saves; loading a slot needs
the session left first (Multiplayer > Disconnect).

Copy YokWare.ManualSaves.dll into $where.
License: GPLv3 (see LICENSE)
TXT
  if command -v zip >/dev/null 2>&1; then
    (cd "$stage" && zip -q -r "$zip" .)
  else
    (cd "$stage" && python3 -m zipfile -c "$zip" ./*)
  fi
  rm -rf "$stage"
  echo "  $zip"
}

make_zip BepInEx
make_zip MelonLoader
make_addon_zip BepInEx
make_addon_zip MelonLoader

echo "Packed:"
ls -l "$ART"/*.zip
