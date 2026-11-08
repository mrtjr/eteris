# CefSharp com sandbox (passos C e D de `docs/sandbox.md`)

`windows-sandbox-info.patch` (v2) aplica-se ao CefSharp **v156.0.150** e acrescenta `CefSettings.WindowsSandboxInfo`
(IntPtr): o ponteiro que o `bootstrap.exe` da CEF entrega a `RunWinMain` (a DLL `build/sandbox/client` expõe-no ao .NET
por `EterisSandboxInfo()`). Quando é definido:
- é passado a `CefInitialize` e `no_sandbox` passa a falso;
- o `BrowserSubprocessPath` é ignorado e limpo na cópia passada à CEF (a CEF desliga o sandbox sempre que
  `browser_subprocess_path` está preenchido; os filhos passam a ser o próprio exe, isto é, o bootstrap) e as
  verificações do processo filho .NET (`BrowserSubprocessPath`, `DependencyChecker`) são saltadas;
- `CefMainArgs` leva o `HINSTANCE` do exe (como a cefsimple; inofensivo sem sandbox).

Sem ele (omissão) o comportamento é o de sempre (sem sandbox). Camadas alteradas: `CefSharp.Core.Runtime` (C++/CLI), a
montagem de referência e `CefSharp.Core` (C#).

## Compilar (verificado a 2026-11-08)

Requisitos: Build Tools do Visual Studio 2022 com `Microsoft.VisualStudio.Component.VC.CLI.Support` (C++/CLI v143) e
`Microsoft.NetCore.Component.SDK`; .NET SDK 9 instalado.

```
git clone --depth 1 --branch v156.0.150 https://github.com/cefsharp/CefSharp.git <pasta>
powershell -File prepare-source.ps1 -Source <pasta>     # patch, SDK 9, net8.0, packages\ da cache do NuGet
build-cefsharp.cmd <pasta>                              # Core.Runtime, Core e montagem de referência (x64)
powershell -File pack-local.ps1 -Source <pasta> -Feed <feed> -N <n>
```

`pack-local.ps1` produz `CefSharp.Common.NETCore` e `CefSharp.Wpf.NETCore` **156.0.150-eteris.N** a partir dos
pacotes oficiais 156.0.150 (cache do NuGet), trocando só `CefSharp.Core.dll`/`CefSharp.Core.Runtime.dll` de win-x64
e as montagens de referência; x86 e arm64 ficam os oficiais (o Éteris só publica win-x64). Compila sem a assinatura de
nome forte (`/p:LinkKeyFile=`): o .NET não a verifica ao carregar.
