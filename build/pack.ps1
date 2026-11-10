param([Parameter(Mandatory = $true)][string]$Version, [switch]$NoSandbox, [switch]$SemLogin)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
# Só uma publicação completa do build/publish.ps1 (com a marca do fim, publish\eteris-build.json), da versão pedida, com
# o login Google e no modo pedido: com sandbox por omissão, -NoSandbox para uma publicação de emergência sem ele (a do
# CI). Uma pasta que sobrou de outra publicação ou de uma que falhou a meio não é empacotada.
$buildInfo = Join-Path $root "publish\eteris-build.json"
if (-not (Test-Path -LiteralPath $buildInfo)) { throw "publish\ sem eteris-build.json: não é uma publicação completa do build\publish.ps1 (publique de novo)" }
$build = Get-Content -LiteralPath $buildInfo -Raw -Encoding UTF8 | ConvertFrom-Json
if ($build.version -ne $Version) { throw "publish\ é da versão $($build.version), pedida a $Version" }
# Login Google (docs/sincronizacao.md): uma release oficial leva o identificador da aplicação Google, que o publish.ps1
# -GoogleOAuthJson põe em publish\ (a marca diz google_oauth e o ficheiro está lá); sem ele o «Entrar com o Google» fica
# «indisponível nesta compilação» para toda a gente. -SemLogin empacota assim mesmo (a publicação de emergência pelo CI,
# que não tem o ficheiro). Aqui só se vê se o ficheiro existe: o conteúdo foi verificado pelo publish.ps1 e não é lido.
if ([bool]$build.google_oauth -and (Test-Path -LiteralPath (Join-Path $root "publish\google-oauth.json") -PathType Leaf)) {
  if ($SemLogin) { throw "publish\ é uma publicação COM login Google e foi pedido -SemLogin" }
} else {
  if (-not $SemLogin) { throw "publish\ é uma publicação SEM login Google: publique de novo com build\publish.ps1 -Version $Version -GoogleOAuthJson <caminho do google-oauth.json> (docs/motor.md, «Como publicar»), ou passe -SemLogin ao pack.ps1 para a empacotar assim mesmo" }
  Write-Warning "Pacote SEM login Google (-SemLogin): o «Entrar com o Google» e a sincronização ficam «indisponível nesta compilação» para quem o instalar."
}
if ([bool]$build.sandbox -eq [bool]$NoSandbox) {
  if ($NoSandbox) { throw "publish\ é uma publicação COM sandbox e foi pedido -NoSandbox" }
  throw "publish\ é uma publicação SEM sandbox (-NoSandbox): para a empacotar assim, passe também -NoSandbox ao pack.ps1"
}
# "Eteris" com E acentuado, escrito por código para não depender da codificação com que o ficheiro é lido
$title = "$([char]0xC9)teris"
# packId diferente da pasta de dados (%LocalAppData%\Eteris): o Velopack apaga a pasta de instalação ao desinstalar
vpk pack --packId EterisBrowser --packVersion $Version --packDir "$root\publish" --mainExe Eteris.Browser.exe --packTitle $title --icon "$root\src\Eteris.Browser\Assets\eteris.ico" --outputDir "$root\Releases"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
