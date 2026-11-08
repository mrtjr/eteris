<#
  Junta a uma pasta de publicacao do Eteris o arranque pelo bootstrap da CEF (docs/sandbox.md, passo D):
    <Name>.exe = bootstrap.exe do runtime do motor em uso (build/cef-codecs/out/runtime; o mesmo commit do libcef.dll)
    <Name>.dll = DLL cliente (build/sandbox/client/build.cmd com o mesmo <Name>)
  Para as experiencias D3/D4 o nome e EterisSB (a DLL nativa arranca o Eteris.Browser.dll .NET de sempre); no passo D6
  passa a ser Eteris.Browser (o .NET muda para Eteris.App).
  Verifica que o libcef.dll da publicacao e o do runtime (SHA256) e que o bootstrap nao traz o instalador da CEF.
  Uso: assemble-bootstrap.ps1 -Publish <pasta> -Runtime <runtime> -ClientDll <dll> [-Name EterisSB]
#>
param([Parameter(Mandatory)] [string]$Publish, [Parameter(Mandatory)] [string]$Runtime,
      [Parameter(Mandatory)] [string]$ClientDll, [string]$Name = 'EterisSB')
$ErrorActionPreference = 'Stop'
$Publish = [IO.Path]::GetFullPath($Publish); $Runtime = [IO.Path]::GetFullPath($Runtime); $ClientDll = [IO.Path]::GetFullPath($ClientDll)
foreach ($f in (Join-Path $Publish 'libcef.dll'), (Join-Path $Runtime 'libcef.dll'), (Join-Path $Runtime 'bootstrap.exe'), $ClientDll) {
  if (-not (Test-Path -LiteralPath $f)) { throw "falta $f" }
}
$a = (Get-FileHash -LiteralPath (Join-Path $Publish 'libcef.dll')).Hash
$b = (Get-FileHash -LiteralPath (Join-Path $Runtime 'libcef.dll')).Hash
if ($a -ne $b) { throw "o libcef.dll da publicacao ($a) nao e o do runtime ($b): o bootstrap tem de ser da mesma compilacao" }
# O instalador embutido do bootstrap (recurso RCDATA CEF_INSTALLER_CONFIG no exe ou na DLL) baixaria a CEF oficial,
# sem os codecs. O nome aparece no codigo do bootstrap: procura-se o RECURSO, nao o texto.
if (-not ('SbxRes.R' -as [type])) {
  Add-Type -Namespace SbxRes -Name R -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern System.IntPtr LoadLibraryExW(string f, System.IntPtr h, uint flags);
[DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr FindResourceW(System.IntPtr m, string name, System.IntPtr type);
[DllImport("kernel32.dll")] public static extern bool FreeLibrary(System.IntPtr m);
'@
}
foreach ($f in (Join-Path $Runtime 'bootstrap.exe'), $ClientDll) {
  $m = [SbxRes.R]::LoadLibraryExW($f, [IntPtr]::Zero, 0x22)   # LOAD_LIBRARY_AS_DATAFILE | AS_IMAGE_RESOURCE
  if ($m -eq [IntPtr]::Zero) { throw "nao abre como recurso: $f" }
  try { if ([SbxRes.R]::FindResourceW($m, 'CEF_INSTALLER_CONFIG', [IntPtr]10) -ne [IntPtr]::Zero) { throw "$f tem o recurso CEF_INSTALLER_CONFIG" } }
  finally { [SbxRes.R]::FreeLibrary($m) | Out-Null }
}
Copy-Item -LiteralPath (Join-Path $Runtime 'bootstrap.exe') -Destination (Join-Path $Publish "$Name.exe") -Force
Copy-Item -LiteralPath $ClientDll -Destination (Join-Path $Publish "$Name.dll") -Force
"OK: $Publish\$Name.exe (bootstrap, libcef $($a.Substring(0, 12))...) + $Name.dll"
