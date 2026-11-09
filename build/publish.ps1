# Publicação (docs/motor.md, «Publicação»). Desde a 0.2.14 sai COM sandbox por omissão (docs/sandbox.md): processos das
# páginas, placa gráfica e armazenamento isolados, serviço de rede no AppContainer. Precisa do motor próprio
# (build/cef-codecs/out/runtime, com o bootstrap.exe), do CefSharp com o patch v2 no feed local build/cefsharp/feed e da
# DLL cliente build/sandbox/client/out/Release/Eteris.Browser.dll — os dois feitos por build/sandbox/build-all.ps1.
# -NoSandbox: só para emergências (por exemplo a publicação pelo CI sem codecs, que não tem nada disto): o Éteris corre
# com --no-sandbox, como até à 0.2.13.
# Com sandbox exige também a marca dos testes aprovados no modo com sandbox (build/sandbox/out/tests-ok.json, do
# build-all.ps1) para o HEAD atual, sem alterações por gravar, e para estas mesmas peças (feed, DLL cliente, motor).
# No fim grava publish\eteris-build.json (versão, modo, commit, CefSharp com o SHA256 das montagens): o pack.ps1 só
# empacota uma pasta com essa marca, e ela fica na instalação como registo do que a release leva. Se alguma verificação
# falhar, a pasta publish\ é apagada.
param([string]$Version = "0.2.12", [switch]$NoSandbox, [string]$CefSharpFeed = "", [int]$CefSharpN = 0, [string]$SandboxClientDll = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "publish"
# Pasta limpa: as duas formas têm ficheiros diferentes com o mesmo nome (Eteris.Browser.dll é o .NET sem sandbox e a DLL
# cliente nativa com ele), e restos da outra forma iriam para o pacote.
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
# Qualquer erro daqui para a frente apaga a publicação a meio (o pack.ps1 também recusa uma pasta sem a marca do fim).
trap {
  if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue }
  break
}
function Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
# git com o stderr fora das exceções do PowerShell 5.1; escreve as linhas da saída (nenhuma se falhar).
function Git-Out([string[]]$Arguments) {
  $old = $ErrorActionPreference; $ErrorActionPreference = "Continue"
  try { $out = @(& git -C $root @Arguments 2>$null); if ($LASTEXITCODE -ne 0) { $out = @() } } finally { $ErrorActionPreference = $old }
  $out
}
$commit = (Git-Out @("rev-parse", "HEAD")) -join ""
$dirty = @(Git-Out @("status", "--porcelain")).Count -gt 0

# Motor com codecs (docs/motor.md, «Motor com codecs»): se existir a compilação própria da CEF, o csproj põe-na no
# lugar da do NuGet. Só se publica o modo «codecs da plataforma» (nenhum descodificador com patentes no libcef) e só
# da mesma versão/commit que o CefSharp espera; cada ficheiro tem de bater com o SHA256 do manifesto.
$cefRuntime = Join-Path $root "build\cef-codecs\out\runtime"
if (Test-Path -LiteralPath (Join-Path $cefRuntime "libcef.dll")) {
  $expectedFileVersion = "156.0.15+gcf671c0+chromium-156.0.8078.80"
  $expectedCommit = "cf671c072925b27823a61181152f4f288e8e4306"
  $manifestPath = Join-Path $cefRuntime "manifest.json"
  if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Motor com codecs sem manifest.json: recompile com build/cef-codecs/build.ps1" }
  $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($manifest.mode -ne "platform-codecs") { throw "Motor com codecs no modo '$($manifest.mode)': só o modo 'platform-codecs' pode ser publicado" }
  if ($manifest.cef.commit -ne $expectedCommit) { throw "Motor com codecs do commit $($manifest.cef.commit), esperado $expectedCommit (o do CefSharp)" }
  $fileVersion = (Get-Item -LiteralPath (Join-Path $cefRuntime "libcef.dll")).VersionInfo.FileVersion
  if ($fileVersion -ne $expectedFileVersion) { throw "libcef.dll $fileVersion, esperado $expectedFileVersion (o do cef.sdk do CefSharp)" }
  foreach ($file in $manifest.files) {
    $path = Join-Path $cefRuntime ($file.path -replace '/', '\')
    if (-not (Test-Path -LiteralPath $path)) { throw "Motor com codecs: falta $($file.path)" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Motor com codecs: $($file.path) não bate com o manifesto" }
  }
  "Motor com codecs da plataforma: $fileVersion ($(@($manifest.files).Count) ficheiros verificados)"
}

$sandboxArgs = @()
$feedInfo = $null
$testsInfo = $null
if ($NoSandbox) {
  Write-Warning "Publicação SEM sandbox (-NoSandbox): só para emergências; o Éteris corre com --no-sandbox."
} else {
  $howTo = "corra build\sandbox\build-all.ps1 (docs/motor.md, «Publicação»), ou -NoSandbox para uma publicação de emergência sem sandbox"
  if (-not (Test-Path -LiteralPath (Join-Path $cefRuntime "bootstrap.exe"))) { throw "Sandbox: falta o motor próprio com o bootstrap.exe em $cefRuntime (build/cef-codecs); $howTo" }
  if (-not $CefSharpFeed) { $CefSharpFeed = Join-Path $root "build\cefsharp\feed" }
  $feedManifest = Join-Path $CefSharpFeed "eteris-cefsharp.json"
  if (-not (Test-Path -LiteralPath $feedManifest)) { throw "Sandbox: sem o CefSharp com o patch v2 em $CefSharpFeed; $howTo" }
  $feedInfo = Get-Content -LiteralPath $feedManifest -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($CefSharpN -le 0) { $CefSharpN = [int]$feedInfo.n }
  if ($CefSharpN -ne [int]$feedInfo.n) { throw "Sandbox: -CefSharpN $CefSharpN, mas o feed $CefSharpFeed é o 156.0.150-eteris.$($feedInfo.n) (eteris-cefsharp.json)" }
  foreach ($id in "cefsharp.common.netcore", "cefsharp.wpf.netcore") {
    if (-not (Test-Path -LiteralPath (Join-Path $CefSharpFeed "$id.156.0.150-eteris.$CefSharpN.nupkg"))) { throw "Sandbox: falta $id 156.0.150-eteris.$CefSharpN em $CefSharpFeed; $howTo" }
  }
  if (-not $SandboxClientDll) { $SandboxClientDll = Join-Path $root "build\sandbox\client\out\Release\Eteris.Browser.dll" }
  if (-not (Test-Path -LiteralPath $SandboxClientDll)) { throw "Sandbox: falta a DLL cliente $SandboxClientDll; $howTo" }
  # Os testes no modo com sandbox têm de ter passado com ESTAS peças e ESTE commit (o CI só testa o modo sem sandbox).
  $testsOk = Join-Path $root "build\sandbox\out\tests-ok.json"
  $rerun = "corra build\sandbox\build-all.ps1 -SkipCefSharp -SkipClient depois do último commit (testes no modo com sandbox)"
  if (-not (Test-Path -LiteralPath $testsOk)) { throw "Sandbox: sem testes aprovados no modo com sandbox ($testsOk); $rerun" }
  if (-not $commit) { throw "Sandbox: sem git não sei que commit se publica; $rerun" }
  if ($dirty) { throw "Sandbox: a árvore tem alterações por gravar (git status); faça commit e $rerun" }
  $testsInfo = Get-Content -LiteralPath $testsOk -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($testsInfo.commit -ne $commit) { throw "Sandbox: os testes com sandbox aprovados são do commit $($testsInfo.commit), o HEAD é $commit; $rerun" }
  if ([int]$testsInfo.n -ne $CefSharpN -or $testsInfo.feed_manifest_sha256 -ne (Sha256 $feedManifest)) { throw "Sandbox: os testes com sandbox aprovados não foram com este CefSharp ($CefSharpFeed, eteris.$CefSharpN); $rerun" }
  if ($testsInfo.client_sha256 -ne (Sha256 $SandboxClientDll)) { throw "Sandbox: os testes com sandbox aprovados não foram com esta DLL cliente ($SandboxClientDll); $rerun" }
  if ($testsInfo.runtime_manifest_sha256 -ne (Sha256 (Join-Path $cefRuntime "manifest.json"))) { throw "Sandbox: os testes com sandbox aprovados não foram com este motor ($cefRuntime); $rerun" }
  "Sandbox: CefSharp 156.0.150-eteris.$CefSharpN ($CefSharpFeed), DLL cliente $SandboxClientDll"
  $sandboxArgs = @("-p:EterisSandbox=true", "-p:EterisCefSharpFeed=$CefSharpFeed", "-p:EterisCefSharpN=$CefSharpN", "-p:EterisSandboxClientDll=$SandboxClientDll")
}
dotnet publish "$root\src\Eteris.Browser" -c Release -r win-x64 --self-contained -o $publishDir -p:Version=$Version @sandboxArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish saiu com $LASTEXITCODE" }

# A forma publicada tem de ser a pedida (docs/sandbox.md, opção B): com sandbox, Eteris.Browser.exe = bootstrap da CEF,
# Eteris.Browser.dll = DLL cliente nativa, .NET em Eteris.App.dll, CefSharp com WindowsSandboxInfo e sem o processo filho
# .NET; sem sandbox, o apphost .NET de sempre.
$exe = (Get-Item -LiteralPath (Join-Path $publishDir "Eteris.Browser.exe")).VersionInfo
$core = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes((Join-Path $publishDir "CefSharp.Core.dll")))
if ($NoSandbox) {
  if ((Test-Path -LiteralPath (Join-Path $publishDir "Eteris.App.dll")) -or -not (Test-Path -LiteralPath (Join-Path $publishDir "CefSharp.BrowserSubprocess.exe"))) { throw "Publicação sem sandbox com a forma errada" }
  if ($core.Contains("WindowsSandboxInfo")) { throw "Publicação sem sandbox com o CefSharp do feed (esperado o oficial)" }
  "Publicado SEM sandbox: $publishDir"
} else {
  $client = (Get-Item -LiteralPath (Join-Path $publishDir "Eteris.Browser.dll")).VersionInfo
  if (-not (Test-Path -LiteralPath (Join-Path $publishDir "Eteris.App.dll"))) { throw "Sandbox: falta Eteris.App.dll na publicação" }
  if (Test-Path -LiteralPath (Join-Path $publishDir "CefSharp.BrowserSubprocess.exe")) { throw "Sandbox: a publicação ainda traz o CefSharp.BrowserSubprocess.exe" }
  if ($client.Comments -notmatch "cef_commit=") { throw "Sandbox: Eteris.Browser.dll não é a DLL cliente" }
  if (-not $core.Contains("WindowsSandboxInfo")) { throw "Sandbox: o CefSharp.Core.dll publicado não é o do feed (sem WindowsSandboxInfo)" }
  if ($exe.ProductVersion -ne $Version) { throw "Sandbox: o Eteris.Browser.exe (bootstrap) tem a versão $($exe.ProductVersion), esperado $Version" }
  # Os binários do CefSharp publicados têm de ser os do feed (a cache do NuGet ganha sempre ao feed: a mesma versão com
  # outros binários seria restaurada com os antigos) e a DLL cliente a que foi testada.
  foreach ($name in "CefSharp.Core.dll", "CefSharp.Core.Runtime.dll") {
    $expected = @($feedInfo.files | Where-Object { $_.entry -eq "runtimes/win-x64/lib/net6.0/$name" })
    if ($expected.Count -ne 1) { throw "Sandbox: o eteris-cefsharp.json não tem o SHA256 de $name" }
    if ((Sha256 (Join-Path $publishDir $name)) -ne $expected[0].sha256) { throw "Sandbox: o $name publicado não é o do feed 156.0.150-eteris.$CefSharpN (outro na cache do NuGet?)" }
  }
  if ((Sha256 (Join-Path $publishDir "Eteris.Browser.dll")) -ne $testsInfo.client_sha256) { throw "Sandbox: a DLL cliente publicada não é a testada" }
  "Publicado COM sandbox: $publishDir (bootstrap $($exe.ProductName) $($exe.ProductVersion), DLL cliente CEF $($client.ProductVersion))"
}

# Marca do fim (só aqui, com tudo verificado): o pack.ps1 exige-a e ela vai na instalação como registo da release.
$build = [ordered]@{
  version               = $Version
  sandbox               = -not $NoSandbox
  commit                = $(if ($commit) { $commit } else { $null })
  dirty                 = $dirty
  published_utc         = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
  cefsharp              = $(if ($NoSandbox) { [ordered]@{ version = "156.0.150"; official = $true } } else { $feedInfo })
  sandbox_client_sha256 = $(if ($NoSandbox) { $null } else { $testsInfo.client_sha256 })
  sandbox_tests_utc     = $(if ($NoSandbox) { $null } else { $testsInfo.tested_utc })
}
[IO.File]::WriteAllText((Join-Path $publishDir "eteris-build.json"), ($build | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
