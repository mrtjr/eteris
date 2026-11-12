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
#   4. descarrega do rascunho o releases.win.json (o que os clientes vão ler). O «vpk upload github» não sobe o
#      Releases\releases.win.json desta máquina, que lista todas as versões da pasta: gera outro, só com os pacotes desta
#      versão (os Full e Delta do Releases\assets.win.json), noutra ordem;
#   5. com -Key: confere cada entrada desse índice com os pacotes desta máquina, em todos os campos em que o Éteris
#      confia (FileName, PackageId, Version, Type, SHA256 e Size), e exige exatamente os Full e Delta do assets.win.json
#      (nem a mais, nem a menos): um token roubado podia ter trocado o índice do rascunho. Só então assina os bytes dele
#      tal como vieram (nunca reescritos aqui: BOM, ConvertTo-Json e escapes do PowerShell 5.1 mudavam-nos), sobe o .sig
#      e descarrega de novo o índice e o .sig. Com -Key -IndiceDaRelease (emergência pelo CI, cujo pacote nasceu no CI,
#      sem pacotes nesta máquina): assina o índice do rascunho sem essa conferência;
#   6. verifica o índice e o .sig descarregados com as chaves de src\Eteris.Browser\Updates\update-keys.txt: se o índice
#      do rascunho não for o que se assinou, falha aqui.
# Só então «gh release edit --draft=false». A frase-passe da chave pede-a a ferramenta build/sign, na consola.
# As mensagens de recusa têm sempre um troço só em ASCII (o que os testes procuram: a consola do CI não é a do dono).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tag = "v$Version"
$signed = Join-Path $root "Releases\assinatura"
$signedIndex = Join-Path $signed "releases.win.json"
$localIndex = Join-Path $root "Releases\releases.win.json"
$localAssets = Join-Path $root "Releases\assets.win.json"
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
# O índice e o .sig que estão agora no rascunho, para Releases\assinatura (os anteriores apagam-se antes). Uma saída
# diferente de 0 por falta do .sig não é erro aqui: a falta diz-se no passo 6.
function Get-DraftIndex {
  New-Item -ItemType Directory -Force -Path $signed | Out-Null
  foreach ($file in @($signedIndex, "$signedIndex.sig")) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } }
  & gh release download $tag --repo $Repo --pattern releases.win.json --pattern releases.win.json.sig --dir $signed --clobber
  if (-not (Test-Path -LiteralPath $signedIndex -PathType Leaf)) { throw "o rascunho $tag não tem o releases.win.json (vpk upload github)" }
}
# C1: confere o índice do rascunho com os pacotes desta máquina antes de o assinar. O conjunto de entradas tem de ser
# exatamente o dos Full e Delta do Releases\assets.win.json (os nomes primeiro: nada do índice do rascunho chega a um
# caminho antes de se saber que é um pacote desta versão); depois, em cada entrada, os campos em que o Éteris confia
# (SignedUpdateSource): PackageId e Version iguais aos do Releases\releases.win.json local (Version também igual a
# -Version), Type o do assets.win.json, SHA256 e Size os do ficheiro em Releases\.
function Assert-DraftIndex {
  if (-not (Test-Path -LiteralPath $localAssets -PathType Leaf) -or -not (Test-Path -LiteralPath $localIndex -PathType Leaf)) {
    throw "falta o Releases\assets.win.json ou o Releases\releases.win.json local: corra o pack.ps1 nesta máquina, ou passe -IndiceDaRelease na emergência pelo CI"
  }
  $expected = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
  foreach ($asset in @((Get-Content -LiteralPath $localAssets -Raw -Encoding UTF8 | ConvertFrom-Json) | ForEach-Object { $_ })) {
    if ($null -eq $asset) { continue }
    $type = switch -CaseSensitive ("$($asset.Type)") { "Full" { "Full" } "1" { "Full" } "Delta" { "Delta" } "2" { "Delta" } default { $null } }
    if ($type) { $expected[[string]$asset.RelativeFileName] = $type }
  }
  if ($expected.Count -eq 0) { throw "o Releases\assets.win.json local não tem nenhum pacote Full nem Delta: corra o pack.ps1 nesta máquina" }
  $local = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
  foreach ($entry in @((Get-Content -LiteralPath $localIndex -Raw -Encoding UTF8 | ConvertFrom-Json).Assets)) {
    if ($null -ne $entry -and -not $local.ContainsKey([string]$entry.FileName)) { $local[[string]$entry.FileName] = $entry }
  }
  $draft = Get-Content -LiteralPath $signedIndex -Raw -Encoding UTF8 | ConvertFrom-Json
  $entries = @(@($draft.Assets) | Where-Object { $null -ne $_ })
  $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
  foreach ($entry in $entries) {
    $name = [string]$entry.FileName
    if (-not $expected.ContainsKey($name)) { throw "o releases.win.json do rascunho $tag tem uma entrada a mais ($name), que não é um pacote desta versão em Releases\assets.win.json: não assino" }
    if (-not $seen.Add($name)) { throw "o releases.win.json do rascunho $tag tem uma entrada repetida ($name): não assino" }
  }
  foreach ($name in $expected.Keys) {
    if (-not $seen.Contains($name)) { throw "o releases.win.json do rascunho $tag tem uma entrada em falta ($name, de Releases\assets.win.json): não assino" }
  }
  foreach ($entry in $entries) {
    $name = [string]$entry.FileName
    $file = Join-Path $root "Releases\$name"
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "falta o Releases\$name local: corra o pack.ps1 nesta máquina" }
    if (-not $local.ContainsKey($name)) { throw "o Releases\releases.win.json local não tem ${name}: corra o pack.ps1 nesta máquina" }
    $mine = $local[$name]
    $checks = @(
      @("PackageId", [string]$entry.PackageId, [string]$mine.PackageId),
      @("Version", [string]$entry.Version, [string]$mine.Version),
      @("Version", [string]$entry.Version, $Version),
      @("Type", [string]$entry.Type, $expected[$name]),
      @("SHA256", ([string]$entry.SHA256).ToUpperInvariant(), (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToUpperInvariant()),
      @("Size", [string]$entry.Size, [string](Get-Item -LiteralPath $file).Length)
    )
    foreach ($check in $checks) {
      if ($check[1] -cne $check[2]) { throw "o releases.win.json do rascunho $tag não bate com o Releases\$name desta máquina (campo $($check[0]): $($check[1]) no rascunho, $($check[2]) aqui): não assino" }
    }
  }
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

  # 4. O que os clientes vão ler: o índice que está no rascunho (o do «vpk upload github», não o desta máquina).
  Get-DraftIndex

  # 5. Com -Key: conferir esse índice com os pacotes desta máquina e assinar os bytes dele, tal como vieram; depois de
  # subir o .sig, descarregar de novo os dois (o passo 6 verifica o que lá ficou).
  if ($Key) {
    if ($IndiceDaRelease) {
      Write-Warning "a assinar o índice que está no rascunho (emergência pelo CI): a assinatura prova que foi o dono a publicar, não o que o CI compilou"
    } else {
      Assert-DraftIndex
    }
    if ((Run-Sign @("sign", $Key, $signedIndex)) -ne 0) { throw "a assinatura do releases.win.json do rascunho falhou (frase-passe ou chave)" }
    & gh release upload $tag "$signedIndex.sig" --repo $Repo --clobber
    if ($LASTEXITCODE -ne 0) { throw "não consegui subir o releases.win.json.sig para $tag (gh release upload)" }
    Get-DraftIndex
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
