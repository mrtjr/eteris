# Bateria da troca de abas (B-ABAS) no Éteris, no Chrome ou no Brave, com os mesmos casos e o mesmo critério
# (analisar.py). Cada sessão é curta (< 5 min); uma sessão repete-se (até -Tentativas) só com as fases que ainda não
# têm >= 10 capturas válidas (o Brave perde às vezes o primeiro plano a meio). Resultados em resultados\abas\.
#
#   .\bateria.ps1 -Navegador eteris -Exe <publicação>\Eteris.Browser.exe -Prefixo L1
#   .\bateria.ps1 -Navegador brave -Prefixo brave -Sessoes reais     (WWt, WWn e WG: a comparação pausada, D-13)
#
# Sessões: locais (LD, LL, LN: páginas locais claras e escuras e nova aba), novas (TL, TD: Ctrl+T; XL: Ctrl+W e
# Ctrl+Shift+T), frias (LD10: troca para uma aba escondida há > 8 s), reais (WG GitHub↔Wikipédia, WWt e WWn
# Wikipédia↔Wikipédia por Ctrl+Tab e por Ctrl+número, WN página↔nova aba), reais-frias (H10 e H30: abas escondidas
# há > 10 s e > 30 s). As reais dependem da rede e dos sites (anúncios, testes A/B): comparar na mesma noite.
param(
  [Parameter(Mandatory)] [ValidateSet('eteris', 'chrome', 'brave')] [string]$Navegador,
  [string]$Exe = '',
  [string]$Prefixo = '',
  [string[]]$Sessoes = @('locais', 'novas', 'frias', 'reais', 'reais-frias'),
  [string]$Tema = 'dark',
  [bool]$Mica = $true,
  [int]$Tentativas = 4,
  [string]$Trabalho = '',
  [int]$Porta = 18473
)
$ErrorActionPreference = 'Stop'
if (-not $Prefixo) { $Prefixo = $Navegador }
$B = "http://127.0.0.1:$Porta/abas"
$L = "$B/light.html"; $L2 = "$B/light2.html"; $D = "$B/dark.html"
$W1 = 'https://pt.wikipedia.org/wiki/Brasil'; $W2 = 'https://pt.wikipedia.org/wiki/Lisboa'; $W3 = 'https://pt.wikipedia.org/wiki/Portugal'
$W4 = 'https://pt.wikipedia.org/wiki/Rio_de_Janeiro'; $GH = 'https://github.com/'
$P = $Prefixo
$abrir = 'key:^t|sleep:2500|key:^1|sleep:2000|key:^2|sleep:2000|key:^3|sleep:2000|key:^4|sleep:1500'
$casos = [ordered]@{
  'locais' = @{ Urls = @($D, $L, $L2); Init = $abrir; Fases = @(
      @{ Tag = "$P-LD"; Pre = 'key:^1'; Act = 'key:^{TAB}'; Act2 = 'key:^+{TAB}' },
      @{ Tag = "$P-LL"; Pre = 'key:^2'; Act = 'key:^{TAB}'; Act2 = 'key:^+{TAB}' },
      @{ Tag = "$P-LN"; Pre = 'key:^3'; Act = 'key:^{TAB}'; Act2 = 'key:^+{TAB}' }) }
  'novas' = @{ Urls = @($D, $L, $D, $L2); Init = 'key:^2|sleep:1500|key:^3|sleep:1500|key:^4|sleep:1500'; Fases = @(
      @{ Tag = "$P-TL"; Pre = 'key:^2'; Act = 'key:^t'; Undo = 'key:^w' },
      @{ Tag = "$P-TD"; Pre = 'key:^1'; Act = 'key:^t'; Undo = 'key:^w' },
      @{ Tag = "$P-XL"; Pre = 'key:^3'; Act = 'key:^w'; Undo = 'key:^+t|sleep:1500'; Reps = 10 }) }
  'frias' = @{ Urls = @($L, $D); Init = 'key:^2|sleep:1500|key:^1|sleep:1500'; Fases = @(
      @{ Tag = "$P-LD10"; Act = 'key:^{TAB}'; Gap = 11; Reps = 10 }) }
  'reais' = @{ Urls = @($GH, $W1, $W2); Init = $abrir; Fases = @(
      @{ Tag = "$P-WG"; Pre = 'key:^1'; Act = 'key:^{TAB}'; Act2 = 'key:^+{TAB}' },
      @{ Tag = "$P-WWt"; Pre = 'key:^2'; Act = 'key:^{TAB}'; Act2 = 'key:^+{TAB}' },
      @{ Tag = "$P-WWn"; Pre = 'key:^2'; Act = 'key:^3'; Act2 = 'key:^2' },
      @{ Tag = "$P-WN"; Pre = 'key:^3'; Act = 'key:^{TAB}'; Act2 = 'key:^+{TAB}' }) }
  'reais-frias' = @{ Urls = @($W1, $W2, $W3, $W4); Init = 'key:^2|sleep:2000|key:^3|sleep:2000|key:^4|sleep:2000|key:^1|sleep:1500'; Fases = @(
      @{ Tag = "$P-H10"; Act = 'key:^{TAB}'; Gap = 11; Reps = 10 },
      @{ Tag = "$P-H30"; Act = 'key:^{TAB}'; Gap = 11; Reps = 13; Skip = 3 }) }
}
$topo = $(if ($Navegador -eq 'eteris') { 23 } else { 21 })
$saida = Join-Path $(if ($Trabalho) { $Trabalho } else { Join-Path ([IO.Path]::GetTempPath()) 'eteris-perf' }) 'resultados\abas'
function Capturas([string]$tag) {
  $f = Join-Path $saida "$tag.total.txt"; if (-not (Test-Path $f)) { return 0 }
  $l = Get-Content $f | Select-String 'TOTAL' | Select-Object -Last 1
  if ($l) { [int]([regex]::Match($l.Line, '"caps": (\d+)').Groups[1].Value) } else { 0 }
}
foreach ($nome in $Sessoes) {
  $c = $casos[$nome]; if (-not $c) { throw "sessão desconhecida: $nome" }
  for ($t = 1; $t -le $Tentativas; $t++) {
    $faltam = @($c.Fases | Where-Object { (Capturas $_.Tag) -lt 10 })
    if (-not $faltam.Count) { break }
    "$nome, tentativa $t : $(($faltam | ForEach-Object { $_.Tag }) -join ',')"
    $a = @{ Sessao = "$P-$nome"; Navegador = $Navegador; Urls = $c.Urls; Init = $c.Init; Fases = $faltam; Topo = $topo; Tema = $Tema; Mica = $Mica; Porta = $Porta }
    if ($Exe) { $a.Exe = $Exe }
    if ($Trabalho) { $a.Trabalho = $Trabalho }
    & "$PSScriptRoot\sessao.ps1" @a
  }
}
