<#
  Lista os processos filhos de um processo principal do motor (CEF/Chromium) e, para cada um, o tipo
  (--type / --utility-sub-type), o nivel de integridade do token, se e AppContainer, se o token e restrito
  e se a linha de comando tem --no-sandbox. So le (PROCESS_QUERY_LIMITED_INFORMATION + TOKEN_QUERY).
  Uso: sandbox-check.ps1 -ProcessId <pid do processo principal> [-Json]
#>
param([Parameter(Mandatory)] [int]$ProcessId, [switch]$Json)
$ErrorActionPreference = 'Stop'
if (-not ('SbxCheck.Tok' -as [type])) {
Add-Type -Namespace SbxCheck -Name Tok -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError=true)] public static extern System.IntPtr OpenProcess(uint a, bool i, int pid);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(System.IntPtr h);
[DllImport("advapi32.dll", SetLastError=true)] public static extern bool OpenProcessToken(System.IntPtr p, uint a, out System.IntPtr t);
[DllImport("advapi32.dll", SetLastError=true)] public static extern bool GetTokenInformation(System.IntPtr t, int c, System.IntPtr b, int l, out int r);
[DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern bool ConvertSidToStringSidW(System.IntPtr sid, out System.IntPtr s);
[DllImport("advapi32.dll")] public static extern bool IsTokenRestricted(System.IntPtr t);
[DllImport("kernel32.dll")] public static extern System.IntPtr LocalFree(System.IntPtr p);
public static string Info(int pid) {
  System.IntPtr p = OpenProcess(0x1000, false, pid);
  if (p == System.IntPtr.Zero) return "open-failed:" + Marshal.GetLastWin32Error();
  try {
    System.IntPtr t;
    if (!OpenProcessToken(p, 0x8, out t)) return "token-failed:" + Marshal.GetLastWin32Error();
    try {
      System.IntPtr b = Marshal.AllocHGlobal(256); int r;
      try {
        string il = "?";
        if (GetTokenInformation(t, 25, b, 256, out r)) {
          System.IntPtr sid = Marshal.ReadIntPtr(b); System.IntPtr s;
          if (ConvertSidToStringSidW(sid, out s)) { il = Marshal.PtrToStringUni(s); LocalFree(s); }
        }
        string ac = "?";
        if (GetTokenInformation(t, 29, b, 4, out r)) ac = Marshal.ReadInt32(b) != 0 ? "1" : "0";
        return il + "|" + ac + "|" + (IsTokenRestricted(t) ? "1" : "0");
      } finally { Marshal.FreeHGlobal(b); }
    } finally { CloseHandle(t); }
  } finally { CloseHandle(p); }
}
'@
}
$names = @{ 'S-1-16-0' = 'Untrusted'; 'S-1-16-4096' = 'Low'; 'S-1-16-8192' = 'Medium'; 'S-1-16-8448' = 'Medium+';
            'S-1-16-12288' = 'High'; 'S-1-16-16384' = 'System' }
$all = @(Get-CimInstance Win32_Process)
$kids = @{}
foreach ($p in $all) { $kids[[int]$p.ParentProcessId] += @($p) }
$queue = New-Object System.Collections.Queue; $queue.Enqueue($ProcessId); $rows = @()
$root = $all | Where-Object { $_.ProcessId -eq $ProcessId }
$list = @($root)
while ($queue.Count) { $id = $queue.Dequeue(); foreach ($c in @($kids[[int]$id])) { if ($c) { $list += $c; $queue.Enqueue([int]$c.ProcessId) } } }
foreach ($p in $list) {
  if (-not $p) { continue }
  $cl = [string]$p.CommandLine
  $type = if ($cl -match '--type=([\w-]+)') { $Matches[1] } else { 'browser' }
  if ($cl -match '--utility-sub-type=([\w.\-]+)') { $type += ':' + ($Matches[1] -replace '^.*\.', '') }
  $parts = [SbxCheck.Tok]::Info([int]$p.ProcessId) -split '\|'
  $il = if ($names.ContainsKey($parts[0])) { $names[$parts[0]] } else { $parts[0] }
  $rows += [pscustomobject]@{
    Pid = [int]$p.ProcessId; Name = $p.Name; Type = $type; Integrity = $il
    AppContainer = if ($parts.Count -gt 1) { $parts[1] } else { '?' }
    Restricted = if ($parts.Count -gt 2) { $parts[2] } else { '?' }
    NoSandboxFlag = [bool]($cl -match '--no-sandbox')
  }
}
if ($Json) { $rows | ConvertTo-Json } else { $rows | Format-Table -AutoSize | Out-String -Width 200 }
