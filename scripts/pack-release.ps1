# Pack the two loader builds into versioned release zips under artifacts\:
#   DarkwoodMP-YokWare-<version>-BepInEx.zip
#   DarkwoodMP-YokWare-<version>-MelonLoader.zip
# plus the optional manual-saves add-on (F3) for each loader:
#   YokWare-ManualSaves-<addon version>-<loader>.zip
# Each holds DarkwoodMP.Mod.dll + LiteNetLib.dll (+ LICENSE and an INSTALL.txt generated from
# PluginInfo.cs, so the text cannot drift from the shipped version/protocol). The BepInEx zip is
# flat (both DLLs go to BepInEx/plugins); the MelonLoader zip mirrors the game folder (Mods/ for the
# mod, UserLibs/ for LiteNetLib), so it can be extracted straight into Darkwood/.
# Usage: scripts\pack-release.ps1 [-NoBuild]
param([switch]$NoBuild)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$info = Get-Content (Join-Path $root "DarkwoodMP.Mod\Bootstrap\PluginInfo.cs") -Raw
$version = [regex]::Match($info, 'const string Version\s*=\s*"([^"]+)"').Groups[1].Value
$protocol = [regex]::Match($info, 'const int ProtocolVersion\s*=\s*(\d+)').Groups[1].Value
if (-not $version -or -not $protocol) { throw "could not read Version/ProtocolVersion from PluginInfo.cs" }
Write-Host "== Packing YokWare Branch $version (protocol $protocol) =="

if (-not $NoBuild) {
    if (-not (Test-Path (Join-Path $root "libs\MelonLoader\MelonLoader.dll"))) {
        Write-Host "== Fetch MelonLoader refs =="
        & (Join-Path $PSScriptRoot "fetch-melonloader-refs.ps1")
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    Write-Host "== Build BepInEx =="
    dotnet build DarkwoodMP.Mod\DarkwoodMP.Mod.csproj -c Release -p:Loader=BepInEx -p:SkipDeploy=true --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "== Build MelonLoader =="
    dotnet build DarkwoodMP.Mod\DarkwoodMP.Mod.csproj -c Release -p:Loader=MelonLoader -p:SkipDeploy=true --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "== Build manual-saves add-on =="
    dotnet build DarkwoodMP.ManualSaves\DarkwoodMP.ManualSaves.csproj -c Release -p:Loader=BepInEx -p:SkipDeploy=true --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build DarkwoodMP.ManualSaves\DarkwoodMP.ManualSaves.csproj -c Release -p:Loader=MelonLoader -p:SkipDeploy=true --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "== Path B tests =="
    dotnet test DarkwoodMP.PathB.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$art = Join-Path $root "artifacts"
Remove-Item $art -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $art | Out-Null

function New-LoaderZip([string]$loader, [string]$loaderText, [string]$cfg) {
    $src = Join-Path $root "DarkwoodMP.Mod\bin\Release\$loader"
    $stage = Join-Path $art "stage-$loader"
    $zip = Join-Path $art "DarkwoodMP-YokWare-$version-$loader.zip"
    foreach ($f in "DarkwoodMP.Mod.dll", "LiteNetLib.dll") {
        if (-not (Test-Path (Join-Path $src $f))) { throw "missing $src\$f (build $loader first)" }
    }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    if ($loader -eq "BepInEx") {
        Copy-Item (Join-Path $src "DarkwoodMP.Mod.dll") $stage
        Copy-Item (Join-Path $src "LiteNetLib.dll") $stage
        $step2 = "Copy DarkwoodMP.Mod.dll and LiteNetLib.dll into Darkwood/BepInEx/plugins/"
    } else {
        New-Item -ItemType Directory -Force -Path (Join-Path $stage "Mods"), (Join-Path $stage "UserLibs") | Out-Null
        Copy-Item (Join-Path $src "DarkwoodMP.Mod.dll") (Join-Path $stage "Mods")
        Copy-Item (Join-Path $src "LiteNetLib.dll") (Join-Path $stage "UserLibs")
        $step2 = "Extract this zip into the Darkwood folder: Mods/DarkwoodMP.Mod.dll and`r`n   UserLibs/LiteNetLib.dll (MelonLoader loads UserLibs before the mod)."
    }
    if (Test-Path (Join-Path $root "LICENSE")) { Copy-Item (Join-Path $root "LICENSE") $stage }
    @"
YokWare Branch $version - Path B (Horde base), $loader build
Wire protocol ${protocol}: every player in a session must run the same version.

1. Install $loaderText for Darkwood (one loader per game install).
2. $step2
3. Launch Darkwood. MULTIPLAYER on the title screen and in the pause menu hosts, joins
   and holds the settings; its screen shows "YokWare Branch $version" at the bottom.
   F4 spectate, Ctrl+C chat. Manual save slots (F3) are a separate optional add-on.
4. Config file (created on first launch): $cfg
License: GPLv3 (see LICENSE)
"@ | Set-Content (Join-Path $stage "INSTALL.txt")
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force
    Remove-Item $stage -Recurse -Force
    Write-Host "  $zip"
}

function New-AddonZip([string]$loader) {
    $src = Join-Path $root "DarkwoodMP.ManualSaves\bin\Release\$loader\YokWare.ManualSaves.dll"
    $addonInfo = Get-Content (Join-Path $root "DarkwoodMP.ManualSaves\PluginInfo.cs") -Raw
    $addonVersion = [regex]::Match($addonInfo, 'const string Version\s*=\s*"([^"]+)"').Groups[1].Value
    $stage = Join-Path $art "stage-saves-$loader"
    $zip = Join-Path $art "YokWare-ManualSaves-$addonVersion-$loader.zip"
    if (-not (Test-Path $src)) { throw "missing $src (build the add-on first)" }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    if ($loader -eq "BepInEx") {
        Copy-Item $src $stage
        $where = "Darkwood/BepInEx/plugins/, next to DarkwoodMP.Mod.dll"
    } else {
        New-Item -ItemType Directory -Force -Path (Join-Path $stage "Mods") | Out-Null
        Copy-Item $src (Join-Path $stage "Mods")
        $where = "Darkwood/Mods/, next to DarkwoodMP.Mod.dll"
    }
    if (Test-Path (Join-Path $root "LICENSE")) { Copy-Item (Join-Path $root "LICENSE") $stage }
    @"
YokWare Manual Saves $addonVersion - optional add-on to YokWare Branch $version, $loader build

Ten extra save slots per profile. F3 in the world opens them in the pause menu.
Needs YokWare Branch installed. In co-op only the host saves; loading a slot needs
the session left first (Multiplayer > Disconnect).

Copy YokWare.ManualSaves.dll into $where.
License: GPLv3 (see LICENSE)
"@ | Set-Content (Join-Path $stage "INSTALL.txt")
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force
    Remove-Item $stage -Recurse -Force
    Write-Host "  $zip"
}

New-LoaderZip "BepInEx" "BepInEx 5.x" "BepInEx/config/com.yokware.branch.cfg"
New-LoaderZip "MelonLoader" "MelonLoader 0.7.x" "UserData/YokWare/com.yokware.branch.cfg"
New-AddonZip "BepInEx"
New-AddonZip "MelonLoader"

Write-Host "Packed:"
Get-ChildItem $art -File | Select-Object Name, Length
