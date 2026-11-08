<#
.SYNOPSIS
  Compila a CEF 156.0.15 (Chromium 156.0.8078.80) com "codecs da plataforma" para o Eteris.

.DESCRIPTION
  Modo "platform-codecs": proprietary_codecs=true + ffmpeg_branding=Chromium.
  O FFmpeg so desmultiplexa H.264/AAC/HEVC (nenhum descodificador com patentes e compilado nem
  distribuido); H.264/HEVC sao descodificados pela placa grafica (D3D11VideoDecoder, processo GPU)
  e AAC pelo descodificador AAC do Media Foundation do Windows. O OpenH264 (H.264 em software)
  tambem fica desligado.

  Patches (aplicados depois dos da CEF, sem ficheiros em comum com eles):
    patches\platform-codecs.patch      relativo a src                     (media/)
    patches\ffmpeg-adts-demuxer.patch  relativo a src\third_party\ffmpeg  (desmultiplexador ADTS e
                                       parser AAC na configuracao Chromium win/x64; sem descodificadores)

  Passos (todos idempotentes; pode voltar a correr o script quantas vezes quiser):
    Check     verifica pre-requisitos (codigo, commits, VS, SDK, PGO, disco, colisao com patches da CEF,
              FFmpeg sem descodificadores com patentes)
    Patch     aplica os patches (so os que ainda nao estiverem aplicados)
    Generate  define GN_DEFINES e corre cef_create_projects.bat (patches da CEF + gn gen)
    Smoke     compila so os objetos dos ficheiros alterados (minutos), para falhar cedo
    Build     compila out\Release_GN_x64 (cefsimple bootstrap bootstrapc, como o automate-git.py)
    Package   make_distrib --minimal, junta o runtime completo em <OutDir>\runtime (com LICENSE.txt,
              CREDITS.html e os patches em runtime\licenses) e escreve runtime\manifest.json
              (modo, GN args, commits, SHA256 de cada ficheiro)

  Smoke e Build correm com prioridade abaixo do normal e com a afinidade do processo limitada a
  -Jobs processadores logicos: os filhos (siso/ninja, clang-cl, lld-link) herdam ambas, por isso
  nem a ligacao ThinLTO final (que usaria todos os nucleos) passa desse limite.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build.ps1 -Steps Check
  powershell -ExecutionPolicy Bypass -File .\build.ps1 -Steps Patch,Generate,Smoke
  powershell -ExecutionPolicy Bypass -File .\build.ps1            # tudo
#>
[CmdletBinding()]
param(
  [string]$CefRoot = 'C:\cef',
  [string]$VsInstall = 'C:\Program Files\Microsoft Visual Studio\18\Insiders',
  [ValidateRange(1, 64)]
  [int]$Jobs = 12,
  # Um ou mais de: All, Check, Patch, Generate, Smoke, Build, Package (tambem aceita "Check,Patch").
  [string[]]$Steps = @('All'),
  [string]$OutDir = (Join-Path $PSScriptRoot 'out'),
  [int]$MinFreeGB = 100
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# --- Versoes fixas ------------------------------------------------------------------------------
$CefVersion       = '156.0.15'
$CefBranch        = '8078'
$CefCommit        = 'cf671c072925b27823a61181152f4f288e8e4306'
$ChromiumVersion  = '156.0.8078.80'
$CefSharpVersion  = '156.0.150'
$WindowsSdk       = '10.0.28000.0'
$OutConfig        = 'Release_GN_x64'
# Os mesmos alvos que o automate-git.py compila no Windows (build-target + "bootstrap bootstrapc");
# para a distribuicao minima basta o cefsimple, que puxa o libcef.dll e os recursos.
$BuildTargets     = @('cefsimple', 'bootstrap', 'bootstrapc')
$Mode             = 'platform-codecs'

# GN args do modo. media_use_openh264=false: o OpenH264 e uma implementacao H.264 com patentes
# (a licenca da Cisco so cobre os binarios dela) e, com ele ligado, o WebRTC (rtc_use_h264) anunciaria
# um descodificador H.264 em software baseado no FFmpeg que aqui nao existe.
$GnDefineList = @(
  'is_official_build=true',
  'proprietary_codecs=true',
  'ffmpeg_branding=Chromium',
  'enable_platform_hevc=true',
  'enable_hevc_parser_and_hw_decoder=true',
  'media_use_openh264=false',
  'symbol_level=1',
  'blink_symbol_level=0',
  'v8_symbol_level=0'
)
$GnDefines = $GnDefineList -join ' '

# Valores derivados (nao estao no args.gn) gravados no manifest.json.
$DerivedGnArgs = @('enable_platform_hevc', 'enable_hevc_parser_and_hw_decoder', 'enable_hls_demuxer',
                   'enable_platform_dolby_vision', 'enable_platform_encrypted_dolby_vision',
                   'platform_has_optional_hevc_encode_support', 'media_use_openh264',
                   'rtc_use_h264', 'rtc_use_h265', 'enable_widevine')

# --- Caminhos -----------------------------------------------------------------------------------
$Src        = Join-Path $CefRoot 'chromium_git\chromium\src'
$CefSrc     = Join-Path $Src 'cef'
$FfmpegSrc  = Join-Path $Src 'third_party\ffmpeg'
$DepotTools = Join-Path $CefRoot 'depot_tools'
$Python     = Join-Path $DepotTools 'python3.bat'
$Autoninja  = Join-Path $DepotTools 'autoninja.bat'
$Gn         = Join-Path $DepotTools 'gn.bat'
$BuildDir   = Join-Path $Src "out\$OutConfig"
$LogDir     = Join-Path $OutDir 'logs'
$DistribDir = Join-Path $OutDir 'distrib'
$RuntimeDir = Join-Path $OutDir 'runtime'
$SymbolsDir = Join-Path $OutDir 'symbols'
$FfmpegCfg  = Join-Path $FfmpegSrc 'chromium\config\Chromium\win\x64'

# Prefix = caminho do repositorio relativo a src (para comparar com os patches da CEF).
$Patches = @(
  [ordered]@{ Name = 'platform-codecs'; File = (Join-Path $PSScriptRoot 'patches\platform-codecs.patch');
              Repo = $Src; Prefix = '' },
  [ordered]@{ Name = 'ffmpeg-adts-demuxer'; File = (Join-Path $PSScriptRoot 'patches\ffmpeg-adts-demuxer.patch');
              Repo = $FfmpegSrc; Prefix = 'third_party/ffmpeg/' }
)

# Compilados no passo Smoke, alem dos .cc alterados pelo platform-codecs.patch: as fontes do FFmpeg
# afetadas pelo ffmpeg-adts-demuxer.patch (listas de demuxers/parsers e as fontes acrescentadas).
$SmokeExtraSources = @(
  'third_party/ffmpeg/libavformat/allformats.c', 'third_party/ffmpeg/libavcodec/parsers.c',
  'third_party/ffmpeg/libavcodec/adts_parser.c', 'third_party/ffmpeg/libavcodec/aac_parser.c',
  'third_party/ffmpeg/libavcodec/aac_ac3_parser.c', 'third_party/ffmpeg/libavcodec/adts_header.c',
  'third_party/ffmpeg/libavformat/apetag.c', 'third_party/ffmpeg/libavformat/img2.c',
  'third_party/ffmpeg/libavformat/autorename_libavformat_aacdec.c'
)

# --- Utilitarios --------------------------------------------------------------------------------
function Write-Step([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "    ok: $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { Write-Host "    aviso: $Text" -ForegroundColor Yellow }
function Fail([string]$Text) { throw "ERRO: $Text" }

# Corre uma linha de comando no cmd.exe (evita os NativeCommandError do PowerShell 5.1 com stderr).
# Os .bat sao chamados sempre pelo caminho completo: com NoDefaultCurrentDirectoryInExePath=1 o cmd
# nao os procura na pasta atual.
function Invoke-Cmd([string]$CommandLine, [switch]$Quiet, [string]$LogFile, [string]$WorkDir) {
  if ($WorkDir) { $CommandLine = "cd /d `"$WorkDir`" && $CommandLine" }
  if ($Quiet) {
    cmd.exe /d /s /c "`"$CommandLine >nul 2>&1`""
  } elseif ($LogFile) {
    cmd.exe /d /s /c "`"$CommandLine 2>&1`"" | Tee-Object -FilePath $LogFile -Append | Out-Host
  } else {
    cmd.exe /d /s /c "`"$CommandLine`"" | Out-Host
  }
  return $LASTEXITCODE
}

# Corre e devolve o stdout (ou $null se falhar).
function Get-CmdOutput([string]$CommandLine, [string]$WorkDir) {
  if ($WorkDir) { $CommandLine = "cd /d `"$WorkDir`" && $CommandLine" }
  $out = cmd.exe /d /s /c "`"$CommandLine 2>nul`""
  if ($LASTEXITCODE -ne 0) { return $null }
  return (($out | Out-String).Trim())
}

function Git([string]$Arguments) { return "git $Arguments" }

function Get-PatchedFiles([string]$PatchPath) {
  $files = @()
  foreach ($line in [IO.File]::ReadAllLines($PatchPath)) {
    if ($line -match '^diff --git (?:a/)?(\S+) ') { $files += $Matches[1] }
  }
  return $files
}

function Test-PatchApplied($Patch) {
  return ((Invoke-Cmd (Git "-C `"$($Patch.Repo)`" apply --reverse --check `"$($Patch.File)`"") -Quiet) -eq 0)
}

function Read-GnArgs([string]$ArgsGn) {
  $result = [ordered]@{}
  foreach ($line in [IO.File]::ReadAllLines($ArgsGn)) {
    $l = $line.Trim()
    if ($l -eq '' -or $l.StartsWith('#')) { continue }
    if ($l -match '^([A-Za-z0-9_]+)\s*=\s*(.+?)\s*$') {
      $name = $Matches[1]; $raw = $Matches[2]
      if ($raw -eq 'true') { $value = $true }
      elseif ($raw -eq 'false') { $value = $false }
      elseif ($raw -match '^-?\d+$') { $value = [int64]$raw }
      elseif ($raw -match '^"(.*)"$') { $value = $Matches[1] }
      else { $value = $raw }
      $result[$name] = $value
    }
  }
  return $result
}

function Set-BuildEnvironment {
  $env:DEPOT_TOOLS_WIN_TOOLCHAIN = '0'
  $env:DEPOT_TOOLS_UPDATE = '0'
  $env:DEPOT_TOOLS_METRICS = '0'
  # Sem isto o autoninja corre "cipd auth-info" em cada compilacao (telemetria de build do depot_tools).
  $env:DEPOT_TOOLS_BUILD_TELEMETRY = '0'
  $env:vs2026_install = $VsInstall
  $env:GYP_MSVS_VERSION = '2026'
  # Caminhos longos do git so por variaveis de ambiente (nunca na configuracao global).
  $env:GIT_CONFIG_COUNT = '1'
  $env:GIT_CONFIG_KEY_0 = 'core.longpaths'
  $env:GIT_CONFIG_VALUE_0 = 'true'
  $env:GN_DEFINES = $GnDefines
  $env:GN_OUT_CONFIGS = $OutConfig
  if (-not ($env:PATH -split ';' | Select-Object -First 1).TrimEnd('\').Equals($DepotTools.TrimEnd('\'), 'OrdinalIgnoreCase')) {
    $env:PATH = "$DepotTools;$env:PATH"
  }
}

function Get-KitsRoot {
  try {
    $k = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -Name KitsRoot10 -ErrorAction Stop
    return $k.KitsRoot10
  } catch {
    return 'C:\Program Files (x86)\Windows Kits\10\'
  }
}

# Prioridade abaixo do normal + afinidade limitada a -Jobs processadores logicos, herdadas pelos
# processos filhos. Devolve o estado anterior para Exit-LowImpact.
function Enter-LowImpact {
  $me = Get-Process -Id $PID
  $state = @{ Priority = $me.PriorityClass; Affinity = $me.ProcessorAffinity }
  $me.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
  $cores = [Environment]::ProcessorCount
  if ($script:EffectiveJobs -lt $cores -and $cores -le 64) {
    $mask = [int64]([Math]::Pow(2, $script:EffectiveJobs) - 1)
    $me.ProcessorAffinity = [IntPtr]$mask
  }
  return $state
}

function Exit-LowImpact($State) {
  $me = Get-Process -Id $PID
  $me.PriorityClass = $State.Priority
  $me.ProcessorAffinity = $State.Affinity
}

# A garantia principal do modo: a configuracao Chromium do FFmpeg que vai ser compilada nao tem
# descodificadores H.264/AAC/HEVC (o nosso patch so acrescenta o desmultiplexador ADTS e o parser AAC).
function Test-FfmpegNoPatentedDecoders {
  $codecList = Join-Path $FfmpegCfg 'libavcodec\codec_list.c'
  $components = Join-Path $FfmpegCfg 'config_components.h'
  foreach ($f in @($codecList, $components)) {
    if (-not (Test-Path -LiteralPath $f)) { Fail "nao encontrado: $f" }
  }
  $bad = @(Select-String -LiteralPath $codecList -Pattern '\bff_(h264|hevc|aac|aac_fixed|aac_latm)_decoder\b')
  if ($bad.Count -gt 0) { Fail "o FFmpeg Chromium/win/x64 tem descodificadores com patentes: $(($bad | ForEach-Object { $_.Line.Trim() }) -join ', ')" }
  $bad = @(Select-String -LiteralPath $components -Pattern '^#define CONFIG_(H264|HEVC|AAC|AAC_FIXED|AAC_LATM)_DECODER 1')
  if ($bad.Count -gt 0) { Fail "config_components.h liga descodificadores com patentes: $(($bad | ForEach-Object { $_.Line.Trim() }) -join ', ')" }
}

# --- Passos -------------------------------------------------------------------------------------
function Invoke-Check {
  Write-Step 'Pre-requisitos'
  if ($env:OS -ne 'Windows_NT') { Fail 'so corre no Windows.' }
  if (-not [Environment]::Is64BitOperatingSystem) { Fail 'e preciso Windows de 64 bits.' }

  foreach ($p in @("$Src\.git", "$CefSrc\.git", "$FfmpegSrc\.git", "$CefSrc\cef_create_projects.bat",
                   "$CefSrc\tools\make_distrib.bat", "$DepotTools\gclient.bat", $Autoninja, $Gn, $Python)) {
    if (-not (Test-Path -LiteralPath $p)) { Fail "nao encontrado: $p" }
  }
  foreach ($p in $Patches) {
    if (-not (Test-Path -LiteralPath $p.File)) { Fail "nao encontrado: $($p.File)" }
  }
  Write-Ok 'codigo, depot_tools e patches presentes'

  $srcHead = Get-CmdOutput (Git "-C `"$Src`" rev-parse HEAD")
  $tagCommit = Get-CmdOutput (Git "-C `"$Src`" rev-parse `"refs/tags/$ChromiumVersion^{commit}`"")
  if (-not $tagCommit) { Fail "a tag $ChromiumVersion nao existe em $Src" }
  if ($srcHead -ne $tagCommit) { Fail "src esta em $srcHead, esperado $ChromiumVersion ($tagCommit)" }
  Write-Ok "Chromium $ChromiumVersion ($srcHead)"

  $cefHead = Get-CmdOutput (Git "-C `"$CefSrc`" rev-parse HEAD")
  if ($cefHead -ne $CefCommit) { Fail "src\cef esta em $cefHead, esperado $CefCommit" }
  Write-Ok "CEF $CefVersion ($CefCommit)"

  $vcvars = Join-Path $VsInstall 'VC\Auxiliary\Build\vcvarsall.bat'
  if (-not (Test-Path -LiteralPath $vcvars)) { Fail "Visual Studio sem VC++ em $VsInstall" }
  $atl = Get-ChildItem -Path (Join-Path $VsInstall 'VC\Tools\MSVC') -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'atlmfc\include\atlbase.h') }
  if (-not $atl) { Fail 'falta o componente ATL do Visual Studio (atlbase.h)' }
  Write-Ok "Visual Studio com ATL ($VsInstall)"

  $kits = Get-KitsRoot
  if (-not (Test-Path -LiteralPath (Join-Path $kits "Include\$WindowsSdk\um\windows.h"))) {
    Fail "falta o Windows SDK $WindowsSdk em $kits"
  }
  if (-not (Test-Path -LiteralPath (Join-Path $kits 'Debuggers\x64\cdb.exe'))) {
    Fail 'faltam as Debugging Tools for Windows (Debuggers\x64\cdb.exe)'
  }
  Write-Ok "Windows SDK $WindowsSdk + Debugging Tools"

  $pgoTxt = Join-Path $Src 'chrome\build\win64.pgo.txt'
  if (-not (Test-Path -LiteralPath $pgoTxt)) { Fail "falta $pgoTxt (is_official_build precisa do perfil PGO)" }
  $pgoName = ([IO.File]::ReadAllText($pgoTxt)).Trim()
  if (-not (Test-Path -LiteralPath (Join-Path $Src "chrome\build\pgo_profiles\$pgoName"))) {
    Fail "falta o perfil PGO $pgoName (descarregue com --with-pgo-profiles / gclient runhooks)"
  }
  Write-Ok 'perfil PGO presente'

  # Os nossos patches nao podem tocar em ficheiros que os patches da CEF alteram.
  $ours = @()
  foreach ($p in $Patches) { $ours += @(Get-PatchedFiles $p.File | ForEach-Object { $p.Prefix + $_ }) }
  $cefFiles = @{}
  foreach ($cp in Get-ChildItem -Path (Join-Path $CefSrc 'patch\patches') -Filter '*.patch') {
    foreach ($f in Get-PatchedFiles $cp.FullName) { $cefFiles[$f] = $cp.Name }
  }
  $clash = @($ours | Where-Object { $cefFiles.ContainsKey($_) })
  if ($clash.Count -gt 0) {
    Fail ("os patches colidem com patches da CEF: " + (($clash | ForEach-Object { "$_ ($($cefFiles[$_]))" }) -join ', '))
  }
  Write-Ok "sem colisoes com os $($cefFiles.Count) ficheiros alterados pelos patches da CEF"

  Test-FfmpegNoPatentedDecoders
  Write-Ok 'FFmpeg Chromium/win/x64 sem descodificadores H.264/HEVC/AAC'

  $drive = (Get-Item -LiteralPath $Src).PSDrive
  $freeGB = [int]($drive.Free / 1GB)
  if ($freeGB -lt $MinFreeGB) { Fail "so ha $freeGB GB livres em $($drive.Name): (minimo $MinFreeGB GB)" }
  Write-Ok "$freeGB GB livres"

  $cores = [Environment]::ProcessorCount
  $script:EffectiveJobs = [Math]::Min($Jobs, $cores)
  Write-Ok "$cores processadores logicos; vai usar no maximo $script:EffectiveJobs"
}

function Invoke-Patch {
  foreach ($p in $Patches) {
    Write-Step "Patch $($p.Name)"
    if (Test-PatchApplied $p) { Write-Ok 'ja estava aplicado'; continue }
    if ((Invoke-Cmd (Git "-C `"$($p.Repo)`" apply --check `"$($p.File)`"")) -ne 0) {
      Fail "$($p.Name): o patch nao aplica de forma limpa (codigo diferente do esperado ou parcialmente aplicado)"
    }
    if ((Invoke-Cmd (Git "-C `"$($p.Repo)`" apply `"$($p.File)`"")) -ne 0) { Fail "$($p.Name): git apply falhou" }
    if (-not (Test-PatchApplied $p)) { Fail "$($p.Name): aplicado, mas a verificacao inversa falhou" }
    Write-Ok 'aplicado'
    Invoke-Cmd (Git "-C `"$($p.Repo)`" diff --stat -- $((Get-PatchedFiles $p.File) -join ' ')") | Out-Null
  }
  Test-FfmpegNoPatentedDecoders
}

function Test-AllPatchesApplied {
  foreach ($p in $Patches) {
    if (-not (Test-PatchApplied $p)) { Fail "o patch $($p.Name) nao esta aplicado (corra o passo Patch)" }
  }
}

function Test-GnArgs {
  $argsGn = Join-Path $BuildDir 'args.gn'
  if (-not (Test-Path -LiteralPath $argsGn)) { Fail "nao existe $argsGn (corra o passo Generate)" }
  $gn = Read-GnArgs $argsGn
  $expect = [ordered]@{
    is_official_build = $true; proprietary_codecs = $true; ffmpeg_branding = 'Chromium';
    enable_platform_hevc = $true; enable_hevc_parser_and_hw_decoder = $true; media_use_openh264 = $false
  }
  foreach ($k in $expect.Keys) {
    if (-not $gn.Contains($k) -or $gn[$k] -ne $expect[$k]) {
      Fail "args.gn: esperado $k = $($expect[$k]), encontrado '$(if ($gn.Contains($k)) { $gn[$k] } else { '<ausente>' })'"
    }
  }
  return $gn
}

function Invoke-Generate {
  Write-Step "Gerar projetos (cef_create_projects.bat, $OutConfig)"
  Write-Host "    GN_DEFINES=$GnDefines"
  New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
  $log = Join-Path $LogDir ("generate-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
  $bat = Join-Path $CefSrc 'cef_create_projects.bat'
  if ((Invoke-Cmd "`"$bat`"" -LogFile $log -WorkDir $CefSrc) -ne 0) { Fail "cef_create_projects.bat falhou (ver $log)" }
  # A CEF aplica os patches dela neste passo; os nossos tem de continuar aplicados.
  Test-AllPatchesApplied
  Test-GnArgs | Out-Null
  Write-Ok "args.gn de $OutConfig confere (proprietary_codecs=true, ffmpeg_branding=Chromium, sem OpenH264)"
}

function Invoke-Smoke {
  Write-Step 'Compilacao rapida dos ficheiros alterados'
  Test-GnArgs | Out-Null
  Test-AllPatchesApplied
  $sources = @(Get-PatchedFiles $Patches[0].File | Where-Object { $_ -match '\.(cc|c)$' }) + $SmokeExtraSources
  $objs = @()
  foreach ($s in $sources) {
    $o = Get-CmdOutput "`"$Gn`" outputs out\$OutConfig //$s" -WorkDir $Src
    if (-not $o) { Fail "gn outputs nao encontrou $s em out\$OutConfig" }
    $objs += @($o -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '\.obj$' })
  }
  New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
  $log = Join-Path $LogDir ("smoke-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
  $state = Enter-LowImpact
  try {
    Write-Host "    $($objs.Count) objetos, prioridade abaixo do normal, $script:EffectiveJobs processadores, registo em $log"
    $code = Invoke-Cmd ("`"$Autoninja`" -C out\$OutConfig -j $script:EffectiveJobs " + ($objs -join ' ')) -LogFile $log -WorkDir $Src
    if ($code -ne 0) { Fail "a compilacao rapida falhou com o codigo $code (ver $log)" }
  } finally { Exit-LowImpact $state }
  Write-Ok 'os objetos alterados compilam'
}

function Invoke-Build {
  Write-Step "Compilar $OutConfig ($($BuildTargets -join ', '))"
  Test-GnArgs | Out-Null
  Test-AllPatchesApplied
  New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
  $log = Join-Path $LogDir ("build-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
  $state = Enter-LowImpact
  try {
    Write-Host "    prioridade abaixo do normal, -j $script:EffectiveJobs e afinidade de $script:EffectiveJobs processadores, registo em $log"
    $start = Get-Date
    # Equivalente ao passo de compilacao do automate-git.py (--no-debug-build --x64-build), com -j.
    $code = Invoke-Cmd ("`"$Autoninja`" -C out\$OutConfig -j $script:EffectiveJobs " + ($BuildTargets -join ' ')) -LogFile $log -WorkDir $Src
    if ($code -ne 0) { Fail "a compilacao falhou com o codigo $code (ver $log)" }
    Write-Ok ("compilado em {0:hh\:mm\:ss}" -f ((Get-Date) - $start))
  } finally { Exit-LowImpact $state }
  $flags = Join-Path $BuildDir 'gen\media\media_buildflags.h'
  if (-not (Select-String -LiteralPath $flags -Pattern 'PLATFORM_PROPRIETARY_DECODERS_ONLY\(\) \(1\)' -Quiet)) {
    Fail "$flags nao tem PLATFORM_PROPRIETARY_DECODERS_ONLY=1 (o patch nao entrou na compilacao)"
  }
  Write-Ok 'media_buildflags.h: PLATFORM_PROPRIETARY_DECODERS_ONLY=1'
}

function Get-DerivedGnArgs {
  $result = [ordered]@{}
  foreach ($a in $DerivedGnArgs) {
    $line = Get-CmdOutput "`"$Gn`" args out\$OutConfig --list=$a --short" -WorkDir $Src
    if ($line -and $line -match '=\s*(.+)$') {
      $raw = $Matches[1].Trim()
      if ($raw -eq 'true') { $result[$a] = $true } elseif ($raw -eq 'false') { $result[$a] = $false }
      else { $result[$a] = $raw.Trim('"') }
    }
  }
  return $result
}

function Get-Sha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Invoke-Package {
  Write-Step 'Distribuicao minima + runtime'
  $gn = Test-GnArgs
  Test-FfmpegNoPatentedDecoders
  if (-not (Test-Path -LiteralPath (Join-Path $BuildDir 'libcef.dll'))) { Fail 'libcef.dll nao existe (corra o passo Build)' }

  foreach ($d in @($DistribDir, $RuntimeDir, $SymbolsDir)) {
    # [IO.Directory]::Delete e nao Remove-Item: no PowerShell 5.1 o Remove-Item falha com caminhos
    # curtos 8.3 (C:\Users\JNIOR~1\...), que e como o -OutDir costuma chegar.
    if (Test-Path -LiteralPath $d) { [IO.Directory]::Delete($d, $true) }
    New-Item -ItemType Directory -Force -Path $d | Out-Null
  }
  New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
  $log = Join-Path $LogDir ("distrib-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
  $tools = Join-Path $CefSrc 'tools'
  # As mesmas opcoes que o automate-git.py usa com --minimal-distrib --no-distrib-docs --x64-build
  # --no-debug-build, sem simbolos nem arquivo.
  $cmd = "`"$(Join-Path $tools 'make_distrib.bat')`" --output-dir=`"$DistribDir`" --ninja-build --x64-build " +
         '--minimal --allow-partial --no-archive --no-docs --no-symbols'
  if ((Invoke-Cmd $cmd -LogFile $log -WorkDir $tools) -ne 0) { Fail "make_distrib falhou (ver $log)" }

  $dist = Get-ChildItem -LiteralPath $DistribDir -Directory | Where-Object { $_.Name -like 'cef_binary_*_windows64_minimal' } | Select-Object -First 1
  if (-not $dist) { Fail "make_distrib nao produziu cef_binary_*_windows64_minimal em $DistribDir" }
  Write-Ok $dist.Name

  # Runtime "plano", como o CefSharp o espera: Release\* (sem .lib) + Resources\* (paks, icudtl, locales).
  Get-ChildItem -LiteralPath (Join-Path $dist.FullName 'Release') -File |
    Where-Object { $_.Extension -ne '.lib' } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $RuntimeDir }
  Copy-Item -Path (Join-Path $dist.FullName 'Resources\*') -Destination $RuntimeDir -Recurse -Force

  # Avisos de licenca que tem de acompanhar o runtime: LICENSE.txt da CEF, CREDITS.html (licencas do
  # Chromium e das dependencias, incluindo a LGPL-2.1 do FFmpeg ligado estaticamente no libcef.dll) e
  # os patches, que sao a fonte correspondente das alteracoes feitas ao codigo.
  $licDir = Join-Path $RuntimeDir 'licenses'
  New-Item -ItemType Directory -Force -Path (Join-Path $licDir 'patches') | Out-Null
  foreach ($f in @('LICENSE.txt', 'CREDITS.html')) {
    $srcFile = Join-Path $dist.FullName $f
    if (-not (Test-Path -LiteralPath $srcFile)) { Fail "falta $f na distribuicao $($dist.Name)" }
    Copy-Item -LiteralPath $srcFile -Destination $licDir
  }
  foreach ($p in $Patches) { Copy-Item -LiteralPath $p.File -Destination (Join-Path $licDir 'patches') }

  foreach ($req in @('libcef.dll', 'chrome_elf.dll', 'icudtl.dat', 'resources.pak', 'v8_context_snapshot.bin',
                     'locales', 'licenses\LICENSE.txt', 'licenses\CREDITS.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $RuntimeDir $req))) { Fail "falta $req no runtime" }
  }
  Copy-Item -LiteralPath (Join-Path $BuildDir 'libcef.dll.pdb') -Destination $SymbolsDir -ErrorAction SilentlyContinue

  # Caminhos relativos sem aritmetica entre caminhos de origens diferentes: o $RuntimeDir pode vir
  # com nomes curtos 8.3 (C:\Users\JNIOR~1) e o FullName do Get-ChildItem com os longos
  # (o nome longo do perfil), com comprimentos diferentes. O Directory.EnumerateFiles devolve cada ficheiro
  # com o prefixo exatamente igual a pasta que recebe, por isso o corte e sempre certo.
  $files = @()
  $root = [IO.Path]::GetFullPath($RuntimeDir).TrimEnd('\')
  $entries = @()
  foreach ($full in [IO.Directory]::EnumerateFiles($root, '*', [IO.SearchOption]::AllDirectories)) {
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
      Fail "caminho fora do runtime: $full"
    }
    $rel = $full.Substring($root.Length + 1)
    if ($rel -eq 'manifest.json') { continue }
    $entries += [pscustomobject]@{ Rel = $rel.Replace('\', '/'); Full = $full }
  }
  foreach ($e in ($entries | Sort-Object Rel)) {
    $files += [ordered]@{
      path   = $e.Rel
      size   = (New-Object IO.FileInfo $e.Full).Length
      sha256 = Get-Sha256 $e.Full
    }
  }

  $patchInfo = @()
  foreach ($p in $Patches) {
    $patchInfo += [ordered]@{
      file   = 'patches/' + (Split-Path -Leaf $p.File)
      repo   = $(if ($p.Prefix) { 'src/' + $p.Prefix.TrimEnd('/') } else { 'src' })
      sha256 = Get-Sha256 $p.File
      files  = @(Get-PatchedFiles $p.File)
    }
  }

  $manifest = [ordered]@{
    schema          = 2
    mode            = $Mode
    description     = 'proprietary_codecs=true + ffmpeg_branding=Chromium: H.264/HEVC descodificados pela GPU (D3D11VideoDecoder), AAC pelo Media Foundation; o FFmpeg nao contem descodificadores H.264/HEVC/AAC (so desmultiplexadores e parsers); OpenH264 desligado.'
    cef             = [ordered]@{ version = $CefVersion; branch = $CefBranch; commit = $CefCommit; distrib = $dist.Name }
    chromium        = [ordered]@{ version = $ChromiumVersion; commit = (Get-CmdOutput (Git "-C `"$Src`" rev-parse HEAD")) }
    ffmpeg          = [ordered]@{
      commit                   = (Get-CmdOutput (Git "-C `"$FfmpegSrc`" rev-parse HEAD"))
      config                   = 'chromium/config/Chromium/win/x64'
      codec_list_sha256        = Get-Sha256 (Join-Path $FfmpegCfg 'libavcodec\codec_list.c')
      config_components_sha256 = Get-Sha256 (Join-Path $FfmpegCfg 'config_components.h')
    }
    cefsharp        = $CefSharpVersion
    gn_defines      = $GnDefines
    args_gn         = $gn
    gn_args_derived = Get-DerivedGnArgs
    patches         = $patchInfo
    toolchain       = [ordered]@{ visual_studio = $VsInstall; windows_sdk = $WindowsSdk }
    built_utc       = (Get-Item -LiteralPath (Join-Path $BuildDir 'libcef.dll')).LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
    packaged_utc    = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    files           = $files
  }
  $json = $manifest | ConvertTo-Json -Depth 8
  [IO.File]::WriteAllText((Join-Path $RuntimeDir 'manifest.json'), $json, (New-Object Text.UTF8Encoding $false))
  Write-Ok "$($files.Count) ficheiros em $RuntimeDir (manifest.json escrito)"
  Test-RuntimeManifest
}

# Rele o manifest.json e confirma que cada caminho existe no runtime com o tamanho e o SHA256
# gravados, e que nenhum ficheiro do runtime ficou de fora.
function Test-RuntimeManifest {
  $manifestPath = Join-Path $RuntimeDir 'manifest.json'
  $m = [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
  $root = [IO.Path]::GetFullPath($RuntimeDir).TrimEnd('\')
  $listed = @{}
  foreach ($e in @($m.files)) {
    if (-not $e.path -or $e.path.StartsWith('/') -or $e.path.Contains('..') -or $e.path.Contains(':')) {
      Fail "manifest.json: caminho invalido '$($e.path)'"
    }
    $full = Join-Path $root ($e.path.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { Fail "manifest.json: '$($e.path)' nao existe em $RuntimeDir" }
    if ((Get-Item -LiteralPath $full).Length -ne [int64]$e.size) { Fail "manifest.json: tamanho diferente em '$($e.path)'" }
    if ((Get-Sha256 $full) -ne $e.sha256) { Fail "manifest.json: SHA256 diferente em '$($e.path)'" }
    $listed[$e.path.ToLowerInvariant()] = $true
  }
  $count = 0
  foreach ($full in [IO.Directory]::EnumerateFiles($root, '*', [IO.SearchOption]::AllDirectories)) {
    $rel = $full.Substring($root.Length + 1).Replace('\', '/')
    if ($rel -eq 'manifest.json') { continue }
    $count++
    if (-not $listed.ContainsKey($rel.ToLowerInvariant())) { Fail "manifest.json: falta '$rel'" }
  }
  if ($count -ne @($m.files).Count) { Fail "manifest.json: $(@($m.files).Count) entradas para $count ficheiros" }
  Write-Ok "manifest.json relido: $count caminhos existem, com tamanho e SHA256 iguais"
}

# --- Principal ----------------------------------------------------------------------------------
$order = @('Check', 'Patch', 'Generate', 'Smoke', 'Build', 'Package')
$Steps = @($Steps | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($s in $Steps) {
  if (@('All') + $order -notcontains $s) { throw "ERRO: passo desconhecido '$s' (use All, $($order -join ', '))" }
}
if ($Steps -contains 'All') { $run = $order } else { $run = @($order | Where-Object { $Steps -contains $_ }) }
if ($run -notcontains 'Check') { $run = @('Check') + $run }   # a verificacao e sempre feita
$script:EffectiveJobs = $Jobs

Set-BuildEnvironment
Write-Host "Eteris / CEF $CefVersion - modo $Mode - passos: $($run -join ', ')"
foreach ($s in $run) {
  switch ($s) {
    'Check'    { Invoke-Check }
    'Patch'    { Invoke-Patch }
    'Generate' { Invoke-Generate }
    'Smoke'    { Invoke-Smoke }
    'Build'    { Invoke-Build }
    'Package'  { Invoke-Package }
  }
}
Write-Host ''
Write-Host 'Concluido.' -ForegroundColor Green
