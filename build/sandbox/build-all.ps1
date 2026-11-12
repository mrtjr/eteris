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
       binários trocados, nada na cache do NuGet com a mesma versão e outros binários), DLL cliente (verify-client.ps1),
       e o caminho do perfil de quem compila: nem a DLL cliente nem o CefSharp.Core.dll/CefSharp.Core.Runtime.dll do
       feed o podem levar (build/profile-paths.ps1, a mesma procura do publish.ps1, que os recusaria no fim).
    4. Testes com o CefSharp do feed: o modo COM sandbox (a constante ETERIS_CEFSHARP_SANDBOX vem do csproj, por causa do
       -p:EterisCefSharpFeed que este script passa; a CI corre o modo sem, com o CefSharp oficial), confirmando que o
       CefSharp restaurado e carregado é o do feed. Só com todos aprovados, numa árvore sem alterações por gravar, grava
       build/sandbox/out/tests-ok.json (commit, N, SHA256 do eteris-cefsharp.json, da DLL cliente e do manifest.json do
       motor): o publish.ps1 recusa publicar com sandbox sem essa marca para o HEAD atual e estas mesmas peças. Por isso
       cada release corre pelo menos `build-all.ps1 -SkipCefSharp -SkipClient` DEPOIS do commit da versão.

  Tudo o que produz fica em pastas ignoradas pelo git: build/cefsharp/src, build/cefsharp/feed,
  build/sandbox/client/out e build/sandbox/out (registos, testes e a marca).

  Caminho com acentos (por exemplo %USERPROFILE%\... com «ú» no nome do utilizador): o cmd.exe, o C++/CLI, o CMake e o
  SourceLink só foram provados com caminhos ASCII, por isso o script trabalha pelo nome curto 8.3 da pasta do
  repositório (por exemplo ABCDEF~1); sem nomes curtos no disco, pára e pede uma cópia num caminho ASCII.
  O nome curto continua a ser o do utilizador, e um repositório dentro do perfil deixaria o caminho nos binários. Não
  o deixa por causa de três opções, sem mudar a raiz de compilação (docs/sandbox.md, «Caminhos de quem compila»):
  /d1trimfile:<CEF_ROOT> no compilador da DLL cliente (build/sandbox/client/CMakeLists.txt: o __FILE__ do
  libcef_dll_wrapper), /PDBALTPATH:%_PDB% no linker do CefSharp.Core.Runtime e DeterministicSourcePaths no
  CefSharp.Core (os dois no Directory.Build.props/.targets que o prepare-source.ps1 escreve no clone).

  Requisitos: git, CMake, Build Tools do Visual Studio 2022 com C++/CLI (VC.CLI.Support) e o SDK do .NET 9 (além do 10);
  o motor próprio já compilado (build/cef-codecs/out/runtime e a distribuição em build/cef-codecs/out/distrib, ou
  -CefDistrib); os pacotes oficiais CefSharp 156.0.150 e cef.sdk 156.0.15 na cache do NuGet (o restauro normal do
  Éteris põe-nos lá). Só o clone do CefSharp vai à rede (GitHub), a menos que -CefSharpSource seja uma pasta local.

  Uso: powershell -ExecutionPolicy Bypass -File build\sandbox\build-all.ps1
         [-CefSharpSource <URL ou pasta com o git do CefSharp>] [-CefDistrib <cef_binary_*_windows64_minimal>]
         [-N <n>] [-SkipCefSharp] [-SkipClient] [-SkipTests]
  -N: o N de 156.0.150-eteris.N. Por omissão, um a mais do que o maior já existente na cache do NuGet
  (a cache guarda cada versão para sempre: reutilizar um N com binários novos daria os antigos ao restaurar). O N só é
  único nesta máquina: o que liga uma release aos binários do CefSharp é o eteris-build.json que o publish.ps1 põe na
  publicação (SHA256 de cada montagem trocada).
  -SkipTests: não grava a marca dos testes, e o publish.ps1 com sandbox recusa publicar.
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

# Caminhos curtos 8.3 (ABCDEF~1) e acentos: só caminhos completos e -LiteralPath. Para apagar ficheiros, [IO.File]::Delete: o
# Remove-Item do PowerShell 5.1, com o caminho literal, não encontra um caminho 8.3 («Não existe um objeto no caminho»).
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($repo -match '[^\x00-\x7F]') {
  # Nome curto 8.3 (só ASCII) da mesma pasta; o cmd.exe, o compilador e o CMake recebem-no em vez do caminho com acentos.
  $short = (New-Object -ComObject Scripting.FileSystemObject).GetFolder($repo).ShortPath
  if (-not $short -or $short -match '[^\x00-\x7F]') {
    throw "build-all: o caminho do repositório ($repo) tem acentos e o disco não dá nomes curtos: copie o repositório para um caminho só com ASCII (por exemplo C:\src\navegador)"
  }
  "repositório pelo nome curto: $short ($repo)"
  $repo = $short
}
$cefsharpDir = Join-Path $repo 'build\cefsharp'
$work = Join-Path $cefsharpDir 'src'
$feed = Join-Path $cefsharpDir 'feed'
$feedManifest = Join-Path $feed 'eteris-cefsharp.json'
$clientOut = Join-Path $repo 'build\sandbox\client\out'
$clientDll = Join-Path $clientOut 'Release\Eteris.Browser.dll'
$outDir = Join-Path $repo 'build\sandbox\out'
$logs = Join-Path $outDir 'logs'
$testsOk = Join-Path $outDir 'tests-ok.json'
$runtime = Join-Path $repo 'build\cef-codecs\out\runtime'
$nugetCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$vcvars = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
# Uma corrida nova invalida a marca da anterior: só volta a existir se esta chegar ao fim com os testes aprovados.
if (Test-Path -LiteralPath $testsOk) { [IO.File]::Delete($testsOk) }

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

# git com o stderr fora das exceções do PowerShell 5.1; escreve as linhas da saída (nenhuma se falhar).
function Git-Out([string[]]$Arguments) {
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { $out = @(& git -C $repo @Arguments 2>$null); if ($LASTEXITCODE -ne 0) { $out = @() } } finally { $ErrorActionPreference = $old }
  $out
}

# New-ProfileScan e Test-ProfilePath: a procura do caminho do perfil que o publish.ps1 também faz.
. (Join-Path $PSScriptRoot '..\profile-paths.ps1')

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
foreach ($tool in 'git', 'cmake', 'dotnet', 'node') { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "falta $tool no PATH" } }
if (-not $SkipCefSharp) {
  if (-not (Test-Path -LiteralPath $vcvars)) { Fail "faltam as Build Tools do Visual Studio 2022 ($vcvars)" }
  if (-not (@(& dotnet --list-sdks) -match '^9\.')) { Fail 'falta o SDK do .NET 9 (o C++/CLI do CefSharp compila com ele: build/cefsharp/README.md)' }
  foreach ($id in 'cefsharp.common.netcore', 'cefsharp.wpf.netcore') {
    if (-not (Test-Path -LiteralPath (Join-Path $nugetCache "$id\$Official\$id.$Official.nupkg"))) { Fail "falta o pacote oficial $id $Official na cache do NuGet (dotnet restore do Éteris)" }
  }
}
"motor: CEF $($motor.cef.version) ($($motor.cef.commit.Substring(0, 12))), CefSharp $Official"
# O commit que os testes provam (para a marca): o HEAD numa árvore sem alterações por gravar.
$testedCommit = (Git-Out @('rev-parse', 'HEAD')) -join ''
$dirtyAtStart = @(Git-Out @('status', '--porcelain')).Count -gt 0
if (-not $SkipTests -and ($dirtyAtStart -or -not $testedCommit)) {
  Write-Warning 'árvore com alterações por gravar (ou sem git): os testes correm, mas sem a marca que o publish.ps1 exige; faça commit e corra de novo'
}

# --- 1. CefSharp com o patch v2 ---------------------------------------------------------------------------------
if ($SkipCefSharp) {
  Step 'CefSharp: saltado (-SkipCefSharp), uso o feed existente'
  if (-not (Test-Path -LiteralPath $feedManifest)) { Fail "sem $feedManifest`: corra sem -SkipCefSharp" }
  $built = Get-Content -LiteralPath $feedManifest -Raw -Encoding UTF8 | ConvertFrom-Json
  $N = [int]$built.n
} else {
  Step "CefSharp $CefSharpTag com o patch v2"
  $log = Join-Path $logs 'cefsharp.log'
  if (Test-Path -LiteralPath $log) { [IO.File]::Delete($log) }
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
  if (Test-Path -LiteralPath $log) { [IO.File]::Delete($log) }
  Remove-Tree $clientOut
  Run $log 'cmd.exe' @('/d', '/c', "`"$(Join-Path $repo 'build\sandbox\client\build.cmd')`" `"$CefDistrib`" `"$clientOut`"")
  if (-not (Test-Path -LiteralPath $clientDll)) { Fail "build.cmd não produziu $clientDll" }
}
if (-not (Test-Path -LiteralPath $clientDll)) { Fail "falta $clientDll (corra sem -SkipClient)" }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-client.ps1') -Dll $clientDll -Runtime $runtime
if ($LASTEXITCODE -ne 0) { Fail 'a DLL cliente não passou a verificação (verify-client.ps1)' }

# --- Sem o caminho de quem compila ------------------------------------------------------------------------------
# O publish.ps1 recusa binários publicados com o caminho do perfil (build/profile-paths.ps1). As peças nativas são
# conferidas aqui, antes dos testes e da marca (que fica presa ao SHA256 delas), também com -SkipCefSharp/-SkipClient:
# um feed ou uma DLL de antes das opções da arrumação 0.2.14 pára já, e não só no fim da publicação.
Step 'Sem o caminho de quem compila nas peças nativas'
$profileScan = New-ProfileScan
if (-not $profileScan) {
  Write-Warning 'sem USERPROFILE: não confiro o caminho do perfil nas peças nativas (o publish.ps1 também não)'
} else {
  $withProfile = @()
  if (Test-ProfilePath $profileScan ([IO.File]::ReadAllBytes($clientDll))) { $withProfile += 'DLL cliente (Eteris.Browser.dll)' }
  Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
  $commonNupkg = Join-Path $feed "cefsharp.common.netcore.$Official-eteris.$N.nupkg"
  if (-not (Test-Path -LiteralPath $commonNupkg)) { Fail "falta $commonNupkg (corra sem -SkipCefSharp)" }
  $zip = [IO.Compression.ZipFile]::OpenRead($commonNupkg)
  try {
    foreach ($entry in 'runtimes/win-x64/lib/net6.0/CefSharp.Core.dll', 'runtimes/win-x64/lib/net6.0/CefSharp.Core.Runtime.dll') {
      $bytes = Read-ZipEntry $zip $entry
      if (-not $bytes) { Fail "o feed não tem $entry" }
      if (Test-ProfilePath $profileScan $bytes) { $withProfile += $entry.Substring($entry.LastIndexOf('/') + 1) }
    }
  } finally { $zip.Dispose() }
  if ($withProfile.Count -gt 0) {
    Fail ("caminho do perfil de quem compila em: $($withProfile -join ', '); o publish.ps1 recusá-los-ia. Volte a compilar " +
          'sem -SkipCefSharp/-SkipClient (as opções estão no prepare-source.ps1 e no client\CMakeLists.txt); se continuar, ' +
          'docs/sandbox.md, «Caminhos de quem compila», tem o plano B (raiz neutra)')
  }
  'sem o caminho do perfil na DLL cliente nem no CefSharp.Core.dll/CefSharp.Core.Runtime.dll do feed'
}

# --- 3. Testes no modo com sandbox ------------------------------------------------------------------------------
if ($SkipTests) {
  Step 'Testes: saltados (-SkipTests)'
} else {
  Step "Testes com o CefSharp do feed ($Official-eteris.$N`: modo com sandbox)"
  $log = Join-Path $logs 'tests.log'
  if (Test-Path -LiteralPath $log) { [IO.File]::Delete($log) }
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
  # A marca para o publish.ps1: só com a árvore igual ao commit do princípio ao fim da corrida.
  $headNow = (Git-Out @('rev-parse', 'HEAD')) -join ''
  if (-not $dirtyAtStart -and $testedCommit -and $headNow -eq $testedCommit -and @(Git-Out @('status', '--porcelain')).Count -eq 0) {
    $mark = [ordered]@{
      commit                  = $testedCommit
      n                       = $N
      version                 = "$Official-eteris.$N"
      feed_manifest_sha256    = Sha256 $feedManifest
      client_sha256           = Sha256 $clientDll
      runtime_manifest_sha256 = Sha256 (Join-Path $runtime 'manifest.json')
      tested_utc              = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    }
    [IO.File]::WriteAllText($testsOk, ($mark | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
    "marca dos testes: $testsOk (commit $($testedCommit.Substring(0, 12)))"
  } else {
    Write-Warning 'sem a marca dos testes (a árvore tinha alterações por gravar ou o HEAD mudou): o publish.ps1 com sandbox vai recusar'
  }
}

Step 'Pronto'
"CefSharp: $Official-eteris.$N em $feed"
"DLL cliente: $clientDll"
if (Test-Path -LiteralPath $testsOk) {
  'A seguir: build\publish.ps1 -Version X.Y.Z (com sandbox por omissão; docs/motor.md, «Publicação»)'
} else {
  Write-Warning 'sem testes aprovados neste commit: o publish.ps1 com sandbox recusa publicar (corra sem -SkipTests, depois do último commit)'
}
