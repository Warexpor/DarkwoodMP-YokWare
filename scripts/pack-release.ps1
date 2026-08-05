# Pack Path B BepInEx drop folder
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Write-Host "== Build Path B (Horde) BepInEx =="
dotnet build DarkwoodMP.Mod\DarkwoodMP.Mod.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== Path B structure tests =="
dotnet test DarkwoodMP.PathB.Tests -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$art = Join-Path $root "artifacts"
$bep = Join-Path $art "bepinex-plugins"
Remove-Item $art -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $bep | Out-Null

$dll = Join-Path $root "DarkwoodMP.Mod\bin\Release\DarkwoodMP.Mod.dll"
$lnl = Join-Path $root "DarkwoodMP.Mod\bin\Release\LiteNetLib.dll"
if (-not (Test-Path $lnl)) { $lnl = Join-Path $root "libs\LiteNetLib.dll" }

Copy-Item $dll $bep
if (Test-Path $lnl) { Copy-Item $lnl $bep }
@"
YokWare Branch 0.7.48 — Path B (Horde base) BepInEx install
1. Install BepInEx for Darkwood.
2. Copy DarkwoodMP.Mod.dll + LiteNetLib.dll into Darkwood/BepInEx/plugins/
3. F2 = multiplayer menu. Protocol 22. Host-authoritative LAN.
4. Do not load archive/yokyy-merge-0.9 or research/ assemblies.
License: GPLv3
"@ | Set-Content (Join-Path $bep "INSTALL.txt")

Write-Host "Packed: $art"
Get-ChildItem $art -Recurse -File | Select-Object FullName, Length
