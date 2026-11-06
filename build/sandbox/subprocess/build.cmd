@echo off
rem Compila o processo filho nativo do motor (passo B de docs/sandbox.md): so CEF, sem .NET.
rem Uso: build.cmd <pasta CEF do pacote cef.sdk 156.0.15, ex. ...\packages\cef.sdk.156.0.15\CEF>
rem Requisitos: Build Tools do Visual Studio 2022 com o compilador C++ (x64).
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul
if errorlevel 1 exit /b 1
cd /d "%~dp0"
set "CEF=%~1"
rem NOMINMAX: as macros min/max do windows.h chocam com os cabecalhos do CEF.
cl /nologo /std:c++20 /O2 /MD /EHsc /DUNICODE /D_UNICODE /DNDEBUG /DNOMINMAX /DWIN32 /D_WINDOWS /I"%CEF%" subprocess.cpp /Fe:CefSharp.BrowserSubprocess.exe /link /SUBSYSTEM:WINDOWS /MANIFEST:EMBED /MANIFESTINPUT:subprocess.manifest /LIBPATH:"%CEF%\x64\release" /LIBPATH:"%CEF%\x64\release\VS2022" libcef.lib libcef_dll_wrapper.lib user32.lib
if errorlevel 1 exit /b 2
echo OK: CefSharp.BrowserSubprocess.exe (substitui o processo auxiliar .NET do CefSharp)
