param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
# "Eteris" com E acentuado, escrito por código para não depender da codificação com que o ficheiro é lido
$title = "$([char]0xC9)teris"
# packId diferente da pasta de dados (%LocalAppData%\Eteris): o Velopack apaga a pasta de instalação ao desinstalar
vpk pack --packId EterisBrowser --packVersion $Version --packDir "$root\publish" --mainExe Eteris.Browser.exe --packTitle $title --icon "$root\src\Eteris.Browser\Assets\eteris.ico" --outputDir "$root\Releases"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
