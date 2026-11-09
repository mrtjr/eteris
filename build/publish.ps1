# -Sandbox (docs/sandbox.md, passo D6): processos das paginas isolados. Precisa do feed local do CefSharp com o patch v2
# (build/cefsharp/pack-local.ps1) e da DLL cliente (build/sandbox/client/build.cmd); sem -Sandbox, nada muda.
# -GoogleOAuthJson <caminho> (docs/sincronizacao.md): o identificador da aplicação Google («App para computador»,
# descarregado da Google Cloud) vai para a pasta publish e o «Entrar com o Google» fica disponível. Nunca no repositório
# (.gitignore). Sem ele publica sem login: a secção Sincronização diz «indisponível nesta compilação».
param([string]$Version = "0.2.13", [switch]$Sandbox, [string]$CefSharpFeed = "", [int]$CefSharpN = 0, [string]$SandboxClientDll = "",
  [string]$GoogleOAuthJson = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

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
if ($Sandbox) {
  if (-not $CefSharpFeed -or $CefSharpN -le 0) { throw "-Sandbox precisa de -CefSharpFeed e -CefSharpN (build/cefsharp/README.md)" }
  $sandboxArgs = @("-p:EterisSandbox=true", "-p:EterisCefSharpFeed=$CefSharpFeed", "-p:EterisCefSharpN=$CefSharpN")
  if ($SandboxClientDll) { $sandboxArgs += "-p:EterisSandboxClientDll=$SandboxClientDll" }
}
dotnet publish "$root\src\Eteris.Browser" -c Release -r win-x64 --self-contained -o "$root\publish" -p:Version=$Version @sandboxArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Nunca um google-oauth.json esquecido de uma publicação anterior: só o que este comando pediu.
$oauthTarget = Join-Path $root "publish\google-oauth.json"
if (Test-Path -LiteralPath $oauthTarget) { Remove-Item -LiteralPath $oauthTarget -Force }
if ($oauth) {
  Copy-Item -LiteralPath $GoogleOAuthJson -Destination $oauthTarget
  "Login Google: google-oauth.json copiado para publish (cliente «App para computador», client_id $(([string]$oauth.installed.client_id).Split('-')[0])-…)"
} else {
  Write-Warning "Publicado SEM login Google (falta -GoogleOAuthJson): a secção Sincronização e o menu dizem «indisponível nesta compilação»."
}
