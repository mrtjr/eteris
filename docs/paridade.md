# Paridade com Chrome, Firefox e Brave

Objetivo: diminuir a distância — ou empatar — com as três referências do Éteris (**Chrome, Firefox e Brave**;
o Edge não é referência) antes de oferecer o navegador a outras pessoas. Cada lacuna tem uma forma de medir e um
critério de «feito»; nada passa a «resolvido» sem número ou teste.

Tabela atualizada em 2026-11-09, versão 0.2.14 (cada secção diz quando e em que versão foi medida; a 1.ª medição
foi a 0.2.2, em 2026-11-06).

## Critérios para começar a distribuir

O Éteris pode ser oferecido a outras pessoas quando os pontos marcados **[bloqueia]** estiverem resolvidos.

| # | Lacuna | Chrome | Firefox | Brave | Éteris hoje | Prioridade |
|---|---|---|---|---|---|---|
| 1 | Custo de desenhar as páginas (processador, bateria) | 6,6–6,7% de 1 núcleo | 21,7–28,3% | 4,8–8,0% | **8–11%** desde a 0.2.3 (era 28–36% na 0.2.2) | ✅ resolvido (0.2.3) |
| 1b | Abas escondidas continuam a ser desenhadas | param | param | param | **param** desde a 0.2.3 (era ~30% por aba animada na 0.2.2) | ✅ resolvido (0.2.3) |
| 1d | Janela que não se vê continua a ser desenhada (página animada, % de 1 núcleo) | minimizada 0,36; tapada por outra 0,28 | 0,95; 0,33 | 0,80; 0,09 | **minimizada: pára** desde a 0.2.14 (0,59; 6,77 na 0.2.13), também noutra área de trabalho e com a sessão bloqueada; **tapada por outra janela: continua a desenhar** (8,96); ver secção 1c, «Medição da 0.2.14» | minimizada ✅ (0.2.14); tapada por outra: em aberto, média |
| 2 | Isolamento das páginas (sandbox) | sim | sim | sim | **ligado por omissão na publicação da 0.2.14**: páginas Untrusted, GPU Low e armazenamento Untrusted, como no Chrome, e serviço de rede no AppContainer (o mesmo que o Chrome 156 com a experiência de rede ligada: no Chromium 156 vem desligado por omissão); funcionalidades verificadas com teclado e rato, rede de segurança no arranque, CefSharp e DLL cliente reprodutíveis a partir do repositório (`build/sandbox/build-all.ps1`) e atualização de uma 0.2.11 sem sandbox provada pelo Velopack (atalhos, perfil e navegador predefinido iguais; desinstalação sem restos). Até à 0.2.13 a publicação corre com `--no-sandbox` (secção 2) | ✅ resolvido na 0.2.14 |
| 3 | Atualizações de segurança do motor | automáticas, dias | automáticas, dias | automáticas, dias | Chromium 156 (igual ao Chrome hoje); Dependabot diário + atualização automática dos utilizadores | ✅ processo montado (falta a 1.ª atualização real) |
| 4 | Interface por cima das páginas (avisos, paleta, pesquisa) | — | — | — | camada numa janela própria, por cima de qualquer motor | ✅ resolvido (0.2.3) |
| 4b | Fluidez da navegação (espera por página, Voltar/Avançar, «piscar» depois de carregar) | — | — | — | desde a 0.2.9: 1.ª pintura ~0,3 s depois do clique (era ~1,25 s), Voltar 6 ms (era ~1,3 s), sem «piscar»; ver secção 1b e `docs/motor.md` | ✅ resolvido (0.2.9) |
| 5 | Abas do motor de vídeo completas (Ctrl+F, zoom, imprimir, leitura) | — | — | — | em falta | alta |
| 6 | Antifingerprinting | médio | forte (FPP/RFP) | forte («farbling») | **medido** (secção 6): a 0.2.9 era *mais* identificável do que o Chrome; no `main` (R0) deixou de o ser — dispositivos, língua, DNT e dicas do cliente como o Chrome. Canvas, WebGL e áudio continuam fixos (F1/F2) | alta |
| 4d | Arranque até à 1.ª página (morno) e memória privada | 474 ms; 143 MB (1 aba), 194 MB (5) | 1079 ms; 340 MB, 453 MB | 426 ms; 141 MB, 199 MB | 0.2.10, publicação como a da release: **625 ms** (0.2.9: 1132 ms na mesma sessão); **207 MB** com 1 aba e **320 MB** com 5 (0.2.9: 254 e 376 MB); ver secção 1c | ✅ arranque melhor do que o Firefox e a ~1,3× do Chrome; memória principal −47 MB, filhos iguais (0.2.10) |
| 4c | Vídeo H.264/AAC (Twitch, Vimeo, MP4 direto, HLS…) | sim | sim (codecs do Windows) | sim | **no motor principal**, como o Firefox: compilação própria da CEF com os codecs do Windows (H.264/HEVC pela placa gráfica, AAC pelo Media Foundation, nenhum descodificador com patentes; MP4, Twitch, g1, BBC, Reddit, TikTok, Dailymotion, HLS, `.m4a` e `.aac` validados, ~2,4% de CPU a 1080p). Sem descodificação por hardware ou num Windows N, uma sonda no arranque devolve tudo ao motor de vídeo (WebView2), como antes. Publicado desde a 0.2.9; o CI não compila este motor (`docs/motor.md`, «Motor com codecs») | ✅ resolvido (0.2.9) |
| 7 | Conteúdo com DRM (Netflix, Spotify) | sim | opcional | opcional | via motor de vídeo, **sempre com pergunta** (botão «Tocar no motor de vídeo»); o motor principal continua sem Widevine. Com DRM o site fala com o seu servidor de licenças e o CDM (Google/Microsoft) pode fazer provisionamento — inerente ao DRM, só depois de o utilizador aceitar | média |
| 8 | Extensões | sim | sim | sim | não | média |
| 9 | Tradução de páginas | sim (servidor) | sim (local) | sim | não (planeada: local) | média |
| 10 | Sincronização entre aparelhos | sim | sim | sim | **sim** desde a 0.2.13, opcional e desligada por omissão: «Entrar com o Google», favoritos, Espaços, definições, decisões do Escudo, notas e o cofre de palavras-passe, cifrados ponta a ponta no PC e guardados na pasta oculta da aplicação no Google Drive (`docs/sincronizacao.md`); sem histórico nem abas abertas, e sem telemóvel | ✅ resolvido (0.2.13) |
| 11 | Pedidos à Google no arranque, sem ação do utilizador | não medido | não medido | não medido | **0** desde a 0.2.11 (0.2.10: 2 por arranque — a hora e o «Modo IA», com os cookies da Google); ver secção 7 | ✅ resolvido (0.2.11) |
| 12 | Escudo nos pedidos sem aba (service workers, shared workers, extensões) | — (sem bloqueador) | sim | sim | **sim** desde a 0.2.11 (0.2.10: o fetch de um service worker ou shared worker, o reencaminhamento `respondWith(fetch(e.request))` de um pedido da página, o `importScripts` de um service worker e o service worker de uma extensão chegavam ao rastreador, sem GPC); ver secção 8 | ✅ resolvido (0.2.11) |
| 13 | Gestor de palavras-passe e preenchimento automático | sim | sim | sim | **sim** desde a 0.2.13: próprio (o do Chromium desligado), cofre cifrado no PC, guardar, preencher, gerar, importar e exportar, pagamentos sem o código de segurança, contactos, documentos e viagens, Windows Hello; nada vai para a Google (`docs/palavras-passe.md`). Sem chaves de acesso próprias (o WebAuthn segue para o Windows, secção 9) nem verificação de fugas | ✅ resolvido (0.2.13) |
| 14 | Pedidos à Google ao enviar formulários | — | — | — | **0** desde a 0.2.13 (antes: o utilizador e a palavra-passe, um prefixo do hash, a `passwordsleakcheck-pa.googleapis.com` em cada início de sessão); ver secção 7 | ✅ resolvido (0.2.13) |

## 1. Desenho das páginas — medido

Medição (2026-11-06): páginas locais `fps.html` (animação) e `scroll.html` (rolagem contínua), uma só aba,
janela 1360×860, 10 s, soma do processador de todos os processos do navegador em percentagem de 1 núcleo; todas
a 60 fps (confirmado pelo título da página). Éteris em build Release; Chrome e Brave com perfis temporários,
apagados no fim. Scripts: `perf-render.ps1` (Éteris) e `perf-browser.ps1` (referências), que já não existem; as
ferramentas de medição atuais estão em `build/perf/` (`README.md`: bateria padrão, A/A, rondas e intervalos de confiança).

| Página | Éteris 0.2.2 (fora da tela) | Protótipo em janela própria | Éteris 0.2.3 | Chrome 156 | Brave 157 | Firefox 159 |
|---|---|---|---|---|---|---|
| Animação | 28,1% | 8,4% | **11,1%** | 6,7% | 8,0% | 28,3% |
| Rolagem contínua | 35,9% | 8,8% | **8,4%** | 6,6% | 4,8% | 21,7% |
| Animação + 1 aba escondida animada | 60,9% | 18,0% | **10,5%** | — | — | — |
| Rolagem + 1 aba escondida animada | 60,0% | 15,5% | **8,0%** | — | — | — |

Firefox 159.0.1 (versão da Microsoft Store) medido com `perf-firefox.ps1`: perfil temporário próprio
(`-no-remote -new-instance`, sem tocar no Firefox aberto), sem ecrãs de primeira utilização, janela 1360×860,
20 s de espera antes de medir. Num perfil novo o Firefox pode ainda ter tarefas de fundo (ex.: listas de
navegação segura), por isso o valor pode estar um pouco acima do uso normal.

O protótipo (ramo `prototipo/hwndhost`, `CefSharp.Wpf.HwndHost`) troca só o controlo do motor e fica ao nível do
Chrome e do Brave. Custo conhecido: tudo o que o Éteris desenha por cima da página (avisos, paleta, barra de
pesquisa, menus internos) fica escondido pela janela da página — o mesmo que já acontece nas abas do motor de
vídeo (#4). O efeito Mica não pesa (medido com e sem: igual).

**Abas escondidas (#1b).** Em modo janela o CEF não deixa dizer ao Chromium que uma aba deixou de se ver: não
resultaram `WasHidden`, esconder a janela do CEF ou a do Chromium, o rastreio de oclusão nem o estilo Chrome
(medido com `vis.html`, que mostra no título `document.visibilityState` e os quadros por segundo). Congelar a
página (`Page.setWebLifecycleState`) parava o custo, mas a página voltava «hidden» e sem desenhar. O que resulta:
**minimizar a janela do Chromium** da aba escondida (`MainWindow.PageVisibility.cs`) — a página passa a «hidden»,
deixa de desenhar e os temporizadores abrandam mas continuam (como nas abas de fundo do Chrome); ao restaurar,
volta «visible» e é redimensionada para a área da aba. Exceção (T6 em `docs/motor.md`): as 1–2 páginas
deixadas há menos tempo ficam «quentes» (tapadas, sem minimizar) durante 8 s, para voltar a elas sem esperar; uma
página animada quente gasta como à vista (~9–11 % de 1 núcleo) só nesses 8 s, e nunca com vídeo a tocar ou som.

Feito (publicado na 0.2.3):
1. Avisos, paleta, pesquisa na página, notas, permissões, código QR, minibarra, bolha do link, folha de atalhos e
   barra de carregamento numa janela transparente presa à principal (`MainWindow.Overlay.cs`), visível só enquanto
   algum deles está à vista.
2. Motor principal em janela própria; atalhos do Éteris com o foco na página (`ShortcutKeyboardHandler`) e metade
   ativa do ecrã dividido pelo foco (`PageFocusHandler`).
3. Abas escondidas sem custo.

Pendentes desta migração:
- Ctrl+roda do rato: o zoom é feito pelo próprio Chromium (lembrado por site, como no Chrome), mas o selo de zoom
  do Éteris na barra de endereço não o acompanha.
- Por cima da paleta e da folha de atalhos a página fica escurecida, mas já não desfocada.
- Os cantos arredondados da página ficam por baixo da página. (O contorno da metade ativa no ecrã dividido passou
  para a camada de sobreposição no ciclo 14 do programa de UX.)

## 1b. Fluidez da navegação — medido (ramo `motor`, 2026-11-07)

O Chromium do Éteris é o mesmo do Chrome 156: a diferença sentida vinha das camadas do Éteris, não do motor.
Medição: perfil de teste, 11 navegações reais (Wikipédia, GitHub, BBC, g1, Amazon, duas voltas), tempos lidos
pela Performance API e alinhados ao clique; «puro» = o mesmo Éteris com todas as camadas desligadas. Detalhe de
cada passo, causa e método em `docs/motor.md`.

| Medida (mediana) | Antes | Depois | «Puro» |
|---|---|---|---|
| Espera do pedido principal | 1000–1010 ms em 9/11 | 0 | 0 |
| TTFB | 1059 ms | 136 ms | 106 ms |
| 1.ª pintura desde o clique | 1249 ms | 284 ms | 319 ms |
| Voltar ×3 | 1280/1335/1342 ms | 6/6/6 ms (back/forward cache) | — |
| «Piscar» depois de carregar (miniatura) | 50–220 ms em ~90% das navegações | 0 | 0 |
| CPU do processo principal, 11 navegações | 5453 ms | 3266 ms | — |
| Thread da janela em escritas do histórico | 3,0–3,6 ms por navegação | ≤ 0,1 ms | — |
| Falhas do processo (frame destacado) | 4 em ~25 min | 0 | — |

Rolagem: 16,7 ms por fotograma, 0 perdidos, antes e depois (igual ao Chrome). Limite que fica: o 1.º documento
de cada aba nova nasce sem os scripts precoces do Escudo (o DevTools só responde depois da 1.ª navegação).

## 1c. Arranque e memória — medido (ramo `arranque`, 2026-11-08)

Método: build Release, perfil de teste com «nova aba» no arranque, `start.html` servida localmente; o tempo vai do
`Process.Start` ao pedido que a página faz depois de 2 `requestAnimationFrame` (1.ª pintura). Antes e depois são A/B
intercalados na mesma janela temporal (7 rondas, medianas; memória: 3 rondas, conjunto de trabalho privado
da árvore de processos aos 15 s com 1 aba e aos 45 s com 5). Chrome 156, Brave 157 e Firefox 159: medição da mesma
noite (06:53–07:21, 3 arranques, medianas), noutra sessão — comparar proporções, não milissegundos. Marcas de tempo
e fotografias da memória: `ETERIS_TRACE_STARTUP` (`Infrastructure/StartupTrace.cs`).

| Medida | Éteris 0.2.9 (`c3fe087`) | Éteris ramo `arranque` | Chrome 156 | Brave 157 | Firefox 159 |
|---|---|---|---|---|---|
| Arranque morno até à 1.ª pintura | 1039 ms | **565 ms** | 474 ms | 426 ms | 1079 ms |
| Memória privada, 1 aba | 208 MB (principal 136) | **164 MB** (principal 88) | 143 MB | 141 MB | 340 MB |
| Memória privada, 5 abas | 292 MB (principal 146) | **239 MB** (principal 93) | 194 MB | 199 MB | 453 MB |

O que mudou, cada passo com o seu A/B (mediana antes → depois, medida na altura de cada um):

| Passo | Ganho medido |
|---|---|
| Lista de atalhos e procura de atualizações só depois da 1.ª página (`Infrastructure/FirstPaint.cs`) | 1116 → 1047 ms |
| O 1.º browser do CEF nasce durante o Show: o fio da interface recebe as mensagens da criação da janela do CEF (`Infrastructure/SentMessages.cs`) | 1035 → 952 ms |
| Escudo: o adblock-rust lê as listas dos ficheiros e guarda o motor compilado no perfil (`filters\motor-*.dat`) | 1 aba 206 → 167 MB, 5 abas 296 → 245 MB; Escudo ativo aos 372 ms (era 552) |
| Tabelas da base de dados da 1.ª aba abertas numa thread de fundo | 924 → 893 ms |
| `Cef.Initialize` numa thread própria, em paralelo com o XAML (o `Cef.Shutdown` volta lá) | 924 → 843 ms (com o anterior 893 → 836) |
| Motor de composição do WPF (fio de desenho, Direct3D) arrancado numa thread à parte no `Main` (`Infrastructure/CompositionWarmup.cs`) | 732 → 678 ms |
| JIT do arranque em vários núcleos guiado pelo arranque anterior (`ProfileOptimization`, `Infrastructure/StartupJit.cs`) | 678 → 567 ms |

Correções da revisão (sem custo no arranque): o motor do Escudo só vai para a cache se leu todas as listas (uma lista
bloqueada pelo antivírus nesse arranque deixava os seguintes sem ela) e grava-se depois de entrar em uso; no arranque,
o pedido da página principal espera pelo motor no máximo 250 ms (sem cache do motor, a margem medida era ~50 ms; a
espera não chegou a ser precisa em 5 rondas); o perfil do JIT para de gravar 5 s depois da 1.ª página ou ao abrir uma
janela privada (o .NET gravava até ao fim do processo: diria que funções a sessão usou) e um arranque `--private` não
o grava; o fecho do motor liberta no fio da interface os browsers que ainda existam (`Cef.PreShutdown`) e desliga o
fecho próprio do CefSharp.Wpf. Medição de novo no fim, contra `c3fe087` (Release, A/B intercalado; arranque 7
rondas, memória 5): 1.ª pintura 1107 → **604 ms**; 1 aba 211 → **168 MB** (principal 135 → 93); 5 abas 298 →
**244 MB** (principal 148 → 97), ~19 MB por aba a mais (eram ~22).

Avaliado e não registado: GC com `System.GC.Gen0MaxBudget` (6 MB) e `ConserveMemory=7` — depois da correção do
Escudo o ganho ficou no ruído (1 aba 163 → 154 MB numa medição, 5 abas sem diferença; `Gen0Size` não é aceite pelo
runtimeconfig, só pela variável de ambiente); `gcConcurrent=0` também sem ganho claro. Painéis do XAML criados só
quando precisos: a amostragem mostrou que os popups e as camadas custam ~35 ms no total — o custo do XAML era a espera
pelo motor de composição do WPF. Em aberto: ReadyToRun no código da aplicação (o pacote crossgen2 não está na cache
local; descarregá-lo precisa de autorização), processos filhos nativos (frente da sandbox, `docs/sandbox.md`), e o
2.º renderer sem página (~12–16 MB; provavelmente o renderer de reserva do Chromium, que o Chrome também tem — por
confirmar). O 1.º arranque depois de uma
atualização do Éteris não aproveita o JIT guiado nem o motor do Escudo guardado (ambos são refeitos nesse arranque).

### Medição final da publicação (`main` com o arranque e o sandbox fundidos, 2026-11-08)

Publicação como a da release (`build/publish.ps1`, motor com codecs da plataforma, *self-contained*) numa pasta
temporária, contra a 0.2.9 publicada da mesma forma a partir do `c3fe087`; perfil de teste com «nova aba» no
arranque; A/B intercalado na mesma janela temporal (arranque 7 rondas depois de 1 de aquecimento, memória 5 rondas;
medianas, intervalo entre parênteses). A 3.ª coluna é a mesma árvore publicada com `-Sandbox` (sem a ACL do serviço
de rede na pasta: o serviço de rede fica sem sandbox, como previsto), noutro perfil. Histórico: o `-Sandbox` do
`publish.ps1` era o da altura; desde a 0.2.14 o sandbox é a omissão (secção 2).

| Medida | Éteris 0.2.9 (`c3fe087`) | Éteris `main` | `main` com `-Sandbox` | Chrome 156 | Brave 157 | Firefox 159 |
|---|---|---|---|---|---|---|
| Arranque morno até à 1.ª pintura | 1132 ms (1086–1194) | **625 ms** (611–645) | 610 ms (589–642) | 474 ms | 426 ms | 1079 ms |
| Memória privada, 1 aba | 254 MB (principal 148) | **207 MB** (principal 101) | — | 143 MB | 141 MB | 340 MB |
| Memória privada, 5 abas | 376 MB (principal 158) | **320 MB** (principal 107) | — | 194 MB | 199 MB | 453 MB |

Os valores absolutos da memória são mais altos do que os da medição acima (build Release noutro perfil; aqui os
renderers medem ~25 MB cada), mas a diferença entre as versões é a mesma: −47 MB com 1 aba e −56 MB com 5, quase toda
no processo principal; os filhos (placa gráfica, rede, armazenamento, renderers) ficam iguais. Por aba a mais:
~30 → ~28 MB. Numa ronda da 0.2.9 o motor escreveu uma vez `Check failed: observers_.empty()` ao fechar; não
apareceu com o `main`.

Verificação funcional ao vivo da publicação (o mesmo roteiro também na cópia com `-Sandbox`, com o mesmo resultado):
Wikipédia (`pt.wikipedia.org/wiki/Lisboa`: documento completo, 16 imagens), vídeo H.264 em MP4 direto
(1280×720, a tocar, 82 fotogramas em ~2,8 s, 0 perdidos, `avc1` «probably» no motor principal), trocar de aba com
`Ctrl+1`/`Ctrl+2` (a página que sai passa a `hidden`; a que saiu há pouco fica «quente», como previsto), janela
privada (`--private` entregue à instância aberta: 2.ª janela com «Privada») e fecho limpo (árvore vazia em ~1,4 s).
Na cópia com `-Sandbox`, os tokens dos filhos depois de fundir — com o `Cef.Initialize` já na thread do arranque —
são os do ramo: renderers e armazenamento Untrusted (restritos), placa gráfica e áudio Low, nenhum `--no-sandbox`.

### Medição da 0.2.14 (2026-11-09)

Ferramentas de `build/perf/` (`README.md`: bateria padrão, rondas alternadas entre braços, medianas). Máquina: Ryzen 7
7700X, 31,2 GB, Windows 11. Braços: **0.2.13** publicada, **0.2.14** publicada com sandbox e a mesma 0.2.14 com
`--eteris-no-sandbox`; Chrome, Brave e Firefox com perfis novos temporários; páginas servidas localmente. Memória =
memória privada sem o processo da placa gráfica; processador = % de um núcleo.

| O que | 0.2.13 | 0.2.14 | Chrome | Brave | Firefox |
|---|---|---|---|---|---|
| Memória, 1 aba (MB) | 251 | **223** (−11%) | 164 | 150 | 482 |
| Memória, 5 abas (MB) | 377 | **322** (−14%) | 239 | 239 | 662 |
| Memória, 20 abas, aos 60 s (MB) | 1239 | **1037** (−16%) | 1279 | 931 | 2034 |
| Processador parado com 20 abas, 5 min | 1,72 | **0,13** | 0,61 | 0,21 | 1,56 |
| Processador, animação (sem a placa gráfica) | 8,05 | 7,87 | 5,93 | 7,75 | 4,75 |
| Processador, rolagem (sem a placa gráfica) | 4,51 | 4,20 | 3,13 | 2,67 | 2,75 |
| Arranque até à 1.ª pintura (ms, 20 rondas) | 703 | 735 | 510* | 472* | 1048* |
| Processador, janela minimizada (animação) | 6,77 | 6,47 sem o ajuste e8, **0,59** com ele | 0,36 | 0,80 | 0,95 |
| Processador, janela tapada por outra (animação) | 9,05 | 8,96 (o e8 não a resolve) | 0,28 | 0,09 | 0,33 |

\\* arranque das referências: 4 rondas de outro passo da mesma noite (nele a 0.2.13 deu 773 e a 0.2.14 794).

- **Memória.** A 0.2.14 sem sandbox dá 222,5 MB com 1 aba e 326,3 MB com 5: o sandbox não custa memória. Com 20 abas,
  dos 60 s aos 5 min: 0.2.14 1037 → 1029, 0.2.13 1239 → 1260, Chrome 1279 → 1526 (35 → 45 processos de página: iframes
  de anúncios, que o Éteris e o Brave bloqueiam), Brave 931 → 922, Firefox 2034 → 2000. Com poucas abas o Éteris ainda
  fica 60 a 80 MB acima do Chrome e do Brave (custo fixo do processo principal, WPF e .NET).
- **Arranque.** A diferença por pares é de +39 ms (0.2.14 com sandbox contra a 0.2.13) e a 0.2.14 sem sandbox fica
  42 ms abaixo da 0.2.14 com ele: o custo é do sandbox, cerca de 40 ms.
- **Janela que não se vê** (2 rondas, página animada, processador total com a placa gráfica). Minimizado, o Éteris
  continuava a desenhar a 60 fotogramas por segundo; com o ajuste e8, ligado por omissão, pára (com 20 abas:
  9,31 → 1,25). Tapado por outra janela continua a 60, com ou sem ele: **lacuna em aberto** (as três referências param).
  **O regresso da janela (restaurar, voltar de outra área de trabalho, desbloquear) não foi medido**: o que falta está
  em `build/perf/README.md`, «Ajustes ligados por omissão».
- **Pouca memória** (ajuste e13, 1 ronda, 8 abas, 90 s, sinal de pouca memória **simulado**): sem ele, 10 processos de
  página e ~395 MB do princípio ao fim; com ele, 10 → 4 e 404 → 253 MB, 6 abas adormecidas. Prova que o mecanismo
  adormece e liberta memória, não quando o Windows dá o sinal a sério.
- **Troca de abas** (gravação do ecrã a 60 fotogramas por segundo, 4 abas já carregadas, 12 trocas por caso; do envio
  da tecla à aba completa no ecrã, mediana e intervalo):

  | Caso | 0.2.14 | Chrome | Brave |
  |---|---|---|---|
  | Escura ↔ clara | **44,5 ms** (38–116) | 50,5 (42–66) | 54,5 (48–64) |
  | Clara ↔ clara | **47,0 ms** (34–108) | 54,5 (46–64) | 53,5 (43–62) |
  | Clara ↔ nova aba | **49,0 ms** (36–109) | 53,0 (47–63) | 55,5 (46–73) |
  | Fotogramas errados em 36 trocas | 0 | 0 | 0 |

  Empate técnico (menos de um fotograma de diferença). A 1.ª troca de cada série demorou 108–116 ms no Éteris (a aba
  ainda não tinha sido mostrada desde que abriu); no Chrome e no Brave não se distingue das outras. Numa passagem
  anterior apareceu 1 fotograma branco em 12 trocas de escura para clara: ocorrência rara (1 em 24), a vigiar.
- **Ajustes que ficam desligados** (A/B com o mesmo binário): a cache dos shaders no disco, as atualizações só ao
  sair, o motor a arrancar na sua thread, a 1.ª nova aba preparada em fundo e uma das funcionalidades do motor (e3,
  e10, e11, e12, e14.4) não mexeram no arranque; a cache de Voltar/Avançar com 3 páginas (e7) não mudou a memória; o
  e14.5 baixa o compositor do Windows mas sobe o total (13,18 → 16,64); a poupança de energia do Chromium (e16) corta
  para 30 fotogramas por segundo (13,18 → 8,30) e só serviria como modo opcional.
- **Em aberto.** Em quatro cenários curtos (janelas de 30 s que começam 8 s depois de abrir: repouso, roda do rato
  sobre links, animação que não muda o ecrã, animação simples) a 0.2.14 ficou 1,4 a 2,9 pontos acima da 0.2.13; está
  por decidir se é custo real, cauda do arranque ou o peso dos contadores de teste (a 0.2.14 correu com eles e a
  0.2.13 sem). Na bateria padrão, com janelas de 60 s a começar aos 15 s, animação e rolagem deram paridade. Por
  medir no Éteris: a 0.2.13 nos casos da troca de abas, abrir e fechar aba, e a troca para uma aba parada há mais de
  8 s.
- **Limites.** 20 abas: 1 ronda por braço; janela que não se vê e cenários curtos: 2 rondas — diferenças abaixo de
  ~10% nesses passos não são conclusivas. O indexador de pesquisa do Windows gastou ~32% de um núcleo durante toda a
  medição, igual em todos os braços.

## 2. Sandbox — resolvido na 0.2.14: ligado por omissão na publicação (2026-11-08)

**0.2.14 (ramo `rede-seguranca`, preparado com o nome 0.2.12):** `build/publish.ps1` passa a publicar **com** sandbox por omissão
(`-NoSandbox` só para emergências; a publicação pelo CI sem codecs usa-o). O CefSharp com o patch v2 e a DLL cliente
saem do repositório com um só script, `build/sandbox/build-all.ps1` (feed local `build/cefsharp/feed` e
`build/sandbox/client/out`, com verificação e a bateria de testes no modo com sandbox); passo a passo em
`docs/sandbox.md`, «Como publicar com o sandbox», e em `docs/motor.md`, «Publicação». A CI continua verde sem eles
(testes no modo sem sandbox, com o CefSharp oficial). Provado com um pacote de teste (`EterisBrowserSbxTest3`): uma
instalação 0.2.11 sem sandbox atualiza-se pelo botão do Éteris para a 0.2.12 com sandbox — renderers e armazenamento
Untrusted, GPU Low, serviço de rede no AppContainer com a ACL dada pelo gancho de atualização, 0 filhos com
`--no-sandbox`, a sessão da 0.2.11 de volta, atalhos/registo/navegador predefinido com 0 diferenças — e a desinstalação
deixa o utilizador igual ao de antes (0 diferenças, incluindo os perfis de AppContainer). Essa prova foi feita com o
ramo antes de fundir a 0.2.11 (histórico: provado com o nome 0.2.12; sai na 0.2.14); depois de fundir (`7723672`) repetiram-se os 2516 testes no modo com sandbox e a
verificação ao vivo da publicação (sandbox como em cima, flags do arranque sem Google e o Escudo nos pedidos sem aba com
o serviço de rede no AppContainer), não a prova do Velopack (`docs/sandbox.md`, «Revisão da 0.2.12»). O «navegador
predefinido igual» só prova que a escolha do dono não mudou (o Éteris não se regista como navegador), e a fotografia
não cobre os ícones afixados na barra de tarefas. A publicação com sandbox exige agora os testes desse modo aprovados
para o próprio commit (`build-all.ps1 -SkipCefSharp -SkipClient` em cada release). Fica de fora: a assinatura
conjunta (só obrigatória quando o Éteris for assinado) e o proxy do sistema / autenticação integrada vistos ao vivo
(sem domínio nem proxy nesta máquina). Histórico da decisão em baixo.

Histórico (antes da 0.2.14; o `-Sandbox` do `publish.ps1` já não existe, o sandbox é a omissão): a publicação oficial
(da 0.2.9 à 0.2.13) corre os processos do motor com `--no-sandbox`. No `main` (fundido do ramo `sandbox`), com
`build/publish.ps1 -Sandbox` (CefSharp próprio com a informação do sandbox, DLL cliente do bootstrap da CEF, opção B:
`Eteris.Browser.exe` = bootstrap, `.NET` = `Eteris.App`), `chrome://sandbox` mostra o mesmo que o Chrome 156 com a
experiência de rede ligada: renderers Renderer/Lockdown/Untrusted, GPU Limited/Low, Storage Service Untrusted e
**Network Service num AppContainer** (`NetworkServiceSandbox`, só com a ACL da pasta confirmada no arranque; a ACL é
dada pelos ganchos do Velopack ao instalar e atualizar). Verificado: todas as funcionalidades do Éteris com teclado e
rato (D5), sem regressões só com sandbox, e menos memória nos filhos (nativos, sem .NET); a atualização de uma
instalação sem sandbox para uma com sandbox pelo Velopack (delta, atalhos e perfil preservados).

Para o ✅: assinatura conjunta (o bootstrap pára se o exe, o `chrome_elf.dll` e a DLL cliente não tiverem a mesma
assinatura) e a CI com o CefSharp próprio. Detalhes, medições e passos em `docs/sandbox.md`.

**Por omissão desligado da 0.2.10 à 0.2.13.** O D5 interativo e a prova de atualização pelo Velopack passaram,
mas ficam pontos em aberto: a release é compilada pela CI, que não tem o CefSharp próprio nem a DLL cliente (ligá-lo
só numa publicação local tiraria a release do caminho da CI); falta a assinatura conjunta; o serviço de rede no
AppContainer ainda não foi visto com proxy do sistema nem autenticação integrada (com proxy por argumentos do motor —
`--proxy-server`, PAC por HTTP e por `data:` — já passou: ramo `rede-seguranca`); e o processo «GPU / Not Sandboxed»
transitório visto numa sessão longa do D5 está «a vigiar». Depois de fundir, a cópia com `-Sandbox` arranca, isola as
páginas e passa o mesmo roteiro ao vivo que a publicação normal (secção 1c, «Medição final»).

**Rede de segurança para ligar o sandbox (ramo `rede-seguranca`, para a 0.2.14):** uma falha só com sandbox não deixa o
Éteris sem abrir (o atualizador está lá dentro). A DLL cliente volta a arrancar o Éteris com `--eteris-no-sandbox` quando
o .NET não arranca; um marcador no perfil (gravado antes do motor, limpo na 1.ª página ou num fecho normal) e a vigia dos
filhos nos primeiros 30 s (3 a morrer com código de falha) contam os arranques falhados com sandbox; 2 seguidos desligam-no
até à versão seguinte, com um aviso na nova aba. Provado ao vivo (queda simulada antes da 1.ª página, renderer + GPU a
cair por CDP, .NET a falhar só com sandbox numa DLL de teste e numa pasta sem .NET). Fica de fora a falha do próprio
bootstrap antes da DLL cliente (assinaturas): resolve-a a assinatura conjunta. Detalhes em `docs/sandbox.md`, «Rede de
segurança do arranque e proxy».

## 3. Atualizações do motor

Critério: cada versão nova do CefSharp (que segue o Chrome estável) chega aos utilizadores em ≤ 14 dias, com os
testes a passar.

Processo (montado em 2026-11-06):
1. **Aviso automático.** `.github/dependabot.yml` procura todos os dias versões novas do CefSharp (os pacotes do
   motor sobem juntos) e abre um pedido de alteração; o fluxo `build` corre os testes nesse pedido. Também vigia os
   restantes pacotes NuGet, os crates do escudo e as ações do GitHub.
2. **Licenças.** `licenses/INDEX.md` regista a versão do CefSharp e do CEF; o teste
   `ChromiumCredits_WereGeneratedForTheCurrentEngine` falha enquanto as licenças não forem geradas para o motor novo
   (`python build/licenses/collect.py`, que extrai os créditos do Chromium da versão nova).
3. **Verificar ao vivo** com o perfil de teste (abrir páginas, medir com `build/perf/medir.ps1`).
4. **Publicar.** Recompilar o motor próprio para a versão nova da CEF (`build/cef-codecs`), subir a versão (csproj,
   `build/publish.ps1`, CHANGELOG, Novidades) e publicar localmente (`docs/motor.md`, «Publicação»; a tag `v*` só põe
   o `release.yml` a verificar); o Velopack atualiza os utilizadores sozinho (pacote delta).

O que falta para o ✅ definitivo: fazer uma atualização real do motor de ponta a ponta e medir o tempo total.

## 6. Antifingerprinting — medido (2026-11-08)

Medição completa (Éteris 0.2.9 contra Chrome 156, Brave 157 e Firefox 159; Cover Your Tracks, BrowserLeaks, CreepJS
e uma sonda própria em 4 sites × 2 sessões) feita fora do repositório, com os scripts `drive.py`/`probe.js`/`analyze.py`.
Correções R0 no `main` (sem motor novo), medidas no Release do `main` com um perfil de teste e o mesmo condutor:

| Sinal (Cover Your Tracks: bits) | Éteris 0.2.9 | Éteris `main` (R0) | Chrome | Brave | Firefox | O que mudou |
|---|---|---|---|---|---|---|
| `enumerateDevices` sem permissão | 8 dispositivos com nome e `deviceId` persistente por site | **3 entradas vazias** | 3 vazias | 3 vazias | 3 vazias | script da sessão (`PrivacySignals.DevicesScript`) até haver permissão ou `getUserMedia` aceite; patch no motor em `docs/motor.md` (FP1) |
| Language | 8,91 (`pt-PT`) | **6,61** (`pt-BR`) | 6,62 | 6,61 | 6,61 | língua dos sites = a do Windows (o `pt-PT` gravado por omissão migra) |
| HTTP_ACCEPT (com Accept-Language) | 13,49 | **10,18** (`pt-BR`) | 7,92 (lista com pesos) | 11,32 / 10,99 | 7,92 | idem (uma só língua: fica acima do Chrome, abaixo do Brave) |
| DNT | 2,03 (`DNT: 1`) | **0,40** | 0,40 | 0,40 | 0,40 | deixa de enviar `DNT: 1`; ficam `Sec-GPC: 1` e `navigator.globalPrivacyControl` |
| UA-CH página / worker dedicado | `10.0.0`, `156.0.0.0` (script) / **`19.0.0`, `156.0.8078.80`** | `10.0.0`, `156.0.0.0` / **`10.0.0`, `156.0.0.0`**, `getHighEntropyValues` nativo | reais nos dois | reais nos dois | — | metadados reduzidos no motor por aba (`Emulation.setUserAgentOverride`, `ReducedClientHints`); service/shared workers continuam com os reais (o CreepJS lê um service worker) |
| Intl / `navigator.language` | `pt-PT` / `pt-PT` | `pt-BR` / `pt-BR` | `pt-BR` / `pt-BR` | — | — | o Locale do motor segue a língua dos sites (sem isto a R0 criava a incoerência `pt-BR`/`pt-PT`) |
| Canvas / WebGL / áudio | 6,82 / 10,94 / 3,24 | 6,82 / 10,92 / 3,25 | 9,18 / 14,49 / 3,24 | 0,91 / 0,98 / 1,38 | 0,91 / 0,98 / 2,19 | nada (F1/F2) |
| **Soma indicativa** | **101,0** | **93,7** | 91,5–97,4 | 66,3–73,8 | 68,2 | |
| Veredicto CYT | «unique» | «unique» | «unique» | «randomized» | «unique» | |

A sonda própria continua com o mesmo identificador em todos os sites (`c3e25442` em example.com, wikipedia.org e
coveryourtracks.eff.org; era `0648fb12`): o critério de «empate» com o Brave (identificador diferente por site e por
sessão) só chega com o ruído em canvas/WebGL/áudio (F1 por scripts, F2 no motor). Limites que ficam: um iframe de
outro site com `allow="camera; microphone"` ainda vê os nomes dos dispositivos (patch do motor), e os service/shared
workers dão a versão real do Windows e do Chromium (`GetUserAgentMetadata` no motor, F2).

## 7. Tráfego sem ação do utilizador — medido (2026-11-08)

A promessa do Éteris é «nada sai do computador sem um pedido seu». Medido com um proxy HTTP local que **regista todos
os pedidos (CONNECT incluídos) e não encaminha nenhum** (responde 502), passado ao motor só por argumento
(`--proxy-server=http://127.0.0.1:8897`, nada no Windows), num perfil de teste novo: 60 s com a nova aba, depois 60 s
com uma página local servida em `127.0.0.1` (o laço local não passa pelo proxy, por isso tudo o que aparece no registo
saiu sem o utilizador pedir).

| Pedido sem ação do utilizador | 0.2.10 | `main` | Origem no Chromium 156 | Corte |
|---|---|---|---|---|
| `GET http://clients2.google.com/time/1/current?cup2key=…` (~1,3 s depois de abrir) | sim, em cada arranque | **não** | relógio de rede: `components/network_time/network_time_tracker.cc` (`kNetworkTimeServiceQuerying`, ligado por omissão no Windows: pergunta sempre que não tem uma hora de rede recente — logo ao arrancar — e depois, a cada verificação de 6 min, com probabilidade de 1,2%). Não depende do `disable-background-networking` | `--disable-features=NetworkTimeServiceQuerying` |
| `CONNECT www.google.com:443` → `GET https://www.google.com/async/folae?async=_fmt:pb&udm=50&client_locale=pt-BR&client_country=br` (~1,4 s) | sim, em cada arranque, **com os cookies da Google** (`cookies_allowed: YES`) | **não** | elegibilidade do «Modo IA» (AIM) da pesquisa Google: `components/omnibox/browser/aim_eligibility_service.cc` (`ChromeAimEligibilityService`, pedido `kStartup` com `kAimServerRequestOnStartupEnabled`); só pergunta porque o motor de pesquisa por omissão **do Chromium** é a Google. Também refrescava ao mudar de conta, de língua ou ao navegar para um URL do «Modo IA» | `--disable-features=AimEnabled` (o «kill switch» do serviço: o construtor sai logo, `IsAimEligible()` fica falso e o throttle de navegação não pede nada) |
| na página local, 60 s | nada | nada | — | — |

Como se chegou à origem (A/B, só por argumentos do motor): `--disable-features=NetworkTimeServiceQuerying` tira só o
`GET …/time/…` e deixa o `CONNECT www.google.com`; o 2.º arranque do mesmo perfil, já com `search.suggest_enabled` e
`net.network_prediction_options` gravados, continuava a fazê-lo (não era a pré-ligação ao motor de pesquisa,
`SearchEnginePreconnector`, que respeita essas preferências e espera 5 s); o NetLog do motor
(`--log-net-log`, `--net-log-capture-mode=Everything`) mostrou o URL completo (`/async/folae`), que só existe no
`AimEligibilityService`; `--disable-features=AimEnabled` tira só o `CONNECT`, e os dois juntos dão **0 pedidos**.

Correção: os dois nomes estão em `EngineHardening.NoGoogleAtStartup` (com o porquê), juntos ao `disable-features` do
motor desde o primeiro processo (as preferências do perfil só se aplicam depois do arranque e não chegariam ao 1.º).
Testes: `Build_FixesEverySwitchThatKeepsTheEngineQuiet` fixa a lista inteira de `disable-features` e os interruptores
de tráfego de fundo; `EngineFeatureNamesTests` prova que os dois nomes são `base::Feature` do `libcef.dll` em uso; a
promessa nova «No arranque, nada sai para a Google» em `eteris://privacy/` fica ✘ e diz o nome se algum faltar.

Medição final (`main` publicado como a release, perfil novo): **0 pedidos** em 60 s de nova aba + 60 s de página
local (0.2.10, mesma sessão: os 2 acima); no 2.º arranque do mesmo perfil, **0 pedidos** em 7 min parado + 1 min com
a página. Com um proxy que encaminha, a pesquisa na Google, o example.com e `eteris://privacy/` (✔ na promessa nova)
funcionam como antes. O que o relógio de rede fazia deixa de existir: o aviso «o relógio do computador está errado»
nas páginas de erro de certificado passa a vir só da data da compilação do motor, como no Chromium sem rede.

Fora desta medição: a procura de atualizações do Éteris instalado (Velopack → GitHub, depois da 1.ª página) não passa
pelo motor (usa o proxy do Windows, não o argumento) e não acontece numa compilação de teste, que não está instalada.

**Formulários (0.2.13; `docs/palavras-passe.md`, A0).** Com o mesmo método, numa página local
com um início de sessão, uma morada e um cartão enviados (dados inventados): o motor mandava o par utilizador/palavra-
passe (prefixo de um hash) a `passwordsleakcheck-pa.googleapis.com/v1/leaks:lookupSingle` logo depois do envio do
início de sessão, apesar de `credentials_enable_service=false` — a verificação de fugas do Chromium corre sem o gestor.
Corte: `profile.password_manager_leak_detection=false` nas preferências do perfil, lida de volta; e, preventivo (0
pedidos medidos), `--disable-features=AutofillServerCommunication` contra o servidor de previsões de formulários
(`content-autofill.googleapis.com`). Depois: **0 pedidos** no proxy e 0 linhas `googleapis` no NetLog. A promessa
«Nada sai do computador sem um pedido seu» em `eteris://privacy/` diz também os formulários.

**Sincronização (0.2.13).** Por omissão está desligada e a promessa fica exatamente como medida
acima: o arranque não faz pedido nenhum à Google (montar o serviço e ler o estado só leem o disco; testes
`SyncAgentTests.Startup_BuildingTheAgentAndReadingTheState_SendsNothing` e
`SyncIntegrationTests.Startup_OnlySchedulesTheFirstRound_ThirtySecondsAfterTheWindow`). Com ela ligada — escolha do
utilizador —, a exceção é dita em `eteris://privacy/` (`PrivacyPage.SyncException`): só os pedidos da sincronização,
cifrados, para a pasta oculta da aplicação no Drive dele, **30 s depois de a janela estar pronta** (ou 10 s depois de
uma alteração, se for antes), 10 s depois de uma alteração, de 10 em 10 minutos e ao abrir o menu do Éteris (⋯) ou com
a secção Sincronização à vista (pedidos de outros computadores; ao responder a um, acompanha-o de 2 em 2 s até ao fim,
no máximo 10 min — numa ronda agendada só se contam), mais a renovação da sessão, o nome e o e-mail da conta (uma vez)
e as ações do utilizador, por um cliente próprio que só fala com `oauth2.googleapis.com` (`/token`, `/revoke`) e
`www.googleapis.com` (`/drive/v3/files`, `/upload/drive/v3/files`, `/drive/v3/about`) — nada pelo motor
(`GoogleSyncTests.HttpClient_OnlyReachesGoogleOAuthAndDrive`, varrimento `SyncLibrary_HttpOnlyInTheGoogleFolder…` e
`BrowserSyncFolder_HttpOnlyThroughTheGoogleClient`). Prova ao vivo com a pasta de teste: a 1.ª escrita chega 31,6 s
depois de abrir; a medição com o Drive real e o proxy fica para a fase «Prova».

## 8. Pedidos sem aba: a fuga do Escudo — medido (2026-11-08)

O estudo das extensões (`C:\cef\notas\viabilidade\viab-extensoes.md`, secção 3) viu um `fetch` a
`google-analytics.com` bloqueado na página passar com 200 no service worker de uma extensão. A causa: o Escudo vivia só
no `PrivacyRequestHandler` de cada aba (`ChromiumWebBrowser.RequestHandler`), e a CEF só lho entrega quando o pedido
tem browser. Os pedidos sem aba vão ao tratador do **contexto de pedidos**, que o Éteris não tinha
(`resource_request_handler_wrapper.cc`, `GetHandler`: primeiro o tratador da aba; sem browser, o do contexto, e para
os workers sem frame o do perfil, `CefBrowserContext::UpdateWorkerRequestContextHandler`).

**Como se mediu.** Um site local em `127.0.0.1:8851` (servidor que regista cada pedido com o `Host` e o `Sec-GPC`),
`--host-resolver-rules` a mapear um rastreador das listas (`sb.scorecardresearch.com`; a `google-analytics.com` tem
HSTS pré-carregado e o `http://` nem sai) e um domínio livre de controlo (`cdn.exemplo-livre.com`) para esse servidor,
perfil de teste novo, a extensão de teste do estudo com `--load-extension`, leitura por DevTools
(`scratchpad\fuga\fuga.ps1`). Cada vetor pede o rastreador e o domínio livre.

| Pedido ao rastreador | 0.2.10 (`16c050f`) | `main` |
|---|---|---|
| `fetch` da página (controlo) | bloqueado | bloqueado |
| `fetch` de um worker dedicado | bloqueado | bloqueado |
| `fetch` de um shared worker | **chegou** (sem GPC) | bloqueado |
| `fetch` do próprio service worker | **chegou** (sem GPC) | bloqueado |
| `fetch` da página reencaminhado pelo service worker (`respondWith(fetch(e.request))`) | **chegou** (sem GPC): o bloqueio da página era contornado | bloqueado |
| `importScripts` de um service worker novo | **chegou** (sem GPC) | bloqueado |
| `fetch` do service worker de uma extensão | **chegou** | bloqueado |
| domínio livre pelos mesmos caminhos | chega, sem GPC nos sem aba | chega, **com GPC** em todos |
| contagem no botão do Escudo da aba | 7 | **11** (os 4 sem aba contam na aba que mostra o site) |
| janela privada (`--private`, contexto em memória), os mesmos vetores sem a extensão | 4 chegavam | **0**; contagem 11 |

**Correção.** `Shields/BrowserlessRequests.cs`: um `RequestContextHandler` que devolve o mesmo
`PrivacyResourceHandler` das abas, em modo «sem aba» (o browser e o frame chegam nulos e nunca se leem: o CefSharp
embrulha um browser nulo num objeto que fecharia o Éteris), com a página de topo deduzida de quem fez o pedido (a
origem do worker; uma origem opaca não é o site de ninguém; sem origem, a do referrer http(s)). Decide como numa aba:
GPC, língua única e dicas do cliente, referrer só com a origem, rede local (com a autorização do site), modo rigoroso,
regras do Observatório, listas, CNAME e cookies de terceiros. Uma navegação reencaminhada por um service worker só leva
os cabeçalhos (saltos, limpeza e HTTPS são da navegação da aba). O contexto global do motor não aceita tratador depois
de criado: um contexto que partilha o armazenamento com ele e tem o tratador põe-no no perfil (instalado logo a seguir
ao `Cef.Initialize`, libertado antes do fecho); a janela privada e o «Espreitar» criam o contexto já com ele. Os
bloqueios contam no total do Escudo, no relatório de privacidade (fora da janela privada) e na aba que mostra o site.

**Custo.** Os pedidos das abas não passam por aqui (a CEF pergunta primeiro à aba). Instalar o tratador leva ~0,5 ms
na thread do motor, fora do fio da interface; 1.ª página com 4 arranques mornos de cada (compilação de depuração):
0.2.10 727–855 ms, `main` 702–746 ms (ruído). O fecho continua em ~0,3 s. Um pedido de worker custa o mesmo que um de
aba.

**O que fica de fora (medido ou por construção).**
- O script principal do service worker (`sw.js`) e as verificações de atualização dele saem sem passar por nenhum
  tratador (sem GPC no registo): são pedidos do processo do navegador. Por especificação são sempre da mesma origem
  da página, por isso não levam nada a terceiros.
- Pedidos do próprio motor sem página (DNS cifrado, `component updater`, relógio de rede): não passam por tratadores da
  CEF; estão medidos na secção 7 (0 pedidos sem ação do utilizador).
- O service worker de um iframe de terceiros tem como topo a origem do iframe (a CEF não diz qual é a página de
  topo); as listas veem-no como «primeira parte» desse terceiro. As listas por domínio continuam a apanhá-lo.
- Beacons (`sendBeacon`): o do mesmo site chega, com GPC (passa pela aba); os de outro site não chegaram ao servidor
  em nenhuma das duas versões, nem o do domínio livre — não é fuga, mas fica por perceber se o motor perde beacons
  legítimos de outro site.

Testes: `BrowserlessRequestsTests` (página de topo deduzida, bloqueio e contagem com o site do worker, GPC, escudo
desligado no site, navegação reencaminhada, extensão, rede local, cookies de terceiros, o tratador nunca lê o browser
nem o frame, cache com teto, e todo o `new RequestContext(` do Éteris com o tratador).

## 9. Chaves de acesso (WebAuthn) — provado (2026-11-09, ramo `sync`)

O Éteris não tem gestor de chaves de acesso próprio (desenho do gestor de palavras-passe, v1): o WebAuthn das páginas
tem de seguir para o Windows, como no Chrome, no Firefox e no Brave. **Prova ao vivo** (`docs/palavras-passe.md`, E2):
numa página local em `http://localhost` com um utilizador inventado, `navigator.credentials.create()` no Éteris (CEF
156, estilo Alloy) abre a janela **«Segurança do Windows › Salvar sua chave de acesso»** do próprio Windows
(`webauthn.dll`), presa à janela do Éteris, com o utilizador e «Chave de acesso do localhost». Neste PC, sem Windows
Hello configurado (`isUserVerifyingPlatformAuthenticatorAvailable()` = `false`, igual ao `DeviceNotPresent` da A3),
o Windows pede uma chave de segurança USB. Nada foi criado (a janela não foi tocada; fecha com a janela do Éteris).
**Diferenças observadas:** o prazo do pedido (30 s) não fecha a janela do Windows (ficou aberta 45 s, até o Éteris a
fechar) — no Chrome o prazo cancela a operação; e, enquanto essa janela está aberta, um segundo pedido da página não
começa. A sugestão de chaves de acesso no campo do utilizador («UI condicional») e as chaves guardadas no próprio
navegador ficam para depois (como no desenho). **Por provar à mão (dono):** num PC com Windows Hello, a mesma página
pede o PIN/rosto/impressão digital, e o «Outro dispositivo» (telemóvel por QR) aparece na mesma janela.

## Restantes

Ordem depois dos três bloqueadores: #5, #6 (medir primeiro com testes públicos de fingerprinting), #9, #7, #8 (a #10, a
sincronização, chegou na 0.2.13).
