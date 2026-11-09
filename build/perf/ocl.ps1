# B-OCL (D4) e restauro da janela (D11): a página animada (ocl.html) continua a gastar CPU quando a janela não aparece?
# E quanto custa voltar? Por estado, -JanelaS segundos: CPU por tipo de processo, dwm.exe, GPU, fps e visibilidade
# vistos pela página (sinais «fps» e «vis») e, no Éteris, os contadores (wpf.lotes…).
#
#   .\ocl.ps1 -Braco L1 -Navegador eteris -Exe <publicação>\Eteris.Browser.exe
#   .\ocl.ps1 -Braco L1-20abas -Navegador eteris -Exe ... -Abas 20 -Estados visivel,minimizada,restauro
#   .\ocl.ps1 -Braco chrome -Navegador chrome -Estados visivel,minimizada,coberta
#
# Estados (-Estados, por esta ordem):
#   visivel     a janela à vista;
#   minimizada  SW_MINIMIZE (o CefSharp põe então a página a 0x0 — leitura 7 do plano);
#   coberta     uma janela opaca maximizada por cima (criada por este script, fechada no fim do estado);
#   restauro    da janela minimizada a SW_RESTORE: CPU nos 2 s seguintes (D11; fotogramas errados: abas\troca.ps1);
#   manual      com o dono à frente do PC: o script espera por Enter, depois -AtrasoManualS segundos (tempo para pôr a
#               janela noutra área de trabalho ou bloquear a sessão com Win+L) e mede durante -JanelaS. Mudar de área
#               de trabalho e bloquear a sessão são ações do dono, nunca do script.
# Os resultados vão para o CSV de medir.ps1 (cenário ocl.<estado>).
param(
  [Parameter(Mandatory)] [string]$Braco,
  [Parameter(Mandatory)] [ValidateSet('eteris', 'chrome', 'brave', 'firefox')] [string]$Navegador,
  [string]$Exe = '',
  [string[]]$Estados = @('visivel', 'minimizada', 'coberta', 'restauro'),
  [string[]]$Ambiente = @(),
  [string[]]$Extra = @(),
  [int]$Abas = 1,
  [int]$Ronda = 1,
  [int]$EsperaS = 10,
  [int]$JanelaS = 30,
  [int]$AtrasoManualS = 10,
  [string]$Trabalho = '',
  [int]$Porta = 18473,
  [string]$Resultados = ''
)
. "$PSScriptRoot\comum.ps1"
Initialize-Perf $Trabalho $Porta
Add-Type -AssemblyName System.Windows.Forms
if (-not ([System.Management.Automation.PSTypeName]'PerfCobertura').Type) {
  Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @'
using System.Threading;
using System.Windows.Forms;
// Uma janela opaca maximizada por cima de tudo, num fio próprio com o seu ciclo de mensagens.
public static class PerfCobertura
{
    static Form _form;
    public static void Mostrar()
    {
        var pronta = new ManualResetEvent(false);
        var t = new Thread(() =>
        {
            _form = new Form { FormBorderStyle = FormBorderStyle.None, WindowState = FormWindowState.Maximized, TopMost = true,
                               ShowInTaskbar = false, BackColor = System.Drawing.Color.FromArgb(40, 40, 40), Text = "cobertura de teste" };
            _form.Shown += (s, e) => pronta.Set();
            Application.Run(_form);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        pronta.WaitOne(5000);
    }
    public static void Fechar()
    {
        var f = _form;
        if (f != null && !f.IsDisposed) f.Invoke((MethodInvoker)(() => f.Close()));
        _form = null;
    }
}
'@
}
if (-not $Exe) { $Exe = Get-ExeNavegador $Navegador }
if (-not $Resultados) { $Resultados = Join-Path $Trabalho 'resultados\medicoes.csv' }
$perfil = Join-Path $Trabalho "perfis\$Braco"; $marcador = $perfil
$env0 = @{}
foreach ($kv in ($Ambiente -join ';' -split ';' | Where-Object { $_ })) { $k, $v = $kv -split '=', 2; $env0[$k] = $v }
if ($Navegador -eq 'eteris' -and -not $env0.ContainsKey('ETERIS_TEST_CONTADORES')) { $env0.ETERIS_TEST_CONTADORES = Join-Path $Trabalho "rastos\$Braco-r$Ronda-ocl-{pid}.tsv" }
if ((Get-Arvore $marcador).Count) { throw "já há processos com o perfil $perfil" }
Start-Servidor
Assert-SemPausa; Wait-Fila $marcador
$tag = "o$Ronda-$(Get-Random)"; $p0 = Get-PosicaoRegisto
Start-Navegador $Navegador $Exe $perfil @("$Base/ocl.html?r=$tag") $env0 $Extra | Out-Null
Wait-Sinal $p0 "/p/vis\?r=$tag" 60000 | Out-Null
$h = Set-JanelaTeste $marcador
if ($Abas -gt 1 -and $Navegador -ne 'firefox') {
  # Abas a mais, paradas, ATRÁS da animada; no fim volta-se à 1.ª (Ctrl+1).
  for ($i = 2; $i -le $Abas; $i++) { (Start-Navegador $Navegador $Exe $perfil @("$Base/mem.html?r=$tag-$i") $env0 $Extra).WaitForExit(15000) | Out-Null; Start-Sleep -Milliseconds 500 }
  Start-Sleep -Seconds 2
  [PerfJanela]::Frente($h) | Out-Null
  [System.Windows.Forms.SendKeys]::SendWait('^1')
}
Start-Sleep -Seconds $EsperaS
$arv = Get-Arvore $marcador
$raizCim = $arv.Values | Where-Object { -not $arv.ContainsKey([int]$_.ParentProcessId) } | Sort-Object CreationDate | Select-Object -First 1
function Medir([string]$Estado, [int]$Segundos) {
  $arvore = Get-Arvore $marcador; $pf = Get-PosicaoRegisto; $f0 = Get-Fotografia
  Start-Sleep -Seconds $Segundos
  $f1 = Get-Fotografia
  $linhas = @(Measure-Cpu $f0 $f1 $arvore)
  $fps = @(Get-ValoresSinal $pf 'fps' $tag)
  $linhas += , @('fps.sinais', $fps.Count)
  if ($fps.Count) { $linhas += , @('fps.media', [math]::Round(($fps | Measure-Object -Average).Average, 1)) }
  $vis = @(Get-ValoresSinal $p0 'vis' $tag)
  if ($vis.Count) { $linhas += , @('pagina.visivel', $vis[-1]) }
  Write-Resultado $Resultados $Ronda $Braco "ocl.$Estado" $linhas | Out-Null
  $linhas | ForEach-Object { "  ocl.$Estado $($_[0]) = $($_[1])" } | Out-Host
  [pscustomobject]@{ F0 = $f0; F1 = $f1 }
}
$janelas = @{}
try {
  foreach ($e in $Estados) {
    Assert-SemPausa
    switch ($e) {
      'visivel' { [PerfJanela]::ShowWindow($h, 9) | Out-Null; Start-Sleep -Seconds 3; $janelas[$e] = Medir $e $JanelaS }
      'minimizada' { [PerfJanela]::ShowWindow($h, 6) | Out-Null; Start-Sleep -Seconds 3; $janelas[$e] = Medir $e $JanelaS }
      'coberta' {
        [PerfJanela]::ShowWindow($h, 9) | Out-Null; Start-Sleep -Seconds 2
        [PerfCobertura]::Mostrar(); Start-Sleep -Seconds 3
        try { $janelas[$e] = Medir $e $JanelaS } finally { [PerfCobertura]::Fechar() }
      }
      'restauro' {
        [PerfJanela]::ShowWindow($h, 6) | Out-Null; Start-Sleep -Seconds 5
        $arvore = Get-Arvore $marcador; $f0 = Get-Fotografia
        [PerfJanela]::ShowWindow($h, 9) | Out-Null
        Start-Sleep -Seconds 2
        $f1 = Get-Fotografia
        $linhas = @(Measure-Cpu $f0 $f1 $arvore) + @(, @('abas', $Abas))
        $janelas[$e] = [pscustomobject]@{ F0 = $f0; F1 = $f1 }
        Write-Resultado $Resultados $Ronda $Braco 'ocl.restauro' $linhas | Out-Null
        $linhas | ForEach-Object { "  ocl.restauro $($_[0]) = $($_[1])" } | Out-Host
      }
      'manual' {
        Read-Host 'Põe a janela de teste no estado a medir (outra área de trabalho, ou bloqueia a sessão depois do Enter) e carrega Enter' | Out-Null
        Start-Sleep -Seconds $AtrasoManualS
        $janelas[$e] = Medir $e $JanelaS
      }
      default { throw "estado desconhecido: $e" }
    }
  }
}
finally {
  [PerfJanela]::ShowWindow($h, 9) | Out-Null
  Close-Arvore $marcador
}
# Contadores do Éteris por estado (o ficheiro só fica completo depois de o processo sair).
if ($env0.ContainsKey('ETERIS_TEST_CONTADORES') -and $raizCim) {
  $fich = $env0.ETERIS_TEST_CONTADORES -replace '\{pid\}', $raizCim.ProcessId
  foreach ($e in $janelas.Keys) {
    $linhas = @(Measure-Contadores $fich $raizCim.CreationDate $janelas[$e].F0.Hora $janelas[$e].F1.Hora)
    Write-Resultado $Resultados $Ronda $Braco "ocl.$e" $linhas | Out-Null
    $linhas | ForEach-Object { "  ocl.$e $($_[0]) = $($_[1])" } | Out-Host
  }
}
