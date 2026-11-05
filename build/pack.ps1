param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
vpk pack --packId Eteris --packVersion $Version --packDir "$root\publish" --mainExe Eteris.Browser.exe --packTitle "Éteris" --outputDir "$root\Releases"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
