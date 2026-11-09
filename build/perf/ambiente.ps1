# Regista o ambiente da máquina antes de uma medição (plano 0.2.14, secção 3.3), SÓ a ler: adaptadores gráficos e
# frequência da tela, HAGS, otimizações para jogos em janela, plano de energia, processador, memória, Windows e a
# versão de cada navegador. Nada pede administrador. Escreve resultados\ambiente-<data>.json na pasta de trabalho.
# O adaptador que cada navegador usa de facto lê-se no chrome://gpu (Éteris, Chrome, Brave) e no about:support.
param([string]$Trabalho = '', [string[]]$Eteris = @())
$ErrorActionPreference = 'Stop'
if (-not $Trabalho) { $Trabalho = Join-Path ([IO.Path]::GetTempPath()) 'eteris-perf' }
New-Item -ItemType Directory -Force (Join-Path $Trabalho 'resultados') | Out-Null
function Ler($script) { try { & $script } catch { "indisponível: $($_.Exception.Message)" } }
function Versao([string]$exe) { if ($exe -and (Test-Path $exe)) { (Get-Item $exe).VersionInfo.ProductVersion } else { $null } }
$amb = [ordered]@{
  data = (Get-Date).ToString('s')
  windows = Ler { $os = Get-CimInstance Win32_OperatingSystem; "$($os.Caption) $($os.Version) (build $($os.BuildNumber))" }
  processador = Ler { (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim() }
  nucleos = [Environment]::ProcessorCount
  memoria_gb = Ler { [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1) }
  adaptadores = Ler { @(Get-CimInstance Win32_VideoController | ForEach-Object { [ordered]@{ nome = $_.Name; controlador = $_.DriverVersion; hz = $_.CurrentRefreshRate; resolucao = "$($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)" } }) }
  # HwSchMode 2 = agendamento de GPU acelerado por hardware ligado; 1 = desligado.
  hags = Ler { (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Name HwSchMode -ErrorAction Stop).HwSchMode }
  otimizacoes_janela = Ler { (Get-ItemProperty 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences' -Name DirectXUserGlobalSettings -ErrorAction Stop).DirectXUserGlobalSettings }
  plano_energia = Ler { (powercfg /getactivescheme) -join ' ' }
  corrente = Ler { $b = @(Get-CimInstance Win32_Battery); if ($b.Count) { "bateria $($b[0].EstimatedChargeRemaining)% (estado $($b[0].BatteryStatus))" } else { 'sem bateria (tomada)' } }
  chrome = Versao "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
  brave = Versao "$env:ProgramFiles\BraveSoftware\Brave-Browser\Application\brave.exe"
  firefox = Ler { $f = @("$env:ProgramFiles\Mozilla Firefox\firefox.exe", "$env:LOCALAPPDATA\Microsoft\WindowsApps\firefox.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1; Versao $f }
  eteris = @($Eteris | ForEach-Object { [ordered]@{ exe = $_; versao = Versao $_ } })
}
$out = Join-Path $Trabalho "resultados\ambiente-$((Get-Date).ToString('yyyyMMdd-HHmmss')).json"
$amb | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $out
"ambiente: $out"
