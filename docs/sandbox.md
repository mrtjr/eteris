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

## Passo D — resultado (2026-11-08, ramo `sandbox`): D0–D4, D6 e D9 feitos, D5 menos o interativo

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
com sandbox 181,9 MB / 269,2 MB. A 1.ª tentativa com teclas (2026-11-08 05:2x) ficou inválida: dois avisos
«Segurança do Windows» da Firewall sobre o **Google Chrome** do dono (Google LLC; abertos às 04:58 e 05:03, antes de
qualquer teste do Éteris nesse intervalo) ficaram no primeiro plano e os scripts de teclas abortam quando o Éteris
não está à frente. Não foram tocados (continuavam abertos às 06:30); a 2.ª parte foi feita sem primeiro plano:

**D5 (2.ª parte, 2026-11-08 05:50–06:30) — regressão sem teclado, rato nem primeiro plano.** Duas publicações do
mesmo ramo com o mesmo `libcef.dll` (SHA256 `4D5097CE…`): **base** = publicação normal (apphost, CefSharp oficial,
filhos `CefSharp.BrowserSubprocess.exe` sem sandbox) e **sandbox** = a publicação do D6 (`Eteris.Browser.exe` =
bootstrap, filhos nativos com sandbox). Perfis de teste novos; o ecrã dividido entra pela sessão gravada
(`session.json` com `Split`), o motor de vídeo por `localhost` na lista de sites de vídeo do perfil (`eteris.db`), e o
resto pelo DevTools de teste (`ETERIS_TEST_DEVTOOLS_PORT`, `Runtime.evaluate`) ou por uma página que escreve o resultado
no título (lido do `session.json`). Fecho por `WM_CLOSE` em todos os arranques, sem processos a sobrar.

| Verificação | Base | Sandbox |
|---|---|---|
| Ecrã dividido reposto da sessão: as duas páginas `visible` ao mesmo tempo (CDP e `BroadcastChannel`) | sim | sim |
| `window.open` sem gesto | bloqueado (0 abas) | bloqueado (0 abas) |
| `window.open` com gesto (`userGesture: true`) | abre 1 aba | abre 1 aba |
| Anúncio em nova aba (`ad.doubleclick.net`, com gesto) | bloqueado | bloqueado |
| WebRTC (2 `RTCPeerConnection` na página, canal de dados) | 0 candidatos, canal sem abrir | igual |
| Avisos novos «Segurança do Windows» | nenhum | nenhum |
| Motor de vídeo (`http://localhost:8814/v.html` entregue à instância aberta) | troca: 6–7 processos `msedgewebview2` | troca: 6–7 |
| Processador, ecrã dividido com animação a 60 fps, 10 s (4 medições, % de 1 núcleo) | 14,4 · 13,4 · 12,7 · 14,8 (média 13,8) | 12,8 · 16,7 · 14,2 · 13,3 (média 14,3) |
| Memória privada dos 6 filhos (MB) | 225,5 · 356,1¹ · 221,8 · 222,6 | 279,3¹ · 280,9¹ · 186,4 · 185,3 |
| Conjunto de trabalho dos 6 filhos (MB) | 426,5 · 556,0¹ · 424,2 · 425,4 | 390,5 · 386,0 · 287,5 · 288,7 |
| Processo principal, memória privada (MB) | 203,6–209,0 | 203,8–211,8 |

¹ Processo da placa gráfica a 176–222 MB em vez de ~88 MB nesse arranque (acontece nas duas cópias).

Leitura: nenhuma regressão. O WebRTC sem candidatos é o comportamento normal do Éteris (política
`webrtc-ip-handling-policy=disable_non_proxied_udp` em `Shields/EngineHardening.cs`: sem proxy não há UDP), igual nas
duas cópias, e por isso também não abriu nenhum aviso da Firewall. Sem as duas medições em que a placa gráfica subiu, os
filhos com sandbox gastam ~36 MB privados e ~137 MB de conjunto de trabalho a menos (filhos nativos sem .NET); o
processador fica dentro do ruído. Nota para os testes: `Get-NetTCPConnection` às vezes não mostra a porta do DevTools
que responde (`/json/version`); verificar a porta pelo HTTP e pelas páginas da própria sessão, nunca só pela tabela de
ligações. **Continua por fazer ao vivo** (precisam de teclado/rato no Éteris de teste, ou seja, do ecrã livre):
centro de multimédia, esconder elemento, escurecer site, leitura F9, captura inteira e escolher arquivo. Todas correm
pelo DevTools do lado do navegador (passo A) e passaram com os filhos nativos sem sandbox; o risco de o sandbox as
afetar é baixo, mas falta vê-las.

**D6 — integração no repositório (opcional, opção B de nomes).** `-p:EterisSandbox=true` (ou
`build/publish.ps1 -Sandbox -CefSharpFeed <feed> -CefSharpN 2 [-SandboxClientDll <dll>]`): o .NET passa a
`Eteris.App`, a saída leva `Eteris.Browser.exe` = bootstrap do runtime e `Eteris.Browser.dll` = DLL cliente
(`build/sandbox/client/build.cmd <distribuição> build\sandbox\client\out`), e a publicação deixa de trazer o
`CefSharp.BrowserSubprocess.*` e o apphost `Eteris.App.exe`. Verificado numa publicação na scratchpad:
`Eteris.Browser.exe` com sandbox (renderers Untrusted, GPU Low) e com `--eteris-no-sandbox` (filhos = o próprio exe,
`--no-sandbox`, integridade Medium), 2.º arranque entrega `--new-tab`, fecho limpo. Sem a opção nada muda (CefSharp
oficial; testes `SandboxInfoTests`). Os dois pack URIs do menu usam o nome da montagem em execução
(`typeof(MainWindow).Assembly`), em vez de `Eteris.Browser` escrito à mão.

**E (primeira parte) — marca no bootstrap.** `build/sandbox/brand-bootstrap.ps1` (corrido pela publicação com
`EterisSandbox`): compila um `.rc` com o `rc.exe` do Windows SDK e troca, na cópia `Eteris.Browser.exe`, os recursos de
ícone e de versão (o manifesto fica). O Explorador e o Gestor de Tarefas passam a mostrar «Éteris» 0.2.9 com o ícone do
Éteris, em vez de «CEF Bootstrap Application»; o arranque com sandbox continua igual (renderers Untrusted, GPU Low).

**D9 — Network Service com sandbox: dá, só com a funcionalidade e permissões na pasta (sem alterar o motor).**
Causa encontrada no registo (`--enable-logging`): `sandbox_win.cc:805 Sandbox cannot access executable
…\cefsimple.exe … Acesso negado (0x5)`. Com `NetworkServiceSandbox` o serviço de rede arranca num **AppContainer**
(`SetupAppContainerProfile` em `sandbox/policy/win/sandbox_win.cc`: capacidades `lpacChromeInstallFiles`,
`registryRead`, `internetClient`, `privateNetworkClientServer`, …; LPAC só com `WinSboxNetworkServiceSandboxIsLPAC`,
desligado por omissão) e, antes de o lançar, verifica se o AppContainer consegue ler o executável. A pasta de uma
aplicação fora de `Program Files` não dá acesso a AppContainers; o instalador do Chrome resolve-o dando leitura+execução
às capacidades `chromeInstallFiles` e `lpacChromeInstallFiles` na pasta de instalação
(`chrome/installer/setup/configure_app_container_sandbox.cc`). Quando o lançamento falha, o
`SystemNetworkContextManager` grava `net.network_service_failed_launch_major_version = 156` no `Local State` e, a partir
daí, `IsNetworkSandboxEnabled` devolve `kDisabledBecauseOfFailedLaunch` **para toda a versão 156** nesse perfil — por isso
a 1.ª sonda via a funcionalidade ligada e o serviço sem sandbox (os perfis `profile-sbx-d9*` tinham essa marca).

Cadeia no código (Chromium 156 / CEF): `ServiceProcessHostImpl::ShouldEnableSandbox(kNetwork)` →
`ContentBrowserClient::ShouldSandboxNetworkService()`; a CEF 156 cria sempre o
`ChromeContentBrowserClientCef` (`ChromeMainDelegateCef::CreateContentBrowserClient`, também com janelas de estilo
Alloy), que não redefine o método, e fica o do Chrome:
`SystemNetworkContextManager::IsNetworkSandboxEnabled()` → (1) arranque falhado nesta versão → não; (2)
`sandbox::policy::features::IsNetworkSandboxSupported()` (Windows 10 20H1+, não ARM64); (3) preferência
`NetworkServiceSandboxEnabled` (política); (4) a funcionalidade `NetworkServiceSandbox` (desligada por omissão). A
CEF não a desliga em lado nenhum (`grep` em `cef/`). Os dados de rede do perfil (`Default/Network`: cookies, etc.) recebem
sozinhos uma entrada para a capacidade do serviço de rede (`content/browser/network_sandbox.cc`): não é preciso tratar
do perfil.

Prova (2026-11-08):

| Arranque | Network Service (`chrome://sandbox` + tokens) | Página |
|---|---|---|
| cefsimple + bootstrap do D0, perfil novo, `--enable-features=NetworkServiceSandbox`, pasta sem ACL | «Not Sandboxed», Medium; `Local State`: `failed_launch_major_version: 156` | carrega |
| a mesma pasta copiada + as duas entradas de ACL (dadas com `icacls`, as mesmas de `build/sandbox/network-sandbox-acl.ps1`; `-Check` dá 2/2), perfil novo | **Network / AppContainer `S-1-15-2-2084…`**, integridade **Low**; sem marca de falha | `example.com` carrega |
| Éteris do D6 (`Eteris.Browser.exe` com sandbox) copiado + ACL, perfil novo, `--enable-features=NetworkServiceSandbox` na linha de comandos | **AppContainer, Low** (renderers Untrusted, GPU Low, Storage Untrusted) | «Example Domain — Éteris», fecho limpo |

Passos para o Éteris (sem patch do motor):
1. **Instalação/atualização:** `network-sandbox-acl.ps1 -Path <pasta raiz da instalação>` — ou o equivalente em C#
   (`DirectorySecurity.AddAccessRule` com os dois SIDs, `ReadAndExecute`, herança `ContainerInherit,ObjectInherit`) nos
   ganchos do Velopack (`OnFirstRun`/depois de instalar e de atualizar). Dado na raiz da instalação do Velopack
   (`%LocalAppData%\<id do pacote>`), com herança, as pastas novas de cada versão herdam-no. Não precisa de administrador (a pasta é do utilizador).
2. **Arranque:** só juntar `NetworkServiceSandbox` ao `enable-features` (`Shields/EngineHardening.cs`) quando o sandbox
   está ligado **e** a verificação (`-Check`, ou a mesma leitura de ACL em C#) passa. Ligar a funcionalidade sem a ACL
   não parte nada (o Chromium recua e as páginas carregam), mas deixa o perfil marcado e sem sandbox de rede até à
   versão principal seguinte do motor.
3. **Atenção:** nas cópias desta prova a ACL foi dada pasta a pasta na scratchpad; nas instalações já existentes, um
   perfil que tenha falhado uma vez só volta a tentar quando o motor mudar de versão principal (ou apagando
   `net.network_service_failed_launch_major_version` do `Local State`).

**Falta:** ver «Estado do ramo `sandbox` e o que falta para fundir no `main`», em baixo.

## Estado do ramo `sandbox` e o que falta para fundir no `main` (2026-11-08)

**Provado:** motor próprio com bootstrap (D0), CefSharp com a informação do sandbox (D1/C), DLL cliente (D2), Éteris
.NET pelo bootstrap (D3), páginas, placa gráfica e serviço de armazenamento isolados no Éteris (D4: renderers
Untrusted, GPU Low, Storage Untrusted, nenhum filho com `--no-sandbox`), regressão sem teclado (D5: impressão, PDF,
codecs, transferência, janela privada, ecrã dividido, pop-ups, anúncio em nova aba, motor de vídeo, WebRTC, memória e
processador — sem regressões, filhos com menos memória), saída opcional no repositório (D6) com a marca do Éteris no
bootstrap (E, 1.ª parte), e o serviço de rede também isolado (D9: só funcionalidade + ACL, sem patch do motor).

**Falta, por ordem:**
1. **D5 interativo** (ecrã livre, 20 min): centro de multimédia, esconder elemento, escurecer site, F9, captura
   inteira, escolher arquivo — com `-p:EterisSandbox=true`.
2. **D9 no código** (1–2 h): ACL na instalação/atualização (Velopack) + `NetworkServiceSandbox` só com a ACL
   verificada (ver D9); teste unitário da decisão e verificação ao vivo com `sandbox-check.ps1` (Network Service
   AppContainer/Low), incluindo páginas em `localhost`/rede local, transferências, proxy do sistema e autenticação
   integrada (o AppContainer tem `enterpriseAuthentication`, mas não foi testado).
3. **E — assinatura conjunta** (½ dia): `Eteris.Browser.exe` (bootstrap com a marca), `Eteris.Browser.dll` (DLL
   cliente), `Eteris.App.dll` e o runtime da CEF (`libcef.dll`, `chrome_elf.dll`) com o mesmo certificado. O bootstrap da
   CEF verifica no processo principal (`CheckDllCodeSigning` em `cef/libcef_dll/bootstrap/bootstrap_win.cc`) que o exe,
   o `chrome_elf.dll` e a DLL cliente estão os três sem assinatura ou os três assinados e válidos com o **mesmo**
   certificado principal — senão o arranque pára (`LOG(FATAL)`). O `libcef.dll` e o `Eteris.App.dll` não entram nessa
   verificação, mas devem ir assinados também. Assinar tudo no mesmo passo da publicação, depois do
   `brand-bootstrap.ps1` (que muda os recursos do exe e invalidaria uma assinatura anterior).
4. **E — Velopack** (½ dia): `vpk pack` com `--mainExe Eteris.Browser.exe` (o bootstrap); confirmar que o atalho, o
   `Update.exe` e a atualização delta funcionam com o exe nativo (o Velopack já não arranca .NET pelo apphost);
   ganchos do Velopack (`--veloapp-*`) passam a chegar ao .NET pela DLL cliente — verificar que `RunWinMain` entrega os
   argumentos tal como estão. Mais a ACL do D9 nos ganchos.
5. **E — CI com o CefSharp próprio** (1 dia): o `release.yml` precisa do feed local `156.0.150-eteris.2`
   (`build/cefsharp`, ~15 min de compilação C++/CLI; ou guardado como artefacto/pacote privado), da distribuição mínima da
   CEF do motor próprio para compilar a DLL cliente (`build/sandbox/client`), do `rc.exe` do Windows SDK
   (`brand-bootstrap.ps1`) e da assinatura de nome forte que esta sessão não pôde fazer (no Actions assina-se
   normalmente).
6. **Custo contínuo** (já em `docs/paridade.md`, #3): cada atualização do motor recompila também o CefSharp próprio
   e a DLL cliente; propor o patch `WindowsSandboxInfo` ao CefSharp oficial reduz isto.

**Recomendação:** fundir no `main` por fases, sempre atrás da opção `EterisSandbox` (por omissão desligada, nada muda
para quem publica sem ela): (a) já, o código do ramo (passos A–D6 e a marca), que não altera a publicação normal e tem
testes; (b) depois do D5 interativo e do D9 no código, uma versão de teste com `-Sandbox` instalada pelo Velopack num
perfil de teste; (c) só então ligar `EterisSandbox` na publicação oficial, com a assinatura conjunta e a CI. A decisão
da versão em que entra é do dono.

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
| D | DLL nativa `RunWinMain` + bootstrap: no processo principal arranca o .NET (`hostfxr`) e passa a informação; nos filhos chama `CefExecuteProcess` | ✅ **feito no ramo `sandbox`** (2026-11-08), menos o D5 interativo; D9 (rede) provado sem patch do motor | `chrome://sandbox` mostra os processos isolados (ver «Passo D — resultado») |
| E | Empacotamento (Velopack, licenças, assinatura conjunta, ACL do D9, CI) | marca no bootstrap ✅; resto ≈ 2 dias | instalador funciona com o sandbox ligado |

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
