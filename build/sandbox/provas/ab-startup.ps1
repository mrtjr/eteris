param([Parameter(Mandatory)][string]$A, [Parameter(Mandatory)][string]$B, [string]$C = "",
  [string]$CArg = "--eteris-no-sandbox", [int]$Rounds = 7, [string]$Work = "", [string]$Csv = "")
# Medição intercalada do arranque (docs/sandbox.md; plano da 0.2.14, P9), em braços:
#   A = -A <exe>  (por exemplo a versão anterior publicada, sem sandbox);
#   B = -B <exe>  (a candidata, com sandbox);
#   C = -C <exe>, ou o mesmo exe de B, arrancado com -CArg (por omissão --eteris-no-sandbox): separa o custo do sandbox
#       do custo das outras mudanças da versão. Sem -C e com -CArg "", não há braço C.
# Em cada ronda a ordem dos braços roda. A 1.ª ronda é de aquecimento (cria os perfis) e fica fora das medianas.
#   frio = Start-Process até a janela principal ter título (perfil já criado);
#   2.º  = com a instância aberta, um 2.º arranque com --new-tab até esse processo sair (entrega à instância aberta).
# Perfis próprios em <Work>\profile-ab-<braço>. Sem a ACL do serviço de rede nas pastas, o serviço de rede fica fora do
# AppContainer (nenhum perfil de AppContainer novo); para medir com ela, dá-la antes com network-sandbox-acl.ps1.
. "$PSScriptRoot\comum.ps1"
$S = Get-ProvasWork $Work
$variants = [ordered]@{ A = @{ exe = $A; args = @() }; B = @{ exe = $B; args = @() } }
$cExe = if ($C) { $C } else { $B }
if ($C -or $CArg) { $variants['C'] = @{ exe = $cExe; args = @($CArg | Where-Object { $_ }) } }
$env:ETERIS_TEST_MUTE = '1'
$results = @{}; foreach ($v in $variants.Keys) { $results[$v] = @{ frio = @(); segundo = @() } }
$rows = @()
function Procs($exe) { @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like ((Split-Path $exe) + '\*') }) }
function WaitQueue {
  for ($i = 0; $i -lt 80; $i++) {
    $o = @(Get-ProvasOthers $S | Where-Object { $_.CommandLine -notmatch 'profile-ab-' })
    if (-not $o.Count) { return }; "aguardando outra prova ao vivo"; Start-Sleep 15 }
}
$names = @($variants.Keys)
for ($r = 0; $r -lt $Rounds; $r++) {
  Test-ProvasPausa $S
  WaitQueue
  $order = for ($k = 0; $k -lt $names.Count; $k++) { $names[($k + $r) % $names.Count] }
  foreach ($v in $order) {
    $exe = $variants[$v].exe; $prof = "$S\profile-ab-$v"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $exe -ArgumentList (@("--user-data-dir=$prof") + $variants[$v].args + @('eteris://about')) -PassThru
    $cold = -1
    while ($sw.ElapsedMilliseconds -lt 30000) { $p.Refresh(); if ($p.MainWindowTitle -like '*ris*') { $cold = $sw.ElapsedMilliseconds; break }; Start-Sleep -Milliseconds 10 }
    Start-Sleep 4
    $sw2 = [Diagnostics.Stopwatch]::StartNew()
    $q = Start-Process -FilePath $exe -ArgumentList (@("--user-data-dir=$prof") + $variants[$v].args + @('--new-tab')) -PassThru
    $second = if ($q.WaitForExit(20000)) { $sw2.ElapsedMilliseconds } else { -1 }
    $code = if ($q.HasExited) { $q.ExitCode } else { 'a correr' }
    Start-Sleep 1
    $close = & "$PSScriptRoot\close.ps1" -ProcessId $p.Id
    for ($i = 0; $i -lt 40 -and (Procs $exe).Count; $i++) { Start-Sleep -Milliseconds 250 }
    $left = (Procs $exe).Count
    "ronda $r $v : frio $cold ms | 2.o arranque $second ms (código $code) | fecho $close | restos $left"
    $rows += [pscustomobject]@{ ronda = $r; braco = $v; frio_ms = $cold; segundo_ms = $second; codigo = $code; fecho = $close; restos = $left }
    if ($r -gt 0 -and $cold -gt 0 -and $second -gt 0) { $results[$v].frio += $cold; $results[$v].segundo += $second }
    Start-Sleep 2
  }
}
if ($Csv) { $rows | Export-Csv -LiteralPath $Csv -NoTypeInformation -Encoding UTF8 }
function Median($xs) { $s = @($xs | Sort-Object); if (-not $s.Count) { return 'n/d' }; if ($s.Count % 2) { $s[[int][Math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 } }
foreach ($v in $names) {
  "$v : frio mediana $(Median $results[$v].frio) ms [$($results[$v].frio -join ', ')] | 2.o arranque mediana $(Median $results[$v].segundo) ms [$($results[$v].segundo -join ', ')]"
}
