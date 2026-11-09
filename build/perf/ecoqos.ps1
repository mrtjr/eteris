# D7: EcoQoS, prioridade e prioridade de memória dos processos de um navegador de teste, com a aba animada à vista,
# escondida (outra aba à frente) e com a janela minimizada. Só lê (GetPriorityClass, GetProcessInformation); com os
# renderers em sandbox o Windows pode recusar abrir o processo: medir em L1c (-Extra --eteris-no-sandbox).
#
#   .\ecoqos.ps1 -Braco L1c -Navegador eteris -Exe <publicação>\Eteris.Browser.exe -Extra --eteris-no-sandbox
#   .\ecoqos.ps1 -Braco chrome -Navegador chrome
param(
  [Parameter(Mandatory)] [string]$Braco,
  [Parameter(Mandatory)] [ValidateSet('eteris', 'chrome', 'brave')] [string]$Navegador,
  [string]$Exe = '',
  [string[]]$Extra = @(),
  [int]$Ronda = 1,
  [int]$EsperaS = 8,
  [string]$Trabalho = '',
  [int]$Porta = 18473,
  [string]$Resultados = ''
)
. "$PSScriptRoot\comum.ps1"
Initialize-Perf $Trabalho $Porta
Add-Type -AssemblyName System.Windows.Forms
if (-not ([System.Management.Automation.PSTypeName]'PerfQos').Type) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PerfQos
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetProcessInformation(IntPtr h, int cls, ref Throttling info, int size);
    [DllImport("kernel32.dll")] static extern bool GetProcessInformation(IntPtr h, int cls, ref uint info, int size);
    [StructLayout(LayoutKind.Sequential)] public struct Throttling { public uint Version, ControlMask, StateMask; }

    // «prioridade|ecoqos|memoria»; «sem acesso» se o processo não deixar abrir.
    public static string Ler(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return "sem acesso|sem acesso|sem acesso";
        try
        {
            uint pc = GetPriorityClass(h);
            string prio = pc == 0x40 ? "baixa" : pc == 0x4000 ? "abaixo" : pc == 0x20 ? "normal" : pc == 0x8000 ? "acima" : pc == 0x80 ? "alta" : pc == 0 ? "?" : pc.ToString("x");
            var t = new Throttling { Version = 1 };
            // ProcessPowerThrottling = 4; bit 1 = EXECUTION_SPEED (EcoQoS).
            string eco = GetProcessInformation(h, 4, ref t, Marshal.SizeOf(typeof(Throttling)))
                ? ((t.ControlMask & 1) == 0 ? "sistema" : (t.StateMask & 1) != 0 ? "ligado" : "desligado") : "?";
            uint mem = 0;
            // ProcessMemoryPriority = 0: 1 muito baixa … 5 normal.
            string memoria = GetProcessInformation(h, 0, ref mem, 4) ? mem.ToString() : "?";
            return prio + "|" + eco + "|" + memoria;
        }
        finally { CloseHandle(h); }
    }
}
'@
}
if (-not $Exe) { $Exe = Get-ExeNavegador $Navegador }
if (-not $Resultados) { $Resultados = Join-Path $Trabalho 'resultados\medicoes.csv' }
$perfil = Join-Path $Trabalho "perfis\$Braco"
if ((Get-Arvore $perfil).Count) { throw "já há processos com o perfil $perfil" }
Start-Servidor
Assert-SemPausa; Wait-Fila $perfil
$tag = "q$Ronda-$(Get-Random)"; $p0 = Get-PosicaoRegisto
Start-Navegador $Navegador $Exe $perfil @("$Base/fps.html?r=$tag") @{} $Extra | Out-Null
Wait-Sinal $p0 "/p/fps\?r=$tag" 60000 | Out-Null
(Start-Navegador $Navegador $Exe $perfil @("$Base/start.html?r=$tag-2") @{} $Extra).WaitForExit(15000) | Out-Null
$h = Set-JanelaTeste $perfil
function Ler([string]$Estado) {
  Start-Sleep -Seconds $EsperaS
  $arv = Get-Arvore $perfil
  $linhas = @()
  foreach ($id in $arv.Keys) {
    $tipo = Get-TipoProcesso $arv[$id]
    $p, $e, $m = ([PerfQos]::Ler($id)) -split '\|'
    "  $Estado pid=$id $tipo prioridade=$p ecoqos=$e memoria=$m" | Out-Host
    $linhas += , @("$tipo.$id.prioridade", $p); $linhas += , @("$tipo.$id.ecoqos", $e); $linhas += , @("$tipo.$id.memoria", $m)
  }
  Write-Resultado $Resultados $Ronda $Braco "ecoqos.$Estado" $linhas | Out-Null
}
try {
  # A 2.ª aba (parada) ficou à frente: Ctrl+1 volta à animada.
  [PerfJanela]::Frente($h) | Out-Null; [System.Windows.Forms.SendKeys]::SendWait('^1'); Ler 'animada-a-vista'
  [PerfJanela]::Frente($h) | Out-Null; [System.Windows.Forms.SendKeys]::SendWait('^2'); Ler 'animada-escondida'
  [PerfJanela]::ShowWindow($h, 6) | Out-Null; Ler 'minimizada'
}
finally { [PerfJanela]::ShowWindow($h, 9) | Out-Null; Close-Arvore $perfil }
