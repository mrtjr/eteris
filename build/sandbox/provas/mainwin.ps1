param([int]$ProcessId)
# Maior janela visível de nível superior do processo (o MainWindowHandle do .NET pode ser uma dica ou a camada).
Add-Type -Namespace MW -Name W -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern int GetWindowLong(System.IntPtr h, int i);
public struct RECT { public int Left, Top, Right, Bottom; }
'@ -ErrorAction SilentlyContinue
$script:best = [IntPtr]::Zero; $script:area = 0
$cb = [MW.W+EnumProc]{ param($h, $l)
    $wpid = 0; [MW.W]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
    if ($wpid -eq $ProcessId -and [MW.W]::IsWindowVisible($h)) {
        # Ignora a camada transparente (WS_EX_LAYERED) que o Éteris mostra por cima da página.
        if (([MW.W]::GetWindowLong($h, -20) -band 0x80000) -ne 0) { return $true }
        $r = New-Object MW.W+RECT; [MW.W]::GetWindowRect($h, [ref]$r) | Out-Null
        $a = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
        if ($a -gt $script:area) { $script:area = $a; $script:best = $h }
    }
    $true }
[MW.W]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
$script:best
