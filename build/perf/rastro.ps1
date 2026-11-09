# Rastro do Chromium pelo DevTools (D5: onde está a CPU; D9: memory-infra), num navegador de teste JÁ aberto com a
# porta de depuração em 127.0.0.1: Éteris com ETERIS_TEST_DEVTOOLS_PORT=<porta> (medir.ps1 -Ambiente), Chrome e Brave
# com --remote-debugging-port=<porta> (medir.ps1 -Extra; só com o perfil temporário). Grava um JSON que se abre no
# Perfetto UI (ui.perfetto.dev: uma página web que processa o ficheiro no próprio navegador; nada a instalar).
#
#   .\rastro.ps1 -PortaDevTools 9333 -Segundos 10 -Saida rastos\L1-fps.json
#   .\rastro.ps1 -PortaDevTools 9333 -Memoria -Saida rastos\L1-memoria.json        (D9: um dump «detailed»)
#
# Categorias por omissão (plano, D5): toplevel, viz, cc, gpu, benchmark, mais a amostragem de CPU do V8 e as tarefas
# do agendador. Só o domínio Tracing do navegador: nenhuma página é tocada.
param(
  [Parameter(Mandatory)] [int]$PortaDevTools,
  [int]$Segundos = 10,
  [switch]$Memoria,
  [string[]]$Categorias = @('toplevel', 'viz', 'cc', 'gpu', 'benchmark', 'blink', 'renderer.scheduler', 'v8', 'disabled-by-default-devtools.timeline.frame', 'disabled-by-default-viz.quads'),
  [Parameter(Mandatory)] [string]$Saida
)
$ErrorActionPreference = 'Stop'
$ver = Invoke-RestMethod "http://127.0.0.1:$PortaDevTools/json/version"
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ws.Options.KeepAliveInterval = [TimeSpan]::FromSeconds(10)
$ws.ConnectAsync([Uri]$ver.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
$script:id = 0
$buf = New-Object byte[] (4MB)
function Enviar([string]$Metodo, [string]$Params = '{}') {
  $script:id++
  $b = [Text.Encoding]::UTF8.GetBytes("{`"id`":$script:id,`"method`":`"$Metodo`",`"params`":$Params}")
  $ws.SendAsync([ArraySegment[byte]]$b, 'Text', $true, [Threading.CancellationToken]::None).Wait()
  $script:id
}
function Receber {
  $sb = New-Object Text.StringBuilder
  do { $r = $ws.ReceiveAsync([ArraySegment[byte]]$buf, [Threading.CancellationToken]::None).Result; [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count)) } until ($r.EndOfMessage)
  $sb.ToString()
}
$pedacos = New-Object System.Collections.Generic.List[string]
# Junta os eventos de cada Tracing.dataCollected sem os desserializar (podem ser dezenas de MB).
function Guardar([string]$m) {
  $prefixo = '{"method":"Tracing.dataCollected","params":{"value":['
  if ($m.StartsWith($prefixo)) { $c = $m.Substring($prefixo.Length); $c = $c.Substring(0, $c.LastIndexOf(']')); if ($c) { $pedacos.Add($c) } }
}
function Esperar([int]$Id, [string]$Evento = '') {
  while ($true) {
    $m = Receber; Guardar $m
    if ($Id -and $m.StartsWith("{`"id`":$Id,")) { return $m }
    if ($Evento -and $m.StartsWith("{`"method`":`"$Evento`"")) { return $m }
  }
}
try {
  if ($Memoria) {
    $cfg = '{"traceConfig":{"includedCategories":["disabled-by-default-memory-infra"],"memoryDumpConfig":{"triggers":[]}},"transferMode":"ReportEvents"}'
    Esperar (Enviar 'Tracing.start' $cfg) | Out-Null
    $r = Esperar (Enviar 'Tracing.requestMemoryDump' '{"deterministic":true,"levelOfDetail":"detailed"}')
    "dump: $r"
  }
  else {
    $cats = ($Categorias | ForEach-Object { '"' + $_ + '"' }) -join ','
    Esperar (Enviar 'Tracing.start' "{`"traceConfig`":{`"includedCategories`":[$cats],`"enableSampling`":true},`"transferMode`":`"ReportEvents`"}") | Out-Null
    "a gravar $Segundos s…"
    $fim = (Get-Date).AddSeconds($Segundos)
    # Os eventos só chegam depois do Tracing.end; esperar sem ler.
    while ((Get-Date) -lt $fim) { Start-Sleep -Milliseconds 200 }
  }
  Enviar 'Tracing.end' | Out-Null
  Esperar 0 'Tracing.tracingComplete' | Out-Null
}
finally { $ws.Dispose() }
$dir = Split-Path -Parent ([IO.Path]::GetFullPath($Saida)); if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Saida), '{"traceEvents":[' + ($pedacos -join ',') + ']}', (New-Object Text.UTF8Encoding $false))
"rastro: $Saida ($($pedacos.Count) blocos)"
