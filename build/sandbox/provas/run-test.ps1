param([Parameter(Mandatory)][string]$Exe, [string]$Url = "", [string]$Out, [int]$Wait = 10, [switch]$KeepOpen,
  [scriptblock]$Before, [string]$Profile = "profile-test", [string]$Work = "", [string[]]$ExtraArgs = @())
# Abre o Éteris de teste num perfil de teste (<Work>\<Profile>, nunca o do utilizador), espera, opcionalmente captura
# (<Work>\<Out>) e fecha com WM_CLOSE. Silencioso: ETERIS_TEST_MUTE=1.
. "$PSScriptRoot\comum.ps1"
$S = Get-ProvasWork $Work
Test-ProvasPausa $S
# Fila do ecrã: enquanto houver uma instância de teste de OUTRO perfil desta pasta aberta, espera (até 45 min), para
# duas provas ao vivo não se atropelarem.
$deadline = (Get-Date).AddMinutes(45)
while ((Get-Date) -lt $deadline) {
  $others = Get-ProvasOthers $S $Profile
  if (-not $others.Count) { break }
  "aguardando: outra prova ao vivo em curso ($($others.Count) Éteris)" | Out-Host
  Start-Sleep 15
}
$env:ETERIS_TEST_MUTE = '1'
$arguments = @("--user-data-dir=$S\$Profile") + $ExtraArgs
if ($Url) { $arguments += $Url }
$p = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru
Start-Sleep $Wait
$p.Refresh()
Add-Type -Namespace RT -Name W -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);' -ErrorAction SilentlyContinue
[RT.W]::ShowWindow((& "$PSScriptRoot\mainwin.ps1" -ProcessId $p.Id), 9) | Out-Null
Start-Sleep 1
if ($Before) { & $Before $p }
if ($Out) { & "$PSScriptRoot\shot.ps1" -ProcessId $p.Id -Out "$S\$Out" -Work $S | Out-Null }
"pid=$($p.Id) title=[$($p.MainWindowTitle)]"
if (-not $KeepOpen -and -not $p.HasExited) { & "$PSScriptRoot\close.ps1" -ProcessId $p.Id }
