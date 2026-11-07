param([string]$Version = "0.2.8")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
dotnet publish "$root\src\Eteris.Browser" -c Release -r win-x64 --self-contained -o "$root\publish" -p:Version=$Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
