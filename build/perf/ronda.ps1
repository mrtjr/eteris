# Várias rondas da bateria em vários braços, com a ordem rodada em cada ronda (quadrado latino: na ronda r começa o
# braço r, depois os seguintes), para a deriva da noite não cair sempre no mesmo navegador. Ver build/perf/README.md.
#
#   .\ronda.ps1 -Config .\bracos.exemplo.psd1 -Rondas 5 -Cenarios fps,scroll -JanelaS 60
#
# O ficheiro de configuração (.psd1) lista os braços: Nome, Navegador, e opcionalmente Exe, Ambiente («VAR=valor»),
# Extra (argumentos), PerfilNovo e ApagarPerfil (referências: perfil temporário novo em cada ronda e apagado no fim),
# SemContadores (Éteris sem ETERIS_TEST_CONTADORES).
# Antes da 1.ª ronda regista o ambiente da máquina (ambiente.ps1). Cada ronda acrescenta linhas ao mesmo CSV.
param(
  [Parameter(Mandatory)] [string]$Config,
  [int]$Rondas = 5,
  [int]$PrimeiraRonda = 1,
  [string[]]$Cenarios = @('arranque', 'memoria', 'fps', 'scroll'),
  [int]$JanelaS = 60,
  [int]$EsperaS = 15,
  [int]$Arranques = 8,
  [switch]$Memoria5min,
  [switch]$Maximizada,
  [switch]$Rastro,
  [string]$Trabalho = '',
  [int]$Porta = 18473,
  [string]$Resultados = ''
)
$ErrorActionPreference = 'Stop'
$cfg = Import-PowerShellDataFile $Config
$bracos = @($cfg.Bracos)
if (-not $bracos.Count) { throw "sem braços em $Config" }
$comuns = @{ Cenarios = $Cenarios; JanelaS = $JanelaS; EsperaS = $EsperaS; Arranques = $Arranques; Porta = $Porta }
if ($Trabalho) { $comuns.Trabalho = $Trabalho }
if ($Resultados) { $comuns.Resultados = $Resultados }
foreach ($s in 'Memoria5min', 'Maximizada', 'Rastro') { if ((Get-Variable $s).Value) { $comuns[$s] = $true } }
& "$PSScriptRoot\ambiente.ps1" -Trabalho $Trabalho | Out-Host
for ($r = $PrimeiraRonda; $r -lt $PrimeiraRonda + $Rondas; $r++) {
  $n = $bracos.Count
  $ordem = 0..($n - 1) | ForEach-Object { $bracos[($_ + $r - 1) % $n] }
  "== ronda $r : $(($ordem | ForEach-Object { $_.Nome }) -join ' -> ')" | Out-Host
  foreach ($b in $ordem) {
    $p = @{} + $comuns
    $p.Braco = $b.Nome; $p.Navegador = $b.Navegador; $p.Ronda = $r
    if ($b.ContainsKey('Exe')) { $p.Exe = $b.Exe }
    if ($b.ContainsKey('Ambiente')) { $p.Ambiente = @($b.Ambiente) }
    if ($b.ContainsKey('Extra')) { $p.Extra = @($b.Extra) }
    if ($b.ContainsKey('PerfilNovo') -and $b.PerfilNovo) { $p.PerfilNovo = $true }
    if ($b.ContainsKey('ApagarPerfil') -and $b.ApagarPerfil) { $p.ApagarPerfil = $true }
    if ($b.ContainsKey('SemContadores') -and $b.SemContadores) { $p.SemContadores = $true }
    & "$PSScriptRoot\medir.ps1" @p
  }
}
