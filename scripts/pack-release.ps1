# Pack the two loader builds into the public release zips under artifacts\ (shared on Discord
# and attached to GitHub releases; packing rules in AGENTS.md, "Release zips"):
#   DarkwoodMP-YokWare-<version>-BepInEx.zip
#   DarkwoodMP-YokWare-<version>-MelonLoader.zip
# With -Addons, also the optional manual-saves add-on (F3) for each loader:
#   YokWare-ManualSaves-<addon version>-<loader>.zip
# Each holds DarkwoodMP.Mod.dll + LiteNetLib.dll (+ LICENSE and INSTALL.txt from
# scripts\release\INSTALL-<loader>.txt, version/protocol filled in from PluginInfo.cs). The BepInEx zip is
# flat (both DLLs go to BepInEx/plugins); the MelonLoader zip mirrors the game folder (Mods/ for the
# mod, UserLibs/ for LiteNetLib), so it can be extracted straight into Darkwood/.
# Usage: scripts\pack-release.ps1 [-NoBuild] [-Addons]
param([switch]$NoBuild, [switch]$Addons)
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
    if ($Addons) {
        Write-Host "== Build manual-saves add-on =="
        dotnet build DarkwoodMP.ManualSaves\DarkwoodMP.ManualSaves.csproj -c Release -p:Loader=BepInEx -p:SkipDeploy=true --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet build DarkwoodMP.ManualSaves\DarkwoodMP.ManualSaves.csproj -c Release -p:Loader=MelonLoader -p:SkipDeploy=true --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    Write-Host "== Path B tests =="
    dotnet test DarkwoodMP.PathB.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$art = Join-Path $root "artifacts"
# Keep earlier versions' packs: only this run's stage dirs are cleared (zips use -Force).
New-Item -ItemType Directory -Force -Path $art | Out-Null
Get-ChildItem $art -Directory -Filter "stage-*" -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

function New-LoaderZip([string]$loader) {
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
    } else {
        New-Item -ItemType Directory -Force -Path (Join-Path $stage "Mods"), (Join-Path $stage "UserLibs") | Out-Null
        Copy-Item (Join-Path $src "DarkwoodMP.Mod.dll") (Join-Path $stage "Mods")
        Copy-Item (Join-Path $src "LiteNetLib.dll") (Join-Path $stage "UserLibs")
    }
    if (Test-Path (Join-Path $root "LICENSE")) { Copy-Item (Join-Path $root "LICENSE") $stage }
    # Shared text with pack-release.sh; CRLF so Notepad shows it right.
    $text = (Get-Content (Join-Path $root "scripts\release\INSTALL-$loader.txt") -Raw).Replace("{VERSION}", $version).Replace("{PROTOCOL}", $protocol)
    [IO.File]::WriteAllText((Join-Path $stage "INSTALL.txt"), ($text -replace "\r?\n", "`r`n"))
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

New-LoaderZip "BepInEx"
New-LoaderZip "MelonLoader"
if ($Addons) {
    New-AddonZip "BepInEx"
    New-AddonZip "MelonLoader"
}

Write-Host "Packed:"
Get-ChildItem $art -File | Select-Object Name, Length
