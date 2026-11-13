# Publicação (docs/motor.md, «Publicação»). Desde a 0.2.14 sai COM sandbox por omissão (docs/sandbox.md): processos das
# páginas, placa gráfica e armazenamento isolados, serviço de rede no AppContainer. Precisa do motor próprio
# (build/cef-codecs/out/runtime, com o bootstrap.exe), do CefSharp com o patch v2 no feed local build/cefsharp/feed e da
# DLL cliente build/sandbox/client/out/Release/Eteris.Browser.dll — os dois feitos por build/sandbox/build-all.ps1.
# -NoSandbox: só para emergências (por exemplo a publicação pelo CI sem codecs, que não tem nada disto): o Éteris corre
# com --no-sandbox, como até à 0.2.13.
# Com sandbox exige também a marca dos testes aprovados no modo com sandbox (build/sandbox/out/tests-ok.json, do
# build-all.ps1) para o HEAD atual, sem alterações por gravar, e para estas mesmas peças (feed, DLL cliente, motor).
# No fim grava publish\eteris-build.json (versão, modo, commit, CefSharp com o SHA256 das montagens, o SHA256 do manifesto do
# motor, as versões das ferramentas e a lista dos ficheiros publicados com o SHA256 de cada um): o pack.ps1 só
# empacota uma pasta com essa marca, e ela fica na instalação como registo do que a release leva. Se alguma verificação
# falhar, a pasta publish\ é apagada.
# -GoogleOAuthJson <caminho> (docs/sincronizacao.md): o identificador da aplicação Google («App para computador»,
# descarregado da Google Cloud) vai para a pasta publish e o «Entrar com o Google» fica disponível. Nunca no repositório
# (.gitignore). Sem ele publica sem login: a secção Sincronização diz «indisponível nesta compilação», e o pack.ps1 só
# empacota essa publicação com -SemLogin. A marca eteris-build.json diz se o levou (google_oauth), nunca o client_id.
param([string]$Version = "0.2.17", [switch]$NoSandbox, [string]$CefSharpFeed = "", [int]$CefSharpN = 0,
  [string]$SandboxClientDll = "", [string]$GoogleOAuthJson = "")
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
$runtimeManifestSha = $null
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
  # Um ficheiro a mais na pasta do motor iria para a publicação sem estar no manifesto (CAD-08): o manifesto lista tudo o
  # que o build.ps1 do motor empacotou, menos ele próprio.
  $inManifest = @{}
  foreach ($file in $manifest.files) { $inManifest[($file.path -replace '\\', '/')] = $true }
  $runtimePrefix = (Resolve-Path -LiteralPath $cefRuntime).Path.TrimEnd('\') + '\'
  foreach ($file in Get-ChildItem -LiteralPath $cefRuntime -File -Recurse) {
    $relative = $file.FullName.Substring($runtimePrefix.Length) -replace '\\', '/'
    if ($relative -eq "manifest.json") { continue }
    if (-not $inManifest.ContainsKey($relative)) { throw "Motor com codecs: $relative está na pasta do motor e fora do manifesto (recompile com build/cef-codecs/build.ps1 ou apague-o)" }
  }
  $runtimeManifestSha = Sha256 $manifestPath
  "Motor com codecs da plataforma: $fileVersion ($(@($manifest.files).Count) ficheiros verificados)"
}

# O identificador da aplicação Google é verificado ANTES de compilar (um ficheiro errado não deixa publicar a meio).
$oauth = $null
if ($GoogleOAuthJson) {
  if (-not (Test-Path -LiteralPath $GoogleOAuthJson -PathType Leaf)) { throw "-GoogleOAuthJson: ficheiro não encontrado: $GoogleOAuthJson" }
  try { $oauth = Get-Content -LiteralPath $GoogleOAuthJson -Raw -Encoding UTF8 | ConvertFrom-Json } catch { throw "-GoogleOAuthJson: não é JSON válido" }
  $installed = $oauth.installed
  if (-not $installed) { throw "-GoogleOAuthJson: não é um cliente «App para computador» da Google (falta o objeto «installed»; um cliente «web» não serve)" }
  if ([string]$installed.client_id -notmatch '^[0-9]+-[a-z0-9]+\.apps\.googleusercontent\.com$') { throw "-GoogleOAuthJson: client_id inválido" }
  if ($installed.token_uri -and $installed.token_uri -ne "https://oauth2.googleapis.com/token") { throw "-GoogleOAuthJson: token_uri inesperado ($($installed.token_uri))" }
  if ($installed.auth_uri -and $installed.auth_uri -notlike "https://accounts.google.com/o/oauth2/*") { throw "-GoogleOAuthJson: auth_uri inesperado ($($installed.auth_uri))" }
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
dotnet publish "$root\src\Eteris.Browser" -c Release -r win-x64 --self-contained -o $publishDir -p:Version=$Version -p:EterisTestHooks=false @sandboxArgs
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

# A via de teste da confirmação do Windows (ETERIS_TEST_REAUTH) não vai no binário publicado (VAU-08): só existe numa
# compilação com a constante ETERIS_TEST_HOOKS, que o dotnet publish de cima desliga. Confere-se o .NET publicado (o
# Eteris.App.dll com sandbox, o Eteris.Browser.dll sem ele) à procura do nome da variável em UTF-16LE, onde o compilador
# guarda os textos; procurado nos bytes, sem alinhamento (um texto pode começar num byte ímpar).
$dotnetDll = Join-Path $publishDir $(if ($NoSandbox) { "Eteris.Browser.dll" } else { "Eteris.App.dll" })
$latin1 = [Text.Encoding]::GetEncoding(28591)
$hookName = $latin1.GetString([Text.Encoding]::Unicode.GetBytes('ETERIS_TEST_REAUTH'))
if ($latin1.GetString([IO.File]::ReadAllBytes($dotnetDll)).Contains($hookName)) { throw "O binário publicado traz a via de teste da reautenticação (ETERIS_TEST_REAUTH): o dotnet publish tem de levar -p:EterisTestHooks=false" }
# O mesmo para a resposta de teste da tranca das janelas privadas (ETERIS_TEST_TRANCA_RESPOSTA, Vault/PrivateUnlock.cs).
$lockHookName = $latin1.GetString([Text.Encoding]::Unicode.GetBytes('ETERIS_TEST_TRANCA_RESPOSTA'))
if ($latin1.GetString([IO.File]::ReadAllBytes($dotnetDll)).Contains($lockHookName)) { throw "O binário publicado traz a via de teste da tranca das janelas privadas (ETERIS_TEST_TRANCA_RESPOSTA): o dotnet publish tem de levar -p:EterisTestHooks=false" }

# Nenhum binário nosso publicado leva o caminho do perfil de quem compila (o nome do utilizador do Windows), nos dois
# modos: Eteris.Browser.dll (o .NET sem sandbox, a DLL cliente nativa com ele), Eteris.App.dll, eteris_shields.dll, o
# bootstrap Eteris.Browser.exe, os do CefSharp e os .pdb. A procura está em build/profile-paths.ps1, a mesma que o
# build-all.ps1 faz às peças nativas logo depois de as compilar: «\Users\<nome>\» nas formas longa e curta 8.3, em UTF-8
# e UTF-16LE, nos .pdb também o nome como segmento solto (o PDB portátil guarda os caminhos aos bocados, cada um com o
# comprimento antes) e o PDB portátil embutido nas montagens .NET, que vai comprimido. O libcef.dll (~250 MB, compilado
# em C:\cef) fica de fora. A mensagem nunca diz o nome. Remédios: PathMap no Directory.Build.props, --remap-path-prefix
# do Escudo no csproj, e as opções das peças nativas do build-all.ps1 (docs/sandbox.md, «Caminhos de quem compila»).
. (Join-Path $PSScriptRoot "profile-paths.ps1")
$profileScan = New-ProfileScan
if (-not $profileScan) {
  Write-Warning "Sem USERPROFILE: não é possível procurar o caminho do perfil nos binários publicados."
} else {
  $ours = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse | Where-Object { $_.Name -match '^(Eteris|eteris_|CefSharp)' -or $_.Extension -eq '.pdb' })
  $withProfile = @(foreach ($file in $ours) {
    if (Test-ProfilePath $profileScan ([IO.File]::ReadAllBytes($file.FullName)) -Pdb:($file.Extension -eq '.pdb')) { $file.Name }
  })
  if ($withProfile.Count -gt 0) { throw "Caminho do perfil de quem compila em: $($withProfile -join ', ') (PathMap, --remap-path-prefix do Escudo ou as opções das peças nativas do build-all.ps1: docs/sandbox.md, «Caminhos de quem compila»)" }
  "Sem o caminho do perfil em $($ours.Count) binários nossos"
}

# Login Google (docs/sincronizacao.md): só o que este comando pediu (publish\ foi limpa no início). Dentro do trap: um
# Copy-Item que falhe apaga a publicação a meio, e a marca só se escreve depois.
if ($oauth) {
  Copy-Item -LiteralPath $GoogleOAuthJson -Destination (Join-Path $publishDir "google-oauth.json")
  "Login Google: google-oauth.json copiado (cliente «App para computador», client_id $(([string]$oauth.installed.client_id).Split('-')[0])-…)"
} else {
  Write-Warning "Publicado SEM login Google (falta -GoogleOAuthJson): a secção Sincronização e o menu dizem «indisponível nesta compilação». O pack.ps1 só a empacota com -SemLogin."
}

# As versões das ferramentas que fizeram esta publicação (nulo quando o comando falha ou não existe) e a lista do que ficou
# em publish\ com o SHA256 de cada ficheiro (CAD-08): o pack.ps1 confere-a antes de empacotar, e a marca fica como registo.
function Tool-Version([string]$Command, [string[]]$Arguments, [string]$LineStart = "") {
  $old = $ErrorActionPreference; $ErrorActionPreference = "Continue"
  try {
    $lines = @(& $Command @Arguments 2>$null | ForEach-Object { (([string]$_).Trim() -replace '\s+', ' ') } | Where-Object { $_ -and $_ -match $LineStart })
    if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) { $null } else { $lines[0] }
  } catch { $null } finally { $ErrorActionPreference = $old }
}
$publishPrefix = (Resolve-Path -LiteralPath $publishDir).Path.TrimEnd('\') + '\'
$publishPaths = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse | ForEach-Object { $_.FullName.Substring($publishPrefix.Length) -replace '\\', '/' })
[Array]::Sort($publishPaths, [StringComparer]::Ordinal)
$publishFiles = @(foreach ($relative in $publishPaths) { [ordered]@{ path = $relative; sha256 = Sha256 (Join-Path $publishDir ($relative -replace '/', '\')) } })

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
  google_oauth          = [bool]$oauth
  runtime_manifest_sha256 = $runtimeManifestSha
  tools                 = [ordered]@{
    dotnet_sdk = Tool-Version "dotnet" @("--version")
    rustc      = Tool-Version "rustc" @("--version")
    vpk        = Tool-Version "dotnet" @("tool", "list", "-g") "^vpk\s"
  }
  files                 = $publishFiles
}
[IO.File]::WriteAllText((Join-Path $publishDir "eteris-build.json"), ($build | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
