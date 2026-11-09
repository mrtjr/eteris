param([int]$ProcessId, [string[]]$Keys, [int]$DelayMs = 700, [string]$Work = "")
# Envia teclas (SendKeys) ao Éteris de teste, uma sequência de cada vez, e pára se outra aplicação estiver em primeiro
# plano (a janela principal ou a camada transparente do Éteris, do mesmo processo, contam como primeiro plano). Nunca
# escreve PIN nem palavras-passe: o Windows Hello e a CredUI são do utilizador.
. "$PSScriptRoot\comum.ps1"
Test-ProvasPausa (Get-ProvasWork $Work)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Drive -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
'@ -ErrorAction SilentlyContinue
[Drive.W]::SetProcessDPIAware() | Out-Null
$h = & "$PSScriptRoot\mainwin.ps1" -ProcessId $ProcessId
function FgPid { $p = 0; [Drive.W]::GetWindowThreadProcessId([Drive.W]::GetForegroundWindow(), [ref]$p) | Out-Null; $p }
if ((FgPid) -ne $ProcessId) {
  # F24 (sem efeito) desbloqueia o SetForegroundWindow para um processo que não recebeu a última entrada.
  [Drive.W]::keybd_event(0x87, 0, 0, [UIntPtr]::Zero); [Drive.W]::keybd_event(0x87, 0, 2, [UIntPtr]::Zero)
  [Drive.W]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 600
}
foreach ($k in $Keys) {
  if ((FgPid) -ne $ProcessId) { "PARADO: o Éteris de teste não está em primeiro plano"; exit 1 }
  [System.Windows.Forms.SendKeys]::SendWait($k)
  Start-Sleep -Milliseconds $DelayMs
}
"enviadas $($Keys.Count) sequência(s) de teclas"
