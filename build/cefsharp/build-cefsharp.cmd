@echo off
rem Compila as bibliotecas do CefSharp alteradas por windows-sandbox-info.patch (x64, Release).
rem Uso: build-cefsharp.cmd <pasta do codigo do CefSharp v156.0.150, com o patch aplicado>
rem Requisitos: Visual Studio 2022 / Build Tools com C++/CLI (VC.CLI.Support) e .NET SDK (NetCore.Component.SDK).
rem Nao usa o build.ps1 oficial: ele importa o ambiente do vcvarsall.bat com uma conversao de texto que estraga
rem nomes de utilizador com acentos (APPDATA, USERPROFILE...).
rem Antes de compilar, na copia do CefSharp:
rem   - global.json: SDK 9.0.x (o .NET 10 com o MSBuild 17.14 da NETSDK1145);
rem   - TargetFramework net6.0 -> net8.0 nos 4 projetos (o C++/CLI precisa do pacote Apphost da versao-alvo).
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64
if errorlevel 1 exit /b 1
cd /d "%~1"
if errorlevel 1 exit /b 1
rem "/p:LinkKeyFile=" compila sem a assinatura de nome forte (a chave do CefSharp precisa do repositorio de
rem chaves do Windows); o .NET nao verifica estas assinaturas ao carregar.
msbuild /nologo /nodeReuse:false /verbosity:minimal /restore /t:build /p:Platform=x64 /p:Configuration=Release "/p:SolutionDir=%CD%\\" "/p:LinkKeyFile=" CefSharp.Core.Runtime\CefSharp.Core.Runtime.netcore.vcxproj
if errorlevel 1 exit /b 2
msbuild /nologo /nodeReuse:false /verbosity:minimal /restore /t:build /p:Platform=x64 /p:Configuration=Release "/p:SolutionDir=%CD%\\" "/p:LinkKeyFile=" CefSharp.Core\CefSharp.Core.netcore.csproj
if errorlevel 1 exit /b 3
echo OK: CefSharp.Core.Runtime\bin.netcore\x64\Release\CefSharp.Core.Runtime.dll e CefSharp.Core\bin.netcore\x64\Release\net8.0\CefSharp.Core.dll
