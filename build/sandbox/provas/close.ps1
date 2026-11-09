param([int]$ProcessId)
# Fecha a janela do Éteris de teste (WM_CLOSE, o fecho normal: guarda a sessão e o marcador da rede de segurança) e
# espera que o processo saia; só se não sair em 10 s é terminado.
Add-Type -Namespace RTC -Name W -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);' -ErrorAction SilentlyContinue
$p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
if ($null -eq $p) { "já tinha saído"; exit 0 }
[RTC.W]::PostMessage((& "$PSScriptRoot\mainwin.ps1" -ProcessId $ProcessId), 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
if (-not $p.WaitForExit(10000)) { Stop-Process -Id $ProcessId -Force; "terminado" } else { "fechado" }
