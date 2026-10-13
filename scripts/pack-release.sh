#!/usr/bin/env bash
# Pack the two loader builds into the public release zips under artifacts/ (the ones shared on
# Discord and attached to GitHub releases; packing rules in AGENTS.md, "Release zips"):
#   DarkwoodMP-YokWare-<version>-BepInEx.zip
#   DarkwoodMP-YokWare-<version>-MelonLoader.zip
# With --addons, also the optional manual-saves add-on (F3) for each loader:
#   YokWare-ManualSaves-<addon version>-<loader>.zip
# Each holds DarkwoodMP.Mod.dll + LiteNetLib.dll (+ LICENSE and INSTALL.txt from
# scripts/release/INSTALL-<loader>.txt, version/protocol filled in from PluginInfo.cs). The BepInEx zip is
# flat (both DLLs go to BepInEx/plugins); the MelonLoader zip mirrors the game folder (Mods/ for the
# mod, UserLibs/ for LiteNetLib), so it can be extracted straight into Darkwood/.
#
# Usage: scripts/pack-release.sh [--no-build] [--addons]
#   --no-build  pack whatever is already in DarkwoodMP.Mod/bin/Release/{BepInEx,MelonLoader}
#   --addons    also pack the manual-saves add-on zips
#
# Needs a GamePath.local.props (Steam GameDir) for the BepInEx build, like any other build.
# Override the SDK location with DOTNET_ROOT (defaults to the Unity-bundled SDK if present).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

NO_BUILD=0
ADDONS=0
for arg in "$@"; do
  case "$arg" in
    --no-build) NO_BUILD=1 ;;
    --addons) ADDONS=1 ;;
    *) echo "error: unknown option $arg" >&2; exit 1 ;;
  esac
done

# Linux hosts often have a runtime-only system dotnet; same hint as build-loaders.sh.
UNITY_DOTNET_SDK="${UNITY_DOTNET_SDK:-/home/warexpor/Unity/Hub/Editor/6000.6.0f1/Editor/Data/DotNetSdk}"
if [[ -z "${DOTNET_ROOT:-}" && -d "$UNITY_DOTNET_SDK" ]]; then
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
  if [[ "$ADDONS" -eq 1 ]]; then
    echo "== Build manual-saves add-on =="
    dotnet build DarkwoodMP.ManualSaves -c Release -p:Loader=BepInEx -p:SkipDeploy=true --nologo
    dotnet build DarkwoodMP.ManualSaves -c Release -p:Loader=MelonLoader -p:SkipDeploy=true --nologo
  fi
  echo "== Path B tests =="
  dotnet test DarkwoodMP.PathB.Tests -c Release --nologo
fi

ART="$ROOT/artifacts"
# Keep earlier versions' packs: only this run's stage dirs and zips are replaced.
mkdir -p "$ART"
rm -rf "$ART"/stage-*

make_zip() {
  local loader="$1"
  local src="$ROOT/DarkwoodMP.Mod/bin/Release/$loader"
  local stage="$ART/stage-$loader"
  local zip="$ART/DarkwoodMP-YokWare-$VERSION-$loader.zip"
  rm -f "$zip"

  for f in DarkwoodMP.Mod.dll LiteNetLib.dll; do
    [[ -f "$src/$f" ]] || { echo "error: missing $src/$f (build $loader first)" >&2; exit 1; }
  done

  mkdir -p "$stage"
  if [[ "$loader" == "BepInEx" ]]; then
    cp "$src/DarkwoodMP.Mod.dll" "$src/LiteNetLib.dll" "$stage/"
  else
    mkdir -p "$stage/Mods" "$stage/UserLibs"
    cp "$src/DarkwoodMP.Mod.dll" "$stage/Mods/"
    cp "$src/LiteNetLib.dll" "$stage/UserLibs/"
  fi
  [[ -f "$ROOT/LICENSE" ]] && cp "$ROOT/LICENSE" "$stage/"

  # The text lives in scripts/release/INSTALL-<loader>.txt (shared with pack-release.ps1);
  # the version and protocol are filled in from PluginInfo.cs, so they cannot drift.
  sed -e "s/{VERSION}/$VERSION/g" -e "s/{PROTOCOL}/$PROTOCOL/g" \
    "$ROOT/scripts/release/INSTALL-$loader.txt" > "$stage/INSTALL.txt"
  # Windows users open the .txt in Notepad: CRLF line ends.
  sed -i 's/$/\r/' "$stage/INSTALL.txt"

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
  rm -f "$zip"
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
if [[ "$ADDONS" -eq 1 ]]; then
  make_addon_zip BepInEx
  make_addon_zip MelonLoader
fi

echo "Packed:"
ls -l "$ART"/DarkwoodMP-YokWare-"$VERSION"-*.zip
[[ "$ADDONS" -eq 1 ]] && ls -l "$ART"/YokWare-ManualSaves-*.zip
for z in "$ART"/DarkwoodMP-YokWare-"$VERSION"-*.zip; do
  echo "--- $(basename "$z")"
  unzip -l "$z" 2>/dev/null || python3 -m zipfile -l "$z"
done
# The DLLs in each zip must be the built ones, byte for byte.
for loader in BepInEx MelonLoader; do
  python3 - "$ART/DarkwoodMP-YokWare-$VERSION-$loader.zip" "$ROOT/DarkwoodMP.Mod/bin/Release/$loader" <<'PY'
import hashlib, os, sys, zipfile
zpath, built = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(zpath) as z:
    for name in z.namelist():
        base = os.path.basename(name)
        if not base.endswith(".dll"):
            continue
        a = hashlib.md5(z.read(name)).hexdigest()
        b = hashlib.md5(open(os.path.join(built, base), "rb").read()).hexdigest()
        print(("ok  " if a == b else "BAD ") + a + "  " + os.path.basename(zpath) + ":" + name)
        if a != b:
            sys.exit(1)
PY
done
