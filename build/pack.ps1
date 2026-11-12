param([Parameter(Mandatory = $true)][string]$Version, [switch]$NoSandbox, [switch]$SemLogin, [string]$SignParams = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
# Só uma publicação completa do build/publish.ps1 (com a marca do fim, publish\eteris-build.json), da versão pedida, com
# o login Google, no modo pedido (com sandbox por omissão, -NoSandbox para uma publicação de emergência sem ele, a do
# CI) e feita a partir do commit que se empacota (o HEAD). Uma pasta que sobrou de outra publicação, de um commit
# anterior ou de uma que falhou a meio não é empacotada.
$buildInfo = Join-Path $root "publish\eteris-build.json"
if (-not (Test-Path -LiteralPath $buildInfo)) { throw "publish\ sem eteris-build.json: não é uma publicação completa do build\publish.ps1 (publique de novo)" }
$build = Get-Content -LiteralPath $buildInfo -Raw -Encoding UTF8 | ConvertFrom-Json
if ($build.version -ne $Version) { throw "publish\ é da versão $($build.version), pedida a $Version" }
# Atualizações assinadas (INF-02, CAD-01; docs/atualizacoes.md): o Éteris só aceita um releases.win.json assinado com uma
# das chaves públicas de update-keys.txt; sem nenhuma, o Éteris empacotado recusaria todas as atualizações para sempre.
$updateKeys = Join-Path $root "src\Eteris.Browser\Updates\update-keys.txt"
$keyLines = @()
if (Test-Path -LiteralPath $updateKeys -PathType Leaf) {
  $keyLines = @(Get-Content -LiteralPath $updateKeys -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith("#") })
}
if ($keyLines.Count -eq 0) { throw "sem chave pública das atualizações em src\Eteris.Browser\Updates\update-keys.txt: o Éteris publicado recusaria todas as atualizações (docs/atualizacoes.md)" }
# Login Google (docs/sincronizacao.md): uma release oficial leva o identificador da aplicação Google, que o publish.ps1
# -GoogleOAuthJson põe em publish\ (a marca diz google_oauth e o ficheiro está lá); sem ele o «Entrar com o Google» fica
# «indisponível nesta compilação» para toda a gente. -SemLogin empacota assim mesmo (a publicação de emergência pelo CI,
# que não tem o ficheiro). Aqui só se vê se o ficheiro existe: o conteúdo foi verificado pelo publish.ps1 e não é lido.
# A marca e o ficheiro têm de dizer o mesmo, com ou sem -SemLogin: o publish.ps1 escreve os dois juntos, por isso uma
# diferença é uma pasta mexida à mão depois de publicar. Um google-oauth.json posto lá depois (que o publish.ps1 não
# verificou) sairia num pacote anunciado como «sem login»; uma marca a dizer que o leva, sem ele, iria na instalação.
$login = [bool]$build.google_oauth
$loginFile = Test-Path -LiteralPath (Join-Path $root "publish\google-oauth.json") -PathType Leaf
if ($login -ne $loginFile) { throw "publish\ incoerente: a marca eteris-build.json diz que a publicação $(if ($login) { 'leva' } else { 'não leva' }) o login Google e o ficheiro publish\google-oauth.json $(if ($loginFile) { 'está lá' } else { 'não está lá' }) (a pasta foi mexida depois de publicar); publique de novo com build\publish.ps1" }
if ($login) {
  if ($SemLogin) { throw "publish\ é uma publicação COM login Google e foi pedido -SemLogin" }
} else {
  if (-not $SemLogin) { throw "publish\ é uma publicação SEM login Google: publique de novo com build\publish.ps1 -Version $Version -GoogleOAuthJson <caminho do google-oauth.json> (docs/motor.md, «Como publicar»), ou passe -SemLogin ao pack.ps1 para a empacotar assim mesmo" }
  Write-Warning "Pacote SEM login Google (-SemLogin): o «Entrar com o Google» e a sincronização ficam «indisponível nesta compilação» para quem o instalar."
}
if ([bool]$build.sandbox -eq [bool]$NoSandbox) {
  if ($NoSandbox) { throw "publish\ é uma publicação COM sandbox e foi pedido -NoSandbox" }
  throw "publish\ é uma publicação SEM sandbox (-NoSandbox): para a empacotar assim, passe também -NoSandbox ao pack.ps1"
}
# A publicação tem de ser a do commit que se empacota (o HEAD, o que a etiqueta da release vai marcar): uma publish\ que
# ficou de um commit anterior (a de uma medição, a de antes da última correção) não sai como release. A marca sem
# commit é a de uma publicação feita sem git (só com -NoSandbox): não tem com que comparar. O git corre com o stderr
# fora das exceções do PowerShell 5.1, como no publish.ps1; sem ele (ou fora de um repositório) não se sabe o HEAD.
if ($build.commit) {
  function Short([string]$Commit) { if ($Commit.Length -gt 12) { $Commit.Substring(0, 12) } else { $Commit } }
  $head = ""
  $old = $ErrorActionPreference; $ErrorActionPreference = "Continue"
  try { $head = (@(& git -C $root rev-parse HEAD 2>$null) -join "").Trim(); if ($LASTEXITCODE -ne 0) { $head = "" } } catch { $head = "" } finally { $ErrorActionPreference = $old }
  if (-not $head) { throw "publish\ é do commit $(Short $build.commit) e não consigo ler o HEAD (git rev-parse HEAD): sem ele não sei se é a publicação do commit que vai ser a release; corra o pack.ps1 no repositório, com o git no PATH, ou publique de novo com build\publish.ps1" }
  if ($head -ne $build.commit) { throw "publish\ é do commit $(Short $build.commit) e o HEAD é $(Short $head): publique de novo com build\publish.ps1 a partir do commit que vai ser a release" }
}
# "Eteris" com E acentuado, escrito por código para não depender da codificação com que o ficheiro é lido
$title = "$([char]0xC9)teris"
# packId diferente da pasta de dados (%LocalAppData%\Eteris): o Velopack apaga a pasta de instalação ao desinstalar
$vpkArgs = @("--packId", "EterisBrowser", "--packVersion", $Version, "--packDir", "$root\publish", "--mainExe", "Eteris.Browser.exe",
  "--packTitle", $title, "--icon", "$root\src\Eteris.Browser\Assets\eteris.ico", "--outputDir", "$root\Releases")
# Authenticode (decisão D3 de docs/atualizacoes.md), só quando houver certificado: os parâmetros do signtool, que o vpk
# passa a cada binário. Com o sandbox, os três do arranque (Eteris.Browser.exe, chrome_elf.dll, Eteris.Browser.dll) têm de
# levar o mesmo certificado (docs/sandbox.md, «Assinatura»).
if ($SignParams) { $vpkArgs += @("--signParams", $SignParams) }
vpk pack @vpkArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
