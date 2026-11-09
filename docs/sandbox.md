# Sandbox no Éteris — investigação (2026-11-06)

Pergunta: dá para isolar os processos das páginas como no Chrome, Firefox e Brave? **Resposta: dá, mas é trabalho
de 1–2 semanas e obriga a alterar o CefSharp.** Ver o plano em baixo.

## Estado atual (medido)

Até à 0.2.13 os 6 processos do motor do Éteris (páginas, placa gráfica, utilitários) correm com `--no-sandbox`: uma
falha explorável no código de uma página fica com os mesmos acessos que o utilizador. Nas três referências esses
processos correm fechados (sem acesso a ficheiros nem ao sistema). **Desde a 0.2.14 (por publicar) a publicação sai
com o sandbox ligado** (ramo `rede-seguranca`, fundido no ramo da 0.2.14): páginas Untrusted, placa gráfica Low, armazenamento Untrusted e serviço
de rede no AppContainer, como no Chrome — ver «Como publicar com o sandbox», a seguir. As provas feitas com o nome 0.2.12 (3.ª prova do
Velopack, «Revisão da 0.2.12») ficam como histórico: o sandbox ficou pronto para a 0.2.12, mas a 0.2.12 e a 0.2.13
saíram sem ele.

## Como publicar com o sandbox (desde a 0.2.14, por omissão)

`build/publish.ps1` publica com o sandbox; `-NoSandbox` fica para emergências (é o que a publicação pelo CI sem codecs
usa no `release.yml`: o CI não tem o motor próprio, que traz o bootstrap, nem o CefSharp com o patch). As duas peças que
não vêm do NuGet — o CefSharp com o patch v2 e a DLL cliente do bootstrap — compilam-se a partir do repositório com
**um só script**, `build/sandbox/build-all.ps1`. Passo a passo, na máquina de publicação:

1. **Motor próprio** (`docs/motor.md`, «Motor com codecs»): `build/cef-codecs/out/runtime` com o `bootstrap.exe` e o
   `manifest.json`, e a distribuição mínima do mesmo motor em
   `build/cef-codecs/out/distrib/cef_binary_156.0.15+gcf671c0+chromium-156.0.8078.80_windows64_minimal` (o passo
   `Package` do `build/cef-codecs/build.ps1` cria as duas; se a distribuição foi apagada, `-Steps Package` volta a
   fazê-la a partir da compilação em `C:\cef`, sem recompilar).
2. **CefSharp e DLL cliente**: `powershell -ExecutionPolicy Bypass -File build\sandbox\build-all.ps1`. Faz, por ordem:
   - clone limpo da tag `v156.0.150` do CefSharp (commit `f4cd42e…` verificado) em `build/cefsharp/src` — o único passo
     que vai à rede (GitHub); `-CefSharpSource <pasta>` usa um clone local, e o `origin` passa sempre a ser o oficial
     (o SourceLink do `CefSharp.Core.Runtime` precisa dele, senão a ligação falha com LNK1104);
   - `build/cefsharp/prepare-source.ps1` (patch v2, SDK 9, net8.0, `packages\` da cache do NuGet e
     `Directory.Build.props`/`.targets` vazios na raiz do clone — sem eles o clone, por estar dentro do repositório,
     apanhava o `TreatWarningsAsErrors` do Éteris e não compilava), `build-cefsharp.cmd` e `pack-local.ps1` para o feed
     local `build/cefsharp/feed` (`156.0.150-eteris.N`; N por omissão = o maior da cache do NuGet + 1, porque a cache
     guarda cada versão para sempre e um N reutilizado com binários novos restauraria os antigos — o script recusa-o);
   - verificação dos pacotes (versão, dependência `[156.0.150-eteris.N]`, assinatura do repositório removida,
     `WindowsSandboxInfo` nas quatro montagens trocadas) e `build/cefsharp/feed/eteris-cefsharp.json` (N, tag, commit,
     SHA256 do patch e das montagens), que o `publish.ps1` lê;
   - DLL cliente (`build/sandbox/client/build.cmd`) contra a distribuição do passo 1, conferida antes contra o runtime
     (commit da CEF no `cef_version.h` e SHA256 do `libcef.dll`), e `verify-client.ps1`;
   - a bateria de testes com o CefSharp do feed (o modo **com** sandbox: a constante `ETERIS_CEFSHARP_SANDBOX` vem do
     csproj por causa do `-p:EterisCefSharpFeed` que o script passa), confirmando que o CefSharp restaurado e carregado é
     o do feed. A CI (`build.yml`, `release.yml`) continua a correr o modo **sem** sandbox, com o CefSharp oficial: os
     testes do sandbox seguem o CefSharp com que se compilou (`CefConfigurator.SandboxSupported`), por isso passam nos
     dois modos sem saltar nenhum — mas os blocos `#if ETERIS_CEFSHARP_SANDBOX` só são compilados e testados aqui;
   - só com os testes todos aprovados, numa árvore sem alterações por gravar e com o mesmo HEAD do princípio ao fim, a
     **marca dos testes** `build/sandbox/out/tests-ok.json` (commit, N, SHA256 do `eteris-cefsharp.json`, da DLL cliente
     e do `manifest.json` do motor). Cada corrida começa por apagar a anterior; `-SkipTests` não a grava.

   Opções: `-CefDistrib <pasta>` (a distribuição noutro sítio — por exemplo quando o caminho do repositório é comprido:
   o compilador não abre ficheiros acima de 260 caracteres e o script recusa logo uma distribuição com caminhos acima de
   250), `-N <n>`, `-SkipCefSharp`, `-SkipClient`, `-SkipTests`. Requisitos: git, CMake, Build Tools do Visual Studio
   2022 com C++/CLI (`VC.CLI.Support`), SDK do .NET 9 (além do 10) e os pacotes oficiais `CefSharp.*` 156.0.150 e
   `cef.sdk` 156.0.15 na cache do NuGet (o restauro normal do Éteris põe-nos lá). Tudo o que produz fica em pastas
   ignoradas pelo git (`build/cefsharp/src`, `build/cefsharp/feed`, `build/sandbox/client/out`, `build/sandbox/out`,
   com os registos em `build/sandbox/out/logs`). A compilação completa só é preciso repetir quando mudar o motor, o
   CefSharp, o patch ou `build/sandbox/client`; **os testes, em cada release**: depois do commit da versão,
   `build-all.ps1 -SkipCefSharp -SkipClient` (alguns minutos), porque o `publish.ps1` exige a marca para o HEAD atual.
   Num caminho com acentos (a cópia em `C:\Users\Júnior\…`) o script trabalha pelo nome curto 8.3 da pasta (o cmd.exe,
   o C++/CLI, o CMake e o SourceLink só foram provados com caminhos ASCII); sem nomes curtos no disco pára e pede uma
   cópia num caminho ASCII. Provado com `-SkipCefSharp -SkipClient` pelo caminho com acentos do worktree (testes e
   marca); a compilação completa do CefSharp e da DLL por esse caminho ainda não foi corrida.
3. **Publicar**: `.\build\publish.ps1 -Version X.Y.Z`. Limpa `publish\`, verifica o motor (manifesto), lê o N do
   `eteris-cefsharp.json` (um `-CefSharpN` diferente é recusado), recusa a falta do feed, da DLL cliente ou do
   bootstrap (com a indicação do `build-all.ps1` e do `-NoSandbox`) e recusa publicar sem a marca dos testes
   (`build/sandbox/out/tests-ok.json`) para o HEAD atual, com alterações por gravar, ou com outro feed, outra DLL cliente
   ou outro motor do que os testados. Compila com `-p:EterisSandbox=true` (o `EterisSandboxCheck` corre o
   `verify-client.ps1` antes de compilar: a DLL tem de ser da mesma compilação do motor e não pode ser a de teste),
   marca o bootstrap (`brand-bootstrap.ps1`, com o `rc.exe` do Windows SDK) e no fim confirma a forma publicada:
   `Eteris.Browser.exe` = bootstrap com a versão pedida, `Eteris.Browser.dll` = DLL cliente (o SHA256 da testada),
   `Eteris.App.dll`, `CefSharp.Core.dll` e `CefSharp.Core.Runtime.dll` com o SHA256 do `eteris-cefsharp.json` (a cache
   do NuGet ganha sempre ao feed: um N reutilizado daria outros binários) e sem `CefSharp.BrowserSubprocess.exe`. Por
   último grava `publish\eteris-build.json` (versão, modo, commit, o `eteris-cefsharp.json` com o SHA256 das montagens
   trocadas, SHA256 da DLL cliente, hora dos testes): vai na instalação e liga cada release ao CefSharp que leva — o N
   só é único na máquina que o compilou (vem da cache do NuGet) e os binários não são idênticos byte a byte entre
   compilações. Se alguma verificação falhar depois de começar, a pasta `publish\` é apagada.
4. **Empacotar**: `.\build\pack.ps1 -Version X.Y.Z`. Só empacota uma pasta `publish\` com o `eteris-build.json` da
   versão pedida e com sandbox; uma publicação de emergência sem sandbox só com `-NoSandbox` também no `pack.ps1` (o
   `release.yml` passa-o). O `mainExe` continua `Eteris.Browser.exe` (agora o bootstrap), por isso os atalhos de quem
   atualiza continuam válidos; a ACL do serviço de rede é dada pelos ganchos do Velopack ao instalar e ao atualizar
   (provado na «3.ª prova do Velopack», em baixo).
5. **Testar** a pasta `publish\` num perfil de teste: `chrome://sandbox` (renderers Lockdown/Untrusted, GPU Low) e
   `build/sandbox/sandbox-check.ps1 -ProcessId <pid>` sem filhos com `--no-sandbox`. Fora de uma instalação a pasta não
   tem a ACL, por isso aí o serviço de rede aparece «Not Sandboxed» (normal).
6. O resto como sempre (`docs/motor.md`, «Publicação»: `vpk download`, `pack.ps1`, tag, `vpk upload`).

**Assinatura:** o Éteris não é assinado, e o bootstrap aceita o exe, o `chrome_elf.dll` e a DLL cliente os três sem
assinatura. Se um dia passar a ser assinado, têm de ser os três com o mesmo certificado (senão o arranque pára), assinados
depois do `brand-bootstrap.ps1` e antes do `vpk pack` (ver «Falta», item 1).

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

## Passo D — resultado (2026-11-08, ramo `sandbox`): D0–D6 e D9 feitos (D9 também no código), Velopack provado

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
ligações. O que faltava ao vivo ficou feito no D5 interativo, em baixo.

**D5 interativo (2026-11-08 09:2x–09:5x) — com teclado e rato, publicação com sandbox depois de fundir o `main`
(c3fe087).** Publicação `-p:EterisSandbox=true` (CefSharp `156.0.150-eteris.2`, DLL cliente do D6, `libcef.dll`
SHA256 `4D5097CE…`, o publicado) copiada para a scratchpad **com a ACL do D9** (por isso com o serviço de rede também
em sandbox); comparada com a publicação normal do mesmo ramo (**base**) onde algo pareceu diferente. Perfis de teste
novos, `ETERIS_TEST_MUTE=1`, páginas num servidor local (`127.0.0.1`), comandos pela paleta (Ctrl+K) e atalhos,
verificação pelo DevTools de teste (`Runtime.evaluate`), por UI Automation e por capturas. Área de transferência
guardada e reposta; ficheiros de teste para a Reciclagem.

| Verificação (com sandbox) | Resultado |
|---|---|
| `chrome://sandbox` | renderers Renderer/Lockdown/Untrusted, GPU Limited/Low, Storage Lockdown/Untrusted, **Network Service no AppContainer** (`NetworkServiceSandbox` ligada, só por haver ACL) |
| Vídeo H.264 no motor principal (`bear.mp4` 1280×720) | toca: 153 quadros em 2,2 s, 0 perdidos, sem erro |
| Centro de multimédia | o botão aparece na barra e o ícone de som na aba |
| Esconder um elemento (paleta → clique) | o elemento fica `display: none` |
| Escurecer este site | estilo `eteris-dark` na página, `filter: invert(0.9) hue-rotate(180deg)` |
| Modo de leitura (F9) | abre (`#eteris-reader`) e fecha |
| Captura da página inteira (Ctrl+Shift+S) | PNG de 525 KB em Imagens › Éteris |
| Guardar como PDF (paleta) | PDF de 1,9 MB (`%PDF-`) em Transferências |
| Transferência (300 000 bytes aleatórios) | ficheiro igual ao original (SHA256); painel de transferências mostra-a |
| Escolher ficheiro (clique real num `<input type=file>`) | o diálogo «Abrir» do Windows abre no processo principal; Esc → evento `cancel` na página |
| Imprimir (Ctrl+P) | o diálogo de impressão do Windows abre; Esc fecha-o |
| Janela privada (Ctrl+Shift+N) | «Nova aba privada — Éteris» |
| Ecrã dividido (paleta) | as duas páginas `visible` ao mesmo tempo; «Fechar ecrã dividido» volta a uma |
| Trocar abas (Ctrl+PgDn ×5, duas páginas escuras) | o centro da página nunca passou de luminosidade 24 (fundo 20–24): sem quadro branco |
| Abrir aba (Ctrl+T) | pior quadro 85 (a imagem da nova aba), sem branco |
| Espreitar (Alt+clique num link) | cartão com título, autor e parágrafos do artigo |
| Motor de vídeo (`localhost` na lista do perfil) | a aba passa ao WebView2 (6 processos `msedgewebview2`) |
| Avisos «Segurança do Windows» novos | nenhum |
| Fecho (WM_CLOSE) | limpo |

Nenhuma falha só com sandbox. Três coisas vistas e esclarecidas:
- **Alt+clique numa aba aberta por um link entregue à instância** (2.º arranque com o endereço), sem recarregar:
  o Chromium transfere o link em vez de o Éteris o espreitar. **Igual na base** (sem sandbox) — não é do sandbox; o
  script do Alt+clique (`LinkPreview.AltClickScript`, registado pelo `HostScriptRegistry`) ainda não está nessa
  primeira página. Depois de recarregar, ou numa aba aberta normalmente, funciona nas duas cópias. Fica para o `main`.
- Na 1.ª tentativa o diálogo «Abrir» não apareceu porque o clique só fechou o painel de transferências aberto pela
  transferência acima; repetido sem o painel, abre nas duas cópias. O diálogo de impressão ficou aberto (o script não
  o via: é de outro processo) e bloqueou os passos seguintes dessa sessão; repetidos com Esc, passam.
- Nessa sessão longa o `chrome://sandbox` mostrou, além da GPU com sandbox, um 2.º processo «GPU / Not Sandboxed»
  transitório. Não voltou a aparecer em 85 s de sessão nem com o diálogo de impressão aberto 8 s (nenhum processo da
  placa gráfica com `--no-sandbox`). Compatível com o processo de recolha de informação da placa gráfica do Chromium
  (`GPU_PROCESS_KIND_INFO_COLLECTION`, sem sandbox também no Chrome, só para ler capacidades DirectX e sair); a vigiar.

Recuo do D9 confirmado ao vivo: a mesma publicação **sem** a ACL (`network-sandbox-acl.ps1 -Check` → 0/2) arranca
com o serviço de rede «Not Sandboxed», as páginas carregam e o `Local State` **não** fica com
`network_service_failed_launch_major_version` (a funcionalidade nem é pedida).

**D6 — integração no repositório (opcional, opção B de nomes).** `-p:EterisSandbox=true` (na altura
`build/publish.ps1 -Sandbox -CefSharpFeed <feed> -CefSharpN 2 [-SandboxClientDll <dll>]`; histórico: desde a 0.2.14 o
sandbox é a omissão do `publish.ps1`, sem argumentos, e o `-Sandbox` já não existe — ver «Como publicar com o
sandbox»): o .NET passa a
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

**D9 no código (2026-11-08).** `Infrastructure/NetworkSandbox.cs`: a decisão
`ShouldEnable(sandbox das páginas, ACL)` — a funcionalidade `NetworkServiceSandbox` só entra no `enable-features`
(`CefConfigurator.Build(..., networkSandbox)`) com o sandbox das páginas ligado **e** a ACL confirmada no próprio
executável (`Granted`: entradas próprias ou herdadas que se aplicam ao objeto, leitura+execução para as duas
capacidades, nenhuma a negar; qualquer erro conta como «não»). A ACL é dada por `Grant` (as mesmas entradas do
`build/sandbox/network-sandbox-acl.ps1`, herdadas) na **raiz da instalação** do Velopack (`InstallRoot`: o pai de
`current` quando lá está o `Update.exe`), pelos ganchos `OnAfterInstallFastCallback` e `OnAfterUpdateFastCallback`
(`Program.Main`), só nas publicações com sandbox e sem administrador (a pasta é do utilizador); uma falha não impede a
instalação — sem ACL o serviço de rede corre como antes. Testes: `NetworkSandboxTests` (decisão, SIDs iguais aos do
script, raiz do Velopack, herança numa pasta temporária real, entrada a negar, `Build`, ganchos ligados e inertes
sem sandbox); `EngineFeatureNamesTests` passa a verificar também `NetworkServiceSandbox` (no CEF 156 a cadeia
`kNetworkServiceSandbox` vem logo depois de um byte de dados, por isso o teste aceita qualquer byte anterior que não
seja letra, algarismo ou `_`).

**E — Velopack com o bootstrap como `mainExe` (prova da atualização, 2026-11-08).** Opção B: o `mainExe` continua a
ser `Eteris.Browser.exe` (agora o bootstrap), por isso `build/pack.ps1` não muda e os atalhos de quem atualiza
continuam válidos. Prova sem tocar na instalação do dono: `packId` de teste `EterisBrowserSbxTest` (instala em
`%LOCALAPPDATA%\EterisBrowserSbxTest`), título «Eteris SbxTest» (atalhos com outro nome que os do dono), feed local
(pasta) com **0.2.9-teste** (publicação normal: apphost .NET, sem sandbox) e **0.2.10-teste** (`-p:EterisSandbox=true`;
o `vpk` gerou também o delta: 6 ficheiros alterados, 3 novos, 6 removidos, 14 MB).

| Passo | Resultado |
|---|---|
| `Setup.exe --silent` da 0.2.9-teste | instala em 3 s; o gancho `--veloapp-install` corre; atalhos «Eteris SbxTest» no Ambiente de Trabalho e no menu Iniciar → `…\EterisBrowserSbxTest\current\Eteris.Browser.exe`; chave de desinstalação criada |
| 0.2.9-teste a correr | todos os processos «Not Sandboxed» (como hoje); ACL da raiz 0/2 (o gancho é inerte sem sandbox) |
| Atualização pelo feed local (`UpdateManager` + `SimpleFileSource`: verificar → transferir → `ApplyUpdatesAndExit`) | escolhe o **delta** sobre o pacote local da 0.2.9; `Update.exe apply` aplica-o; o `Eteris.Browser.exe` passa a ser o bootstrap («Éteris» 0.2.10, com `Eteris.App.dll`, sem `CefSharp.BrowserSubprocess.exe`) |
| Gancho `--veloapp-updated` **pelo bootstrap** (bootstrap → DLL cliente → .NET) | correu: ACL dada à raiz (2/2) e herdada por `current` (2/2) |
| Atalhos depois da atualização | os mesmos, para o mesmo `current\Eteris.Browser.exe` (agora o bootstrap) |
| 0.2.10-teste aberta pelo destino do atalho, com o perfil da 0.2.9 | arranca; a sessão volta (a página aberta na 0.2.9) e mostra as Novidades; `chrome://sandbox`: GPU Limited/Low, **Network Service no AppContainer**, Storage Untrusted, 4 renderers Untrusted; nenhum filho com `--no-sandbox`; fecho limpo |
| `Update.exe --uninstall` | remove a pasta, os dois atalhos e a chave de desinstalação (no fim mostra «Desinstalação Concluída», que pede OK) |
| Estado do utilizador antes/depois (atalhos com destino e SHA256, chaves de desinstalação, `StartMenuInternet`, `RegisteredApplications`, `Classes`, escolhas de http/https/.htm/.html/.pdf) | 0 diferenças **nestes sítios** — mas a fotografia não olhava para os perfis de AppContainer, e a desinstalação **deixou** o do serviço de rede (`cr.sb.netB382764A…`: pasta em `%LOCALAPPDATA%\Packages` e entrada em `AppContainer\Mappings`). Corrigido na revisão, em baixo; o Éteris não se regista como navegador na instalação |

Notas para a publicação:
- O `Setup.exe` do Velopack 1.2.161 entra em pânico com argumentos para a aplicação (`Setup.exe -- <args>`: erro do
  clap em `EXE_ARGS`) e fica parado; o Éteris não os usa. Na prova, a aplicação que o Setup arranca depois de instalar
  saía logo por um gancho de arranque do .NET só de teste (`DOTNET_STARTUP_HOOKS` no ambiente do Setup), para nunca
  abrir com o perfil por omissão nem falar com a instância aberta do dono — no modo silencioso o Setup acabou por não
  a arrancar.
- `brand-bootstrap.ps1` só aceita versões `x.y.z` (os recursos de versão do Windows são numéricos): as publicações de
  teste usaram `-p:Version=0.2.10` e só o pacote tem o sufixo `-teste`.
- Ficheiros da prova na scratchpad (`sbx/vpk-test.ps1`, `sbx/vpk-uninstall.ps1`, `sbx/vpk-updater`, `sbx/vpk-hook`).

**Revisão (2026-11-08, 10:00–10:50) — correções e 2.ª prova do Velopack.** Problemas confirmados e corrigidos:

1. **A desinstalação deixava o perfil do AppContainer do serviço de rede.** Com `NetworkServiceSandbox` o Chromium cria
   um perfil por caminho do executável (`GetAppContainerProfileName` em `sandbox/policy/win/sandbox_win.cc`:
   `cr.sb.net` + SHA1 em hexadecimal maiúsculo do caminho UTF-8 do programa dos filhos, que é o `GetModuleFileName` do
   processo principal), com a pasta `%LOCALAPPDATA%\Packages\<nome>` e a chave
   `HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings\<SID>`.
   Confirmado: `ProfileName(C:\Users\Júnior\AppData\Local\EterisBrowserSbxTest\current\Eteris.Browser.exe)` =
   `cr.sb.netB382764A951831BB0A76C124F3484E6BDFFFB515`, e esse perfil continuava lá depois da 1.ª desinstalação.
   **Correção:** gancho `OnBeforeUninstallFastCallback` → `NetworkSandbox.OnUninstalling` (na altura só nas publicações
   com sandbox e pelo bootstrap; desde a revisão da 0.2.12, que sai na 0.2.14, em todas, dentro de uma instalação do Velopack, para uma
   versão sem sandbox instalada por cima de uma com sandbox também os apagar) → `DeleteAppContainerProfile` para os
   perfis desta instalação: os que o arranque guardou em
   `<raiz do Velopack>\network-sandbox-profiles.txt` (`NetworkSandbox.Remember`, só quando o serviço de rede vai mesmo
   para o AppContainer e só se o nome ainda lá não estiver) e os do próprio caminho do gancho nas formas dada, longa e
   curta. O nome depende do caminho exato: arrancar pela forma curta 8.3 (`C:\Users\JNIOR~1\…`) cria **outro** perfil
   (visto nos testes: `sbx\d0\cefsimple.exe` tem dois), por isso o arranque guarda o nome de cada caminho usado.
   Para cópias à mão: `build/sandbox/appcontainer-profiles.ps1` (lista; com `-Exe … -Remove` apaga só os desses exes).
2. **Atualização a partir da 0.2.9 pelo caminho real do Éteris** (não por um atualizador externo): 0.2.9 compilada do
   `c3fe087` (o `main` publicado + correções), só com um patch de teste fora do repositório (`sbx/patch-029t.py`: feed
   local em vez do GitHub e `--user-data-dir` do perfil de teste nos argumentos do reinício, para a versão nova nunca
   abrir o perfil do dono). Abaixo.
3. **A publicação não protegia contra uma DLL cliente desfasada do motor.** A DLL passa a ter recurso de versão com a
   CEF da distribuição com que foi compilada (`ProductVersion` = `CEF_VERSION`, `Comments` = `cef_commit=<commit>`,
   gerado pelo CMake a partir de `include/cef_version.h`), e o `EterisSandboxCheck` corre `build/sandbox/verify-client.ps1`
   **antes de compilar**: commit e versão iguais aos do `manifest.json` do runtime, importações diretas só do
   `KERNEL32.dll` (tabela de importações do PE; `libcef.dll` em diferido), exportações só `EterisSandboxInfo` e
   `RunWinMain`, e nenhum `CEF_INSTALLER_CONFIG` no bootstrap nem na DLL. Exige também `SelfContained=true`. Provado:
   com a DLL do D6 (sem versão) a publicação pára («a DLL não tem a versão da CEF gravada»); com a nova passa
   (`156.0.15+gcf671c0`, diretas: `KERNEL32.dll`; diferidas: `libcef.dll`, `USER32.dll`, `SHELL32.dll`).
4. **Licenças:** o `brand-bootstrap.ps1` mantém o `LegalCopyright` do bootstrap da CEF («Copyright (C) 2026 The
   Chromium Embedded Framework Authors», verificado na publicação) e a DLL cliente declara o `libcef_dll_wrapper`;
   `licenses/INDEX.md` (gerado por `build/licenses/collect.py`) regista o CefSharp `156.0.150-eteris.N` modificado e o
   bootstrap/`libcef_dll_wrapper` (BSD-3-Clause, `CEF-LICENSE.txt`); `LicenseComplianceTests` verifica-o.
5. **Testes com o CefSharp do feed:** `SandboxSupported` passa a ser comparado com o CefSharp compilado
   (`CefSettings.WindowsSandboxInfo` existe ou não), o ramo com sandbox de `Build` é testado (o ponteiro chega ao
   CefSharp e o caminho dos filhos não aponta para o bootstrap), e os ganchos só atuam com o CefSharp do Éteris **e** o
   arranque pelo bootstrap (`SandboxInfo.LaunchedByBootstrap`; uma compilação com o feed mas sem o bootstrap já não dá a
   ACL). Os testes do sandbox passam com `-p:EterisCefSharpFeed=… -p:EterisCefSharpN=2` (49/49) sem dar a ACL à pasta
   dos testes (`-Check` → 0/2), e a bateria normal também.
6. **Processo filho por argumento:** a DLL cliente lê a linha de comandos com o `CommandLineToArgvW` (como o
   `base::CommandLine` do Chromium) e só um argumento `--type=<valor>` (prefixos `--`, `-` ou `/`, sem distinção de
   maiúsculas, antes de um `--`) conta; antes procurava a sequência na linha inteira. Ao vivo: um endereço com
   `?q=--type=renderer` abre a janela («Nova aba — Éteris», com filhos GPU, utilitários e renderers).
7. **Aviso quando o .NET não arranca:** sem a `hostfxr.dll` (ou com erro do host) aparece «O Éteris não conseguiu
   arrancar. Falta o .NET na pasta do programa (hostfxr.dll): … Detalhes: …\Eteris-bootstrap.log» (visto ao vivo numa
   pasta só com o exe, a DLL cliente e o `chrome_elf.dll`), menos nos ganchos `--veloapp-*`.
8. **ACL:** `GrantedForLaunch` verifica também o `libcef.dll` e a DLL cliente (atualização interrompida a meio), e aceita
   «ALL APPLICATION PACKAGES» (`S-1-15-2-1`, o que Program Files já dá: o serviço de rede não é LPAC) em vez das duas
   capacidades; nenhuma entrada dessas três pode negar.

**2.ª prova do Velopack (packId `EterisSbxTeste2`, feed local `sbx/vpk2-feed`: 0.2.9 do `c3fe087` sem sandbox e 0.2.10
do ramo com sandbox e a DLL cliente nova).** Fotografia antes/depois alargada (`sbx/vpk2-snapshot.ps1`: o de antes mais
as pastas `cr.sb.*` em `Packages` e as entradas `cr.sb.*` em `AppContainer\Mappings`). Feita duas vezes, igual:

| Passo | Resultado |
|---|---|
| `Setup.exe --silent` da 0.2.9 | instala em 3 s; apphost .NET, com `CefSharp.BrowserSubprocess.exe` |
| 0.2.9 a correr (perfil de teste) | `CheckAndDownloadAsync` no arranque encontra a 0.2.10 no feed e transfere o **delta** |
| Botão «Atualizar para 0.2.10» (UI Automation: `RestartRequested = true`, fecha as janelas, `ApplyPendingOnExit`) | +535 ms: o **próprio processo da 0.2.9** arranca `Update.exe --silent apply --package …0.2.10-full.nupkg --waitPid <pid da 0.2.9> … -- --user-data-dir=…`; a 0.2.9 sai com código 0 |
| `Update.exe` | aplica; +3,6 s: gancho `--veloapp-updated 0.2.10` **pelo bootstrap** (ACL dada à raiz: 2/2); +5,3 s: arranca `current\Eteris.Browser.exe --user-data-dir=…` (pai = o `Update.exe`) |
| 0.2.10 reiniciada | «Éteris» 0.2.10, `Eteris.App.dll`, sem `CefSharp.BrowserSubprocess.exe`; janela «Novidades do Éteris»; `chrome://sandbox`: GPU Limited/Low, **Network Service no AppContainer** `S-1-15-2-3146526238-…` (= `cr.sb.netC236E207…`, o perfil do caminho da instalação), Storage Lockdown/Untrusted, renderers Lockdown/Untrusted; nenhum filho com `--no-sandbox`; `network-sandbox-profiles.txt` = `cr.sb.netC236E2070ED2582B667660D85545E6ED94813633` |
| `Update.exe --silent uninstall` | gancho `--veloapp-uninstall` pelo bootstrap (125–140 ms); pasta, atalhos e chave de desinstalação removidos; **perfil `cr.sb.netC236E207…` apagado** (pasta e entrada em `Mappings`) |
| Estado do utilizador antes/depois (incluindo `Packages\cr.sb.*` e `Mappings` `cr.sb.*`) | **0 diferenças** (626 linhas iguais), nas duas vezes |

**Restos dos testes de 8 de novembro na máquina do dono (apagados com DeleteAppContainerProfile, com a confirmação do dono, a 8 de novembro):** seis perfis
`cr.sb.net*`, todos identificados pelo SHA1 do caminho de um exe de teste (nenhum é de um navegador do dono; os
`cr.sb.cdm*`, `cr.sb.odm*` e `cr.sb.xr*` de 7 de novembro não são do Éteris e não se mexe neles):

| Perfil | Criado | Exe de teste (caminho tal como foi arrancado) |
|---|---|---|
| `cr.sb.net379F5396…` | 05:34 | `…\scratchpad\sbx\d0\cefsimple.exe` (forma longa, `Júnior`) |
| `cr.sb.net66206779…` | 05:46 | `…\scratchpad\sbx\d0\cefsimple.exe` (forma curta, `JNIOR~1`) |
| `cr.sb.net2F6D58F6…` | 05:47 | `…\scratchpad\sbx\d9-acl\cefsimple.exe` (curta) |
| `cr.sb.netAB446901…` | 06:17 | `…\scratchpad\sbx\app-d9\Eteris.Browser.exe` (curta) |
| `cr.sb.net6C9F810C…` | 09:27 | `…\scratchpad\sbx\app-d5i\Eteris.Browser.exe` (curta) |
| `cr.sb.netB382764A…` | 10:00 | `%LOCALAPPDATA%\EterisBrowserSbxTest\current\Eteris.Browser.exe` (1.ª prova do Velopack, antes da correção) |

Para os apagar (só estes): `build/sandbox/appcontainer-profiles.ps1 -Exe <cada um dos caminhos acima, na forma
indicada> -Remove` — ou `DeleteAppContainerProfile` com cada nome.

**Custo do bootstrap no arranque (A/B intercalado, `sbx/ab-startup.ps1`).** Base = publicação normal do ramo (apphost)
contra sandbox = publicação com `EterisSandbox` (bootstrap: `chrome_elf.dll`, DLL cliente e broker do sandbox antes do
.NET), mesmo `libcef.dll`, sem a ACL do D9 nas duas (sem perfis de AppContainer novos). 7 rondas com a ordem alternada,
a 1.ª de aquecimento fora das medianas (6 medições cada):

| Medição | Base | Sandbox | Diferença |
|---|---|---|---|
| Arranque até a janela ter título (perfil já criado) | mediana **861,5 ms** (842–887) | mediana **882 ms** (859–914) | +20 ms (+2,4 %) |
| 2.º arranque com a instância aberta (`--new-tab`, até sair) | mediana **114 ms** (111–115) | mediana **119,5 ms** (117–123) | +5,5 ms (+5 %) |

O 2.º arranque é sistematicamente um pouco mais lento (as 6 medições com sandbox acima das 6 da base): é o custo de
carregar o `chrome_elf.dll` e o `libcef.dll` e preparar o broker antes de o .NET entregar o link. Pequeno, mas real.

**Falta:** ver «Estado do ramo `sandbox` e o que falta para fundir no `main`», em baixo.

## Estado do ramo `sandbox` e o que falta para fundir no `main` (2026-11-08, depois de fundir o `main` c3fe087)

**Provado:** motor próprio com bootstrap (D0), CefSharp com a informação do sandbox (D1/C), DLL cliente (D2), Éteris
.NET pelo bootstrap (D3), páginas, placa gráfica e serviço de armazenamento isolados no Éteris (D4), regressão sem e
**com teclado e rato** (D5: as funcionalidades do Éteris todas, sem regressões só com sandbox; filhos com menos
memória), saída opcional no repositório (D6) com a marca do Éteris no bootstrap (E, 1.ª parte), o serviço de rede
também isolado — **no código**, com a ACL dada pelos ganchos do Velopack e verificada no arranque (D9) — e o
**Velopack com o bootstrap como `mainExe`**: instalar, atualizar por delta de uma versão sem sandbox para uma com
sandbox (o gancho de atualização corre pelo bootstrap e dá a ACL), atalhos e perfil preservados, e — desde a revisão —
a atualização feita pelo próprio Éteris (botão «Atualizar para …», reinício pelo `Update.exe`) a partir de uma 0.2.9
compilada do `c3fe087`, e a desinstalação sem restos também no perfil do AppContainer do serviço de rede (gancho de
desinstalação; 0 diferenças com os perfis `cr.sb.*` na fotografia). A publicação com sandbox verifica a DLL cliente
contra o runtime do motor. Os seis perfis `cr.sb.net*` dos testes de 8 de novembro (tabela acima) foram apagados com a confirmação do
dono.

**Falta, por ordem:**
1. **E — assinatura conjunta** (½ dia): `Eteris.Browser.exe` (bootstrap com a marca), `Eteris.Browser.dll` (DLL
   cliente), `Eteris.App.dll` e o runtime da CEF (`libcef.dll`, `chrome_elf.dll`) com o mesmo certificado. O bootstrap da
   CEF verifica no processo principal (`CheckDllCodeSigning` em `cef/libcef_dll/bootstrap/bootstrap_win.cc`) que o exe,
   o `chrome_elf.dll` e a DLL cliente estão os três sem assinatura ou os três assinados e válidos com o **mesmo**
   certificado principal — senão o arranque pára (`LOG(FATAL)`). O `libcef.dll` e o `Eteris.App.dll` não entram nessa
   verificação, mas devem ir assinados também. Assinar tudo no mesmo passo da publicação, depois do
   `brand-bootstrap.ps1` (que muda os recursos do exe e invalidaria uma assinatura anterior) e antes do `vpk pack`.
   **Não bloqueia a 0.2.14:** o Éteris não é assinado, e os três sem assinatura passam a verificação (provado em baixo,
   «3.ª prova do Velopack»). Só passa a ser obrigatório no dia em que se assinar o Éteris.
2. ~~**E — CI com o CefSharp próprio**~~ — **resolvido de outra forma (0.2.14; preparado com o nome 0.2.12):** as releases publicam-se localmente
   (`docs/motor.md`, «Publicação»: o CI nem compila o motor próprio, que traz o bootstrap), por isso o CefSharp próprio e
   a DLL cliente passam a ser reprodutíveis a partir do repositório com `build/sandbox/build-all.ps1` na máquina de
   publicação («Como publicar com o sandbox», em cima). O CI continua verde sem eles: o `build.yml` e a verificação das
   tags no `release.yml` compilam e testam com o CefSharp oficial (o modo sem sandbox; os testes do sandbox seguem o
   CefSharp compilado e não saltam nenhum), e a publicação de emergência pelo CI usa `publish.ps1 -NoSandbox`
   (`WorkflowTests`). A assinatura de nome forte do CefSharp continua por fazer (o .NET não a verifica ao carregar).
3. **D9 — casos de rede por ver ao vivo**: proxy do sistema e autenticação integrada do Windows com o serviço de rede
   no AppContainer (tem a capacidade `enterpriseAuthentication`, mas não há proxy nem domínio nesta máquina). Páginas
   em `127.0.0.1`/`localhost`, transferências e HTTPS já passaram com ele, e desde o ramo `rede-seguranca` também o
   proxy por argumentos do motor (`--proxy-server`, PAC por HTTP e por `data:`; ver «Rede de segurança do arranque e
   proxy»).
4. **Custo contínuo** (já em `docs/paridade.md`, #3): cada atualização do motor recompila também o CefSharp próprio
   e a DLL cliente; propor o patch `WindowsSandboxInfo` ao CefSharp oficial reduz isto.
5. Fora do sandbox (visto no D5, igual sem sandbox): Alt+clique numa aba aberta por um link entregue à instância
   transfere o link em vez de o espreitar até a página ser recarregada.

**Fundido no `main` em 2026-11-08** (passo (a) abaixo), atrás da opção `EterisSandbox`, desligada por omissão da
0.2.10 à 0.2.13 (razões em `docs/paridade.md`, secção 2); **ligada por omissão na publicação a partir da 0.2.14**
(ramo `rede-seguranca`, passo (c) abaixo). Com o arranque fundido, o `Cef.Initialize` corre na thread
do arranque (`CefConfigurator.Prepare` decide o sandbox e o serviço de rede, `InitializeInBackground` inicializa):
a cópia publicada com `-Sandbox` manteve renderers e armazenamento Untrusted, placa gráfica e áudio Low.

**Rede de segurança (ramo `rede-seguranca`):** o Éteris abre sempre, sem sandbox se for preciso, e avisa na nova aba
(DLL cliente volta a arrancá-lo sem sandbox quando o .NET não arranca; 2 arranques com sandbox falhados seguidos —
queda antes da 1.ª página ou 3 filhos a morrer nos primeiros 30 s — desligam-no até à versão seguinte). Ver «Rede de
segurança do arranque e proxy», em baixo.

**Recomendação:** fundir no `main` por fases, sempre atrás da opção `EterisSandbox` (por omissão desligada, nada muda
para quem publica sem ela): (a) já, o código do ramo (passos A–D6, D9 e a marca), que não altera a publicação normal
(os ganchos do Velopack só mexem na ACL nas publicações com sandbox) e tem testes; (b) uma versão de teste com
`-Sandbox` para o dono, pelo canal normal do Velopack (a atualização já foi provada); (c) só então ligar
`EterisSandbox` na publicação oficial, com a assinatura conjunta e a CI. A decisão da versão em que entra é do dono.
(a) e (b) feitos; (c) preparado para a 0.2.12 e adiado para a 0.2.14 — com a rede de segurança, sem assinatura (o Éteris não é assinado) e com
o CefSharp e a DLL reprodutíveis pelo `build-all.ps1` em vez da CI (ver «Falta», itens 1 e 2).

### 3.ª prova do Velopack: da 0.2.11 sem sandbox para a 0.2.12 com sandbox por omissão (2026-11-08)

*(Histórico: provado com o nome 0.2.12; o sandbox não saiu na 0.2.12 nem na 0.2.13 e sai na 0.2.14.)*

Pacote de teste à parte: `packId` `EterisBrowserSbxTest3`, título «Eteris SbxTest3», feed local numa pasta
(`scratchpad\sbx3\feed`), perfil de teste próprio; o Éteris instalado do dono, a versão de teste dele e os navegadores
dele não foram tocados. As duas versões só levam o patch de teste de sempre (`sbx/patch-029t.py`: feed local em vez do
GitHub e `--user-data-dir` do perfil de teste nos argumentos do reinício):

- **0.2.11-teste** = `main` `16c050f` (0.2.10 publicada + a correção dos pedidos à Google), `build/publish.ps1 -Version
  0.2.11` desse commit: apphost .NET, `CefSharp.BrowserSubprocess.exe`, motor próprio, sem sandbox;
- **0.2.12-teste** = ramo `rede-seguranca` `bf9c230` com o feed (`156.0.150-eteris.5`) e a DLL cliente acabados de
  compilar pelo `build-all.ps1`, `build/publish.ps1 -Version 0.2.12` **sem argumentos** (a omissão: «Publicado COM
  sandbox … bootstrap Éteris 0.2.12»). O `vpk` gerou o delta (14 MB).

| Passo | Resultado |
|---|---|
| Fotografia antes (`sbx3/snapshot.ps1`: atalhos com destino e SHA256, desinstalação, `StartMenuInternet`, `RegisteredApplications`, `Classes`, `UserChoice` de http/https com o hash e de .htm/.html/.pdf, pastas `Packages\cr.sb.*` e entradas `cr.sb.*` em `AppContainer\Mappings`) | 617 linhas |
| `Setup.exe --silent` da 0.2.11-teste | 3 s, código 0; «Éteris» 0.2.11 com o subprocesso .NET; atalhos «Eteris SbxTest3» (Ambiente de Trabalho e menu Iniciar) e chave de desinstalação; ACL da raiz 0/2 (sem sandbox o gancho é inerte) |
| 0.2.11-teste a correr (perfil de teste, `ETERIS_TEST_MUTE=1`) | abre `example.com` por CDP; 5 filhos com `--no-sandbox`; no arranque encontra a 0.2.12-teste no feed e transfere o **delta** |
| Botão «Atualizar para 0.2.12-teste» (UI Automation) | +647 ms: a própria 0.2.11 arranca `Update.exe --silent apply … --waitPid <0.2.11>` e sai com 0; +3,6 s: gancho `--veloapp-updated 0.2.12-teste` **pelo bootstrap novo**; +5,8 s: o `Update.exe` arranca `current\Eteris.Browser.exe --user-data-dir=…` |
| 0.2.12-teste reiniciada | «Éteris» 0.2.12 (bootstrap com a marca), `Eteris.App.dll`, DLL cliente `cef_commit=cf671c07…`, sem `CefSharp.BrowserSubprocess.exe`; janela «Novidades do Éteris» e a aba `example.com` da 0.2.11 de volta (perfil preservado); `chrome://sandbox`: GPU Limited/Low, **Network Service no AppContainer** `S-1-15-2-2747827548-…` (integridade Default), Storage Lockdown/Untrusted, 3 renderers Lockdown/Untrusted; **0 filhos com `--no-sandbox`**; `sandbox-fallback.json` = `{"Starting":false,"WithSandbox":true,"Failures":0,"DisabledFor":null}` |
| ACL do serviço de rede dada pelo gancho de atualização | raiz 2/2 e `current` 2/2; `network-sandbox-profiles.txt` = `cr.sb.net0E1E02BF4C281C911D73A58BF1143D5B17DB1F74` (o perfil do caminho da instalação) |
| Atalhos, registo e navegador predefinido: instalada (0.2.11) → atualizada (0.2.12) | **0 diferenças** (só aparece o perfil `cr.sb.net0E1E02BF…`, esperado) |
| `Update.exe --silent uninstall` | 2 s, código 0; gancho `--veloapp-uninstall` pelo bootstrap (148 ms); atalhos, pasta da instalação, pasta temporária do Velopack e chave de desinstalação removidos; **perfil `cr.sb.net0E1E02BF…` apagado** pelo gancho (pasta e entrada em `Mappings`); perfil de teste apagado |
| Fotografia depois | **0 diferenças** com a de antes (617 linhas); o único `cr.sb.net*` que resta (`cr.sb.net2A5A0156…`) já lá estava e é o da versão de teste do dono (`EterisTeste\app-sbx-9e19e49`), que não se toca |

Também verificado com o `publish.ps1` novo: sem o feed pára logo («Sandbox: sem o CefSharp com o patch v2 em … corra
build\sandbox\build-all.ps1 …, ou -NoSandbox …»); com `-NoSandbox` publica a forma antiga (apphost .NET, subprocesso
.NET, CefSharp **oficial** — o mesmo SHA256 do pacote 156.0.150) e avisa. O `build-all.ps1` completo (clone local da
tag, CefSharp, DLL cliente, verificação e 2487 testes no modo com sandbox) passou; os binários do CefSharp
reconstruídos têm o mesmo tamanho dos do `156.0.150-eteris.2` que o dono usou, mas não são idênticos byte a byte (o
compilador grava caminhos e datas) — a equivalência é a dos testes e desta prova.

O que esta prova **não** cobre (revisão da 0.2.12):
- **A 0.2.12-teste era mais antiga do que a 0.2.11-teste.** O ramo `rede-seguranca` partia da 0.2.10 (`d0f6330`), por
  isso o `bf9c230` não tinha o `16c050f` (pedidos à Google no arranque) nem o `7295bd0` (Escudo nos pedidos sem aba,
  com um tratador no contexto de pedidos): a prova do Velopack e os 2487 testes com sandbox foram sem eles. Refeito
  depois de fundir o `main` (0.2.11) — ver «Revisão da 0.2.12», em baixo.
- **Navegador predefinido.** O Éteris não se regista como navegador (não há `StartMenuInternet`, `Capabilities` nem
  `UrlAssociations` em `src/`), e a fotografia não tinha nenhuma entrada `Classes`/`StartMenuInternet` do
  `SbxTest3`. As «0 diferenças» provam que o `UserChoice` do dono não mudou, não que um Éteris escolhido como
  predefinido continua a sê-lo depois de atualizar (o `mainExe` e os atalhos não mudam de caminho, por isso é o
  esperado, mas não foi visto).
- **Ícones afixados na barra de tarefas** (`User Pinned\TaskBar`): a fotografia não os cobre. Os atalhos do Ambiente
  de Trabalho e do menu Iniciar apontam para `current\Eteris.Browser.exe` e ficaram iguais.

### Revisão da 0.2.12 (2026-11-08): o que mudou e a prova depois de fundir o `main`

*(Histórico: provado com o nome 0.2.12; o sandbox não saiu na 0.2.12 nem na 0.2.13 e sai na 0.2.14.)*

Problemas confirmados na revisão e corrigidos no ramo:

1. **Peças publicadas sem testes aprovados.** O `build-all.ps1` gravava o `eteris-cefsharp.json` antes da DLL cliente e
   dos testes, e o `publish.ps1` só conferia a existência dos ficheiros: com os testes do modo com sandbox a falhar, ou
   com `-SkipTests`, a publicação saía na mesma; e as docs só pediam o `build-all` quando mudasse o motor ou o CefSharp,
   por isso as releases seguintes sairiam com o modo com sandbox por testar. Agora: marca `tests-ok.json` só depois dos
   testes, `publish.ps1` exige-a para o HEAD atual e as mesmas peças, e cada release corre
   `build-all.ps1 -SkipCefSharp -SkipClient` depois do commit da versão (passos 2 e 3, em cima).
2. **A 0.2.12 provada não era a real** (em cima). Fundido o `main` (`91ca45f`, 0.2.11) no ramo; prova repetida, em baixo.
3. **Restos ao desinstalar uma versão de emergência sem sandbox.** O gancho de desinstalação só corria com sandbox
   (`CefConfigurator.SandboxSupported && SandboxInfo.LaunchedByBootstrap`): uma publicação de emergência pelo CI
   (`-NoSandbox`, CefSharp oficial) instalada por cima da 0.2.12 deixava, ao desinstalar, o perfil
   `%LOCALAPPDATA%\Packages\cr.sb.net<SHA1>` e a entrada em `AppContainer\Mappings`. Agora o gancho corre em todas as
   publicações, só dentro de uma instalação do Velopack (`Update.exe` na raiz); um perfil que não existe é ignorado.
   Teste: `NetworkSandboxTests.Uninstall_DoesNotDependOnTheSandbox`.
4. **Pasta `publish\` com a forma errada empacotada.** O `pack.ps1` empacotava qualquer coisa em `publish\` (uma
   publicação que falhou a meio, ou uma `-NoSandbox` de teste que sobrou). Agora o `publish.ps1` apaga `publish\` em
   qualquer erro (um `trap`) e grava `publish\eteris-build.json` no fim; o `pack.ps1` exige-o, com a versão e o modo
   pedidos (`-NoSandbox` explícito, também no `release.yml`).
5. **Caminho com acentos**: o `build-all.ps1` passa ao nome curto 8.3 (e apaga ficheiros com `[IO.File]::Delete`,
   porque o `Remove-Item -LiteralPath` do PowerShell 5.1 não encontra caminhos 8.3 — visto ao corrê-lo pelo caminho com
   acentos).
6. **Binários do CefSharp publicados** conferidos com o SHA256 do `eteris-cefsharp.json`, e a DLL cliente com a testada.
7. **Proveniência**: `eteris-build.json` na publicação (e na instalação).

**Prova depois de fundir** (`7723672`: `main` 0.2.11 + estas correções), no worktree, pelo caminho com acentos:
- `build-all.ps1 -SkipCefSharp -SkipClient` (feed `156.0.150-eteris.5` e DLL cliente já compilados): DLL verificada,
  **2516 testes aprovados no modo com sandbox** (CefSharp do feed restaurado e carregado), marca gravada para `7723672`.
- `publish.ps1 -Version 0.2.12` sem argumentos: «Publicado COM sandbox … bootstrap Éteris 0.2.12», SHA256 do CefSharp e
  da DLL cliente iguais aos do feed e da marca, `eteris-build.json` com o commit e o `eteris-cefsharp.json`.
  `pack.ps1 -Version 0.2.12` empacota (pacote local apagado a seguir); `pack.ps1 -Version 0.2.12 -NoSandbox` e
  `-Version 0.2.11` recusam. `publish.ps1` com um ficheiro por gravar recusa («a árvore tem alterações por gravar») e
  com `-CefSharpN 4` recusa; uma falha simulada depois do `dotnet publish` apaga `publish\`.
- Ao vivo (cópia `scratchpad\sbx-0212m` com a ACL do D9, perfil de teste novo, `ETERIS_TEST_MUTE=1`, a página da prova
  do Escudo nos pedidos sem aba, `fuga/www`): `chrome://sandbox` com GPU Limited/Low, **Network Service no AppContainer**
  (`S-1-15-2-2265444392-…`), Storage Lockdown/Untrusted e 3 renderers Lockdown/Untrusted; `NetworkServiceSandbox`
  ligada; **0 filhos com `--no-sandbox`**; o serviço de rede arranca com `NetworkTimeServiceQuerying` e `AimEnabled` em
  `--disable-features` (o `16c050f`); o Escudo bloqueia o rastreador nos pedidos da página, do worker, do shared worker,
  do service worker, do `importScripts` e do `fetch` intercetado pelo service worker, e deixa passar o domínio livre —
  ao servidor só chegaram os pedidos ao domínio livre, os mesmos da prova sem sandbox (`docs/paridade.md`, secção 8);
  `sandbox-fallback.json` = `{"Starting":false,"WithSandbox":true,"Failures":0,"DisabledFor":null}`; fecho em 0,3 s sem
  processos a sobrar. O perfil do AppContainer da cópia foi apagado no fim (`appcontainer-profiles.ps1 -Remove`).
- Não repetido depois de fundir: a prova do Velopack 0.2.11 → 0.2.12 (instalar, atualizar, desinstalar) e a
  compilação completa do CefSharp e da DLL cliente (a fusão não mexe no patch, no `build/sandbox/client` nem no motor).

## Rede de segurança do arranque e proxy (2026-11-08, ramo `rede-seguranca`, para a 0.2.14 com sandbox)

Com o sandbox ligado na publicação oficial, uma falha que só aconteça com ele (driver, antivírus, política da máquina,
versão do Windows) não pode deixar o Éteris sem abrir: o atualizador vive dentro do Éteris, por isso um Éteris que não
abre também nunca recebe a correção. Três camadas:

| Camada | Onde | O que faz |
|---|---|---|
| **(a) o .NET não arranca** | DLL cliente (`build/sandbox/client/eteris_client.cpp`, `RelaunchWithoutSandbox`) | Se a `hostfxr.dll` falta ou o host do .NET devolve erro num arranque com sandbox, a DLL volta a arrancar o próprio exe com `--eteris-no-sandbox --eteris-sandbox-retry` (uma só vez: o novo arranque já leva o interruptor; nunca nos ganchos `--veloapp-*`) e passa-lhe o primeiro plano. Só se esse também falhar aparece o aviso «O Éteris não conseguiu arrancar». A nova aba desse arranque avisa («desligado neste arranque … da próxima vez volta a tentar»). |
| **(b) o processo cai antes da 1.ª página** | `Infrastructure/StartupGuard.cs` + `SandboxFallback.cs` (lógica pura) | `CefConfigurator.Prepare` grava `sandbox-fallback.json` no perfil (`Starting = true`) **antes** do `Cef.Initialize`; a 1.ª página apresentada (`MainWindow.PageVisibility`) ou um fecho normal (`App.OnExit`) limpa-o. Se no arranque seguinte o marcador ainda lá está e esse arranque era com sandbox, conta uma falha; **2 seguidas** → `DisabledFor = <versão>`: os arranques dessa versão correm sem sandbox (o mesmo que `--eteris-no-sandbox`: sem `WindowsSandboxInfo`, filhos com `--no-sandbox`, sem `NetworkServiceSandbox`) e a nova aba (normal e privada) avisa «O isolamento das páginas (sandbox) foi desligado por precaução … na próxima versão do Éteris volta a ser ligado». Uma versão nova volta a tentar do zero. |
| **(c) filhos a morrer no arranque** | `StartupGuard.WatchChildren` e `OnRenderProcessTerminated` | Só com sandbox e só nos primeiros 30 s: uma thread de fundo vê, de 250 em 250 ms, os filhos do processo com o mesmo exe (páginas, placa gráfica, utilitários; não os do WebView2) e o código com que saem. Contam as exceções do Windows (`0xC…`), o `0x80000003` dos `CHECK` do Chromium e os códigos com que o sandbox mata um filho que não consegue fechar (`SBOX_FATAL_*` 7006–7011 e 7013; não o 7012, limite de memória); do CefSharp contam as páginas que o motor nem conseguiu lançar (`TS_LAUNCH_FAILED`/`TS_INTEGRITY_FAILURE`, 4 e 5). **3 falhas** fazem desse arranque um arranque falhado (mesmo que a página já tenha aparecido), com o mesmo limite de 2 seguidos. |

- Sem sandbox possível (publicação normal, CefSharp oficial) nada disto corre: nem ficheiro, nem vigia.
- Uma queda **sem** sandbox (por exemplo com `--eteris-no-sandbox` manual) não conta: não é culpa dele.
- `--eteris-no-sandbox` manual continua igual (sem aviso).
- O que fica de fora: uma falha do próprio bootstrap **antes** do `RunWinMain` (assinaturas diferentes, `chrome_elf.dll`
  em falta). Aí o sandbox não muda nada: o bootstrap inicializa o broker do sandbox no processo principal mesmo com
  `--no-sandbox` (`bootstrap_win.cc`: `if (!is_subprocess || is_sandboxed)`), por isso não há arranque «sem sandbox» que
  o resolva — é a assinatura conjunta (E) que o evita. Pela mesma razão, no caso (a) o novo arranque só ajuda em falhas
  passageiras (ficheiro preso pelo antivírus a meio de uma atualização, por exemplo); numa pasta sem .NET o aviso aparece
  na mesma, mas só uma vez e depois de tentar.
- Testes: `SandboxFallbackTests` (estados, contagem, versão nova, manual, DLL cliente, códigos de saída, ficheiro
  partido, guarda inerte sem sandbox, aviso na nova aba normal e privada, interruptores iguais nos dois lados);
  `SandboxBootstrapSourceTests` (recurso de versão com a marca das DLLs de teste).
- Ganchos só de teste: `ETERIS_TEST_CRASH_BEFORE_PAINT=1` (`TestSwitches`: `TerminateProcess` logo a seguir ao
  `Cef.Initialize`, código `0xE7E50001`, sem relatório do Windows) e, só numa DLL cliente compilada com
  `cmake -DETERIS_TEST_HOOKS=ON`, `ETERIS_TEST_DOTNET_FAILS_WITH_SANDBOX=1`. Essa DLL leva `test_hooks` no recurso de
  versão e `verify-client.ps1` recusa-a na publicação («é uma DLL de teste»), também pela cadeia `ETERIS_TEST_` no binário
  (verificado: a DLL normal passa, a de teste é recusada).

**Prova ao vivo** (publicação `-Sandbox` do ramo, CefSharp `156.0.150-eteris.2`, DLL cliente nova; cópia
`scratchpad\sbx-rede` com a ACL do D9; perfis de teste novos; `ETERIS_TEST_MUTE=1`; verificação por CDP
(`ETERIS_TEST_DEVTOOLS_PORT`), linhas de comandos dos filhos e capturas; fecho por `WM_CLOSE`, sem processos a sobrar;
scripts em `scratchpad\rede\live-*.ps1`):

| Caso | Resultado |
|---|---|
| (b) 1.º e 2.º arranques com `ETERIS_TEST_CRASH_BEFORE_PAINT=1` | saem com `0xE7E50001`; estado `{"Starting":true,"WithSandbox":true,"Failures":0}` e depois `"Failures":1` |
| (b) 3.º arranque, normal | `{"WithSandbox":false,"Failures":2,"DisabledFor":"0.2.9"}`; 5/5 filhos com `--no-sandbox`; `chrome://sandbox`: tudo «Not Sandboxed»; a nova aba mostra o aviso |
| (b) 4.º arranque, mesma versão | continua sem sandbox (5 filhos com `--no-sandbox`) |
| (b) versão «nova» (`DisabledFor` trocado para `0.2.8`) | volta ao sandbox: 0/5 filhos com `--no-sandbox`, renderers Lockdown/Untrusted, GPU Low, Network Service no AppContainer, Storage Untrusted; sem aviso; no fecho `{"WithSandbox":true,"Failures":0,"DisabledFor":null}` |
| (c) sessão com sandbox: `Page.crash` + `Browser.crashGpuProcess` + `Page.crash` por CDP (renderer, GPU, renderer) | depois da 1.ª página `"Failures":0`; depois das 3 quedas `"Failures":1` (a página mostra o erro -9001); 2.ª sessão igual → `"Failures":2`; a 3.ª abre sem sandbox (5/5 `--no-sandbox`) e com o aviso |
| (a) DLL de teste, `ETERIS_TEST_DOTNET_FAILS_WITH_SANDBOX=1` | o 1.º processo sai com 0; o principal passa a ser um filho dele com `… --new-tab --eteris-no-sandbox --eteris-sandbox-retry`; 5/5 filhos sem sandbox; aviso «desligado neste arranque»; registo: «hostfxr.dll em falta …» e «novo arranque sem sandbox (--eteris-no-sandbox), processo …». O arranque seguinte sem a falha simulada volta ao sandbox (0/5), sem aviso |
| (a) caso real: pasta só com o exe, a DLL cliente publicada e o `chrome_elf.dll` (sem .NET) | 1.º processo → novo arranque sem sandbox → também falha → **um** aviso «O Éteris não conseguiu arrancar. Falta o .NET …» (do 2.º processo); duas linhas «hostfxr.dll em falta» no registo |
| (e) `--eteris-no-sandbox` manual | 7/7 filhos `--no-sandbox`, sem aviso; duas quedas com ele não contam (`"Failures":0`); sem o interruptor volta ao sandbox |

**Proxy com o serviço de rede no AppContainer (D9).** Só por argumentos do motor na linha de comandos do Éteris (nada
nas definições do Windows): um proxy HTTP de teste em Python (`127.0.0.1:8899`, regista cada pedido; responde ele
próprio a anfitriões `*.test`, que não existem no DNS, e encaminha o resto; `CONNECT` para HTTPS) e um PAC servido em
`127.0.0.1:8898` (`PROXY 127.0.0.1:8899` para tudo menos `127.0.0.1`/`localhost`). Cópia `sbx-rede` com a ACL (2/2),
perfil novo por caso; `chrome://sandbox` em todos: **Network Service no AppContainer** `S-1-15-2-373877586-…`
(integridade Default), renderers Lockdown/Untrusted, GPU Limited/Low.

| Argumento | `http://eteris-proxy.test:8080/…` (só existe no proxy) | `https://example.com/` | Registo do proxy | `network_service_failed_launch` no `Local State` |
|---|---|---|---|---|
| `--proxy-server=http://127.0.0.1:8899` | «Proxy de teste: eteris-proxy.test» | «Example Domain» | `GET http://eteris-proxy.test:8080/server`, `CONNECT example.com:443` | não |
| `--proxy-pac-url=http://127.0.0.1:8898/proxy.pac` | igual | igual | `PAC /proxy.pac` (o PAC é lido pelo serviço de rede), depois `GET …/pac-http`, `CONNECT example.com:443` | não |
| `--proxy-pac-url=data:application/x-ns-proxy-autoconfig;base64,…` | igual | igual | `GET …/pac-data`, `CONNECT example.com:443` | não |
| `--proxy-pac-url=file:///…/proxy.pac` | `ERR_NAME_NOT_RESOLVED` (o PAC não é usado: ligação direta) | carrega (direto) | nada | não |

O PAC por `file://` falha **igual sem sandbox** (`--eteris-no-sandbox`, tudo «Not Sandboxed»): o Chromium não usa PAC
por `file://` aqui; não é do sandbox. Um PAC «por ficheiro local» faz-se com `data:` (como em cima) ou servido por HTTP.
O proxy em `127.0.0.1` funciona a partir do AppContainer.

**Não testável aqui sem mudar o Windows** (fica para uma máquina de empresa ou uma VM):
- **proxy do sistema** (definições de proxy do Windows, incluindo a deteção automática WPAD por DHCP/DNS, que nesta rede
  não existe): o Chromium lê-o no processo principal e entrega a configuração ao serviço de rede, por isso o caminho no
  AppContainer é o dos casos em cima (proxy explícito e PAC por HTTP); não foi visto ao vivo porque obrigava a mudar as
  definições do Windows do dono;
- **autenticação integrada** (NTLM/Negotiate/Kerberos num proxy ou site de domínio): o AppContainer do serviço de rede
  tem a capacidade `enterpriseAuthentication`, mas não há domínio nem proxy com autenticação nesta máquina.

**Visto de passagem (não é do sandbox, igual com `--eteris-no-sandbox`):** com o proxy à frente aparecem no arranque um
`GET http://clients2.google.com/time/1/current?cup2key=…` (o relógio de rede do Chromium) e um `CONNECT
www.google.com:443`, apesar do `disable-background-networking` e do `disable-component-update`. Fica para o `main`
(candidato: `NetworkTimeServiceQuerying` no `disable-features` e procurar a origem do `www.google.com`).

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
| D | DLL nativa `RunWinMain` + bootstrap: no processo principal arranca o .NET (`hostfxr`) e passa a informação; nos filhos chama `CefExecuteProcess` | ✅ **feito no ramo `sandbox`** (2026-11-08), com o D5 interativo; D9 (rede) no código, sem patch do motor | `chrome://sandbox` mostra os processos isolados (ver «Passo D — resultado») |
| E | Empacotamento (Velopack, licenças, assinatura conjunta, ACL do D9, CI) | marca no bootstrap ✅, ACL do D9 nos ganchos ✅, Velopack (instalar, atualizar 0.2.9 → com sandbox, desinstalar) ✅; falta assinatura conjunta e CI (≈ 1,5 dias) | instalador funciona com o sandbox ligado |

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
