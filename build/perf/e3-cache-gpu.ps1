# E3 (ajuste e3, decisão D-4): a cache dos shaders da GPU no disco e o que acontece a ela depois de uma janela privada.
# Prova de privacidade do plano: a partição da janela privada é só em memória (nada novo na GPUCache do perfil), e as
# caches globais (ShaderCache, GrShaderCache, GraphiteDawnCache), que não são por janela, apagam-se ao sair depois de
# uma janela privada (Infrastructure/ProfileCleanup.cs).
#
#   .\e3-cache-gpu.ps1 -Exe <publicação>\Eteris.Browser.exe
#
# Passos, num perfil de teste novo (perfis\e3-cache-gpu), com --eteris-ajustes=e3:
#   1. janela normal com webgl.html, fechar: as caches existem (a cache funciona);
#   2. a mesma sem o ajuste (--eteris-ajustes=-e3): nada novo nas caches (o interruptor desliga-a);
#   3. janela privada (--private) com webgl.html: nada novo na GPUCache do perfil durante a sessão; ao sair, as caches
#      globais e as da partição foram apagadas.
# Escreve PASSOU/FALHOU por verificação e sai com 1 se alguma falhar.
param(
  [Parameter(Mandatory)] [string]$Exe,
  [string]$Trabalho = '',
  [int]$Porta = 18473
)
. "$PSScriptRoot\comum.ps1"
Initialize-Perf $Trabalho $Porta
$perfil = Join-Path $script:Trabalho 'perfis\e3-cache-gpu'
if (Test-Path $perfil) { Remove-Item -Recurse -Force $perfil }
$globais = 'ShaderCache', 'GrShaderCache', 'GraphiteDawnCache'
$particao = 'GPUCache', 'DawnWebGPUCache', 'DawnGraphiteCache'
$falhas = 0

function Get-Caches {
  $r = @{}
  foreach ($f in $globais) { $r[$f] = @(Get-ChildItem -Recurse -File -ErrorAction SilentlyContinue (Join-Path $perfil $f) | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) }
  foreach ($f in $particao) { $r["Default\$f"] = @(Get-ChildItem -Recurse -File -ErrorAction SilentlyContinue (Join-Path $perfil "Default\$f") | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) }
  $r
}
# Os ficheiros de $Depois que não estavam em $Antes (novos ou mudados).
function Get-Novos($Antes, $Depois) { @(@($Depois) | Where-Object { $_ -and $_ -notin @($Antes) }) }
function Test-Passo([string]$Nome, [bool]$Ok, [string]$Detalhe = '') {
  if ($Ok) { "PASSOU  $Nome $Detalhe" } else { "FALHOU  $Nome $Detalhe"; $script:falhas++ }
}
function Invoke-Sessao([string[]]$Extra, [scriptblock]$Durante = $null) {
  Assert-SemPausa; Wait-Fila $perfil
  $tag = "e3-$(Get-Random)"; $p0 = Get-PosicaoRegisto
  Start-Navegador 'eteris' $Exe $perfil @("$Base/webgl.html?r=$tag") @{} $Extra | Out-Null
  $pintou = Wait-Sinal $p0 "/p/painted\?r=$tag&v=1" 60000
  Start-Sleep -Seconds 3
  if ($Durante) { & $Durante }
  Close-Arvore $perfil
  $pintou
}

Start-Servidor
try {
  $pintou = Invoke-Sessao @('--eteris-ajustes=e3')
  $c1 = Get-Caches
  Test-Passo 'webgl desenhou (janela normal)' $pintou
  Test-Passo 'com o ajuste, há cache da GPU no disco' (($c1.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum -gt 0) (($c1.Keys | Where-Object { $c1[$_].Count } | Sort-Object) -join ',')

  Invoke-Sessao @('--eteris-ajustes=-e3') | Out-Null
  $c2 = Get-Caches
  $novos = @($c2.Keys | Where-Object { (Get-Novos $c1[$_] $c2[$_]).Count })
  Test-Passo 'sem o ajuste, nada novo nas caches' ($novos.Count -eq 0) ($novos -join ',')

  $antes = $c2['Default\GPUCache']
  $script:durante = $null
  $pintou = Invoke-Sessao @('--eteris-ajustes=e3', '--private') { $script:durante = @(Get-ChildItem -Recurse -File -ErrorAction SilentlyContinue (Join-Path $perfil 'Default\GPUCache') | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) }
  Test-Passo 'webgl desenhou (janela privada)' $pintou
  $mudou = Get-Novos $antes $script:durante
  Test-Passo 'janela privada: nada novo na GPUCache do perfil durante a sessão' ($mudou.Count -eq 0) "$($mudou.Count) ficheiro(s)"
  $c3 = Get-Caches
  $ficaram = @($c3.Keys | Where-Object { $c3[$_].Count })
  Test-Passo 'depois de uma janela privada, as caches da GPU apagadas ao sair' ($ficaram.Count -eq 0) ($ficaram -join ',')
}
finally { Stop-Servidor }
if ($falhas) { exit 1 }
