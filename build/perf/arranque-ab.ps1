# A/B do arranque com as variantes intercaladas lançamento a lançamento (B-ARR; ordem invertida nas rondas pares:
# A,B / B,A / A,B…), cada lançamento com o rastro das fases. Para efeitos de poucos ms (E10, E12) julgam-se as marcas
# (fases.py) com >= 20 rondas, não o total. A 1.ª ronda é de aquecimento e fica fora do CSV.
#
#   .\arranque-ab.ps1 -Variantes "base=C:\pub-a\Eteris.Browser.exe","exp=C:\pub-b\Eteris.Browser.exe" -Rondas 20
#   .\arranque-ab.ps1 -Variantes "a=...exe","b=...exe" -Ambiente "b:DOTNET_TieredPGO=0" -Extra "b:--eteris-no-sandbox"
#
# Cada variante tem o seu perfil de teste (perfis\arranque-<variante>). Resultado: resultados\arranque-<etiqueta>.csv
# (Ronda, Variante, Ms, Rastro) e as medianas no fim; fases.py "rastos\arranque-<etiqueta>-<variante>-*.tsv".
param(
  [Parameter(Mandatory)] [string[]]$Variantes,
  [string[]]$Ambiente = @(),
  [string[]]$Extra = @(),
  [int]$Rondas = 8,
  [string]$Etiqueta = 'ab',
  [int]$EsperaS = 3,
  [string]$Trabalho = '',
  [int]$Porta = 18473
)
. "$PSScriptRoot\comum.ps1"
Initialize-Perf $Trabalho $Porta
$defs = [ordered]@{}
foreach ($v in ($Variantes -join ',' -split ',')) { $n, $exe = $v -split '=', 2; $defs[$n] = @{ Exe = $exe; Env = @{}; Extra = @() } }
foreach ($e in ($Ambiente -join ';' -split ';' | Where-Object { $_ })) { $n, $kv = $e -split ':', 2; $k, $val = $kv -split '=', 2; $defs[$n].Env[$k] = $val }
foreach ($e in ($Extra -join ';' -split ';' | Where-Object { $_ })) { $n, $a = $e -split ':', 2; $defs[$n].Extra += $a }
$nomes = @($defs.Keys)
Start-Servidor
$csv = Join-Path $Trabalho "resultados\arranque-$Etiqueta.csv"
$res = @()
for ($r = 0; $r -le $Rondas; $r++) {
  $ordem = $(if ($r % 2) { $a = @($nomes); [array]::Reverse($a); $a } else { $nomes })
  foreach ($n in $ordem) {
    Assert-SemPausa
    $d = $defs[$n]; $perfil = Join-Path $Trabalho "perfis\arranque-$n"
    Wait-Fila $perfil
    $rastro = Join-Path $Trabalho "rastos\arranque-$Etiqueta-$n-r$r.tsv"; [IO.File]::Delete($rastro)
    $env1 = @{ ETERIS_TRACE_STARTUP = $rastro }; foreach ($k in $d.Env.Keys) { $env1[$k] = $d.Env[$k] }
    $tag = "$n$r-$(Get-Random)"; $p0 = Get-PosicaoRegisto
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Start-Navegador 'eteris' $d.Exe $perfil @("$Base/start.html?r=$tag") $env1 $d.Extra | Out-Null
    $ok = Wait-Sinal $p0 "/p/painted\?r=$tag" 60000; $ms = $sw.ElapsedMilliseconds
    "r$r $n $(if ($ok) { "$ms ms" } else { 'sem pintura' })" | Out-Host
    if ($r -gt 0) { $res += [pscustomobject]@{ Ronda = $r; Variante = $n; Ms = $(if ($ok) { $ms } else { -1 }); Rastro = $rastro } }
    Start-Sleep -Seconds $EsperaS
    Close-Arvore $perfil
    Start-Sleep -Milliseconds 1500
  }
}
$res | Export-Csv -NoTypeInformation -Encoding utf8 $csv
$res | Group-Object Variante | ForEach-Object {
  $v = @($_.Group | Where-Object { $_.Ms -gt 0 } | ForEach-Object { $_.Ms } | Sort-Object)
  "{0,-12} mediana={1} ms  ({2})" -f $_.Name, $(if ($v.Count) { $v[[int][math]::Floor(($v.Count - 1) / 2)] } else { '-' }), ($v -join ' ')
}
"CSV: $csv"
