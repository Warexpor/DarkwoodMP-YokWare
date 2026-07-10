# Pack BepInEx + MelonLoader + dedicated server drop folders under artifacts/
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Write-Host "== Build BepInEx =="
dotnet build DarkwoodMP.Mod\DarkwoodMP.Mod.csproj -c Release -p:Loader=BepInEx --nologo
Write-Host "== Build MelonLoader =="
dotnet build DarkwoodMP.Mod\DarkwoodMP.Mod.csproj -c Release -p:Loader=MelonLoader --nologo
Write-Host "== Build Server =="
dotnet build DarkwoodMP.Server\DarkwoodMP.Server.csproj -c Release --nologo
Write-Host "== Protocol tests =="
dotnet test DarkwoodMP.Protocol.Tests -c Release --nologo

$art = Join-Path $root "artifacts"
$bep = Join-Path $art "bepinex-plugins"
$ml = Join-Path $art "melonloader-Mods"
$srv = Join-Path $art "dedicated-server"
Remove-Item $art -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $bep, $ml, $srv | Out-Null

$bepDll = Join-Path $root "DarkwoodMP.Mod\bin\Release\BepInEx\net472\DarkwoodMP.Mod.dll"
$mlDll = Join-Path $root "DarkwoodMP.Mod\bin\Release\MelonLoader\net472\DarkwoodMP.Mod.dll"
$mlLite = Join-Path $root "DarkwoodMP.Mod\bin\Release\MelonLoader\net472\LiteNetLib.dll"
$cfg = Join-Path $root "DarkwoodMP.Mod\config_template.ini"

Copy-Item $bepDll $bep
Copy-Item $cfg $bep
@"
YokWare Branch 0.9 — BepInEx install
1. Install BepInEx for Darkwood (x86/x64 matching game).
2. Copy DarkwoodMP.Mod.dll into Darkwood/BepInEx/plugins/
3. Copy config_template.ini ideas into %LocalAppData%\DarkwoodMP\config.ini (or let the mod create it).
4. Set the SAME WorldSeed on all peers; start NEW games.
5. F1 = menu, LeftCtrl+C = chat.
License: GPLv3 — see LICENSE in the repository.
"@ | Set-Content (Join-Path $bep "INSTALL.txt")

Copy-Item $mlDll $ml
if (Test-Path $mlLite) { Copy-Item $mlLite $ml }
Copy-Item $cfg $ml
@"
YokWare Branch 0.9 — MelonLoader 0.7.0 install
1. Install MelonLoader 0.7.0 for Darkwood (https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.0).
2. Copy DarkwoodMP.Mod.dll (+ LiteNetLib.dll if present) into Darkwood/Mods/
3. Same config / WorldSeed rules as BepInEx build.
4. Do not mix BepInEx and MelonLoader on the same game install.
License: GPLv3 — see LICENSE in the repository.
"@ | Set-Content (Join-Path $ml "INSTALL.txt")

$srvOut = Join-Path $root "DarkwoodMP.Server\bin\Release\net8.0"
Copy-Item (Join-Path $srvOut "*") $srv -Recurse
@"
YokWare dedicated server (reliable relay, Ironbark v2)
  dotnet DarkwoodMP.Server.dll
  or run DarkwoodMP.Server.exe
Default: AuthoritativeWorld=false — in-game time authority is lowest client id.
See DarkwoodMP.Server/README.md
License: GPLv3
"@ | Set-Content (Join-Path $srv "INSTALL.txt")

Write-Host "Packed: $art"
Get-ChildItem $art -Recurse -File | Select-Object FullName, Length
