<#
  Prepara uma copia do CefSharp v156.0.150 para o build-cefsharp.cmd:
    1. aplica windows-sandbox-info.patch (CefSettings.WindowsSandboxInfo, patch v2);
    2. global.json -> SDK 9.0.x (o .NET 10 com o MSBuild 17.14 das Build Tools 2022 da NETSDK1145);
    3. net6.0 -> net8.0 nos 4 projetos compilados (o C++/CLI precisa do pacote Apphost da versao-alvo);
    4. pasta packages\ (formato packages.config do CefSharp.Core.Runtime: cef.sdk e SourceLink) copiada da cache
       global do NuGet (%USERPROFILE%\.nuget\packages), onde o restauro do Eteris ja as pos; o /restore do msbuild
       nao restaura packages.config.
  Uso: prepare-source.ps1 -Source <pasta do clone>   (git clone --depth 1 --branch v156.0.150 https://github.com/cefsharp/CefSharp.git)
#>
param([Parameter(Mandatory)] [string]$Source)
$ErrorActionPreference = 'Stop'
# Caminhos curtos (JNIOR~1) e acentos: so -LiteralPath e caminhos completos.
$Source = [IO.Path]::GetFullPath($Source)
$patch = Join-Path $PSScriptRoot 'windows-sandbox-info.patch'
Push-Location -LiteralPath $Source
try {
  $tag = (& git describe --tags --exact-match 2>$null)
  if ($tag -ne 'v156.0.150') { throw "esperado o CefSharp v156.0.150, encontrado '$tag'" }
  & git apply --check $patch
  if ($LASTEXITCODE -ne 0) { throw 'o patch nao se aplica (ja aplicado?)' }
  & git apply $patch
  if ($LASTEXITCODE -ne 0) { throw 'git apply falhou' }
  $gj = '{' + "`n" + '  "sdk": {' + "`n" + '    "version": "9.0.100",' + "`n" + '    "rollForward": "latestFeature"' + "`n" + '  }' + "`n" + '}' + "`n"
  [IO.File]::WriteAllText((Join-Path $Source 'global.json'), $gj)
  foreach ($p in 'CefSharp\CefSharp.netcore.csproj', 'CefSharp.Core\CefSharp.Core.netcore.csproj',
                 'CefSharp.Core.Runtime\CefSharp.Core.Runtime.netcore.vcxproj',
                 'CefSharp.Core.Runtime.RefAssembly\CefSharp.Core.Runtime.RefAssembly.netcore.csproj') {
    $f = Join-Path $Source $p
    $t = [IO.File]::ReadAllText($f)
    if ($t -notmatch '<TargetFramework>net6\.0</TargetFramework>') { throw "sem net6.0: $p" }
    [IO.File]::WriteAllText($f, ($t -replace '<TargetFramework>net6\.0</TargetFramework>', '<TargetFramework>net8.0</TargetFramework>'))
  }
  $cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
  [xml]$cfg = Get-Content -LiteralPath (Join-Path $Source 'CefSharp.Core.Runtime\packages.CefSharp.Core.Runtime.netcore.config')
  foreach ($pkg in $cfg.packages.package) {
    $from = Join-Path $cache (Join-Path $pkg.id.ToLowerInvariant() $pkg.version)
    $to = Join-Path $Source "packages\$($pkg.id).$($pkg.version)"
    if (-not (Test-Path -LiteralPath $from)) { throw "falta na cache do NuGet: $($pkg.id) $($pkg.version) (nuget restore)" }
    if (-not (Test-Path -LiteralPath $to)) { Copy-Item -LiteralPath $from -Destination $to -Recurse }
  }
  'OK: patch aplicado, SDK 9, net8.0 e packages'
} finally { Pop-Location }
