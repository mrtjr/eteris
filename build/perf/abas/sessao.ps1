# Uma sessão da troca de abas (B-ABAS, T1–T6 de docs/motor.md) num navegador de teste: abre as abas, corre as fases
# (cada uma repete uma ação — uma tecla, um clique — a capturar a janela a 60 fps) e analisa cada fase (analisar.py).
# O mesmo critério para o Éteris, o Chrome e o Brave. Chamado por bateria.ps1.
#
# Éteris: as abas entram pela sessão guardada do perfil de teste (session.json; só recebe 1 endereço na linha de
# comandos), com o tema e o Mica pedidos (settings.json do perfil de teste). Chrome e Brave: as abas vão na linha de
# comandos, perfil temporário apagado no fim.
# Ações (-Init e, em cada fase, Pre, Act, Act2 — nas repetições pares —, Undo): «key:<SendKeys>», «click:x,y»
# (relativo à janela), «sleep:ms», separadas por «|».
param(
  [Parameter(Mandatory)] [string]$Sessao,
  [Parameter(Mandatory)] [ValidateSet('eteris', 'chrome', 'brave')] [string]$Navegador,
  [string]$Exe = '',
  [Parameter(Mandatory)] [string[]]$Urls,
  [int]$Ativa = 0,
  [string]$Init = '',
  [Parameter(Mandatory)] [object[]]$Fases,
  [string]$Tema = 'dark',
  [bool]$Mica = $true,
  [int]$Aquecer = 8,
  [int]$Topo = 22,
  [double]$Segundos = 1.6,
  [int]$LimiteS = 285,
  [string]$Trabalho = '',
  [int]$Porta = 18473
)
. "$PSScriptRoot\..\comum.ps1"
Initialize-Perf $Trabalho $Porta
Add-Type -AssemblyName System.Windows.Forms
if (-not ([System.Management.Automation.PSTypeName]'PerfCaptura').Type) { Add-Type -Path (Join-Path $PSScriptRoot 'Captura.cs') }
if (-not $Exe) { $Exe = Get-ExeNavegador $Navegador }
$perfil = Join-Path $Trabalho "perfis\abas-$Navegador"
$saida = Join-Path $Trabalho 'resultados\abas'; New-Item -ItemType Directory -Force $saida | Out-Null
if ((Get-Arvore $perfil).Count) { throw "já há processos com o perfil $perfil" }
Start-Servidor
Assert-SemPausa; Wait-Fila $perfil

if ($Navegador -eq 'eteris') {
  New-Item -ItemType Directory -Force $perfil | Out-Null
  $setFile = Join-Path $perfil 'settings.json'
  $set = $(if (Test-Path $setFile) { Get-Content $setFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{} })
  $set | Add-Member -Force NoteProperty Theme $Tema
  $set | Add-Member -Force NoteProperty Mica $Mica
  $set | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $setFile
  $n = $Urls.Count
  $sessao = [ordered]@{ Urls = $Urls; ActiveIndex = $Ativa; Pinned = @(@($false) * $n); GroupIds = @(@(-1) * $n); Groups = @(); Metas = @(1..$n | ForEach-Object { @{ Locked = $false } }) }
  $sessao | ConvertTo-Json -Depth 5 | Set-Content -Encoding ascii (Join-Path $perfil 'session.json')
  Start-Navegador $Navegador $Exe $perfil @() | Out-Null
}
else { if (Test-Path $perfil) { [IO.Directory]::Delete($perfil, $true) }; Start-Navegador $Navegador $Exe $perfil $Urls | Out-Null }
Start-Sleep -Seconds $Aquecer
$h = Set-JanelaTeste $perfil
"[$Sessao] janela $h" | Out-Host

function Acao([string]$a) {
  if (-not $a) { return }
  foreach ($um in $a.Split('|')) {
    if (-not $um) { continue }
    $tipo, $arg = $um.Split(':', 2)
    switch ($tipo) {
      'key' { if (-not [PerfJanela]::Frente($h)) { throw 'a janela de teste não está à frente' }; [System.Windows.Forms.SendKeys]::SendWait($arg) }
      'click' { $x, $y = $arg.Split(','); if (-not [PerfJanela]::Clique($h, [int]$x, [int]$y)) { throw 'a janela de teste não está à frente' } }
      'sleep' { Start-Sleep -Milliseconds ([int]$arg) }
    }
  }
}
# Captura a área da janela enquanto corre a ação (o mesmo que o antigo flashcap/bcap).
function Capturar([string]$Saida, [string]$A) {
  "$h" | Out-File -Encoding ascii "$Saida.hwnd"
  $w = [PerfJanela]::List() | Where-Object { $_.H -eq $h } | Select-Object -First 1
  [PerfCaptura]::SetProcessDPIAware() | Out-Null
  [PerfCaptura]::Start($w.X, $w.Y, $w.Wd, $w.Ht, 4, 60, [int]($Segundos * 60 * 1.2) + 60)
  $t0 = [DateTime]::UtcNow
  Start-Sleep -Milliseconds 300
  "action-start $([DateTime]::UtcNow.Ticks)" | Out-File -Encoding ascii "$Saida.action"
  Acao $A
  "action-end $([DateTime]::UtcNow.Ticks)" | Out-File -Encoding ascii -Append "$Saida.action"
  $resto = $Segundos - ([DateTime]::UtcNow - $t0).TotalSeconds
  if ($resto -gt 0) { Start-Sleep -Milliseconds ([int]($resto * 1000)) }
  [PerfCaptura]::Stop($Saida) | Out-Null
}

$inicio = Get-Date; $feitas = @()
try {
  if ($Init) { Acao $Init; Start-Sleep -Seconds 2 }
  foreach ($f in $Fases) {
    $tag = $f.Tag; $reps = $(if ($f.ContainsKey('Reps')) { $f.Reps } else { 12 }); $gap = $(if ($f.ContainsKey('Gap')) { $f.Gap } else { 1.5 })
    Get-ChildItem -LiteralPath $saida -Filter "$tag-*" | ForEach-Object { [IO.File]::Delete($_.FullName) }
    if ($f.ContainsKey('Pre')) { Acao $f.Pre; Start-Sleep -Milliseconds 2000 }
    $ok = $true
    for ($k = 1; $k -le $reps; $k++) {
      Assert-SemPausa
      if (((Get-Date) - $inicio).TotalSeconds -gt $LimiteS) { "[$Sessao] parado em $tag $k : limite de tempo da sessão" | Out-Host; $ok = $false; break }
      $a = $(if ($f.ContainsKey('Act2') -and $k % 2 -eq 0) { $f.Act2 } else { $f.Act })
      Capturar (Join-Path $saida "$tag-$k") $a
      if ($f.ContainsKey('Undo')) { Start-Sleep -Milliseconds 300; Acao $f.Undo }
      Start-Sleep -Milliseconds ([int]($gap * 1000))
    }
    $feitas += $tag
    if (-not $ok) { break }
  }
}
catch { "[$Sessao] ERRO: $_" | Out-Host }
finally {
  Close-Arvore $perfil
  if ($Navegador -eq 'eteris') { [IO.File]::Delete((Join-Path $perfil 'session.json')) }
  elseif (Test-Path $perfil) { try { [IO.Directory]::Delete($perfil, $true) } catch { Start-Sleep 3; [IO.Directory]::Delete($perfil, $true) } }
  "[$Sessao] fim ($([int]((Get-Date) - $inicio).TotalSeconds) s)" | Out-Host
}
foreach ($f in $Fases) {
  if ($feitas -notcontains $f.Tag) { continue }
  $sk = $(if ($f.ContainsKey('Skip')) { $f.Skip } else { 0 })
  python -I (Join-Path $PSScriptRoot 'analisar.py') $saida $f.Tag $Topo $sk 2>&1 | Out-File -Encoding utf8 (Join-Path $saida "$($f.Tag).total.txt")
  Get-Content (Join-Path $saida "$($f.Tag).total.txt") | Select-String 'TOTAL' | ForEach-Object { "[$($f.Tag)] $_" }
  Get-ChildItem -LiteralPath $saida -Filter "$($f.Tag)-*.raw" | ForEach-Object { [IO.File]::Delete($_.FullName) }
}
