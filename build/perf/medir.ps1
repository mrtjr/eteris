# Uma passagem da bateria padrão num «braço» (um navegador, ou uma variante do Éteris), com os resultados em formato
# longo num CSV (Ronda, Braco, Cenario, Metrica, Valor, Hora). Ver build/perf/README.md.
#
#   .\medir.ps1 -Braco L1 -Navegador eteris -Exe <pasta da publicação>\Eteris.Browser.exe -Ronda 1
#   .\medir.ps1 -Braco chrome -Navegador chrome -Ronda 1 -PerfilNovo
#   .\medir.ps1 -Braco L1-puro -Navegador eteris -Exe ... -Ambiente ETERIS_TEST_PURO=1 -Cenarios fps,scroll
#   .\medir.ps1 -Braco L1c -Navegador eteris -Exe ... -Extra --eteris-no-sandbox
#
# Cenários (-Cenarios, por esta ordem):
#   arranque   B-ARR: -Arranques lançamentos de start.html (o 1.º é aquecimento, a menos que -PerfilNovo: então é o
#              1.º arranque do perfil e conta como tal); pintura_ms = do Process.Start ao sinal da página depois de 2 rAF.
#              Com -Rastro, o Éteris escreve o rastro das fases (ETERIS_TRACE_STARTUP) em rastos\ (fases.py).
#   memoria    B-MEM C1/C2: 1 aba (mem.html) medida aos 15 s; mais 4 abas; medida aos 45 s e, com -Memoria5min, aos
#              300 s (a purga de fundo do Blink chega 1–4 min depois de esconder). PMF e conjunto de trabalho privado
#              por tipo de processo; o processo da GPU à parte. Firefox (-no-remote não deixa abrir abas de fora): as 5
#              abas abrem-se juntas noutro lançamento.
#   fps, scroll, dano-zero, iframes, fps0, scroll0   B-CPU: a página animada; -EsperaS de espera e -JanelaS de medição.
#              «0» = a mesma página sem os sinais periódicos (sem um fetch por segundo).
#   links      B-CPU: links.html com a roda do rato (cursor parado no centro: a bolha do link muda a cada passo).
#   repouso    1 aba parada (start.html): CPU em repouso.
#   entrada    B-ENTRADA: 100 teclas (SendInput) em entrada.html; mediana e p95 das latências.
#   pedidos    B-CARGA: pedidos.html com 6 ciclos (~300 subrecursos em 20 nomes); CPU do 2.º ao último ciclo.
# No Éteris, os cenários de CPU, entrada e pedidos ligam os contadores (ETERIS_TEST_CONTADORES; custo: um temporizador
# de 1 s e um Stopwatch por pedido; a 0.2.13 não os tem). -SemContadores tira-os.
# Os nomes *.eteris-teste.example (iframes, pedidos) vão para 127.0.0.1: Éteris por ETERIS_TEST_HOST_RULES, Chrome e
# Brave por --host-resolver-rules; o Firefox usa ?local=1 (a mesma origem).
param(
  [Parameter(Mandatory)] [string]$Braco,
  [Parameter(Mandatory)] [ValidateSet('eteris', 'chrome', 'brave', 'firefox')] [string]$Navegador,
  [string]$Exe = '',
  [int]$Ronda = 1,
  [string[]]$Cenarios = @('arranque', 'memoria', 'fps', 'scroll'),
  [string[]]$Ambiente = @(),
  [string[]]$Extra = @(),
  [string]$Perfil = '',
  [switch]$PerfilNovo,
  [switch]$ApagarPerfil,
  [int]$Arranques = 8,
  [int]$EsperaS = 15,
  [int]$JanelaS = 60,
  [switch]$Memoria5min,
  [switch]$Maximizada,
  [switch]$Rastro,
  [switch]$SemContadores,
  [string]$Trabalho = '',
  [int]$Porta = 18473,
  [string]$Resultados = ''
)
. "$PSScriptRoot\comum.ps1"
Initialize-Perf $Trabalho $Porta
if (-not $Exe) { $Exe = Get-ExeNavegador $Navegador }
if (-not $Perfil) { $Perfil = Join-Path $Trabalho "perfis\$Braco" }
$Perfil = [IO.Path]::GetFullPath($Perfil)
if (-not $Resultados) { $Resultados = Join-Path $Trabalho 'resultados\medicoes.csv' }
$Marcador = $Perfil
$Env0 = @{}
foreach ($kv in ($Ambiente -join ';' -split ';' | Where-Object { $_ })) { $k, $v = $kv -split '=', 2; $Env0[$k] = $v }
$Mapa = 'MAP *.eteris-teste.example 127.0.0.1'
$script:ExtraBraco = @($Extra)

if ((Get-Arvore $Marcador).Count) { throw "já há processos com o perfil $Perfil" }
if ($PerfilNovo -and (Test-Path $Perfil)) { [IO.Directory]::Delete($Perfil, $true) }
$script:Lancamento = 0
Start-Servidor

function Lancar([string[]]$Urls, [hashtable]$Mais = @{}, [string[]]$Extra = @()) {
  Assert-SemPausa
  Wait-Fila $Marcador
  $e = @{}; foreach ($k in $Env0.Keys) { $e[$k] = $Env0[$k] }; foreach ($k in $Mais.Keys) { $e[$k] = $Mais[$k] }
  $script:Lancamento++
  Start-Navegador $Navegador $Exe $Perfil $Urls $e ($script:ExtraBraco + $Extra)
}
# Mais uma aba na instância aberta (Éteris, Chrome, Brave: o 2.º lançamento entrega o endereço à instância do perfil).
function AbrirAba([string]$Url) {
  $p = Start-Navegador $Navegador $Exe $Perfil @($Url) $Env0
  $p.WaitForExit(15000) | Out-Null
}
function Resultado([string]$Cenario, $Linhas) { Write-Resultado $Resultados $Ronda $Braco $Cenario $Linhas | Out-Null; $Linhas | ForEach-Object { "  $Cenario $($_[0]) = $($_[1])" } | Out-Host }
function Mediana([double[]]$v) { $s = @($v | Sort-Object); if (-not $s.Count) { return $null }; if ($s.Count % 2) { $s[[int][math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 } }
function Percentil([double[]]$v, [double]$p) { $s = @($v | Sort-Object); if (-not $s.Count) { return $null }; $s[[int][math]::Min($s.Count - 1, [math]::Ceiling($p * $s.Count) - 1)] }
function ContadoresDe([string]$Cen) {
  if ($Navegador -ne 'eteris' -or $SemContadores) { return @{} }
  @{ ETERIS_TEST_CONTADORES = (Join-Path $Trabalho "rastos\$Braco-r$Ronda-$Cen-{pid}.tsv") }
}
function Raiz { $t = Get-Arvore $Marcador; $r = $t.Values | Where-Object { -not $t.ContainsKey([int]$_.ParentProcessId) } | Select-Object -First 1; if ($r) { Get-Process -Id $r.ProcessId -ErrorAction SilentlyContinue } }

foreach ($cen in $Cenarios) {
  "[$Braco r$Ronda] $cen" | Out-Host
  switch -Regex ($cen) {
    '^arranque$' {
      for ($i = 1; $i -le $Arranques; $i++) {
        $tag = "a$Ronda-$i-$(Get-Random)"; $p0 = Get-PosicaoRegisto
        $mais = @{}
        if ($Rastro -and $Navegador -eq 'eteris') { $mais.ETERIS_TRACE_STARTUP = Join-Path $Trabalho "rastos\$Braco-r$Ronda-arranque-$i.tsv" }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Lancar @("$Base/start.html?r=$tag") $mais | Out-Null
        $ok = Wait-Sinal $p0 "/p/painted\?r=$tag" 60000; $ms = $sw.ElapsedMilliseconds
        $valor = $(if ($ok) { $ms } else { -1 })
        # Perfil novo: o 1.º lançamento é o 1.º arranque do perfil (Chrome e Brave ainda sem a semente das variações).
        # Senão é aquecimento e não conta.
        if ($i -eq 1 -and $PerfilNovo) { Resultado 'arranque' @(, @('pintura_ms.primeiro', $valor)) }
        elseif ($i -gt 1) { Resultado 'arranque' @(, @('pintura_ms', $valor)) }
        Start-Sleep -Seconds 3; Close-Arvore $Marcador; Start-Sleep -Milliseconds 1500
      }
    }
    '^memoria$' {
      $tags = 1..5 | ForEach-Object { "m$Ronda-$_-$(Get-Random)" }; $urls = @($tags | ForEach-Object { "$Base/mem.html?r=$_" })
      $p0 = Get-PosicaoRegisto; $clock = [Diagnostics.Stopwatch]::StartNew()
      Lancar @($urls[0]) | Out-Null
      Wait-Sinal $p0 "/p/painted\?r=$($tags[0])" 60000 | Out-Null
      Set-JanelaTeste $Marcador -Maximizada:$Maximizada | Out-Null
      while ($clock.ElapsedMilliseconds -lt 15000) { Start-Sleep -Milliseconds 200 }
      Resultado 'memoria' (Measure-Memoria (Get-Fotografia) (Get-Arvore $Marcador) 'c1.15s')
      if ($Navegador -eq 'firefox') {
        Close-Arvore $Marcador; $p0 = Get-PosicaoRegisto; $clock.Restart()
        Lancar $urls | Out-Null
        Wait-Sinal $p0 "/p/painted\?r=$($tags[4])" 60000 | Out-Null
        Set-JanelaTeste $Marcador -Maximizada:$Maximizada | Out-Null
      }
      else { foreach ($u in $urls[1..4]) { AbrirAba $u; Start-Sleep -Seconds 2 } }
      $loaded = @($tags | Where-Object { (Read-Registo $p0) -match "/mem.html\?r=$_" }).Count
      while ($clock.ElapsedMilliseconds -lt 45000) { Start-Sleep -Milliseconds 200 }
      Resultado 'memoria' ((Measure-Memoria (Get-Fotografia) (Get-Arvore $Marcador) 'c2.45s') + @(, @('c2.abas_carregadas', $loaded)))
      if ($Memoria5min) {
        while ($clock.ElapsedMilliseconds -lt 300000) { Assert-SemPausa; Start-Sleep -Seconds 1 }
        Resultado 'memoria' (Measure-Memoria (Get-Fotografia) (Get-Arvore $Marcador) 'c2.300s')
      }
      Close-Arvore $Marcador
    }
    '^(fps|scroll|dano-zero|iframes|links|repouso)(0?)$' {
      $pagina = @{ fps = 'fps'; scroll = 'scroll'; 'dano-zero' = 'dano-zero'; iframes = 'iframes'; links = 'links'; repouso = 'start' }[$Matches[1]]
      $sem = $Matches[2] -eq '0'
      $tag = "c$Ronda-$cen-$(Get-Random)"
      $q = "r=$tag"; if ($sem) { $q += '&sinal=0' }
      $mais = ContadoresDe $cen; $extra = @()
      if ($pagina -eq 'iframes') {
        if ($Navegador -eq 'eteris') { $mais.ETERIS_TEST_HOST_RULES = $Mapa }
        elseif ($Navegador -eq 'firefox') { $q += '&local=1' }
        else { $extra = @("--host-resolver-rules=$Mapa") }
      }
      Lancar @("$Base/$pagina.html?$q") $mais $extra | Out-Null
      Start-Sleep -Seconds 3
      $h = Set-JanelaTeste $Marcador -Maximizada:$Maximizada
      Start-Sleep -Seconds $EsperaS
      $root = Raiz
      $arvore = Get-Arvore $Marcador
      $pf = Get-PosicaoRegisto; $f0 = Get-Fotografia
      if ($pagina -eq 'links') {
        $passos = [PerfJanela]::Roda($h, $JanelaS * 1000, 50, 120, 40)
        if ($passos -lt 0) { "  AVISO: a janela de teste deixou de estar à frente; passagem descartada" | Out-Host }
      }
      else { Start-Sleep -Seconds $JanelaS }
      $f1 = Get-Fotografia
      $arvore2 = Get-Arvore $Marcador; foreach ($k in $arvore.Keys) { if (-not $arvore2.ContainsKey($k)) { $arvore2[$k] = $arvore[$k] } }
      $linhas = @(Measure-Cpu $f0 $f1 $arvore2)
      $fps = @(Get-ValoresSinal $pf 'fps' $tag)
      if ($fps.Count) { $linhas += , @('fps.mediana', (Mediana $fps)) }
      $titulo = ([PerfJanela]::List() | Where-Object { $_.H -eq $h } | Select-Object -First 1)
      if ($titulo -and $titulo.Title -match '(\d+) fps') { $linhas += , @('fps.titulo', [int]$Matches[1]) }
      if ($pagina -eq 'links') { $linhas += , @('roda.passos', $passos) }
      if ($mais.ContainsKey('ETERIS_TEST_CONTADORES') -and $root) {
        $fich = $mais.ETERIS_TEST_CONTADORES -replace '\{pid\}', $root.Id
        Close-Arvore $Marcador
        $linhas += @(Measure-Contadores $fich $root.StartTime $f0.Hora $f1.Hora)
      }
      else { Close-Arvore $Marcador }
      $linhas += , @('processos', $arvore2.Count)
      Resultado $cen $linhas
    }
    '^entrada$' {
      $tag = "e$Ronda-$(Get-Random)"; $p0 = Get-PosicaoRegisto
      Lancar @("$Base/entrada.html?r=$tag") (ContadoresDe $cen) | Out-Null
      Wait-Sinal $p0 "/p/painted\?r=$tag" 60000 | Out-Null
      $h = Set-JanelaTeste $Marcador -Maximizada:$Maximizada
      Start-Sleep -Seconds 3
      [PerfJanela]::Clique($h, 300, 200) | Out-Null
      $pf = Get-PosicaoRegisto; $enviadas = 0
      for ($i = 0; $i -lt 100; $i++) { if ([PerfJanela]::Tecla($h, 0x41)) { $enviadas++ } else { break }; Start-Sleep -Milliseconds 120 }
      Start-Sleep -Seconds 2
      $q = @(Get-ValoresSinal $pf 'quadro' $tag); $ev = @(Get-ValoresSinal $pf 'evento' $tag)
      Close-Arvore $Marcador
      Resultado 'entrada' @(@('teclas', $enviadas), @('quadro.n', $q.Count), @('quadro.mediana', (Mediana $q)), @('quadro.p95', (Percentil $q 0.95)),
        @('evento16.n', $ev.Count), @('evento16.mediana', (Mediana $ev)), @('evento16.p95', (Percentil $ev 0.95)))
    }
    '^pedidos$' {
      $tag = "p$Ronda-$(Get-Random)"; $ciclos = 6
      $mais = ContadoresDe $cen; $extra = @()
      $url = "http://www.eteris-teste.example:$Porta/pedidos.html?r=$tag&ciclos=$ciclos"
      if ($Navegador -eq 'eteris') { $mais.ETERIS_TEST_HOST_RULES = $Mapa }
      elseif ($Navegador -eq 'firefox') { $url = "$Base/pedidos.html?r=$tag&ciclos=$ciclos&local=1" }
      else { $extra = @("--host-resolver-rules=$Mapa") }
      $p0 = Get-PosicaoRegisto
      Lancar @($url) $mais $extra | Out-Null
      Wait-Sinal $p0 "/p/carregado\?r=$tag&v=\d+&c=1&" 60000 | Out-Null
      $root = Raiz; $arvore = Get-Arvore $Marcador; $f0 = Get-Fotografia
      Wait-Sinal $p0 "/p/carregado\?r=$tag&v=\d+&c=$ciclos&" 120000 | Out-Null
      $f1 = Get-Fotografia
      $cargas = @(Get-ValoresSinal $p0 'carregado' $tag)
      $linhas = @(Measure-Cpu $f0 $f1 $arvore)
      $linhas += , @('load_ms.mediana', (Mediana @($cargas | Select-Object -Skip 1)))
      $linhas += , @('ciclos', $cargas.Count)
      if ($mais.ContainsKey('ETERIS_TEST_CONTADORES') -and $root) {
        $fich = $mais.ETERIS_TEST_CONTADORES -replace '\{pid\}', $root.Id
        Close-Arvore $Marcador
        $linhas += @(Measure-Contadores $fich $root.StartTime $f0.Hora $f1.Hora)
      }
      else { Close-Arvore $Marcador }
      Resultado 'pedidos' $linhas
    }
    default { throw "cenário desconhecido: $cen" }
  }
}
# Chrome, Brave e Firefox: o perfil temporário apaga-se no fim (regra 3.3); o do Éteris fica para a ronda seguinte.
if ($ApagarPerfil -and (Test-Path $Perfil)) { Close-Arvore $Marcador; [IO.Directory]::Delete($Perfil, $true) }
