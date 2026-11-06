# CefSharp com sandbox (passo C de `docs/sandbox.md`)

`windows-sandbox-info.patch` aplica-se ao CefSharp **v156.0.150** (`git clone --branch v156.0.150
https://github.com/cefsharp/CefSharp.git` e `git apply windows-sandbox-info.patch`).

Acrescenta `CefSettings.WindowsSandboxInfo` (IntPtr): o ponteiro que o `bootstrap.exe` do CEF entrega a `RunWinMain`.
Quando é definido, é passado a `CefInitialize` e `no_sandbox` passa a falso. Sem ele (omissão), o comportamento é o
de sempre (sem sandbox). Camadas alteradas: `CefSharp.Core.Runtime` (C++/CLI), a montagem de referência e
`CefSharp.Core` (C#).

Para compilar é preciso o Visual Studio 2022 (ou as Build Tools) com o componente
`Microsoft.VisualStudio.Component.VC.CLI.Support` (C++/CLI v143).

Compilação verificada a 2026-11-06 (ver «Passo C — resultado» em `docs/sandbox.md`): `build-cefsharp.cmd <pasta do
CefSharp>` depois de aplicar o patch, fixar o SDK 9 no `global.json` e mudar `net6.0` para `net8.0` nos 4 projetos.
Requer também o componente `Microsoft.NetCore.Component.SDK` das Build Tools.
