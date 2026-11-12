param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$Key = "",
  [switch]$IndiceDaRelease,
  [string]$Repo = "mrtjr/eteris"
)
# Publica a release vX.Y.Z que o «vpk upload github» deixou em RASCUNHO (CAD-02, ARQ-01; docs/motor.md, «Como publicar»;
# docs/atualizacoes.md). Só publica depois de conferir, por esta ordem:
#   1. a etiqueta existe e aponta para o HEAD, e a publish\ desta máquina (se a houver) é desse commit;
#   2. todos os fluxos do GitHub Actions desse commit acabaram, nenhum falhou e o «build» passou;
#   3. a release existe e ainda é um rascunho;
#   4. com -Key: assina o Releases\releases.win.json que o pack.ps1 deixou NESTA máquina e sobe o .sig (nunca o índice
#      que está no GitHub, que um token roubado podia ter trocado); com -Key -IndiceDaRelease (emergência pelo CI, cujo
#      pacote nasceu no CI): assina o índice que está no rascunho;
#   5. descarrega do rascunho o releases.win.json e o .sig (os que os clientes vão ler) e verifica-os com as chaves de
#      src\Eteris.Browser\Updates\update-keys.txt: se o índice do rascunho não for o que se assinou, falha aqui.
# Só então «gh release edit --draft=false». A frase-passe da chave pede-a a ferramenta build/sign, na consola.
# As mensagens de recusa têm sempre um troço só em ASCII (o que os testes procuram: a consola do CI não é a do dono).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tag = "v$Version"
$signed = Join-Path $root "Releases\assinatura"
$localIndex = Join-Path $root "Releases\releases.win.json"
$sign = Join-Path $root "build\sign"

# O git e o gh correm com o stderr fora das exceções do PowerShell 5.1 (como o Git-Out do publish.ps1); nulo se falharem.
function Native-Out([string]$Command, [string[]]$Arguments) {
  $old = $ErrorActionPreference; $ErrorActionPreference = "Continue"
  try { $out = @(& $Command @Arguments 2>$null); $code = $LASTEXITCODE } finally { $ErrorActionPreference = $old }
  if ($code -ne 0) { return $null }
  ($out -join "`n").Trim()
}
function Short([string]$Commit) { if ($Commit.Length -gt 12) { $Commit.Substring(0, 12) } else { $Commit } }
function Clear-Signed { if (Test-Path -LiteralPath $signed) { Remove-Item -LiteralPath $signed -Recurse -Force } }
# A ferramenta build/sign (a frase-passe pede-a ela, na consola); a saída vai para o ecrã, a função devolve só o código.
function Run-Sign([string[]]$Arguments) {
  & dotnet run --project $sign -c Release -- @Arguments | Out-Host
  $LASTEXITCODE
}

Clear-Signed
try {
  # 1. Etiqueta, HEAD e publish\.
  $head = Native-Out "git" @("-C", $root, "rev-parse", "HEAD")
  $commit = Native-Out "git" @("-C", $root, "rev-list", "-n", "1", $tag)
  if (-not $head) { throw "não consigo ler o HEAD (git rev-parse HEAD): corra o release.ps1 no repositório, com o git no PATH" }
  if (-not $commit) { throw "etiqueta em falta: $tag não existe neste repositório (git tag $tag e git push origin $tag)" }
  if ($commit -ne $head) { throw "$tag aponta para $(Short $commit) e o HEAD é $(Short $head): publique a partir do commit da etiqueta" }
  $buildInfo = Join-Path $root "publish\eteris-build.json"
  if (Test-Path -LiteralPath $buildInfo) {
    $build = Get-Content -LiteralPath $buildInfo -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($build.commit -and $build.commit -ne $commit) { throw "publish\eteris-build.json é do commit $(Short $build.commit) e $tag é do $(Short $commit): o pacote não é o da etiqueta" }
  }

  # 2. Os fluxos do CI desse commit.
  $runsJson = Native-Out "gh" @("run", "list", "--repo", $Repo, "--commit", $commit, "--json", "workflowName,status,conclusion", "--limit", "50")
  if ($null -eq $runsJson) { throw "não consegui ler os fluxos do GitHub Actions para o commit $(Short $commit) (gh run list)" }
  $runs = @(($runsJson | ConvertFrom-Json) | ForEach-Object { $_ })
  $running = @($runs | Where-Object { $_.status -ne "completed" })
  if ($running.Count -gt 0) { throw "há fluxos ainda a correr para o commit $(Short $commit): $(($running | ForEach-Object { $_.workflowName }) -join ', '); espere que acabem" }
  $failed = @($runs | Where-Object { $_.conclusion -ne "success" -and $_.conclusion -ne "skipped" })
  if ($failed.Count -gt 0) { throw "o fluxo $($failed[0].workflowName) falhou para o commit $(Short $commit) ($($failed[0].conclusion)): corrija antes de publicar" }
  if (@($runs | Where-Object { $_.workflowName -eq "build" -and $_.conclusion -eq "success" }).Count -eq 0) {
    throw "sem verificação verde do fluxo build para o commit $(Short $commit): o CI tem de passar antes de publicar"
  }

  # 3. A release tem de existir e ser um rascunho.
  $releaseJson = Native-Out "gh" @("release", "view", $tag, "--repo", $Repo, "--json", "isDraft")
  if ($null -eq $releaseJson) { throw "$tag não existe no GitHub: suba-a com vpk upload github (sem --publish), que a deixa em rascunho" }
  $release = $releaseJson | ConvertFrom-Json
  if ($release.isDraft -ne $true) { throw "$tag não é um rascunho: já foi publicada (ou subiu com --publish); nada a fazer aqui" }

  # 4. Assinar o índice desta máquina (o caso normal).
  if ($Key -and -not $IndiceDaRelease) {
    if (-not (Test-Path -LiteralPath $localIndex -PathType Leaf)) { throw "falta o Releases\releases.win.json local: corra o pack.ps1 nesta máquina, ou passe -IndiceDaRelease na emergência pelo CI" }
    if ((Run-Sign @("sign", $Key, $localIndex)) -ne 0) { throw "a assinatura do Releases\releases.win.json falhou (frase-passe ou chave)" }
    & gh release upload $tag "$localIndex.sig" --repo $Repo --clobber
    if ($LASTEXITCODE -ne 0) { throw "não consegui subir o releases.win.json.sig para $tag (gh release upload)" }
  }

  # 5. O que os clientes vão ler: o índice e a assinatura que estão no rascunho. Uma saída diferente de 0 por falta do
  # .sig não é erro aqui: a falta diz-se a seguir.
  New-Item -ItemType Directory -Force -Path $signed | Out-Null
  & gh release download $tag --repo $Repo --pattern releases.win.json --pattern releases.win.json.sig --dir $signed --clobber
  $signedIndex = Join-Path $signed "releases.win.json"
  if (-not (Test-Path -LiteralPath $signedIndex -PathType Leaf)) { throw "o rascunho $tag não tem o releases.win.json (vpk upload github)" }
  if ($Key -and $IndiceDaRelease) {
    Write-Warning "a assinar o índice que está no rascunho (emergência pelo CI): a assinatura prova que foi o dono a publicar, não o que o CI compilou"
    if ((Run-Sign @("sign", $Key, $signedIndex)) -ne 0) { throw "a assinatura do releases.win.json do rascunho falhou (frase-passe ou chave)" }
    & gh release upload $tag "$signedIndex.sig" --repo $Repo --clobber
    if ($LASTEXITCODE -ne 0) { throw "não consegui subir o releases.win.json.sig para $tag (gh release upload)" }
  }
  if (-not (Test-Path -LiteralPath "$signedIndex.sig" -PathType Leaf)) { throw "falta a assinatura releases.win.json.sig em $tag (passe -Key)" }

  # 6. Verificar com as chaves que o Éteris publicado leva.
  $keys = Join-Path $root "src\Eteris.Browser\Updates\update-keys.txt"
  if ((Run-Sign @("verify", $keys, $signedIndex, "$signedIndex.sig")) -ne 0) { throw "assinatura inválida para as chaves de update-keys.txt: o releases.win.json de $tag não é o que foi assinado" }

  # 7. Publicar.
  & gh release edit $tag --repo $Repo --draft=false
  if ($LASTEXITCODE -ne 0) { throw "não consegui publicar $tag (gh release edit --draft=false)" }
  Write-Host "$tag publicada."
} finally {
  Clear-Signed
}
