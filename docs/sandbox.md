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

## Passo D — resultado (2026-11-08, ramo `sandbox`): D0–D2 feitos

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
| D | DLL nativa `RunWinMain` + bootstrap: no processo principal arranca o .NET (`hostfxr`) e passa a informação; nos filhos chama `CefExecuteProcess` | 2 dias | `chrome://sandbox` mostra os processos isolados |
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
