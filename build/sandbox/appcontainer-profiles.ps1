<#
  Perfis de AppContainer do serviço de rede com sandbox (docs/sandbox.md, D9). Com NetworkServiceSandbox o Chromium cria
  um perfil por caminho do executável: nome «cr.sb.net» + SHA1 (UTF-8, maiúsculas) do caminho tal como o processo
  principal foi arrancado (a forma curta 8.3 dá outro perfil), com a pasta %LOCALAPPDATA%\Packages\<nome> e a chave
  HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings\<SID>. Numa
  instalação do Velopack o gancho de desinstalação apaga-os (NetworkSandbox.OnUninstalling); as cópias à mão (testes,
  pastas portáteis) deixam-nos ficar.
  Sem -Exe lista todos os perfis cr.sb.net* do utilizador. Com -Exe mostra os que pertencem a esses executáveis (caminho
  dado, forma longa e forma curta) e, com -Remove, apaga SÓ esses (DeleteAppContainerProfile).
  Uso: appcontainer-profiles.ps1 [-Exe <caminho do exe> ...] [-Remove]
#>
param([string[]]$Exe = @(), [switch]$Remove)
$ErrorActionPreference = 'Stop'
if ($Remove -and -not $Exe.Count) { throw '-Remove precisa de -Exe (só se apagam os perfis de executáveis indicados)' }
if (-not ('EterisAc.Native' -as [type])) {
  Add-Type -Namespace EterisAc -Name Native -MemberDefinition @'
[DllImport("userenv.dll", CharSet=CharSet.Unicode)] public static extern int DeleteAppContainerProfile(string name);
[DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern int GetLongPathNameW(string p, System.Text.StringBuilder b, int n);
[DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern int GetShortPathNameW(string p, System.Text.StringBuilder b, int n);
'@
}
function ProfileName([string]$path) {
  $sha = [Security.Cryptography.SHA1]::Create()
  try { 'cr.sb.net' + (($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($path)) | ForEach-Object { $_.ToString('X2') }) -join '') }
  finally { $sha.Dispose() }
}
function Forms([string]$path) {
  $path
  foreach ($long in $true, $false) {
    $b = New-Object Text.StringBuilder 1024
    $n = if ($long) { [EterisAc.Native]::GetLongPathNameW($path, $b, 1024) } else { [EterisAc.Native]::GetShortPathNameW($path, $b, 1024) }
    if ($n -gt 0 -and $n -lt 1024) { $b.ToString() }
  }
}

$mappings = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings'
$packages = Join-Path $env:LOCALAPPDATA 'Packages'
$all = @{}
foreach ($k in Get-ChildItem -LiteralPath $mappings -ErrorAction SilentlyContinue) {
  $moniker = (Get-ItemProperty -LiteralPath $k.PSPath -ErrorAction SilentlyContinue).Moniker
  if ($moniker -like 'cr.sb.net*') { $all[$moniker] = $k.PSChildName }
}
foreach ($d in Get-ChildItem -LiteralPath $packages -Directory -Filter 'cr.sb.net*' -ErrorAction SilentlyContinue) {
  if (-not $all.ContainsKey($d.Name)) { $all[$d.Name] = '' }
}

$owned = @{}
foreach ($e in $Exe) { foreach ($f in Forms $e | Select-Object -Unique) { $owned[(ProfileName $f)] = $f } }

$rows = foreach ($name in ($all.Keys + $owned.Keys | Select-Object -Unique | Sort-Object)) {
  if ($Exe.Count -and -not $owned.ContainsKey($name)) { continue }
  [pscustomobject]@{
    Perfil = $name
    SID = $all[$name]
    Pasta = Test-Path -LiteralPath (Join-Path $packages $name)
    Exe = $owned[$name]
  }
}
$rows | Where-Object { $_.SID -or $_.Pasta -or -not $Exe.Count } | Format-Table -AutoSize | Out-String -Width 400
if ($Remove) {
  foreach ($r in $rows | Where-Object { $_.SID -or $_.Pasta }) {
    $hr = [EterisAc.Native]::DeleteAppContainerProfile($r.Perfil)
    $left = Test-Path -LiteralPath (Join-Path $packages $r.Perfil)
    "apagado $($r.Perfil) (HRESULT 0x$($hr.ToString('X8'))); pasta ainda existe: $left"
  }
}
