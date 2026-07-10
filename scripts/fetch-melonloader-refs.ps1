# Fetches MelonLoader v0.7.0 net35 reference DLLs into libs/MelonLoader (build-only).
# GPLv3 project; MelonLoader is third-party — not redistributed in git.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root "libs\MelonLoader"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$zip = Join-Path $env:TEMP "MelonLoader.x64.v0.7.0.zip"
$uri = "https://github.com/LavaGang/MelonLoader/releases/download/v0.7.0/MelonLoader.x64.zip"
Write-Host "Downloading $uri ..."
Invoke-WebRequest -Uri $uri -OutFile $zip -UseBasicParsing
$extract = Join-Path $env:TEMP "ML_v0.7.0_extract"
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
Expand-Archive $zip -DestinationPath $extract -Force
$net35 = Join-Path $extract "MelonLoader\net35"
Copy-Item (Join-Path $net35 "MelonLoader.dll") $dest -Force
Copy-Item (Join-Path $net35 "0Harmony.dll") $dest -Force
Write-Host "OK: $dest"
Get-ChildItem $dest -Filter *.dll | Format-Table Name, Length
