<#
  Feed NuGet local com o CefSharp do Eteris (156.0.150-eteris.N): parte dos pacotes OFICIAIS 156.0.150 (cache global
  do NuGet) e troca so o que o patch muda, para win-x64:
    CefSharp.Common.NETCore: runtimes/win-x64/lib/net6.0/CefSharp.Core.dll, CefSharp.Core.Runtime.dll (+ .pdb) e
                             ref/net6.0/CefSharp.Core.dll, CefSharp.Core.Runtime.dll (montagens de referencia);
    CefSharp.Wpf.NETCore:    so a versao e a dependencia [156.0.150-eteris.N] do Common (binarios oficiais).
  x86 e arm64 ficam os oficiais (sem WindowsSandboxInfo): o Eteris so publica win-x64.
  Remove a assinatura do repositorio (.signature.p7s), que deixa de ser valida.
  Uso: pack-local.ps1 -Source <clone compilado por build-cefsharp.cmd> -Feed <pasta> -N <numero>
#>
param([Parameter(Mandatory)] [string]$Source, [Parameter(Mandatory)] [string]$Feed, [Parameter(Mandatory)] [int]$N)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$Source = [IO.Path]::GetFullPath($Source); $Feed = [IO.Path]::GetFullPath($Feed)
$official = '156.0.150'; $version = "$official-eteris.$N"
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
New-Item -ItemType Directory -Force -Path $Feed | Out-Null

$built = @{
  'runtimes/win-x64/lib/net6.0/CefSharp.Core.dll'         = 'CefSharp.Core\bin.netcore\x64\Release\net8.0\CefSharp.Core.dll'
  'runtimes/win-x64/lib/net6.0/CefSharp.Core.Runtime.dll' = 'CefSharp.Core.Runtime\bin.netcore\x64\Release\CefSharp.Core.Runtime.dll'
  'runtimes/win-x64/lib/net6.0/CefSharp.Core.Runtime.pdb' = 'CefSharp.Core.Runtime\bin.netcore\x64\Release\CefSharp.Core.Runtime.pdb'
  'ref/net6.0/CefSharp.Core.dll'                          = 'CefSharp.Core\bin.netcore\x64\Release\net8.0\CefSharp.Core.dll'
  'ref/net6.0/CefSharp.Core.Runtime.dll'                  = 'CefSharp.Core.Runtime.RefAssembly\bin.netcore\x64\Release\net8.0\CefSharp.Core.Runtime.dll'
}

function Repack([string]$Id, [hashtable]$Replace, [hashtable]$DepVersions) {
  $lower = $Id.ToLowerInvariant()
  $src = Join-Path $cache "$lower\$official\$lower.$official.nupkg"
  if (-not (Test-Path -LiteralPath $src)) { throw "pacote oficial nao esta na cache: $src" }
  $dst = Join-Path $Feed "$lower.$version.nupkg"
  Copy-Item -LiteralPath $src -Destination $dst -Force
  $zip = [IO.Compression.ZipFile]::Open($dst, [IO.Compression.ZipArchiveMode]::Update)
  try {
    foreach ($e in @($zip.Entries)) { if ($e.FullName -eq '.signature.p7s') { $e.Delete() } }
    foreach ($name in $Replace.Keys) {
      $file = Join-Path $Source $Replace[$name]
      if (-not (Test-Path -LiteralPath $file)) { throw "nao compilado: $file" }
      $old = $zip.GetEntry($name); if (-not $old) { throw "entrada inexistente no pacote: $name" }
      $old.Delete()
      [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, $name) | Out-Null
    }
    $nuspec = @($zip.Entries | Where-Object { $_.FullName -like '*.nuspec' -and $_.FullName -notlike '*/*' })[0]
    $r = New-Object IO.StreamReader($nuspec.Open()); $xml = $r.ReadToEnd(); $r.Close()
    $xml = $xml -replace "<version>$([regex]::Escape($official))</version>", "<version>$version</version>"
    foreach ($d in $DepVersions.Keys) {
      $xml = $xml -replace "(id=`"$([regex]::Escape($d))`" version=`")\[$([regex]::Escape($official))\]", "`${1}[$version]"
    }
    if ($xml -notmatch "<version>$([regex]::Escape($version))</version>") { throw "versao nao substituida em $Id" }
    $name = $nuspec.FullName; $nuspec.Delete()
    $w = New-Object IO.StreamWriter($zip.CreateEntry($name).Open(), (New-Object Text.UTF8Encoding($false))); $w.Write($xml); $w.Close()
  } finally { $zip.Dispose() }
  "$Id $version -> $dst"
}

Repack 'CefSharp.Common.NETCore' $built @{}
Repack 'CefSharp.Wpf.NETCore' @{} @{ 'CefSharp.Common.NETCore' = $true }
