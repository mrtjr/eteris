# Funções comuns das medições de desempenho (build/perf/README.md). Carregar com: . "$PSScriptRoot\comum.ps1"
#
# Regras (plano de desempenho 0.2.14, secção 3.3): só perfis de teste dentro da pasta de trabalho; nunca a instância
# nem o perfil do dono; Chrome, Brave e Firefox numa instância própria com perfil temporário; nada pede administrador;
# nada é descarregado. Os processos de teste reconhecem-se pela pasta do perfil na linha de comandos (o «marcador»).
#
# Medidas sem abrir os processos (funciona com os renderers em sandbox): os contadores do Windows em bruto pelo CIM
# (nomes ingleses e estáveis, mesmo num Windows em português, ao contrário do Get-Counter):
#   CPU    Win32_PerfRawData_PerfProc_Process.PercentProcessorTime (100 ns) / Timestamp_Sys100NS;
#   PMF    Win32_PerfRawData_PerfProc_Process.PrivateBytes — a «memória privada» do critério do Chrome (private memory
#          footprint = commit privado, o que o Gestor de tarefas do Chrome mostra);
#   WSP    Win32_PerfRawData_PerfProc_Process.WorkingSetPrivate — o conjunto de trabalho privado (a métrica antiga);
#   GPU    Win32_PerfRawData_GPUPerformanceCounters_GPUEngine.RunningTime (motores 3D) e GPUProcessMemory.DedicatedUsage.
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture

$PerfRaiz = $PSScriptRoot
if (-not ([System.Management.Automation.PSTypeName]'PerfJanela').Type) { Add-Type -Path (Join-Path $PerfRaiz 'Janela.cs') }
[PerfJanela]::SetProcessDPIAware() | Out-Null

# Pasta de trabalho: perfis de teste, registo do servidor, resultados. Por omissão fora do repositório, em %TEMP%.
function Initialize-Perf([string]$Trabalho = '', [int]$Porta = 18473) {
  if (-not $Trabalho) { $Trabalho = Join-Path ([IO.Path]::GetTempPath()) 'eteris-perf' }
  $script:Trabalho = [IO.Path]::GetFullPath($Trabalho)
  $script:Porta = $Porta
  $script:Base = "http://127.0.0.1:$Porta"
  $script:RegistoServidor = Join-Path $script:Trabalho "servidor-$Porta.log"
  foreach ($d in 'perfis', 'resultados', 'rastos') { New-Item -ItemType Directory -Force (Join-Path $script:Trabalho $d) | Out-Null }
}

# PAUSA-UI na pasta de trabalho: o dono está a usar o PC; nada abre janelas nem envia teclas.
function Assert-SemPausa { if (Test-Path (Join-Path $script:Trabalho 'PAUSA-UI')) { throw 'PAUSA-UI: medições suspensas (o dono está a usar o PC)' } }

# ---------------------------------------------------------------- servidor local das páginas
function Start-Servidor {
  if (Get-NetTCPConnection -LocalPort $script:Porta -State Listen -ErrorAction SilentlyContinue) { return }
  $pag = Join-Path $PerfRaiz 'paginas'
  $p = Start-Process -FilePath python -ArgumentList @('-I', '-u', (Join-Path $PerfRaiz 'servidor.py'), $pag, "$script:Porta", $script:RegistoServidor) -WindowStyle Hidden -PassThru
  $p.Id | Set-Content (Join-Path $script:Trabalho "servidor-$script:Porta.pid")
  for ($i = 0; $i -lt 50 -and -not (Get-NetTCPConnection -LocalPort $script:Porta -State Listen -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 100 }
}
function Stop-Servidor {
  $f = Join-Path $script:Trabalho "servidor-$script:Porta.pid"
  if (Test-Path $f) { Stop-Process -Id ([int](Get-Content $f)) -Force -ErrorAction SilentlyContinue; Remove-Item $f }
}
function Get-PosicaoRegisto { if (Test-Path $script:RegistoServidor) { (Get-Item $script:RegistoServidor).Length } else { 0 } }
function Read-Registo([long]$Desde) {
  if (-not (Test-Path $script:RegistoServidor)) { return '' }
  $fs = [IO.File]::Open($script:RegistoServidor, 'Open', 'Read', 'ReadWrite')
  try { $fs.Seek($Desde, 'Begin') | Out-Null; (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
}
# Espera por um sinal das páginas (ex.: "/p/painted\?r=<etiqueta>") escrito no registo depois da posição dada.
function Wait-Sinal([long]$Desde, [string]$Padrao, [int]$LimiteMs = 60000) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $LimiteMs) { if ((Read-Registo $Desde) -match $Padrao) { return $true }; Start-Sleep -Milliseconds 5 }
  $false
}
# Valores de um sinal (v=) entre duas posições do registo.
function Get-ValoresSinal([long]$Desde, [string]$Sinal, [string]$Etiqueta) {
  [regex]::Matches((Read-Registo $Desde), "/p/$Sinal\?r=$([regex]::Escape($Etiqueta))&v=(-?\d+)") | ForEach-Object { [double]$_.Groups[1].Value }
}

# ---------------------------------------------------------------- navegadores
function Get-ExeNavegador([string]$Navegador) {
  switch ($Navegador) {
    'chrome' { $c = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") }
    'brave' { $c = @("$env:ProgramFiles\BraveSoftware\Brave-Browser\Application\brave.exe", "$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\Application\brave.exe") }
    'firefox' { $c = @("$env:ProgramFiles\Mozilla Firefox\firefox.exe", "$env:LOCALAPPDATA\Microsoft\WindowsApps\firefox.exe") }
    default { throw "navegador sem caminho por omissão: $Navegador (passar -Exe)" }
  }
  foreach ($x in $c) { if ($x -and (Test-Path $x)) { return $x } }
  throw "$Navegador não encontrado"
}

# Argumentos de uma instância de teste. Chrome e Brave: perfil próprio, sem a 1.ª execução, sem som, a mesma janela.
# Firefox: perfil próprio numa instância nova (-no-remote: nunca entrega à janela do dono).
# Éteris: perfil de teste; ETERIS_TEST_MUTE=1 vai no ambiente (Start-Navegador).
function Get-ArgsNavegador([string]$Navegador, [string]$Perfil, [string[]]$Urls, [string[]]$Extra = @()) {
  switch ($Navegador) {
    'eteris' { @("--user-data-dir=$Perfil") + $Extra + $Urls }
    { $_ -in 'chrome', 'brave' } {
      @("--user-data-dir=$Perfil", '--no-first-run', '--no-default-browser-check', '--mute-audio', '--disable-sync',
        '--window-size=1360,860', '--window-position=100,100') + $Extra + $Urls }
    'firefox' { @('-no-remote', '-new-instance', '-profile', $Perfil) + $Extra + $Urls }
  }
}

# Lança e devolve o processo. $Ambiente: variáveis só para este processo (as de medição de uma corrida anterior saem).
function Start-Navegador([string]$Navegador, [string]$Exe, [string]$Perfil, [string[]]$Urls, [hashtable]$Ambiente = @{}, [string[]]$Extra = @()) {
  New-Item -ItemType Directory -Force $Perfil | Out-Null
  if ($Navegador -eq 'brave' -and -not (Test-Path (Join-Path $Perfil 'Local State'))) {
    # P3A do Brave desligado e aviso já visto: nada enviado e nenhuma barra por cima da página.
    '{"brave":{"p3a":{"enabled":false,"notice_acknowledged":true}}}' | Set-Content -Encoding ascii (Join-Path $Perfil 'Local State')
  }
  $a = (Get-ArgsNavegador $Navegador $Perfil $Urls $Extra | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
  $psi = New-Object Diagnostics.ProcessStartInfo($Exe, $a); $psi.UseShellExecute = $false
  foreach ($k in @($psi.EnvironmentVariables.Keys)) { if ("$k" -like 'ETERIS_TEST_*' -or "$k" -like 'ETERIS_TRACE_*' -or "$k" -like 'DOTNET_*') { $psi.EnvironmentVariables.Remove($k) } }
  if ($Navegador -eq 'eteris') { $psi.EnvironmentVariables['ETERIS_TEST_MUTE'] = '1' }
  foreach ($k in $Ambiente.Keys) { $psi.EnvironmentVariables[$k] = [string]$Ambiente[$k] }
  [Diagnostics.Process]::Start($psi)
}

# Árvore de processos de teste: os que têm o marcador (a pasta do perfil) na linha de comandos e os descendentes.
function Get-Arvore([string]$Marcador) {
  $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CommandLine, Name, CreationDate
  $set = @{}
  $all | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($Marcador, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | ForEach-Object { $set[[int]$_.ProcessId] = $_ }
  do { $a = $false; foreach ($p in $all) { if ($set.ContainsKey([int]$p.ParentProcessId) -and -not $set.ContainsKey([int]$p.ProcessId)) { $set[[int]$p.ProcessId] = $p; $a = $true } } } while ($a)
  $set
}

# Tipo do processo: <exe>:<tipo>. Chromium: --type= (+ subtipo do utilitário); Firefox: o último argumento de
# -contentproc (tab, gpu, rdd, socket, utility…); sem tipo: principal.
function Get-TipoProcesso($Proc) {
  $cl = "$($Proc.CommandLine)"; $exe = ($Proc.Name -replace '\.exe$', '')
  if ($cl -match '--type=([\w-]+)') {
    $t = $Matches[1]
    if ($cl -match '--utility-sub-type=([\w.]+)') { $t += ':' + ($Matches[1] -replace '^.*\.', '') }
  }
  elseif ($cl -match '-contentproc\b') { $t = ($cl.Trim() -split '\s+')[-1] }
  else { $t = 'principal' }
  "${exe}:$t"
}

# Janela principal da árvore: posição fixa 1360x860 em (100,100) (ou maximizada) e à frente das outras.
function Set-JanelaTeste([string]$Marcador, [switch]$Maximizada) {
  $tree = Get-Arvore $Marcador
  $h = [PerfJanela]::Principal([int[]]@($tree.Keys))
  if ($h -eq [IntPtr]::Zero) { return $h }
  if ($Maximizada) { [PerfJanela]::ShowWindow($h, 3) | Out-Null }
  else { [PerfJanela]::ShowWindow($h, 9) | Out-Null; [PerfJanela]::SetWindowPos($h, [IntPtr]::Zero, 100, 100, 1360, 860, 0x0014) | Out-Null }
  [PerfJanela]::Frente($h) | Out-Null
  $h
}

# Fecha a árvore: WM_CLOSE às janelas; ao fim de 12 s, termina o que restar (só processos da árvore de teste).
function Close-Arvore([string]$Marcador) {
  $tree = Get-Arvore $Marcador
  foreach ($w in ([PerfJanela]::List() | Where-Object { $tree.ContainsKey([int]$_.Pid) -and $_.Wd -gt 300 -and -not $_.Layered })) {
    [PerfJanela]::PostMessage($w.H, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
  }
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt 12000 -and (Get-Arvore $Marcador).Count -gt 0) { Start-Sleep -Milliseconds 200 }
  foreach ($id in @((Get-Arvore $Marcador).Keys)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Milliseconds 700
}

# Espera enquanto houver outra medição a correr (outro perfil de teste desta pasta de trabalho aberto).
function Wait-Fila([string]$Marcador) {
  $perfis = Join-Path $script:Trabalho 'perfis'
  $limite = (Get-Date).AddMinutes(30)
  while ((Get-Date) -lt $limite) {
    Assert-SemPausa
    $outros = @(Get-CimInstance Win32_Process -Property CommandLine | Where-Object {
        $_.CommandLine -and $_.CommandLine.IndexOf($perfis, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $_.CommandLine.IndexOf($Marcador, [StringComparison]::OrdinalIgnoreCase) -lt 0 })
    if (-not $outros.Count) { return }
    "à espera: outra medição em curso ($($outros.Count) processos)" | Out-Host
    Start-Sleep 15
  }
  throw 'fila: tempo esgotado'
}

# ---------------------------------------------------------------- contadores do Windows (CIM, em bruto)
# Fotografia de todos os processos (CPU acumulada, memória), da GPU por processo e do desempenho do processador.
function Get-Fotografia {
  $procs = @{}
  foreach ($r in (Get-CimInstance Win32_PerfRawData_PerfProc_Process -Property IDProcess, Name, PercentProcessorTime, PrivateBytes, WorkingSetPrivate, Timestamp_Sys100NS)) {
    if ($r.Name -eq '_Total' -or $r.Name -eq 'Idle') { continue }
    $procs[[int]$r.IDProcess] = [pscustomobject]@{ Nome = ($r.Name -replace '#\d+$', ''); Cpu = [double]$r.PercentProcessorTime; Pmf = [double]$r.PrivateBytes; Wsp = [double]$r.WorkingSetPrivate }
    $t = [double]$r.Timestamp_Sys100NS
  }
  $gpu = @{}; $gmem = @{}
  try {
    foreach ($g in (Get-CimInstance Win32_PerfRawData_GPUPerformanceCounters_GPUEngine -Property Name, RunningTime)) {
      if ($g.Name -match '^pid_(\d+)_.*engtype_3D$') { $id = [int]$Matches[1]; $gpu[$id] = $(if ($gpu.ContainsKey($id)) { $gpu[$id] } else { 0 }) + [double]$g.RunningTime }
    }
    foreach ($g in (Get-CimInstance Win32_PerfRawData_GPUPerformanceCounters_GPUProcessMemory -Property Name, DedicatedUsage)) {
      if ($g.Name -match '^pid_(\d+)_') { $id = [int]$Matches[1]; $gmem[$id] = $(if ($gmem.ContainsKey($id)) { $gmem[$id] } else { 0 }) + [double]$g.DedicatedUsage }
    }
  } catch { }
  $pp = $null
  try { $pp = Get-CimInstance Win32_PerfRawData_Counters_ProcessorInformation -Filter "Name='_Total'" -Property PercentProcessorPerformance, PercentProcessorPerformance_Base, ProcessorFrequency } catch { }
  [pscustomobject]@{ T = $t; Procs = $procs; Gpu = $gpu; GpuMem = $gmem; ProcPerf = $pp; Hora = Get-Date }
}

# CPU (% de 1 núcleo) entre duas fotografias, por tipo de processo da árvore, mais o dwm.exe, a atividade de fundo
# (antivírus e Windows Update) e o desempenho do processador (frequência relativa). Devolve linhas métrica/valor.
function Measure-Cpu($F0, $F1, [hashtable]$Arvore) {
  $dt = $F1.T - $F0.T
  if ($dt -le 0) { throw 'fotografias sem intervalo' }
  function Delta($id) { if ($F0.Procs.ContainsKey($id) -and $F1.Procs.ContainsKey($id)) { 100 * ($F1.Procs[$id].Cpu - $F0.Procs[$id].Cpu) / $dt } elseif ($F1.Procs.ContainsKey($id)) { 100 * $F1.Procs[$id].Cpu / $dt } else { 0 } }
  function DeltaGpu($id) { $a = $(if ($F0.Gpu.ContainsKey($id)) { $F0.Gpu[$id] } else { 0 }); $b = $(if ($F1.Gpu.ContainsKey($id)) { $F1.Gpu[$id] } else { $a }); 100 * ($b - $a) / $dt }
  $porTipo = @{}; $gpuTipo = @{}; $total = 0; $semGpu = 0; $gpuTotal = 0
  foreach ($id in $Arvore.Keys) {
    $tipo = Get-TipoProcesso $Arvore[$id]
    $c = Delta $id; $g = DeltaGpu $id
    $porTipo[$tipo] = $(if ($porTipo.ContainsKey($tipo)) { $porTipo[$tipo] } else { 0 }) + $c
    $gpuTipo[$tipo] = $(if ($gpuTipo.ContainsKey($tipo)) { $gpuTipo[$tipo] } else { 0 }) + $g
    $total += $c; $gpuTotal += $g
    if ($tipo -notmatch ':gpu') { $semGpu += $c }
  }
  $nomes = @{ dwm = 0; fundo = 0 }
  foreach ($id in $F1.Procs.Keys) {
    $n = $F1.Procs[$id].Nome
    if ($n -eq 'dwm') { $nomes.dwm += Delta $id }
    elseif ($n -in 'MsMpEng', 'MsMpEngCP', 'TiWorker', 'TrustedInstaller', 'SearchIndexer', 'MpDefenderCoreService') { $nomes.fundo += Delta $id }
  }
  $out = New-Object System.Collections.Generic.List[object]
  foreach ($k in $porTipo.Keys) { $out.Add(@("cpu.$k", [math]::Round($porTipo[$k], 2))); $out.Add(@("gpu3d.$k", [math]::Round($gpuTipo[$k], 2))) }
  $out.Add(@('cpu.total', [math]::Round($total, 2))); $out.Add(@('cpu.total_sem_gpu', [math]::Round($semGpu, 2)))
  $out.Add(@('gpu3d.total', [math]::Round($gpuTotal, 2)))
  $out.Add(@('cpu.dwm', [math]::Round($nomes.dwm, 2))); $out.Add(@('cpu.fundo', [math]::Round($nomes.fundo, 2)))
  $out.Add(@('fundo_alto', $(if ($nomes.fundo -gt 2) { 1 } else { 0 })))
  if ($F0.ProcPerf -and $F1.ProcPerf) {
    $db = [double]$F1.ProcPerf.PercentProcessorPerformance_Base - [double]$F0.ProcPerf.PercentProcessorPerformance_Base
    if ($db -gt 0) { $out.Add(@('cpu.desempenho_processador', [math]::Round(([double]$F1.ProcPerf.PercentProcessorPerformance - [double]$F0.ProcPerf.PercentProcessorPerformance) / $db, 1))) }
    $out.Add(@('cpu.freq_mhz', [double]$F1.ProcPerf.ProcessorFrequency))
  }
  $out
}

# Memória da árvore numa fotografia: PMF (critério do Chrome) e conjunto de trabalho privado por tipo, o processo da
# GPU sempre à parte (bimodal: 88 contra 176–222 MB, docs/sandbox.md), renderers e memória dedicada da GPU.
function Measure-Memoria($F, [hashtable]$Arvore, [string]$Prefixo) {
  $pmf = @{}; $wsp = @{}; $n = @{}; $semGpu = 0; $semGpuWs = 0; $gmem = 0
  foreach ($id in $Arvore.Keys) {
    if (-not $F.Procs.ContainsKey($id)) { continue }
    $tipo = Get-TipoProcesso $Arvore[$id]
    $p = $F.Procs[$id].Pmf / 1MB; $w = $F.Procs[$id].Wsp / 1MB
    $pmf[$tipo] = $(if ($pmf.ContainsKey($tipo)) { $pmf[$tipo] } else { 0 }) + $p
    $wsp[$tipo] = $(if ($wsp.ContainsKey($tipo)) { $wsp[$tipo] } else { 0 }) + $w
    $n[$tipo] = $(if ($n.ContainsKey($tipo)) { $n[$tipo] } else { 0 }) + 1
    if ($tipo -notmatch ':gpu') { $semGpu += $p; $semGpuWs += $w }
    if ($F.GpuMem.ContainsKey($id)) { $gmem += $F.GpuMem[$id] / 1MB }
  }
  $out = New-Object System.Collections.Generic.List[object]
  foreach ($k in $pmf.Keys) {
    $out.Add(@("$Prefixo.pmf.$k", [math]::Round($pmf[$k], 1))); $out.Add(@("$Prefixo.wsp.$k", [math]::Round($wsp[$k], 1)))
    $out.Add(@("$Prefixo.n.$k", $n[$k]))
  }
  $out.Add(@("$Prefixo.pmf.total_sem_gpu", [math]::Round($semGpu, 1))); $out.Add(@("$Prefixo.wsp.total_sem_gpu", [math]::Round($semGpuWs, 1)))
  $out.Add(@("$Prefixo.gpu_dedicada", [math]::Round($gmem, 1)))
  $out
}

# Contadores do Éteris (ETERIS_TEST_CONTADORES) numa janela de tempo: média por segundo de cada contador.
function Measure-Contadores([string]$Ficheiro, [datetime]$InicioProcesso, [datetime]$De, [datetime]$Ate) {
  $out = New-Object System.Collections.Generic.List[object]
  if (-not (Test-Path $Ficheiro)) { return $out }
  $a = ($De - $InicioProcesso).TotalMilliseconds; $b = ($Ate - $InicioProcesso).TotalMilliseconds
  $soma = @{}; $seg = 0
  $linhas = @(Get-Content $Ficheiro)
  # Um contador que existe mas não mexeu na janela vale 0 (ex.: wpf.lotes em repouso, a prova do E1), não «falta».
  foreach ($l in $linhas) {
    if ($l -match "`tinfo`twpf\.lotes=MediaContext") { $soma['wpf.lotes'] = 0 }
    foreach ($m in [regex]::Matches($l, "`t([^=`t]+)=-?\d+")) { if ($l -match "`tseg`t") { $soma[$m.Groups[1].Value] = 0 } }
  }
  foreach ($l in $linhas) {
    $f = $l -split "`t"
    # Um segundo sem nenhum contador a mexer também conta (vale 0 para todos).
    if ($f.Count -lt 2 -or $f[1] -ne 'seg') { continue }
    $t = [double]$f[0]; if ($t -le $a -or $t -gt $b) { continue }
    $seg++
    if ($f.Count -lt 3) { continue }
    foreach ($kv in $f[2..($f.Count - 1)]) { if ($kv -match '^(.+)=(-?\d+)$') { $soma[$Matches[1]] = $(if ($soma.ContainsKey($Matches[1])) { $soma[$Matches[1]] } else { 0 }) + [double]$Matches[2] } }
  }
  if ($seg -gt 0) { foreach ($k in $soma.Keys) { $out.Add(@("contador.$k", [math]::Round($soma[$k] / $seg, 2))) } }
  $out.Add(@('contador.segundos', $seg))
  $out
}

# Linhas de resultado (formato longo, um CSV para tudo; estatistica.py lê-o).
function Write-Resultado([string]$Csv, [int]$Ronda, [string]$Braco, [string]$Cenario, $Linhas) {
  $hora = (Get-Date).ToString('s')
  $rows = foreach ($l in $Linhas) { [pscustomobject]@{ Ronda = $Ronda; Braco = $Braco; Cenario = $Cenario; Metrica = $l[0]; Valor = $l[1]; Hora = $hora } }
  if ($rows) { $rows | Export-Csv -NoTypeInformation -Encoding utf8 -Append $Csv }
  $rows
}
