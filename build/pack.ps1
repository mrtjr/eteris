param([Parameter(Mandatory = $true)][string]$Version, [switch]$NoSandbox)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
# Só uma publicação completa do build/publish.ps1 (com a marca do fim, publish\eteris-build.json), da versão pedida e no
# modo pedido: com sandbox por omissão, -NoSandbox para uma publicação de emergência sem ele (a do CI). Uma pasta que
# sobrou de outra publicação ou de uma que falhou a meio não é empacotada.
$buildInfo = Join-Path $root "publish\eteris-build.json"
if (-not (Test-Path -LiteralPath $buildInfo)) { throw "publish\ sem eteris-build.json: não é uma publicação completa do build\publish.ps1 (publique de novo)" }
$build = Get-Content -LiteralPath $buildInfo -Raw -Encoding UTF8 | ConvertFrom-Json
if ($build.version -ne $Version) { throw "publish\ é da versão $($build.version), pedida a $Version" }
if ([bool]$build.sandbox -eq [bool]$NoSandbox) {
  if ($NoSandbox) { throw "publish\ é uma publicação COM sandbox e foi pedido -NoSandbox" }
  throw "publish\ é uma publicação SEM sandbox (-NoSandbox): para a empacotar assim, passe também -NoSandbox ao pack.ps1"
}
# "Eteris" com E acentuado, escrito por código para não depender da codificação com que o ficheiro é lido
$title = "$([char]0xC9)teris"
# packId diferente da pasta de dados (%LocalAppData%\Eteris): o Velopack apaga a pasta de instalação ao desinstalar
vpk pack --packId EterisBrowser --packVersion $Version --packDir "$root\publish" --mainExe Eteris.Browser.exe --packTitle $title --icon "$root\src\Eteris.Browser\Assets\eteris.ico" --outputDir "$root\Releases"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
