# Quando nasce cada processo filho, visto de fora (hora de criação relativa ao processo principal), em arranques
# mornos. Serve para o Chrome e o Brave, que não têm o rastro do Éteris; no Éteris o rastro já traz as linhas «filho»
# (ETERIS_TRACE_STARTUP, fases.py). A hora vem do CIM (Win32_Process.CreationDate): não abre os processos, por isso
# funciona também com os renderers em sandbox.
#
#   .\filhos.ps1 -Navegador chrome -Rondas 5
#   .\filhos.ps1 -Navegador eteris -Exe <publicação>\Eteris.Browser.exe -Extra --eteris-no-sandbox
param(
  [Parameter(Mandatory)] [ValidateSet('eteris', 'chrome', 'brave', 'firefox')] [string]$Navegador,
  [string]$Exe = '',
  [string[]]$Extra = @(),
  [int]$Rondas = 5,
  [string]$Trabalho = '',
  [int]$Porta = 18473
)
. "$PSScriptRoot\comum.ps1"
Initialize-Perf $Trabalho $Porta
if (-not $Exe) { $Exe = Get-ExeNavegador $Navegador }
$perfil = Join-Path $Trabalho "perfis\filhos-$Navegador"
Start-Servidor
for ($i = 0; $i -le $Rondas; $i++) {
  Assert-SemPausa; Wait-Fila $perfil
  $tag = "f$i-$(Get-Random)"; $p0 = Get-PosicaoRegisto; $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Navegador $Navegador $Exe $perfil @("$Base/start.html?r=$tag") @{} $Extra
  Wait-Sinal $p0 "/p/painted\?r=$tag" 60000 | Out-Null; $pintura = $sw.ElapsedMilliseconds
  Start-Sleep -Seconds 2
  $arvore = Get-Arvore $perfil
  # A raiz da árvore (o processo lançado pode ser só um intermediário, como o atalho do Firefox da Microsoft Store).
  $raiz = $arvore.Values | Where-Object { -not $arvore.ContainsKey([int]$_.ParentProcessId) } | Sort-Object CreationDate | Select-Object -First 1
  if (-not $raiz) { 'sem processos'; continue }
  $t0 = $raiz.CreationDate
  $linha = [ordered]@{ ronda = $i; pintura = $pintura }
  foreach ($k in $arvore.Keys) {
    if ($k -eq [int]$raiz.ProcessId) { continue }
    $tipo = (Get-TipoProcesso $arvore[$k]) -replace '^[^:]+:', ''
    $ms = [math]::Round(($arvore[$k].CreationDate - $t0).TotalMilliseconds)
    $nome = $tipo; $j = 2; while ($linha.Contains($nome)) { $nome = "$tipo$j"; $j++ }
    $linha[$nome] = $ms
  }
  # A ronda 0 é de aquecimento (perfil criado).
  if ($i -gt 0) { ($linha.Keys | ForEach-Object { "$_=$($linha[$_])" }) -join ' ' | Out-Host }
  Close-Arvore $perfil; Start-Sleep -Seconds 1
}
