# Pack Path B BepInEx drop folder (+ optional dedicated server research tree)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$scratchNote = "artifacts only — not the live load path for Ironbark server clients"

Write-Host "== Build Path B (Horde) BepInEx =="
dotnet build DarkwoodMP.Mod\DarkwoodMP.Mod.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== Path B structure tests =="
dotnet test DarkwoodMP.PathB.Tests -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== Protocol (Ironbark research) tests =="
dotnet test DarkwoodMP.Protocol.Tests -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$art = Join-Path $root "artifacts"
$bep = Join-Path $art "bepinex-plugins"
$srv = Join-Path $art "dedicated-server-ironbark-research"
Remove-Item $art -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $bep, $srv | Out-Null

$dll = Join-Path $root "DarkwoodMP.Mod\bin\Release\DarkwoodMP.Mod.dll"
$lnl = Join-Path $root "DarkwoodMP.Mod\bin\Release\LiteNetLib.dll"
if (-not (Test-Path $lnl)) { $lnl = Join-Path $root "libs\LiteNetLib.dll" }

Copy-Item $dll $bep
if (Test-Path $lnl) { Copy-Item $lnl $bep }
@"
YokWare Branch 0.9.1 — Path B (Horde base) BepInEx install
1. Install BepInEx for Darkwood.
2. Copy DarkwoodMP.Mod.dll + LiteNetLib.dll into Darkwood/BepInEx/plugins/
3. F2 = multiplayer menu. Protocol 19. Host-authoritative LAN.
4. Do not load archive/yokyy-merge-0.9 assemblies.
License: GPLv3
"@ | Set-Content (Join-Path $bep "INSTALL.txt")

dotnet build DarkwoodMP.Server\DarkwoodMP.Server.csproj -c Release --nologo
$srvOut = Join-Path $root "DarkwoodMP.Server\bin\Release\net8.0"
if (Test-Path $srvOut) {
  Copy-Item (Join-Path $srvOut "*") $srv -Recurse
  @"
Ironbark dedicated server — RESEARCH only ($scratchNote).
Not a drop-in peer for Path B Horde protocol 19 clients.
See docs/PATH_B_FEATURE_INVENTORY.md
"@ | Set-Content (Join-Path $srv "INSTALL.txt")
}

Write-Host "Packed: $art"
Get-ChildItem $art -Recurse -File | Select-Object FullName, Length
