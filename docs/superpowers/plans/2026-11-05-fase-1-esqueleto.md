# Éteris — Fase 1: Esqueleto — Plano de Implementação

> **Documento histórico (2026-11-05).** Descreve o desenho inicial; partes foram abandonadas (o motor de pesquisa Atenix, o «DNT: 1», o «sem contas», a sincronização e o gestor de palavras-passe fora do âmbito). O estado atual está em `docs/motor.md`, `docs/sandbox.md`, `docs/palavras-passe.md`, `docs/sincronizacao.md` e `docs/paridade.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Um navegador Windows instalável (`Eteris-Setup.exe`, sem admin) que abre uma janela com uma aba Chromium, carrega páginas, e verifica atualizações no GitHub Releases ao arrancar.

**Architecture:** Projeto WPF (.NET 10, win-x64) com CefSharp a renderizar; `Program.Main` arranca o Velopack antes do WPF; caminhos de dados centralizados em `AppPaths`; CI no GitHub Actions compila/testa em push e publica instalador em tag `v*`.

**Tech Stack:** .NET 10 SDK, WPF, CefSharp.Wpf.NETCore (último estável), Velopack + `vpk`, xUnit, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-05-eteris-browser-design.md` (§3, §4.1 parcial, §4.4 caminhos, §4.7, §10 CI, §11 fase 1)

## Global Constraints

- Target `net10.0-windows`, `RuntimeIdentifier` `win-x64`, `SelfContained` true (spec §2, §4.7).
- Dados em `%LocalAppData%\Eteris\` — `eteris.db`, `profile\` (cache CEF), `filters\` (spec §4.4, §5.2).
- Instalação por utilizador, sem permissões de administrador (spec §4.7).
- Sem telemetria, sem crash reporting externo (spec §4.5). O único pedido de rede fora das páginas visitadas é a verificação de updates no GitHub.
- Nome do produto: **Éteris**; id de pacote Velopack: `Eteris`; exe: `Eteris.Browser.exe`.
- Página inicial provisória da fase 1: `https://pt.wikipedia.org` (nunca Google).
- Idioma da UI e textos: português de Portugal.
- Toolchains disponíveis: .NET SDK 10, Node 22, git. Rust ainda não (só na fase 3).
- Commits só depois de a identidade git estar configurada pelo dono (`git config --global user.name/email`); se não estiver, parar e pedir.

## Review Focus

1. Nome de utilizador Windows com acentos (`C:\Users\<utilizador>`, por exemplo «José») — o cache do CEF e a DB têm de abrir normalmente em caminhos não-ASCII. → teste em Task 2 (`AppPaths` devolve caminho sob `LocalApplicationData` sem alterar caracteres) + verificação manual em Task 6.
2. Abrir o Éteris duas vezes — o CEF falha se dois processos partilharem `profile\`. A segunda instância deve trazer a primeira para a frente e sair. → Task 3.
3. Arrancar sem rede — a verificação de updates não pode bloquear a UI nem mostrar erro. → teste em Task 5 (`UpdateService.CheckAsync` devolve `null` em exceção).
4. Fechar a janela principal — o processo tem de terminar (`Cef.Shutdown()`), sem processos `CefSharp.BrowserSubprocess` órfãos. → verificação manual em Task 6.
5. Instalar por cima de uma versão já instalada — Velopack deve atualizar sem pedir admin e manter `%LocalAppData%\Eteris\profile`. → verificação manual em Task 6 (instalar 0.1.0, depois 0.1.1).

---

## Estrutura de ficheiros (fase 1)

```
navegador/
├── .gitignore                         # bin/ obj/ node_modules/ target/ publish/ Releases/
├── .editorconfig
├── Eteris.sln
├── Directory.Build.props              # Nullable, ImplicitUsings, LangVersion, TreatWarningsAsErrors
├── README.md
├── build/
│   ├── publish.ps1                    # dotnet publish → publish/
│   └── pack.ps1                       # vpk pack → Releases/
├── src/Eteris.Browser/
│   ├── Eteris.Browser.csproj
│   ├── Program.cs                     # Main: Velopack → SingleInstance → App
│   ├── App.xaml / App.xaml.cs         # Cef.Initialize / Cef.Shutdown
│   ├── MainWindow.xaml / .cs          # barra de endereço + ChromiumWebBrowser
│   ├── Infrastructure/AppPaths.cs
│   ├── Infrastructure/SingleInstance.cs
│   ├── Infrastructure/CefConfigurator.cs
│   └── Updates/UpdateService.cs
├── tests/Eteris.Browser.Tests/
│   ├── Eteris.Browser.Tests.csproj
│   ├── AppPathsTests.cs
│   ├── CefConfiguratorTests.cs
│   └── UpdateServiceTests.cs
└── .github/workflows/
    ├── build.yml
    └── release.yml
```

---

### Task 1: Esqueleto do repositório

**Files:**
- Create: `.gitignore`, `.editorconfig`, `Directory.Build.props`, `Eteris.sln`, `README.md`

**Interfaces:**
- Produces: solução `Eteris.sln` à qual as tasks seguintes adicionam projetos; `Directory.Build.props` com `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.

- [ ] **Step 1: Criar `.gitignore`** com `bin/`, `obj/`, `publish/`, `Releases/`, `node_modules/`, `target/`, `*.user`, `.vs/`, `TestResults/`.

- [ ] **Step 2: Criar `.editorconfig`** (UTF-8, LF, 4 espaços para `.cs`, 2 para `.xml/.yml/.json`, `csharp_style_namespace_declarations = file_scoped`).

- [ ] **Step 3: Criar `Directory.Build.props`** com as três propriedades do bloco Interfaces.

- [ ] **Step 4: Criar a solução**

Run: `dotnet new sln -n Eteris`
Expected: `Eteris.sln` criado.

- [ ] **Step 5: Criar `README.md`** — título "Éteris", uma frase (navegador privado para Windows), secção "Desenvolvimento" com `dotnet build` e `dotnet test`, link para a spec.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: esqueleto do repositório Éteris"
```

---

### Task 2: Projeto WPF com CefSharp e `AppPaths`

**Files:**
- Create: `src/Eteris.Browser/Eteris.Browser.csproj`, `src/Eteris.Browser/App.xaml`, `src/Eteris.Browser/App.xaml.cs`, `src/Eteris.Browser/MainWindow.xaml`, `src/Eteris.Browser/MainWindow.xaml.cs`, `src/Eteris.Browser/Infrastructure/AppPaths.cs`, `src/Eteris.Browser/Infrastructure/CefConfigurator.cs`
- Create: `tests/Eteris.Browser.Tests/Eteris.Browser.Tests.csproj`, `tests/Eteris.Browser.Tests/AppPathsTests.cs`, `tests/Eteris.Browser.Tests/CefConfiguratorTests.cs`

**Interfaces:**
- Produces:
  - `static class AppPaths` (namespace `Eteris.Browser.Infrastructure`): `string Root` (= `%LocalAppData%\Eteris`), `string Database` (= `Root\eteris.db`), `string Profile` (= `Root\profile`), `string Filters` (= `Root\filters`), `void EnsureDirectories()` (cria `Root`, `Profile`, `Filters`). Todas as propriedades construídas com `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` + `Path.Combine`.
  - `static class CefConfigurator`: `CefSettings Build(string profilePath)` — devolve `CefSettings` com `CachePath = profilePath`, `Locale = "pt-PT"`, `LogSeverity = LogSeverity.Disable`, e `CefCommandLineArgs` contendo `enable-media-stream=false`, `disable-gpu-shader-disk-cache`; `void Initialize()` chama `AppPaths.EnsureDirectories()` e `Cef.Initialize(Build(AppPaths.Profile), performDependencyCheck: true, browserProcessHandler: null)`.
  - `MainWindow`: `TextBox AddressBox`, `ChromiumWebBrowser Browser`; Enter em `AddressBox` chama `Navigate(string input)` que prefixa `https://` quando não há `://` e faz `Browser.Load`. `Browser.AddressChanged` atualiza `AddressBox.Text`; `Browser.TitleChanged` atualiza `Title` para `"{titulo} — Éteris"`.

- [ ] **Step 1: Criar o projeto WPF e o de testes e adicioná-los à solução**

Run:
```
dotnet new wpf -n Eteris.Browser -o src/Eteris.Browser -f net10.0
dotnet new xunit -n Eteris.Browser.Tests -o tests/Eteris.Browser.Tests -f net10.0
dotnet sln add src/Eteris.Browser tests/Eteris.Browser.Tests
dotnet add tests/Eteris.Browser.Tests reference src/Eteris.Browser
```
Expected: ambos os projetos na solução.

- [ ] **Step 2: Configurar `Eteris.Browser.csproj`** — `<TargetFramework>net10.0-windows</TargetFramework>`, `<UseWPF>true</UseWPF>`, `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, `<SelfContained>true</SelfContained>`, `<AssemblyName>Eteris.Browser</AssemblyName>`, `<ApplicationIcon>` (deixar vazio nesta fase), `<Version>0.1.0</Version>`, `<Product>Éteris</Product>`, e `dotnet add package CefSharp.Wpf.NETCore`. No projeto de testes, `<TargetFramework>net10.0-windows</TargetFramework>`, `<UseWPF>true</UseWPF>`, `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`.

- [ ] **Step 3: Escrever os testes de `AppPaths`** em `AppPathsTests.cs`

```csharp
[Fact] public void Root_IsUnderLocalApplicationData_NamedEteris()
    => Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Eteris"), AppPaths.Root);
[Fact] public void Database_IsEterisDbInsideRoot() => Assert.Equal(Path.Combine(AppPaths.Root, "eteris.db"), AppPaths.Database);
[Fact] public void Profile_IsProfileInsideRoot()   => Assert.Equal(Path.Combine(AppPaths.Root, "profile"), AppPaths.Profile);
[Fact] public void Filters_IsFiltersInsideRoot()   => Assert.Equal(Path.Combine(AppPaths.Root, "filters"), AppPaths.Filters);
[Fact] public void Root_PreservesNonAsciiUserName() => Assert.Contains(Environment.UserName, AppPaths.Root); // falha se algum normalizador alterar acentos
```

- [ ] **Step 4: Correr os testes para confirmar que falham**

Run: `dotnet test tests/Eteris.Browser.Tests`
Expected: erro de compilação — `AppPaths` não existe.

- [ ] **Step 5: Implementar `AppPaths`** conforme Interfaces.

- [ ] **Step 6: Correr os testes**

Run: `dotnet test tests/Eteris.Browser.Tests`
Expected: 5 PASS.

- [ ] **Step 7: Escrever os testes de `CefConfigurator.Build`** em `CefConfiguratorTests.cs`

```csharp
[Fact] public void Build_SetsCachePathToGivenProfile() => Assert.Equal(@"C:\x\profile", CefConfigurator.Build(@"C:\x\profile").CachePath);
[Fact] public void Build_UsesPortugueseLocale()        => Assert.Equal("pt-PT", CefConfigurator.Build("p").Locale);
[Fact] public void Build_DisablesCefLogging()          => Assert.Equal(LogSeverity.Disable, CefConfigurator.Build("p").LogSeverity);
[Fact] public void Build_DisablesMediaStreamByDefault() => Assert.Equal("false", CefConfigurator.Build("p").CefCommandLineArgs["enable-media-stream"]);
```

- [ ] **Step 8: Correr, confirmar que falha a compilar; implementar `CefConfigurator`; correr de novo**

Run: `dotnet test tests/Eteris.Browser.Tests`
Expected: 9 PASS.

- [ ] **Step 9: Implementar `App.xaml.cs`** — `OnStartup`: `CefConfigurator.Initialize()`, criar e mostrar `MainWindow`; `OnExit`: `Cef.Shutdown()`. Em `App.xaml`, remover `StartupUri`.

- [ ] **Step 10: Implementar `MainWindow`** — Grid com duas linhas: `AddressBox` (altura auto) e `Browser` (`Address="https://pt.wikipedia.org"`); handlers conforme Interfaces.

- [ ] **Step 11: Correr a aplicação**

Run: `dotnet run --project src/Eteris.Browser`
Expected: janela "Wikipédia — Éteris" com a página carregada; escrever `example.com` + Enter navega para `https://example.com`.

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "feat: janela WPF com CefSharp, AppPaths e CefConfigurator"
```

---

### Task 3: Instância única

**Files:**
- Create: `src/Eteris.Browser/Infrastructure/SingleInstance.cs`, `src/Eteris.Browser/Program.cs`
- Modify: `src/Eteris.Browser/Eteris.Browser.csproj` (adicionar `<StartupObject>Eteris.Browser.Program</StartupObject>`)
- Test: `tests/Eteris.Browser.Tests/SingleInstanceTests.cs`

**Interfaces:**
- Produces:
  - `sealed class SingleInstance : IDisposable` com `static SingleInstance? TryAcquire(string name)` — devolve instância se este processo obteve o `Mutex` global `Local\{name}`, `null` se já existe outro dono; `Dispose` liberta.
  - `Program.Main()` (`[STAThread]`): `TryAcquire("Eteris.Browser")`; se `null`, chama `SingleInstance.ActivateExisting("Eteris.Browser")` (procura processo `Eteris.Browser` com `MainWindowHandle != 0` e chama `SetForegroundWindow` via P/Invoke) e retorna; senão cria `App`, `InitializeComponent()`, `Run()`.

- [ ] **Step 1: Escrever o teste**

```csharp
[Fact] public void TryAcquire_SecondCallWithSameName_ReturnsNull()
{
    var name = "Eteris.Test." + Guid.NewGuid();
    using var first = SingleInstance.TryAcquire(name);
    Assert.NotNull(first);
    Assert.Null(SingleInstance.TryAcquire(name));
}
[Fact] public void TryAcquire_AfterDispose_SucceedsAgain()
{
    var name = "Eteris.Test." + Guid.NewGuid();
    SingleInstance.TryAcquire(name)!.Dispose();
    using var again = SingleInstance.TryAcquire(name);
    Assert.NotNull(again);
}
```

- [ ] **Step 2: Correr para confirmar falha de compilação** — `dotnet test tests/Eteris.Browser.Tests`.

- [ ] **Step 3: Implementar `SingleInstance` e `Program.cs`**, configurar `StartupObject`.

- [ ] **Step 4: Correr os testes** — Expected: 11 PASS.

- [ ] **Step 5: Verificar manualmente** — `dotnet run --project src/Eteris.Browser` duas vezes em terminais distintos: a segunda termina de imediato e a janela da primeira vem para a frente.

- [ ] **Step 6: Commit** — `git commit -am "feat: instância única com mutex e Program.Main explícito"` (usar `git add -A` antes).

---

### Task 4: CI de build e testes

**Files:**
- Create: `.github/workflows/build.yml`

**Interfaces:**
- Produces: workflow `build` que corre em `push` e `pull_request`, runner `windows-latest`, passos: `actions/checkout@v4`, `actions/setup-dotnet@v4` com `dotnet-version: 10.0.x`, `dotnet restore`, `dotnet build -c Release --no-restore`, `dotnet test -c Release --no-build`.

- [ ] **Step 1: Escrever `build.yml`** conforme Interfaces.

- [ ] **Step 2: Validar localmente a sequência** — correr os três comandos `dotnet` na raiz. Expected: build OK, 11 testes PASS.

- [ ] **Step 3: Commit** — `git add .github && git commit -m "ci: build e testes no GitHub Actions"`.

---

### Task 5: Velopack — instalador e atualizações

**Files:**
- Create: `src/Eteris.Browser/Updates/UpdateService.cs`, `build/publish.ps1`, `build/pack.ps1`, `.github/workflows/release.yml`
- Modify: `src/Eteris.Browser/Program.cs` (primeira linha de `Main`: `VelopackApp.Build().Run();`), `src/Eteris.Browser/App.xaml.cs` (após mostrar a janela: `_ = UpdateService.CheckAndDownloadAsync()`), `src/Eteris.Browser/Eteris.Browser.csproj` (`dotnet add package Velopack`)
- Test: `tests/Eteris.Browser.Tests/UpdateServiceTests.cs`

**Interfaces:**
- Produces:
  - `static class UpdateService`: `const string RepoUrl = "https://github.com/OWNER/eteris"` (OWNER substituído quando o repositório GitHub existir — ver Task 6); `Task<UpdateInfo?> CheckAsync(IUpdateSource source)` — cria `UpdateManager(source)`, se `!IsInstalled` devolve `null`, senão `await CheckForUpdatesAsync()`; **qualquer exceção devolve `null`** (sem rede, rate-limit); `Task CheckAndDownloadAsync()` — usa `new GithubSource(RepoUrl, null, false)`, chama `CheckAsync`, se houver update faz `DownloadUpdatesAsync` e guarda em `static UpdateInfo? Pending` (aplicação no próximo arranque; o botão "Reiniciar para atualizar" fica para a fase 4 no menu ⋮).
  - `build/publish.ps1`: `dotnet publish src/Eteris.Browser -c Release -r win-x64 --self-contained -o publish`.
  - `build/pack.ps1 -Version <x.y.z>`: `vpk pack --packId Eteris --packVersion $Version --packDir publish --mainExe Eteris.Browser.exe --packTitle "Éteris" --outputDir Releases`.
  - `release.yml`: em `push` de tag `v*`, runner `windows-latest`: setup-dotnet 10, `dotnet tool install -g vpk`, `publish.ps1`, `pack.ps1 -Version ${tag sem v}`, `vpk upload github --repoUrl $RepoUrl --publish --releaseName "Éteris $tag" --tag $tag --token ${{ secrets.GITHUB_TOKEN }}`.

- [ ] **Step 1: Escrever o teste** em `UpdateServiceTests.cs`

```csharp
sealed class ThrowingSource : IUpdateSource
{
    public Task<VelopackAssetFeed> GetReleaseFeed(ILogger l, string c, string? ch, bool p, VelopackAsset? a) => throw new HttpRequestException("sem rede");
    public Task DownloadReleaseEntry(ILogger l, VelopackAsset a, string f, Action<int> pr, CancellationToken ct) => throw new HttpRequestException("sem rede");
}
[Fact] public async Task CheckAsync_WhenSourceThrows_ReturnsNull()
    => Assert.Null(await UpdateService.CheckAsync(new ThrowingSource()));
[Fact] public async Task CheckAsync_WhenNotInstalled_ReturnsNull()   // a correr do dotnet test, a app nunca está "instalada"
    => Assert.Null(await UpdateService.CheckAsync(new GithubSource(UpdateService.RepoUrl, null, false)));
```

(Se a assinatura de `IUpdateSource` na versão instalada do Velopack diferir, ajustar o stub à interface real — o comportamento testado não muda.)

- [ ] **Step 2: Correr para confirmar falha** — `dotnet test tests/Eteris.Browser.Tests`.

- [ ] **Step 3: Implementar `UpdateService`, alterar `Program.cs` e `App.xaml.cs`.**

- [ ] **Step 4: Correr os testes** — Expected: 13 PASS.

- [ ] **Step 5: Escrever `publish.ps1`, `pack.ps1`, `release.yml`.**

- [ ] **Step 6: Gerar um instalador local**

Run: `dotnet tool install -g vpk; .\build\publish.ps1; .\build\pack.ps1 -Version 0.1.0`
Expected: `Releases\Eteris-win-Setup.exe` existe.

- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat: instalador e auto-update com Velopack"`.

---

### Task 6: Verificação de ponta a ponta e repositório GitHub

**Files:**
- Modify: `src/Eteris.Browser/Updates/UpdateService.cs` (`RepoUrl` real), `README.md` (secção "Instalar")

- [ ] **Step 1: Pedir ao dono o repositório GitHub** — nome sugerido `eteris`, público ou privado à escolha. Criar com `gh repo create <owner>/eteris --source . --push` **só com autorização explícita**. Substituir `OWNER` em `RepoUrl`.

- [ ] **Step 2: Instalar `Releases\Eteris-win-Setup.exe` sem elevação** — Expected: instala em `%LocalAppData%\Eteris`, atalho no Menu Iniciar, janela abre com a Wikipédia; `%LocalAppData%\Eteris\profile` criado com nome de utilizador acentuado intacto.

- [ ] **Step 3: Fechar a janela** — Expected: no Gestor de Tarefas não resta `Eteris.Browser` nem `CefSharp.BrowserSubprocess`.

- [ ] **Step 4: Testar atualização** — bump `<Version>` para 0.1.1, `pack.ps1 -Version 0.1.1`, correr `Releases\Eteris-win-Setup.exe` de novo por cima. Expected: atualiza sem admin, `profile\` mantido (cookies da Wikipédia continuam).

- [ ] **Step 5: Tag e release** — `git tag v0.1.0 && git push --tags` (com autorização). Expected: workflow `release` verde, Release `v0.1.0` com `Eteris-win-Setup.exe` e `RELEASES`.

- [ ] **Step 6: Atualizar `README.md`** com o link do último release e commit — `git commit -am "docs: instruções de instalação"`.

---

## Roteiro das fases seguintes (cada uma terá o seu plano)

| Fase | Plano | Entregável | Tarefas principais |
|---|---|---|---|
| 2 | `fase-2-navegador-usavel.md` | Navegador do dia a dia | TabViewModel + barra de abas; `OmniboxClassifier` (URL vs pesquisa, tabela de casos); `SearchEngine` presets com `{q}`; SQLite (`HistoryRepository`, `BookmarkRepository`, `SessionRepository`, `SettingsRepository`); sugestões locais; downloads (`IDownloadHandler`); atalhos; privacidade por defeito (DNT/GPC, cookies 3ª parte, WebRTC) |
| 3 | `fase-3-shields.md` | Bloqueio ativo por defeito | Instalar rustup; crate `eteris_shields` cdylib + C-ABI; `cargo test`; P/Invoke `ShieldsNative`; `OnBeforeResourceLoad`; CSS cosmético; `FilterListUpdater` semanal + listas embutidas; contador por aba; exceções por host; teste de fumo |
| 4 | `fase-4-webui.md` | Páginas `eteris://` | Vite+TS+Preact; `ISchemeHandlerFactory` com verificação de origem; `window.eteris` tipado; newtab, settings, history, bookmarks, downloads, error; Playwright |
| 5 | `fase-5-atenix.md` | Atenix no ar | `settings.yml`, tema, docker-compose (SearXNG+Caddy+Valkey), `deploy.sh`, VM Oracle Free, DuckDNS, smoke test, motor padrão + reserva no browser |
| 6 | `fase-6-lancamento.md` | `v1.0.0` público | Tema escuro, logo/ícone, página sobre, política de privacidade, README final, release |
| pós | `atenix-aspnet.md` | Atenix próprio | Spec própria + plano, mantendo o contrato `/search?q=&format=json` |
