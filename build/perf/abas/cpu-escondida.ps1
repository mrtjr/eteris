# CPU de uma aba animada escondida (outra aba à frente) e à vista (P1, E8: «aba quente animada ~10%»). Abas: uma
# página clara e anim.html; troca para a animada e volta (Ctrl+Tab), espera e mede -JanelaS; depois com a animada à
# vista. CPU por tipo de processo e contadores do Éteris no CSV de medir.ps1 (cenários escondida.oculta e .vista).
#
#   .\cpu-escondida.ps1 -Braco L1 -Navegador eteris -Exe <publicação>\Eteris.Browser.exe
param(
  [Parameter(Mandatory)] [string]$Braco,
  [Parameter(Mandatory)] [ValidateSet('eteris', 'chrome', 'brave')] [string]$Navegador,
  [string]$Exe = '',
  [int]$Ronda = 1,
  [int]$EsperaS = 5,
  [int]$JanelaS = 30,
  [string]$Trabalho = '',
  [int]$Porta = 18473,
  [string]$Resultados = ''
)
. "$PSScriptRoot\..\comum.ps1"
Initialize-Perf $Trabalho $Porta
Add-Type -AssemblyName System.Windows.Forms
if (-not $Exe) { $Exe = Get-ExeNavegador $Navegador }
if (-not $Resultados) { $Resultados = Join-Path $Trabalho 'resultados\medicoes.csv' }
$perfil = Join-Path $Trabalho "perfis\$Braco"
if ((Get-Arvore $perfil).Count) { throw "já há processos com o perfil $perfil" }
Start-Servidor
Assert-SemPausa; Wait-Fila $perfil
$urls = @("$Base/abas/light.html", "$Base/abas/anim.html")
Start-Navegador $Navegador $Exe $perfil @($urls[0]) | Out-Null
Start-Sleep -Seconds 6
(Start-Navegador $Navegador $Exe $perfil @($urls[1])).WaitForExit(15000) | Out-Null
Start-Sleep -Seconds 3
$h = Set-JanelaTeste $perfil
function Tecla([string]$k) { if (-not [PerfJanela]::Frente($h)) { throw 'a janela de teste não está à frente' }; [System.Windows.Forms.SendKeys]::SendWait($k) }
function Medir([string]$Estado) {
  Start-Sleep -Seconds $EsperaS
  $arv = Get-Arvore $perfil; $f0 = Get-Fotografia
  Start-Sleep -Seconds $JanelaS
  $f1 = Get-Fotografia
  $linhas = @(Measure-Cpu $f0 $f1 $arv)
  Write-Resultado $Resultados $Ronda $Braco "escondida.$Estado" $linhas | Out-Null
  $linhas | Where-Object { $_[0] -like 'cpu.*' } | ForEach-Object { "  $Estado $($_[0]) = $($_[1])" } | Out-Host
}
try {
  # A animada (2.ª) ficou à frente: Ctrl+1 esconde-a atrás da clara.
  Tecla '^1'; Medir 'oculta'
  Tecla '^2'; Medir 'vista'
}
finally { Close-Arvore $perfil }
