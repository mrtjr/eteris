param([int]$ProcessId, [string]$Out, [switch]$Screen, [string]$Work = "")
# Captura a janela principal do processo (PrintWindow) ou a área do ecrã que ela ocupa (-Screen: inclui a camada).
# Nunca captura janelas com segredos (o Windows Hello, a CredUI, o cofre a mostrar uma palavra-passe).
. "$PSScriptRoot\comum.ps1"
Test-ProvasPausa (Get-ProvasWork $Work)
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Shot -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
public struct RECT { public int Left, Top, Right, Bottom; }
'@ -ErrorAction SilentlyContinue
[Shot.W]::SetProcessDPIAware() | Out-Null
$h = & "$PSScriptRoot\mainwin.ps1" -ProcessId $ProcessId
$r = New-Object Shot.W+RECT
[Shot.W]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
if ($Screen) {
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hgt))
} else {
    $hdc = $g.GetHdc()
    [Shot.W]::PrintWindow($h, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
}
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
