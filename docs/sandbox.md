# Sandbox no Éteris — investigação (2026-11-06)

Pergunta: dá para isolar os processos das páginas como no Chrome, Firefox e Brave? **Resposta: dá, mas é trabalho
de 1–2 semanas e obriga a alterar o CefSharp.** Ver o plano em baixo.

## Estado atual (medido)

Os 6 processos do motor do Éteris (páginas, placa gráfica, utilitários) correm com `--no-sandbox`. Uma falha
explorável no código de uma página fica com os mesmos acessos que o utilizador. Nas três referências esses
processos correm fechados (sem acesso a ficheiros nem ao sistema).

## Como o sandbox funciona hoje no CEF (Windows)

- Desde o CEF 138, a única forma suportada é o **bootstrap**: um executável do CEF (`bootstrap.exe`, renomeado
  para o nome da aplicação) arranca a aplicação como uma **DLL nativa** que exporta `RunWinMain` e entrega-lhe a
  informação do sandbox. A biblioteca `cef_sandbox.lib` (o modelo antigo) deixou de ser distribuída.
  Interface: [`cef_sandbox_win.h`](https://github.com/chromiumembedded/cef/blob/master/include/cef_sandbox_win.h).
- Essa informação tem de chegar ao `CefInitialize` no processo principal (é ele que cria os processos fechados) e ao
  `CefExecuteProcess` em cada processo filho, lançados também através do bootstrap.
- O bootstrap e a `libcef.dll` têm de ser da mesma versão; o bootstrap vem na distribuição binária completa do CEF
  (não no pacote NuGet). A assinatura de código fica a cargo da aplicação.
- Outros projetos fizeram esta migração recentemente: [Tauri](https://github.com/tauri-apps/tauri/pull/16431),
  [waterui](https://github.com/water-rs/waterui/issues/897).

## Porque é difícil no Éteris

1. **O CefSharp não deixa passar a informação do sandbox** ao `CefInitialize` (não há nenhuma API para isso; o
   código C++/CLI chama-o com `nullptr`). É preciso alterar o CefSharp: versão própria compilada a partir do código
   (BSD) ou contribuição aceite no projeto oficial.
2. **Os processos das páginas não podem carregar o .NET dentro do sandbox.** O processo filho do CefSharp
   (`CefSharp.BrowserSubprocess`) é .NET; com sandbox, os filhos têm de ser nativos (só CEF). O que o Éteris usava
   desse processo filho (até ao passo A) deixava de funcionar:
   - 19 chamadas de JavaScript (`EvaluateScriptAsync`, `ExecuteScriptAsync`, `EvaluateScriptAsPromiseAsync`,
     `ExecuteJavaScriptAsync`) em 10 ficheiros;
   - `CosmeticFilterHandler.OnContextCreated` (filtros cosméticos).
   Tudo isto passou a ser feito pelo DevTools do lado do navegador (`Runtime.evaluate`,
   `Page.addScriptToEvaluateOnNewDocument`), como os scriptlets já faziam — ver «Passo A — resultado» e «Regras para
   o código».

## Passo D — resultado (2026-11-08, ramo `sandbox`): D0–D4 e D6 feitos, D5 em parte

**D0 — o motor próprio isola as páginas com o bootstrap (sem .NET).** `cefsimple_dll` compilado na árvore do motor
(`autoninja -n` antes: 220 passos, só o `libcef_dll_wrapper_dll` e o `cefsimple.dll`; o `libcef.dll` ficou igual,
SHA256 `4D5097CE…F20C` antes e depois, o mesmo do runtime publicado). Pasta de teste com o runtime, `bootstrap.exe`
renomeado para `cefsimple.exe` e o `cefsimple.dll`. `chrome://sandbox` lido por CDP:

| Processo | Sandbox | Bloqueio | Integridade |
|---|---|---|---|
| Renderer (2) | Renderer | Lockdown | S-1-16-0 Untrusted |
| GPU | GPU | Limited | S-1-16-4096 Low |
| Storage Service | Service | Lockdown | Untrusted |
| Audio Service | (script de tokens) | — | Low |
| Network Service | **Not Sandboxed** (igual ao Chrome 156 sem experiência de campo: D9) | | |

`build/sandbox/sandbox-check.ps1` (tokens dos filhos, sem olhar para a tela) confirma: renderers Untrusted com token
restrito, GPU Low, nenhum filho com `--no-sandbox`. Com o GPU fechado, o MP4 H.264 1280×720 toca (126 quadros, 0
perdidos, `err: null`), o `.m4a` e o `.aac` (AAC-LC) descodificam, e a sonda dá `probably` para H.264, AAC, HE-AAC,
HEVC, VP9, AV1, Opus e MP3.

**D1 — CefSharp com o patch v2** (`build/cefsharp`, ver o README): compila (`prepare-source.ps1` +
`build-cefsharp.cmd`) e `pack-local.ps1` produz o feed local `156.0.150-eteris.1`.

**D2 — DLL cliente do bootstrap** (`build/sandbox/client`: `eteris_client.cpp`, CMake contra a distribuição do
próprio motor, `make_distrib --minimal`): `dumpbin /exports` → `EterisSandboxInfo`, `RunWinMain`; `/imports` →
só `KERNEL32.dll` direto, `libcef.dll` e `SHELL32.dll` em carga diferida; CRT estática (`/MT`, sem `VCRUNTIME140`).
Testes: `SandboxBootstrapSourceTests` (esquema `eteris://` igual nos dois lados, exportações, patch).

**D3 — o Éteris .NET arranca pelo bootstrap (ainda sem sandbox).** Cópia de `publish` com `EterisSB.exe` (o
`bootstrap.exe` do runtime) + `EterisSB.dll` (a DLL cliente compilada com `ETERIS_APP_DLL=Eteris.Browser.dll`;
`build/sandbox/assemble-bootstrap.ps1` junta-os e verifica o SHA256 do `libcef.dll` e a ausência do recurso
`CEF_INSTALLER_CONFIG`). O processo principal é o `EterisSB.exe` com o .NET dentro (hostfxr): abriu a Wikipédia, e um
2.º arranque entregou `--new-tab` (abre `eteris://newtab`) e um link à instância aberta e saiu em 116–195 ms com código
0. O WPF arrancou, portanto a thread é STA. Janela com título em 827–989 ms, contra 869–919 ms pelo apphost (mesmo
perfil, 2 arranques cada): sem diferença mensurável.

**D4 — sandbox ligado no Éteris.** Publicação com o CefSharp do feed local (`-p:EterisCefSharpFeed=<feed>
-p:EterisCefSharpN=2`, que define `ETERIS_CEFSHARP_SANDBOX`), `Infrastructure/SandboxInfo.cs` (lê `EterisSandboxInfo`
da DLL já carregada) e `CefConfigurator` (`WindowsSandboxInfo` + `performDependencyCheck: false`). Os filhos passaram a
ser o próprio `EterisSB.exe` (nenhum `CefSharp.BrowserSubprocess.exe`). `chrome://sandbox` lido por CDP
(`ETERIS_TEST_DEVTOOLS_PORT`): **3 renderers Renderer/Lockdown/Untrusted, GPU Limited/Low, Storage Service
Lockdown/Untrusted; só o Network Service «Not Sandboxed»** (como no motor puro e no Chrome 156 sem experiência; D9).
`sandbox-check.ps1`: nenhum filho com `--no-sandbox`; Audio Service Low. Com o sandbox: Wikipédia; MP4 H.264
1280×720 (127 quadros, 0 perdidos), `.m4a` e `.aac` tocam; `eteris://newtab` (o esquema registado pela DLL nativa
nos filhos); janela privada (`--private`, «Nova aba privada»); transferência de um ficheiro (23 912 bytes, igual ao
original); fecho limpo (WM_CLOSE, sem forçar).

Nota de compilação: a montagem de referência do CefSharp (`RefAssembly`) sai assinada e o `CefSharp.Core` compilado
referencia o `CefSharp.Core.Runtime` sem nome forte; o pacote local usa a própria montagem x64 como referência (o
`eteris.1` falhava a compilar o Éteris; `eteris.2` corrige).

**D5 (parcial) — regressão sob sandbox, sem teclado nem rato.** Impressão (`Page.printToPDF`: PDF de 7 páginas,
470 KB) e o leitor de PDF da CEF (`chrome-extension://mhjf…/index.html`) a abrir esse PDF; memória dos filhos (mesma
página, 6 filhos): processo filho .NET sem sandbox 237,9 MB privados / 417,5 MB de conjunto de trabalho, filhos nativos
com sandbox 181,9 MB / 269,2 MB. **Por fazer** (precisam de teclado/rato no Éteris de teste): centro de multimédia,
esconder elemento, escurecer site, leitura F9, captura inteira, anúncio em nova aba, escolher arquivo, WebRTC, tela
dividida, abas do motor de vídeo, `perf-render.ps1`. A 1.ª tentativa (2026-11-08 05:2x) ficou inválida: dois avisos
«Segurança do Windows» da Firewall sobre o **Google Chrome** do dono (Google LLC; abertos às 04:58 e 05:03, antes de
qualquer teste do Éteris nesse intervalo) ficaram no primeiro plano e os scripts de teclas abortam quando o Éteris
não está à frente. Não foram tocados.

**D6 — integração no repositório (opcional, opção B de nomes).** `-p:EterisSandbox=true` (ou
`build/publish.ps1 -Sandbox -CefSharpFeed <feed> -CefSharpN 2 [-SandboxClientDll <dll>]`): o .NET passa a
`Eteris.App`, a saída leva `Eteris.Browser.exe` = bootstrap do runtime e `Eteris.Browser.dll` = DLL cliente
(`build/sandbox/client/build.cmd <distribuição> build\sandbox\client\out`), e a publicação deixa de trazer o
`CefSharp.BrowserSubprocess.*` e o apphost `Eteris.App.exe`. Verificado numa publicação na scratchpad:
`Eteris.Browser.exe` com sandbox (renderers Untrusted, GPU Low) e com `--eteris-no-sandbox` (filhos = o próprio exe,
`--no-sandbox`, integridade Medium), 2.º arranque entrega `--new-tab`, fecho limpo. Sem a opção nada muda (CefSharp
oficial; testes `SandboxInfoTests`). Os dois pack URIs do menu usam o nome da montagem em execução
(`typeof(MainWindow).Assembly`), em vez de `Eteris.Browser` escrito à mão.

**D9 (primeira sonda).** No motor puro (cefsimple + bootstrap), `--enable-features=NetworkServiceSandbox` liga a
funcionalidade (`chrome://sandbox`: `NetworkServiceSandbox` `enabled: true`) mas o Network Service continua «Not
Sandboxed» e com integridade Medium; as páginas carregam. Em `SystemNetworkContextManager::IsNetworkSandboxEnabled` só
sobram dois motivos: a preferência `NetworkServiceSandboxEnabled` no Local State ou o recuo depois de um arranque
falhado em LPAC (`kDisabledBecauseOfFailedLaunch`). Próximo passo do D9: registo da CEF ligado
(`--enable-logging --v=1`) para ver qual.

**Falta:** D5 interativo (em cima), D9 (`NetworkServiceSandbox`) e o passo E (ícone/versão do bootstrap — hoje
«CEF Bootstrap Application» —, assinatura do exe, `chrome_elf.dll` e DLL com o mesmo certificado, Velopack, CI).

## Passo A — resultado (2026-11-07): feito

Toda a execução de JavaScript nas páginas passou para `Browser/PageScript.cs` (DevTools `Runtime.evaluate`, uma
sessão por página, reutilizada): 18 chamadas em 9 ficheiros (`MainWindow.Audio.cs`, `.Fullscreen.cs`, `.Hide.cs`,
`.Media.cs`, `.Palette.cs`, `.Reader.cs`, `.Screenshot.cs`, `.Shield.cs`, `Shields/PickerScript.cs`), sem nenhuma
`EvaluateScriptAsync`/`ExecuteScriptAsync` no código. `Shields/CosmeticFilterHandler.cs` (o `OnContextCreated` do
processo filho) foi apagado: as regras cosméticas do site passaram a `CosmeticScripts.InjectEarly`, registadas pelo
`ScriptletInjector` antes dos scripts da página (`MainWindow.Shield.cs`, `SiteCosmeticScript`), como os scriptlets.
O Éteris corre com o processo filho nativo de `build/sandbox/subprocess` (passo B).

Verificado por teste: `SandboxRulesTests` (nenhuma API do processo filho no código) e `InjectedScriptsSyntaxTests`
(sintaxe dos scripts). Verificado ao vivo (2026-11-07), com o mesmo build Release em duas cópias — processo filho
oficial do CefSharp (153 KB, .NET) e processo filho nativo (454 KB, só CEF) — com resultados idênticos: centro de
multimédia (o som arranca na página e o ícone de som na aba e o botão do centro aparecem), esconder um elemento
desta página (seletor + CSS; o título escolhido desaparece), escurecer este site ao vivo (Wikipédia fica escura),
modo de leitura (F9), captura da página inteira (PNG de 275 KB), anúncio em nova aba bloqueado, nova aba
`eteris://`. Regras cosméticas genéricas: verificadas ao nível do motor (`.ad-slot{display:none!important}` para
`example.com`, com ou sem porta); não se aplicam a `localhost`, endereços IP nem `*.nip.io` (comportamento
pré-existente do motor, igual na 0.2.3 publicada), por isso não são testáveis com o servidor local.

## Passo C — resultado (2026-11-06): dá

O CefSharp 156.0.150 compila a partir do código com a alteração (`build/cefsharp/windows-sandbox-info.patch`,
36 linhas em 4 ficheiros: `CefSettings.WindowsSandboxInfo`, passada a `CefInitialize`), e o Éteris 0.2.3 corre com
as bibliotecas próprias no lugar das oficiais: abriu a Wikipédia, o motor arrancou (5 processos) e a execução de
JavaScript funciona (deteção do modo de leitura). Sem `WindowsSandboxInfo` o comportamento é o de sempre.

O que foi preciso (receita em `build/cefsharp/build-cefsharp.cmd`):
- Build Tools do Visual Studio 2022 com os componentes **C++/CLI** e **.NET SDK** (instalados nesta máquina).
- Não usar o `build.ps1` oficial: estraga os caminhos com nomes de utilizador acentuados («Júnior»).
- SDK 9 (`global.json`) e alvo `net8.0` em vez de `net6.0` (falta o pacote Apphost do .NET 6, de que o C++/CLI precisa).
- Sem a assinatura de nome forte (`/p:LinkKeyFile=`): assinar precisa do repositório de chaves do Windows, que as
  ferramentas desta sessão não podem usar; numa linha de comandos normal ou no GitHub Actions assina-se normalmente.
  O .NET não verifica estas assinaturas ao carregar.

Riscos que ficam: o custo contínuo (recompilar o CefSharp a cada atualização do motor até o oficial aceitar a
alteração) e os passos B e D, ainda não provados.

## Passo B — resultado (2026-11-06): dá

Processo filho nativo mínimo (`build/sandbox/subprocess/`: ~30 linhas de C++, `CefExecuteProcess` + registo do esquema
`eteris://`, 454 KB, sem .NET) no lugar do `CefSharp.BrowserSubprocess.exe` do Éteris 0.2.3. Os 5 processos do
motor (páginas, placa gráfica, utilitários) passaram a ser o nativo e o Éteris funcionou: Wikipédia, nova aba
(`eteris://newtab`, com relógio, sites frequentes e dicas), bloqueio de anúncio em nova aba, contador do escudo.
Sem bloqueios nem fechos inesperados.

Deixava de funcionar — tudo o que executava JavaScript pelo processo filho do CefSharp (resolvido no passo A, em
cima): 18 chamadas em 9 ficheiros, mais o `CosmeticFilterHandler.OnContextCreated` (entretanto apagado):

| Funcionalidade | Ficheiro |
|---|---|
| Centro de multimédia (estado, tocar/pausar, pausar) | `MainWindow.Audio.cs`, `MainWindow.Media.cs` |
| Sair do ecrã inteiro da página | `MainWindow.Fullscreen.cs` |
| Esconder elementos (seletor e CSS) | `MainWindow.Hide.cs` |
| Escurecer este site (ao vivo) e cor do site | `MainWindow.Palette.cs` |
| Modo de leitura (deteção e abrir) e seguir links pelo teclado | `MainWindow.Reader.cs` |
| Captura da página inteira | `MainWindow.Screenshot.cs` |
| Filtros cosméticos (genéricos e do site) | `MainWindow.Shield.cs` (`SiteCosmeticScript`), `Shields/CosmeticScripts.cs` (`InjectEarly`); antes em `Shields/CosmeticFilterHandler.cs`, apagado |

Continua a funcionar sem nada mudar: navegação, páginas internas `eteris://`, bloqueador (pedidos), scriptlets e
scripts injetados pelo DevTools (escuro, deteção de vídeo, último clique), mensagens da consola, transferências,
pop-ups, abas, ecrã dividido.

## Plano proposto (por ordem, cada passo verificável)

| Passo | O quê | Custo | Verificação |
|---|---|---|---|
| A | Trocar as 19 chamadas e o `OnContextCreated` por DevTools (útil já: menos dependência do processo filho do CefSharp) | ✅ **feito** (2026-11-07) | testes (`SandboxRulesTests`) + funcionalidades ao vivo com os dois processos filhos (ver «Passo A — resultado») |
| B | Prova: Éteris a correr com um processo filho nativo mínimo (só CEF, ainda sem sandbox) | ✅ **feito** (2026-11-06) | tudo funciona sem o `CefSharp.BrowserSubprocess` |
| C | CefSharp com a informação do sandbox no `Cef.Initialize` (versão própria; propor ao projeto oficial) | ✅ **feito** (2026-11-06) | compila e arranca |
| D | DLL nativa `RunWinMain` + bootstrap: no processo principal arranca o .NET (`hostfxr`) e passa a informação; nos filhos chama `CefExecuteProcess` | ✅ **feito no ramo `sandbox`** (2026-11-08), menos o D5 interativo | `chrome://sandbox` mostra os processos isolados (ver «Passo D — resultado») |
| E | Empacotamento (Velopack, licenças, assinatura) | 1 dia | instalador funciona |

Custo contínuo: enquanto o CefSharp oficial não aceitar a alteração do passo C, cada atualização do motor (ver
`docs/paridade.md`, #3) obriga a recompilar a versão própria do CefSharp.

## Regras para o código

- **Nunca** usar no código do Éteris `EvaluateScriptAsync`, `ExecuteScriptAsync`, `ExecuteJavaScriptAsync`,
  `EvaluateScriptAsPromiseAsync`, `IRenderProcessMessageHandler`, `JavascriptObjectRepository`,
  `JavascriptMessageReceived` nem `RegisterJsObject`. Tudo isso passa pelo processo filho do CefSharp, que é .NET e
  não corre dentro do sandbox: com o processo filho nativo (passo B) essas chamadas não chegam a lado nenhum e falham
  em silêncio.
- JavaScript nas páginas só por `Browser/PageScript.cs` (`PageScript.EvaluateAsync`/`PageScript.Run`, DevTools
  `Runtime.evaluate`) ou registado antes dos scripts da página pelo `ScriptletInjector`
  (`Page.addScriptToEvaluateOnNewDocument`). `userGesture: true` só em ações pedidas pelo utilizador.
- O teste `tests/Eteris.Browser.Tests/SandboxRulesTests.cs` percorre `src/Eteris.Browser/**/*.cs` e falha se algum
  destes nomes aparecer fora de comentários.

## Alternativas consideradas

- **Ficar sem sandbox:** inaceitável para distribuir (é a maior diferença de segurança face às referências).
- **Carregar o .NET dentro dos processos fechados:** o sandbox bloqueia o acesso a ficheiros de que o .NET precisa.
- **Trocar o CefSharp por outra camada sobre o CEF:** reescrita muito maior do que os passos A–E.
