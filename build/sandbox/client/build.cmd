@echo off
rem Compila a DLL cliente do bootstrap (passo D de docs/sandbox.md) com o CMake e as Build Tools do VS 2022.
rem Uso: build.cmd <distribuicao binaria da CEF do motor em uso> <pasta de saida> [nome da DLL] [montagem .NET]
rem   A distribuicao vem do make_distrib do proprio motor (build/cef-codecs: --minimal ja traz include, libcef_dll e
rem   cmake), para o wrapper ser do mesmo commit do libcef.dll. Nome padrao Eteris.Browser (o exe do bootstrap
rem   chama-se Eteris.Browser.exe) e montagem padrao Eteris.App.dll.
rem Resultado: <saida>\Release\<nome>.dll, com CRT estatica (/MT) e importacoes diretas so do KERNEL32.
setlocal
if "%~2"=="" (echo uso: build.cmd ^<CEF_ROOT^> ^<saida^> [nome] [montagem] & exit /b 1)
set "NAME=%~3"
if "%NAME%"=="" set "NAME=Eteris.Browser"
set "APPDLL=%~4"
if "%APPDLL%"=="" set "APPDLL=Eteris.App.dll"
cmake -S "%~dp0." -B "%~2" -G "Visual Studio 17 2022" -A x64 "-DCEF_ROOT=%~1" "-DETERIS_CLIENT_NAME=%NAME%" "-DETERIS_APP_DLL=%APPDLL%"
if errorlevel 1 exit /b 2
cmake --build "%~2" --config Release --target eteris_client -- /nologo /verbosity:minimal /nodeReuse:false
if errorlevel 1 exit /b 3
echo OK: %~2\Release\%NAME%.dll
