<#
  Peças do sandbox que não vêm do NuGet, compiladas a partir do repositório (docs/sandbox.md, «Como publicar com o
  sandbox»; docs/motor.md, «Publicação»). A publicação normal (build/publish.ps1) usa-as por omissão.

    1. CefSharp 156.0.150 com o patch v2 (build/cefsharp/windows-sandbox-info.patch): clone limpo da tag v156.0.150
       (commit verificado) em build/cefsharp/src, prepare-source.ps1, build-cefsharp.cmd e pack-local.ps1 para o feed
       NuGet local build/cefsharp/feed (156.0.150-eteris.N), com build/cefsharp/feed/eteris-cefsharp.json (N, commits,
       SHA256 do patch e dos binários trocados), que o publish.ps1 lê.
    2. DLL cliente do bootstrap (build/sandbox/client) contra a distribuição mínima do PRÓPRIO motor (make_distrib de
       build/cef-codecs: build/cef-codecs/out/distrib/<cef.distrib do manifest.json>), conferida contra o runtime (commit
       da CEF e SHA256 do libcef.dll), para build/sandbox/client/out/Release/Eteris.Browser.dll.
    3. Verificação: pacotes do feed (versão, dependência, assinatura do repositório removida, WindowsSandboxInfo nos
       binários trocados, nada na cache do NuGet com a mesma versão e outros binários), DLL cliente (verify-client.ps1).
    4. Testes com o CefSharp do feed (o modo COM sandbox: ETERIS_CEFSHARP_SANDBOX; a CI corre o modo sem, com o CefSharp
       oficial), confirmando que o CefSharp restaurado e carregado é o do feed.

  Tudo o que produz fica em pastas ignoradas pelo git: build/cefsharp/src, build/cefsharp/feed,
  build/sandbox/client/out e build/sandbox/out (registos e testes).

  Requisitos: git, CMake, Build Tools do Visual Studio 2022 com C++/CLI (VC.CLI.Support) e o SDK do .NET 9 (além do 10);
  o motor próprio já compilado (build/cef-codecs/out/runtime e a distribuição em build/cef-codecs/out/distrib, ou
  -CefDistrib); os pacotes oficiais CefSharp 156.0.150 e cef.sdk 156.0.15 na cache do NuGet (o restauro normal do
  Éteris põe-nos lá). Só o clone do CefSharp vai à rede (GitHub), a menos que -CefSharpSource seja uma pasta local.

  Uso: powershell -ExecutionPolicy Bypass -File build\sandbox\build-all.ps1
         [-CefSharpSource <URL ou pasta com o git do CefSharp>] [-CefDistrib <cef_binary_*_windows64_minimal>]
         [-N <n>] [-SkipCefSharp] [-SkipClient] [-SkipTests]
  -N: o N de 156.0.150-eteris.N. Por omissão, um a mais do que o maior já existente na cache do NuGet
  (a cache guarda cada versão para sempre: reutilizar um N com binários novos daria os antigos ao restaurar).
#>
param(
  [string]$CefSharpSource = 'https://github.com/cefsharp/CefSharp.git',
  [string]$CefDistrib = '',
  [int]$N = 0,
  [switch]$SkipCefSharp,
  [switch]$SkipClient,
  [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$CefSharpTag = 'v156.0.150'
$CefSharpCommit = 'f4cd42eb4043d67a4a8153a4c6b66a315a2da0b3'   # git rev-parse v156.0.150^{commit}
$Official = '156.0.150'

# Caminhos curtos (JNIOR~1) e acentos: só caminhos completos e -LiteralPath.
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$cefsharpDir = Join-Path $repo 'build\cefsharp'
$work = Join-Path $cefsharpDir 'src'
$feed = Join-Path $cefsharpDir 'feed'
$feedManifest = Join-Path $feed 'eteris-cefsharp.json'
$clientOut = Join-Path $repo 'build\sandbox\client\out'
$clientDll = Join-Path $clientOut 'Release\Eteris.Browser.dll'
$outDir = Join-Path $repo 'build\sandbox\out'
$logs = Join-Path $outDir 'logs'
$runtime = Join-Path $repo 'build\cef-codecs\out\runtime'
$nugetCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$vcvars = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat'
New-Item -ItemType Directory -Force -Path $logs | Out-Null

function Step([string]$Text) { Write-Host ''; Write-Host "== $Text" }
function Fail([string]$Text) { throw "build-all: $Text" }
function Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# Programa externo com a saída num registo (o stderr do git/msbuild não pode virar exceção do PowerShell 5.1).
function Run([string]$Log, [string]$Exe, [string[]]$Arguments) {
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try {
    & $Exe @Arguments 2>&1 | ForEach-Object { "$_" } | Out-File -LiteralPath $Log -Append -Encoding utf8
    $code = $LASTEXITCODE
  } finally { $ErrorActionPreference = $old }
  if ($code -ne 0) {
    Get-Content -LiteralPath $Log -Tail 25 | ForEach-Object { Write-Host "  $_" }
    Fail "$([IO.Path]::GetFileName($Exe)) saiu com $code (registo: $Log)"
  }
}

function Remove-Tree([string]$Path) {
  if (Test-Path -LiteralPath $Path) {
    # O .git tem ficheiros só de leitura; o rmdir do cmd trata-os e aguenta caminhos compridos.
    & cmd.exe /d /c "rmdir /s /q `"$Path`"" 2>&1 | Out-Null
    if (Test-Path -LiteralPath $Path) { Fail "não consegui apagar $Path" }
  }
}

# Texto ASCII dentro de um binário (os nomes dos membros vão no heap #Strings dos metadados, em UTF-8).
function Contains-Text([byte[]]$Bytes, [string]$Text) {
  [Text.Encoding]::GetEncoding(28591).GetString($Bytes).Contains($Text)
}

function Read-ZipEntry($Zip, [string]$Name) {
  $e = $Zip.GetEntry($Name)
  if (-not $e) { return $null }
  $ms = New-Object IO.MemoryStream
  $s = $e.Open()
  try { $s.CopyTo($ms) } finally { $s.Dispose() }
  return ,$ms.ToArray()
}

function Highest-N {
  $max = 0
  foreach ($dir in @((Join-Path $nugetCache 'cefsharp.common.netcore'), (Join-Path $nugetCache 'cefsharp.wpf.netcore'))) {
    if (Test-Path -LiteralPath $dir) {
      foreach ($d in Get-ChildItem -LiteralPath $dir -Directory) {
        if ($d.Name -match "^$([regex]::Escape($Official))-eteris\.(\d+)$") { $max = [Math]::Max($max, [int]$Matches[1]) }
      }
    }
  }
  $max
}

# --- Requisitos -------------------------------------------------------------------------------------------------
Step 'Requisitos'
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'manifest.json'))) { Fail "falta o motor próprio em $runtime (build/cef-codecs/build.ps1)" }
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'bootstrap.exe'))) { Fail "o runtime do motor não tem o bootstrap.exe (build/cef-codecs/build.ps1)" }
$motor = Get-Content -LiteralPath (Join-Path $runtime 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($motor.cefsharp -ne $Official) { Fail "o motor é para o CefSharp $($motor.cefsharp), este script compila o $Official" }
foreach ($tool in 'git', 'cmake', 'dotnet') { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "falta $tool no PATH" } }
if (-not $SkipCefSharp) {
  if (-not (Test-Path -LiteralPath $vcvars)) { Fail "faltam as Build Tools do Visual Studio 2022 ($vcvars)" }
  if (-not (@(& dotnet --list-sdks) -match '^9\.')) { Fail 'falta o SDK do .NET 9 (o C++/CLI do CefSharp compila com ele: build/cefsharp/README.md)' }
  foreach ($id in 'cefsharp.common.netcore', 'cefsharp.wpf.netcore') {
    if (-not (Test-Path -LiteralPath (Join-Path $nugetCache "$id\$Official\$id.$Official.nupkg"))) { Fail "falta o pacote oficial $id $Official na cache do NuGet (dotnet restore do Éteris)" }
  }
}
"motor: CEF $($motor.cef.version) ($($motor.cef.commit.Substring(0, 12))), CefSharp $Official"

# --- 1. CefSharp com o patch v2 ---------------------------------------------------------------------------------
if ($SkipCefSharp) {
  Step 'CefSharp: saltado (-SkipCefSharp), uso o feed existente'
  if (-not (Test-Path -LiteralPath $feedManifest)) { Fail "sem $feedManifest`: corra sem -SkipCefSharp" }
  $built = Get-Content -LiteralPath $feedManifest -Raw -Encoding UTF8 | ConvertFrom-Json
  $N = [int]$built.n
} else {
  Step "CefSharp $CefSharpTag com o patch v2"
  $log = Join-Path $logs 'cefsharp.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
  Remove-Tree $work
  "clone de $CefSharpSource ($CefSharpTag) para $work"
  Run $log 'git' @('-c', 'advice.detachedHead=false', 'clone', '--quiet', '--depth', '1', '--branch', $CefSharpTag, $CefSharpSource, $work)
  $head = (& git -C $work rev-parse HEAD).Trim()
  if ($head -ne $CefSharpCommit) { Fail "a tag $CefSharpTag aponta para $head, esperado $CefSharpCommit" }
  # O SourceLink do CefSharp.Core.Runtime precisa de um origin no GitHub (sem ele não gera o sourcelink.json e a ligação
  # falha com LNK1104): com uma pasta local como fonte, o origin passa a ser o oficial (só metadados, não vai à rede).
  Run $log 'git' @('-C', $work, 'remote', 'set-url', 'origin', 'https://github.com/cefsharp/CefSharp.git')
  Run $log 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $cefsharpDir 'prepare-source.ps1'), '-Source', $work)
  "a compilar (C++/CLI; registo $log)"
  $t = Get-Date
  Run $log 'cmd.exe' @('/d', '/c', "`"$(Join-Path $cefsharpDir 'build-cefsharp.cmd')`" `"$work`"")
  "compilado em $([int]((Get-Date) - $t).TotalSeconds) s"

  if ($N -le 0) { $N = (Highest-N) + 1 }
  $version = "$Official-eteris.$N"
  if (Test-Path -LiteralPath $feed) { Get-ChildItem -LiteralPath $feed -File | Remove-Item }
  Run $log 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $cefsharpDir 'pack-local.ps1'), '-Source', $work, '-Feed', $feed, '-N', "$N")

  # Verificação dos pacotes do feed.
  Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
  $files = @()
  foreach ($id in 'CefSharp.Common.NETCore', 'CefSharp.Wpf.NETCore') {
    $nupkg = Join-Path $feed "$($id.ToLowerInvariant()).$version.nupkg"
    if (-not (Test-Path -LiteralPath $nupkg)) { Fail "pack-local.ps1 não produziu $nupkg" }
    $zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
      if ($zip.GetEntry('.signature.p7s')) { Fail "$id ainda tem a assinatura do repositório" }
      $nuspec = [Text.Encoding]::UTF8.GetString((Read-ZipEntry $zip "$id.nuspec"))
      if ($nuspec -notmatch "<version>$([regex]::Escape($version))</version>") { Fail "$id sem a versão $version no nuspec" }
      if ($id -eq 'CefSharp.Wpf.NETCore' -and $nuspec -notmatch "id=`"CefSharp.Common.NETCore`" version=`"\[$([regex]::Escape($version))\]`"") {
        Fail "$id não depende do CefSharp.Common.NETCore [$version]"
      }
      if ($id -eq 'CefSharp.Common.NETCore') {
        foreach ($entry in 'runtimes/win-x64/lib/net6.0/CefSharp.Core.dll', 'runtimes/win-x64/lib/net6.0/CefSharp.Core.Runtime.dll',
                           'ref/net6.0/CefSharp.Core.dll', 'ref/net6.0/CefSharp.Core.Runtime.dll') {
          $bytes = Read-ZipEntry $zip $entry
          if (-not $bytes) { Fail "$id sem $entry" }
          if (-not (Contains-Text $bytes 'WindowsSandboxInfo')) { Fail "$entry sem WindowsSandboxInfo: o patch não entrou" }
          $sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
          $files += [ordered]@{ package = $id; entry = $entry; sha256 = $sha }
        }
      }
    } finally { $zip.Dispose() }
    "$id $version verificado"
  }

  # A cache do NuGet ganha sempre ao feed: a mesma versão com outros binários seria restaurada com os antigos.
  foreach ($f in $files) {
    $cached = Join-Path $nugetCache ("cefsharp.common.netcore\$version\" + $f.entry.Replace('/', '\'))
    if ((Test-Path -LiteralPath $cached) -and (Sha256 $cached) -ne $f.sha256) {
      Fail "a cache do NuGet já tem $version com outros binários ($cached): corra de novo sem -N (ou com -N $((Highest-N) + 1))"
    }
  }

  $built = [ordered]@{
    n               = $N
    version         = $version
    cefsharp_tag    = $CefSharpTag
    cefsharp_commit = $CefSharpCommit
    patch           = 'build/cefsharp/windows-sandbox-info.patch'
    patch_sha256    = Sha256 (Join-Path $cefsharpDir 'windows-sandbox-info.patch')
    built_utc       = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    files           = $files
  }
  [IO.File]::WriteAllText($feedManifest, ($built | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
  "feed: $feed ($version)"
}

# --- 2. DLL cliente do bootstrap --------------------------------------------------------------------------------
if ($SkipClient) {
  Step 'DLL cliente: saltada (-SkipClient), uso a existente'
} else {
  Step 'DLL cliente do bootstrap'
  if (-not $CefDistrib) { $CefDistrib = Join-Path $repo "build\cef-codecs\out\distrib\$($motor.cef.distrib)" }
  $CefDistrib = [IO.Path]::GetFullPath($CefDistrib)
  if (-not (Test-Path -LiteralPath (Join-Path $CefDistrib 'cmake\FindCEF.cmake'))) {
    Fail "sem a distribuição mínima do motor em $CefDistrib (build/cef-codecs/build.ps1 -Steps Package volta a criá-la; ou -CefDistrib)"
  }
  # A distribuição tem de ser a da MESMA compilação do runtime: commit da CEF e o próprio libcef.dll.
  $header = Get-Content -LiteralPath (Join-Path $CefDistrib 'include\cef_version.h') -Raw
  if ($header -notmatch '#define CEF_COMMIT_HASH "([0-9a-f]{40})"' -or $Matches[1] -ne $motor.cef.commit) {
    Fail "a distribuição $CefDistrib não é do commit $($motor.cef.commit) do runtime"
  }
  $libcef = @($motor.files | Where-Object { $_.path -eq 'libcef.dll' })[0]
  if ((Sha256 (Join-Path $CefDistrib 'Release\libcef.dll')) -ne $libcef.sha256) { Fail 'o libcef.dll da distribuição não é o do runtime (outra compilação do motor)' }
  # O cl.exe não abre ficheiros com caminho completo acima de 260 caracteres (C1083 a meio da compilação do wrapper).
  $longest = (Get-ChildItem -LiteralPath $CefDistrib -Recurse -File | ForEach-Object { $_.FullName.Length } | Measure-Object -Maximum).Maximum
  if ($longest -gt 250) { Fail "a distribuição tem caminhos com $longest caracteres (o compilador só aceita 260): copie-a para uma pasta mais curta e passe -CefDistrib <pasta>" }
  "distribuição: $CefDistrib (libcef.dll igual ao do runtime)"
  $log = Join-Path $logs 'client.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
  Remove-Tree $clientOut
  Run $log 'cmd.exe' @('/d', '/c', "`"$(Join-Path $repo 'build\sandbox\client\build.cmd')`" `"$CefDistrib`" `"$clientOut`"")
  if (-not (Test-Path -LiteralPath $clientDll)) { Fail "build.cmd não produziu $clientDll" }
}
if (-not (Test-Path -LiteralPath $clientDll)) { Fail "falta $clientDll (corra sem -SkipClient)" }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-client.ps1') -Dll $clientDll -Runtime $runtime
if ($LASTEXITCODE -ne 0) { Fail 'a DLL cliente não passou a verificação (verify-client.ps1)' }

# --- 3. Testes no modo com sandbox ------------------------------------------------------------------------------
if ($SkipTests) {
  Step 'Testes: saltados (-SkipTests)'
} else {
  Step "Testes com o CefSharp do feed ($Official-eteris.$N`: modo com sandbox)"
  $log = Join-Path $logs 'tests.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
  $artifacts = Join-Path $outDir 'tests'
  # O feed também como fonte do projeto de testes: o restauro dele resolve as dependências do Eteris.Browser com as
  # SUAS fontes, e uma versão nova do feed (ainda fora da cache do NuGet) não seria encontrada (NU1603).
  Run $log 'dotnet' @('test', (Join-Path $repo 'tests\Eteris.Browser.Tests'), '--artifacts-path', $artifacts, "-p:EterisCefSharpFeed=$feed",
                      "-p:EterisCefSharpN=$N", "-p:RestoreAdditionalProjectSources=$feed", '--nologo')
  Get-Content -LiteralPath $log | Where-Object { $_ -match 'Aprovado!|Passed!|Com falha!|Failed!' } | ForEach-Object { "  $_" }
  # O CefSharp que os testes carregaram tem de ser o do feed (com WindowsSandboxInfo), não o oficial.
  $core = @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'bin\Eteris.Browser.Tests') -Recurse -Filter 'CefSharp.Core.dll' | Where-Object { $_.FullName -notmatch '\\runtimes\\' })
  if (-not $core.Count -or -not (Contains-Text ([IO.File]::ReadAllBytes($core[0].FullName)) 'WindowsSandboxInfo')) { Fail 'os testes não correram com o CefSharp do feed' }
  $cached = Join-Path $nugetCache "cefsharp.common.netcore\$Official-eteris.$N\runtimes\win-x64\lib\net6.0\CefSharp.Core.Runtime.dll"
  $expected = @($built.files | Where-Object { $_.entry -eq 'runtimes/win-x64/lib/net6.0/CefSharp.Core.Runtime.dll' })[0].sha256
  if ((Sha256 $cached) -ne $expected) { Fail "a cache do NuGet restaurou outro CefSharp.Core.Runtime.dll para $Official-eteris.$N" }
  'modo com sandbox: OK (o CefSharp do feed foi restaurado e carregado)'
}

Step 'Pronto'
"CefSharp: $Official-eteris.$N em $feed"
"DLL cliente: $clientDll"
'A seguir: build\publish.ps1 -Version X.Y.Z (com sandbox por omissão; docs/motor.md, «Publicação»)'
