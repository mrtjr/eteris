# Motor: fluidez da navegação

Pedido do dono (2026-11-07): melhorar a fluidez da navegação, «provavelmente começando pelo motor». Três queixas:

1. **«Piscar» depois de carregar** — a página pisca rápido, como uma recarga muito curta, logo depois de carregar.
2. **Fluidez** — em relação ao Firefox, Brave e Chrome, o Éteris está muito atrás.
3. **Vídeo** — vídeos de outros sites não carregam; o Éteris deve reproduzir qualquer vídeo em qualquer site.

O Chromium do Éteris é o 156, o mesmo do Chrome 156: as diferenças de fluidez em relação ao Chrome vêm sobretudo da
camada do Éteris (handlers .NET, janela própria da página com o HwndHost, flags, Escudo, sobreposições). Cada passo
abaixo é medido ao vivo antes e depois (perfil de teste `profile-motor`, janela 1360×860, sem som:
`ETERIS_TEST_MUTE=1`).

## Como se mede o «piscar»

Captura do ecrã a 60 fps (59–60 fps conseguidos, intervalo mediano 16,6 ms) da área da janela durante cada
navegação pela omnibox, com um build de diagnóstico que regista os eventos (FrameLoadEnd, início e fim da miniatura…)
com hora UTC precisa. Por fotograma: luminância média da área da página e diferença ao fotograma anterior.
`thumb_bad_ms` = tempo em que a página difere (> 8) do fotograma anterior à captura da miniatura, entre o início
da captura e 150 ms depois do fim; `thumb_maxdiff` = maior diferença nessa janela; `thumb_dur` = duração do
`Page.captureScreenshot`. Roteiro: Wikipédia/Portugal → GitHub, BBC, g1, The Verge, Wikipédia/Brasil, Wikipédia/Lisboa.

## Interruptores só de teste

- `ETERIS_TEST_MUTE=1` (no ambiente do processo que lança o Éteris): nenhum som sai dos dois motores
  (`mute-audio` no Chromium, `CoreWebView2.IsMuted` no motor de vídeo). Ver `Infrastructure/TestSwitches.cs`.

## Resultado (build final, 11 navegações, perfil `profile-motor`, sem som)

| Medida (mediana) | Antes | Depois | «Puro» (diagnóstico) |
|---|---|---|---|
| Espera do pedido principal | 1000–1010 ms em 9/11 | **0** | 0 |
| TTFB | 1059 ms | **136 ms** | 106 ms |
| 1.ª pintura desde o clique | 1249 ms | **284 ms** | 319 ms |
| LCP desde o clique | 1317 ms | **391 ms** | 319 ms |
| Voltar ×3 | 1280/1335/1342 ms | **6/6/6 ms** | — |
| CPU do processo principal (11 navegações) | 5453 ms | **3266 ms** | — |
| Thread da janela em escritas do histórico, por navegação | 3,6 ms | **0,0 ms** | — |
| Página perturbada pela miniatura | 82–149 ms em 6/6 | **0 em 6/6** | 0 |
| Falhas do processo (frame destacado) | 4 em ~25 min | **0** em ~35 min de roteiro | — |
| Nova aba (1.ª pintura) | 128/135 ms | 115/119 ms | — |
| Rolagem 8000 px | 16,7 ms/fotograma, 0 perdidos | igual | igual |

Linha de base e final medidas com o mesmo roteiro e o mesmo build de medição (instrumentação de
`scratchpad/diag/diag-fluidez.patch`, nunca no commit), depois do F0 e do F1 (antes deles a linha de base tinha
duas páginas a carregar ao mesmo tempo e o processo caía). As falhas registadas durante estes testes vieram de
outro build (`artifacts2`, trabalho paralelo, `libcef.dll` 0xc0000005), nenhuma dos builds deste ramo.

## Motor com codecs (preparado, por compilar: decisão do dono)

O motor de vídeo (WebView2) é um recurso: troca de árvore de processos, perde funções que só existem no motor
principal (`docs/paridade.md` #5) e o Escudo só chega lá por rede e cosmética. A causa é a CEF publicada (a que o
CefSharp usa) ser compilada **sem** `proprietary_codecs`: o `libcef.dll` 156.0.15 não tem H.264/AAC (medido: 0
ocorrências de `h264_decoder`; o acelerador `d3d_h264_accelerator.cc` está lá, bloqueado acima dele).

**Recomendação: opção (b), «codecs da plataforma».** CEF 156.0.15 compilada no commit exato
`cf671c072925b27823a61181152f4f288e8e4306` (ramo 8078, Chromium 156.0.8078.80, FFmpeg `9db86ce5`) com
`is_official_build=true proprietary_codecs=true ffmpeg_branding=Chromium enable_platform_hevc=true
enable_hevc_parser_and_hw_decoder=true symbol_level=1 blink_symbol_level=0 v8_symbol_level=0` (e `media_use_openh264=false`):
H.264 e HEVC descodificados pela placa gráfica (`D3D11VideoDecoder`), AAC pelo descodificador do Media Foundation do
Windows. O `libcef.dll` não leva nenhum descodificador com patentes — o modelo do Firefox e da Vivaldi e a posição do
mantenedor da CEF (problema #3559). O WebView2 fica para DRM, máquinas sem descodificação por hardware e Windows N.

**Patch próprio (5 pontos)**, `build/cef-codecs/patches/platform-codecs.patch`, gerado sobre a árvore depois do
checkout: (1) `media/BUILD.gn` — levantar o `assert` de «proprietary codecs + ffmpeg_branding Chromium» quando
`enable_cef`; (2) `media_foundation_audio_decoder.cc` `GetTypeInfo` — `MFAudioFormat_AAC` para AAC-LC/HE/HEv2, não só
xHE; (3) `gpu_mojo_media_client_win.cc` — anunciar `kAAC` quando o Media Foundation inicializa; (4) `supported_types.cc`
(`IsDecoderAACSupported` e `case kH264`) — consultar as caches de descodificadores suplementares, como já se faz para
VP8/HEVC: sem placa gráfica o `isTypeSupported('avc1…')` diz «não» e a deteção de vídeo (V1) leva a página ao WebView2;
(5) `ffmpeg_common.cc` — `sample_format` F32 para AAC quando o FFmpeg só desmultiplexa.

**Integração (já no repositório, inativa sem a compilação):**
- `Eteris.Browser.csproj`: alvos `EterisCefRuntimeAfterBuild`/`AfterPublish` copiam o runtime **completo**
  (`libcef.dll`, `chrome_elf.dll`, `.pak`, `icudtl.dat`, `v8_context_snapshot.bin`, `d3dcompiler_47.dll`,
  `dxcompiler.dll`, SwiftShader, Vulkan, `locales\`) de `build/cef-codecs/out/runtime` por cima do do NuGet, só quando
  lá existem `libcef.dll` e `manifest.json` (testado com um runtime falso: substitui, e sem a pasta nada muda). O
  CefSharp 156.0.150 não muda: o hash da API vem dos cabeçalhos `include/`, não dos GN args.
- `build/publish.ps1`: recusa publicar se o manifesto não disser `mode = platform-codecs`, se o commit não for o
  `cf671c0…`, se o `libcef.dll` não for `156.0.15+gcf671c0+chromium-156.0.8078.80` ou se algum ficheiro não bater com o
  SHA256 do manifesto (testado: modo `chrome` e versão errada recusados).
- `build/licenses/collect.py`: os créditos do Chromium saem do `resources.pak` da compilação própria quando existe, e o
  índice diz «compilação própria, codecs do Windows».
- `.gitignore`: `build/cef-codecs/out/`.
- `CefCodecsRuntimeTests`: o alvo nunca pode ficar incondicional.

**Por fazer quando o dono autorizar:** juntar a `build/cef-codecs/` o `build.ps1` (verificações, aplicação idempotente
do patch, `cef_create_projects`, compilação com prioridade baixa, `make_distrib --minimal`, recolha do runtime e
`manifest.json` com modo, GN args, commits e SHA256), o `platform-codecs.patch` e o `README.md`. Um trabalho paralelo
já os tem em rascunho fora do repositório (com um patch extra para o desmultiplexador ADTS do FFmpeg); entram
aqui quando estiverem fechados, para não haver duas versões. Neste passo não se descarregou nem compilou nada. Ao juntar:
acrescentar a `licenses/INDEX.md` a linha «Chromium Embedded Framework 156.0.15 (compilação própria, codecs do
Windows)», uma nota em `THIRD-PARTY-NOTICES.md` (H.264/HEVC/AAC descodificados por componentes do Windows; o Éteris não
inclui descodificadores desses formatos), o script e o patch publicados (fonte correspondente da LGPL-2.1 do FFmpeg) e
a oferta escrita do código do FFmpeg `9db86ce5`.

**Pré-requisitos (instala o dono):** Windows SDK 10.0.28000 com Debugging Tools for Windows; VS 18 Insiders (já tem
ATL/MFC e é aceite pelo `vs_toolchain.py` do 156) ou VC.ATLMFC nas Build Tools 2022; árvore num caminho ASCII com menos
de 35 caracteres (`C:\cef`); exclusão do Defender para `C:\cef`, opcional (definição de segurança: faz ele).
**Tempo e disco:** download 1–3 h; 1.ª compilação oficial (ThinLTO + PGO) 5–8 h; total 6–11 h; recompilar depois de
mexer no patch: minutos + 20–60 min de ligação. 110–150 GB (fonte ~60–80, `out` ~40–60, distribuição ~3); havia
183,8 GB livres, abaixo dos 200 GB recomendados. 31,2 GB de RAM chegam. Cada versão nova da CEF repete as 6–11 h e o
patch; não cabe nos runners alojados do GitHub (~14 GB de disco).

**Riscos.** Técnicos de (b): o AAC pelo Media Foundation no pipeline normal não está provado no 156 (no M117 falhou sem
patch); sem placa gráfica o H.264 não toca no motor principal (fica o WebView2); 5 pontos de patch a manter. Legais de
(b): baixos (descodificadores do Windows, licenciados pela Microsoft). Opção (a), `ffmpeg_branding=Chrome`
(H.264/AAC por software no FFmpeg): compila sem patches e serve para validar o pipeline e medir, mas **distribuí-la**
traz o risco de patentes (pool AVC da Via LA, HE-AAC) e a cláusula «liberty or death» da GPL-3/LGPL-2.1 — só para
testes locais, nunca publicar sem decisão explícita do dono ou parecer jurídico (isto não é aconselhamento jurídico).

**Verificação depois de compilar** (perfil de teste, `ETERIS_TEST_MUTE=1`): `MediaSource.isTypeSupported` para
`avc1.640028`, `mp4a.40.2`, `mp4a.40.5` e `hvc1` = true; MP4 com som (samplelib) toca; Twitch toca no motor principal;
`chrome://media-internals` mostra `D3D11VideoDecoder` + Media Foundation/`MojoAudioDecoder`; com
`--disable-accelerated-video-decode` o `avc1` dá «não» e o recurso ao WebView2 dispara;
`--enable-features=MediaFoundationClearPlayback` só como experiência. Com o motor próprio, a lista `KnownH264Sites`
(V2) deixa de fazer sentido e sai.

## Troca de abas sem piscar (T1, T2)

Queixa do dono depois do P1/P2: «a tela ainda pisca um pouco, mas está piscando mesmo quando troca de aba ou abre uma
nova aba». **Medição**: captura do ecrã a 60 fps da janela inteira (`scratchpad/abas/abasrun.ps1` + `swap.py`), 10
repetições por caso, páginas locais (clara, outra clara, escura `#0d1117`) servidas em `127.0.0.1` e a Wikipédia.
Fotograma **errado** = a área da página está longe do estado ANTES da ação e do estado DEPOIS (mais de 3 % dos píxeis
com |ΔL| > 30 em relação a ambos): branco, Mica, fundo vazio ou página por desenhar. A moldura (barras) foi medida
à parte: 0 fotogramas errados em todos os casos, antes e depois.

**Causas, provadas por A/B** (build de diagnóstico com interruptores; LD = clara↔escura, WW = Wikipédia↔Wikipédia,
TL = Ctrl+T a partir da clara; fotogramas errados em 10 capturas):

1. **Esconder antes de a nova aba ter desenhado.** A janela da aba que sai escondia-se no mesmo instante em que a da
   que entra aparecia. Mas a janela de uma aba escondida está minimizada (#1b) e o CefSharp encolhe-a para 0×0 ao
   esconder (`OnIsVisibleChanged` → `ResizeBrowser(0, 0)`); ao reaparecer, o Chromium tem de voltar a dispor e
   rasterizar a página e, até apresentar, a janela mostra um fotograma vazio: o branco por baixo (1 fotograma), depois o
   Mica (a cadeia de apresentação redimensionada ainda vazia, 1–2), e numa página pesada o branco da página por
   rasterizar (Wikipédia: até 20 fotogramas, ~340 ms). Nenhum dos sintomas desaparece mexendo só numa das peças:
   não minimizar (LD 14 → 9, WW 204 → 195), manter o tamanho da janela escondida (14 → 14, 204 → 173), mostrar antes de
   esconder sem esperar (14 → 13, 204 → 206). Com **dupla memória** — a nova aparece POR BAIXO da antiga, e a antiga só
   se esconde depois de um `requestAnimationFrame` duplo da nova (+34 ms para o DWM) — LD 14 → **0**, WW 204 → 11
   (2/10; com limite de 300 ms ainda 48, por isso 1 s).
2. **Aba nova mostrada antes do 1.º documento.** Uma aba acabada de criar só tem, durante ~100–250 ms, o documento
   vazio inicial (branco) e a seguir a nova aba a meio de se preencher. Esperar pelo `FrameLoadEnd` do 1.º documento
   antes do rAF duplo: TL 51 → 11 → 3 com a margem de 34 ms (os restantes eram o último fotograma antigo ainda no ecrã).
   Pintar o fundo do Chromium da cor da página interna (`BrowserSettings.BackgroundColor`) **não** muda nada com a
   dupla memória (TL 11 → 11, WW 11 → 12): não entra.
3. **Fechar a aba ativa:** a página fechada era destruída antes de a vizinha estar ativa (o `TabsViewModel` tira a
   aba da lista primeiro, a ativa passa por `null`). Agora fica, calada, por cima, até a vizinha apresentar (recurso:
   2 s). XL 20 → 1 (10 capturas).
4. **Teclado perdido (não é piscar, mas apareceu na medição):** depois de trocar de aba, o foco do Windows ficava na
   janela minimizada da aba anterior: o 3.º Ctrl+Tab seguido, e qualquer tecla, iam para uma página invisível
   (2 trocas efetivas em 8 Ctrl+Tab). A página da aba que entra passa a receber o foco, como no Chrome: **8 em 8**.

Avaliado e recusado: `WS_CLIPSIBLINGS` nas janelas das páginas (20 repetições × 4 casos: 4 fotogramas errados com e 4
sem). CPU de uma aba animada escondida (#1b, 10 s, todos os processos da instância): 4,8 % de 1 núcleo antes → 2,2 %
depois (à vista: 10,9 → 9,7 %); a aba que sai continua ativa no máximo até a nova apresentar (≤ 1 s, ~150 ms típico).

| Caso (10 capturas) | Tema escuro + Mica: antes → depois | Claro + Mica | Escuro sem Mica | Latência mediana até ao estado final |
|---|---|---|---|---|
| Ctrl+Tab clara ↔ clara | 15 (10/10) → **0** | — | — | 108 → 136 ms |
| Ctrl+Tab clara ↔ escura | 14 (9/10) → **0** | 49 → **0** | 10 → **0** | 109 → 133 ms |
| Clique na aba clara ↔ escura | 14 (7/10) → **0** | — | — | 289 → 305 ms |
| Ctrl+Tab clara ↔ nova aba (eteris://) | 44 (10/10) → **0** | 32 → **0** | 28 → **0** | 125 → 132 ms |
| Ctrl+Tab com a aba escondida há > 10 s | 14 (10/10) → 1 (1/10, Mica) | — | — | 109 → 133 ms |
| Ctrl+T a partir da clara | 96 (10/10) → **0** | 71 → **0** | 68 → **0** | 245 → 205 ms |
| Ctrl+T a partir da escura | 99 (10/10) → **0** | 71 → **0** | 71 → **0** | 237 → 207 ms |
| Botão + | 97 (10/10) → **0** | — | — | 417 → 369 ms |
| Fechar a aba ativa (a vizinha aparece) | 24 (10/10) → 1 (1/10) | 20 → **0** | 20 → 1 | 121 → 135 ms |
| Ctrl+Tab Wikipédia ↔ Wikipédia | 204 (10/10; até ~340 ms de branco) → 4 (1/10) | — | — | 379 → 270 ms |

**Resíduo:** 1 fotograma isolado (17 ms) em ~1 de 40 trocas, sem padrão de caso (Mica ou branco, a ~70–90 ms da
ação, antes de a antiga sair); e a primeira vez que se mostra uma aba pesada aberta em segundo plano (nunca desenhada
ao tamanho real), o rAF duplo resolve antes de a rasterização acabar e vê-se a página em branco por desenhar até
~150 ms (WW: 1 captura em 10, a 1.ª). A nova aba preenche-se a si própria (saudação, papel de parede): isso é a
própria página, não o motor.

## Troca de abas: o resto do estudo do Chromium (T3–T6)

Seguimento do estudo `C:\cef\notas\brave-comparacao\chromium-abas.md` (§5). A recomendação 1 (segurar a página que
sai por cima até a nova apresentar) é o T1. A recomendação 6 (não mexer em `BrowserSettings.background_color`)
mantém-se: nada abaixo lhe toca. **Tudo isto foi escrito sem medição ao vivo (o dono estava a usar o PC):** só
compilação e testes unitários; os casos a medir estão no fim da secção.

**T3 — Mica por baixo da página (rec. 4).** O estudo propunha duas vias: limitar o vidro (`GlassFrameThickness`) às
barras, ou impedir as janelas da página de pintarem por GDI por cima da cor do `PageBackdrop`. Escolhida a segunda,
porque a primeira:
- obrigava a recalcular o vidro a cada mudança da moldura (abas laterais, painel lateral, barras automáticas que
  deslizam, ecrã inteiro, ecrã dividido), e cada mudança é um `DwmExtendFrameIntoClientArea` (risco de piscar a
  moldura, que hoje tem 0 fotogramas errados);
- mudava o aspeto do que o WPF desenha translúcido dentro da área da página (o separador do ecrã dividido ficaria
  sobre preto, não sobre o Mica);
- não tirava o fotograma errado, só lhe mudava a cor: fora do vidro, o que a static pinta por GDI aparece com a cor
  do pincel de controlo do sistema (cinzento claro), pior do que o Mica no tema escuro.

A única janela que pinta por GDI na área da página é a «static» que o CefSharp cria para alojar a do CEF: a
`CefBrowserWindow` não tem pincel nem apaga o fundo, e o `FillRect` do `Chrome_WidgetWin` só cobre o que fica fora
da janela de apresentação do GPU (que o cobre inteiro). A static vê-se sempre que a janela do CEF ainda não a cobre:
aba nova antes de o CEF criar a sua janela e aba que volta de escondida (o CefSharp encolhe a do CEF a 0×0 ao
esconder). Agora as abas usam `Browser/PageBrowser.cs` (o `ChromiumWebBrowser` com a static sem desenho:
`WM_ERASEBKGND` responde «apagado» e `WM_PAINT` só valida). Na área de vidro o GDI escrevia alfa 0 e via-se o Mica;
sem pintura fica o que o WPF desenhou por baixo (P2), ou seja, a cor de fundo da página, com Mica e sem Mica.

**T4 — Aba nova por baixo e já no tamanho final (rec. 5).** A espera pela 1.ª pintura já vinha do T1 (a página à
vista fica por cima até a aba nova acabar o 1.º documento e apresentar: rAF duplo + 34 ms). O teto continua a ser
1 s e não os ~120 ms do estudo: com 300 ms a Wikipédia ainda dava 48 fotogramas errados em 10 capturas (T1); os
~120 ms são o tempo que se quer ver medido, não o limite. Faltavam duas coisas, agora em `Browser/PageBrowser.cs`
(chamadas pelo CefSharp dentro de `BuildWindowCore`, antes de pedir a janela ao CEF):
- a static nasce **por baixo** das outras páginas (uma janela nova fica por cima das irmãs e taparia a página à
  vista assim que aparecesse; até agora isso dependia de o WPF já ter criado a static quando a troca corria);
- a janela do CEF nasce **com o tamanho da área das páginas** (`PageWindow.BirthSize`): o CefSharp usava o tamanho
  da static nesse momento, 0×0 numa aba acabada de criar, e o Chromium começava a página a 0×0 e voltava a dispô-la
  logo a seguir. O tamanho em píxeis segue a mesma conta do CefSharp (`PageWindow.PixelSize`), para que o
  redimensionamento que ele faz no fim da criação não mude nada.

Não feito: um browser «sobressalente» já com a nova aba carregada (o estudo dá-o como opcional). Custa um processo de
página sempre vivo; fica para decisão do dono depois de medir o T4.

**T5 — Aba fria restaurada num só passo (rec. 3).** Uma aba escondida há mais do que o período de graça (T6) está
minimizada e a sua janela do CEF encolhida a 0×0. Ao voltar, a janela do Chromium era restaurada com `ShowWindow`
(que repõe o último tamanho «normal», às vezes o 0×0) e corrigida a seguir com `SetWindowPos`: duas mudanças de
tamanho com a página ainda «hidden», e cada uma avança a superfície do Chromium (página em branco por desenho,
estudo §2.5). Agora, ainda com a static escondida e por baixo da página à vista, a janela do Chromium é restaurada
com `SetWindowPlacement` (`rcNormalPosition` = área da página, `SW_SHOWNOACTIVATE`) já no tamanho que o CefSharp lhe
vai dar (`PageWindow.PixelSize`, depois de dispor a grelha se o ecrã dividido acabou de mudar): uma só mudança, e o
redimensionamento do CefSharp ao ficar visível não muda nada. Com o tamanho por saber, fica o caminho antigo
(`PageWindow.RestoreStep`). Ao esconder, a ordem inverte-se: minimizar primeiro e só depois esconder no WPF (que faz o
CefSharp encolher a janela do CEF a 0×0) — antes, o Chromium ainda dispunha e apresentava a página a 0×0 enquanto
«visible». Não evita a nova rasterização (o Chromium larga os tiles de uma página «hidden»), só tira a mudança de
tamanho a mais e encurta o tempo até ao 1.º fotograma.

**T6 — Abas «quentes» (rec. 2).** A página que sai deixa de ser minimizada logo: fica no tamanho inteiro, não
minimizada, tapada pela da aba ativa, durante um período de graça (`WarmTabs.Grace` = 8 s; no máximo
`WarmTabs.Limit` = 2 páginas, as deixadas há menos tempo). Voltar a uma delas é só subir a sua janela: o DWM já tem
o conteúdo dela e o Chromium nunca a deu por escondida, por isso não há disposição, rasterização nem espera (é o
«saved frame» do Chrome, caso A do estudo §1.3, que o CEF em janela não consegue ter com a página «hidden»). A troca
de abas trata-a como uma página que já está à vista (`PageSwap.Plan`: `Keep` enquanto tapada, `Show` sem segurar a
que sai quando volta). Passada a graça, a página é minimizada como antes (tapada, não se vê).

Quem está por cima de quem passa a ser explícito (`StackPages`, `PageSwap.StackOrder`): à vista → a sair (por cima da
que entra até esta apresentar) → a entrar fria → quentes (a mais recente primeiro), num só `DeferWindowPos` e só se a
ordem atual for outra. Antes bastava pôr a que entra em `HWND_BOTTOM`; com páginas quentes visíveis por baixo, a que
entra ficaria tapada por elas.

Nunca fica quente (`MainWindow.CanStayWarm` + `WarmTabs.Keep`): no ecrã dividido (as metades não tapam a área toda),
com a janela minimizada ou escondida, sem uma página à vista por cima, numa página que entrou fria e ainda não
apresentou um fotograma desde essa chegada (`PagePresentation`: Ctrl+Tab de ida e volta antes de a página
apresentar, ou Ctrl+T seguido de duas trocas; voltaria sem espera, com a superfície em branco ou antiga), numa aba com som (`IsAudible` ou multimédia a
tocar; arrefece no momento em que começa), numa aba a passar para o motor de vídeo, numa aba fechada a sair, e numa
página com `<video>` a tocar (verificação ao sair, `WarmTabs.PlayingVideoScript`: documento, raízes de sombra abertas
e iframes do mesmo site, até 250 ms; sem resposta conta como «a tocar»). Limite conhecido: um vídeo **sem som** num
iframe de outro site não se deixa ver; essa página pode ficar quente até 8 s. Outro: se a página que entra fria não
apresentar dentro do teto de 1 s do T1, por baixo dela pode ver-se uma página quente em vez da cor de fundo.

**Custo de CPU esperado.** Uma página quente desenha como se estivesse à vista: numa página com animação a 60 fps,
~9–11 % de 1 núcleo (`docs/paridade.md` #1: 11,1 % à vista; #1b: 10,9 → 9,7 % medido no T1), numa página parada
quase nada. No pior caso, 2 páginas animadas durante 8 s depois de cada troca: ~20 % de 1 núcleo nesses 8 s. Depois
da graça o custo de uma aba escondida volta ao de #1b (2,2 % de 1 núcleo medido no T1 para uma aba animada
escondida): a exceção a «abas escondidas não gastam» é só o período de graça.

**A medir ao vivo (T3–T6), quando o PC estiver livre.** Mesmo método do T1 (`scratchpad/abas/abasrun.ps1` +
`swap.py`, 60 fps, 10 capturas por caso, tema escuro + Mica, depois claro e sem Mica); critério: **0 fotogramas
errados** e tempo da tecla até à página final **≤ ~120 ms** (o Brave: 0 errados, ~90–100 ms).
1. T3: Ctrl+T ×5 a partir de uma página escura (`mica.py`): 0 fotogramas de Mica ou cinzento na área da página.
2. T4: Ctrl+T e botão + (antes 205 e 369 ms de mediana); confirmar no registo que a janela do CEF da aba nova não
   muda de tamanho depois de criada (também a 125 % e 150 % de escala).
3. T5: Ctrl+Tab para uma aba fria (deixada há > 10 s), clara↔escura e Wikipédia↔Wikipédia (antes 133 e 270 ms).
4. T6: Ctrl+Tab ida e volta em < 8 s: 0 errados e a página certa no 1.º fotograma depois da troca; rodízio de 3 abas
   (só 2 quentes); página local com `<video muted autoplay loop>` deixada → `visibilityState` «hidden» logo
   (`vis.html`), como antes; aba com som, ecrã dividido e janela minimizada → nenhuma quente.
5. CPU (#1b, 10 s, todos os processos): aba animada deixada — durante a graça ~ como à vista (~10 % de 1 núcleo); a
   partir dos 8 s, **igual a antes** (2,2 % de 1 núcleo no T1).
6. Sem regressões: fechar a aba ativa (vizinha quente e vizinha fria), troca para o motor de vídeo (V3), 8 Ctrl+Tab
   seguidos sem clicar (T2: 8/8), painel web da barra lateral com o tamanho certo, minimizar/restaurar a janela com
   uma aba quente.

## Diário do motor

| Passo | Alteração | Antes → depois |
|---|---|---|
| — | Interruptor `ETERIS_TEST_MUTE=1` para os testes ao vivo (silêncio nos dois motores) | — (sem efeito para o utilizador) |
| P1 | «Piscar» depois de carregar: a miniatura da aba deixa de pedir ao Chromium um `clip` com escala (com clip, `PageHandler::CaptureScreenshot` aplica emulação de dispositivo à página À VISTA — vista 1×1 e depois escala 0,235 — e o HwndHost mostrava esses fotogramas ~1,2 s depois de cada carregamento). Captura sem clip e reduz no .NET (`Thumbnails.Decode`, fora da thread da janela). Regra nova: `CaptureRulesTests` proíbe `CaptureScreenshotAsync` com clip | Tema escuro, 6 sites, mesmo build (A/B por interruptor): página perturbada 83/85/117/100/100/82 ms (6/6, diferença máx. até 189) → **0 ms em 6/6** (diferença máx. 0,0). Tema claro: 82–149 ms (diagnóstico) → **0 ms em 6/6** (máx. 2,1, animação do site). Captura: 66–136 ms (mediana 81) → 44–103 ms (mediana 48) |
| P2 | Fundo opaco por baixo da página: a janela principal deixa de recortar as janelas filhas (`WS_CLIPCHILDREN`), por isso o fundo do WPF (que já existia, mas ficava recortado) passa a estar na superfície por baixo da janela do Chromium; antes, com o Mica, o que lá estava era transparente. A cor segue a página (`Theme/PageBackdrop.cs`): branco nas páginas web (como o Chrome), `#0B0D13`/`#F3F4F9` nas páginas internas conforme o tema, `#1A1A1A` num site escurecido. `CefSettings.BackgroundColor` branco foi medido e **não** muda nada (o Chromium já usa branco): não entra | Fotograma vazio forçado (janela de apresentação do Chromium escondida por instantes): via-se o Mica RGB (40,43,31) em toda a área → branco (web) / `#0B0D13` (interna). Navegações no mesmo site (Wikipédia): 1 fotograma Mica em 40 (17 ms, 77% da área) → 0 em 40 (A/B) e 0 em 20 (build final). Aba nova a partir de página escura: flash branco de 1 fotograma 3/3 → 0/3; fotogramas Mica ao abrir 5 abas novas: 22 (até ~100 ms) → 4–5 (1–3 fotogramas em 2–3 aberturas; resto por explicar) |
| F0 | Aba duplicada ao arrancar com um endereço: a janela dona da sessão abria-o pelo plano de arranque e outra vez no ramo do ecrã dividido (`StartupPages.OpensInitialUrlSeparately`). Pré-requisito das medições: as linhas de base anteriores tinham duas páginas a carregar ao mesmo tempo | `run-test.ps1 -Url https://pt.wikipedia.org/wiki/Brasil`: 2 abas iguais na sessão → **1 aba** |
| F1 | Falha que fechava o Éteris inteiro: `frame.Url`/`browser.MainFrame.Url` num frame já destacado lançava uma exceção na thread IO do CEF. Leituras seguras (`Browser/SafeFrame.cs`, também no pop-up, no ícone e no menu da página), `PrivacyResourceHandler.Decide` nunca lança (segue, ou cancela se já tinha decidido bloquear) e pedidos que não são da rede (eteris://, data:, blob:…) não passam pelas listas | Registo de eventos «.NET Runtime» com `CefFrameWrapper.get_Url`: 4 falhas em ~25 min de roteiro (13:14–13:53, incluindo um build sem alterações) → **0** em todas as execuções de medição deste diário (ver D1) |
| F2 | Tirada a espera de ~1 s por navegação. Causa: o pedido da página esperava pelo registo dos scriptlets (até 1 s), mas depois de `DidStartNavigation` o Chromium suspende as mensagens DevTools para o renderer até ao fim da navegação — a resposta nunca chegava a tempo e o script ficava registado tarde. Agora o pedido segue logo; o `ScriptletInjector` regista o script base uma vez por separador e os scripts de cada site (scriptlets, cosmética precoce, modo escuro) protegidos pelo hostname da página de topo (`HostScriptRegistry`), acumulados (até 24 sites) e registados com a aba parada: antes das navegações pedidas pelo Éteris (omnibox, recarregar, ≤ 150 ms), depois de cada commit e ao passar o rato num link. Os registos sobrevivem às navegações entre processos (medido). Limite medido: o 1.º documento de cada aba nova nasce sem eles (antes da 1.ª navegação não há renderer e o DevTools só responde depois do commit; esperar 300 ms não adiantava), como antes | 11 navegações (wiki, GitHub, BBC, g1, Amazon ×2): espera do pedido principal 1000–1010 ms em 9/11 → **0**; TTFB mediano 1059 → **120 ms**; 1.ª pintura desde o clique 1249 → **291 ms** (o «puro» mediu 319); CPU do processo principal 5453 → 3516 ms; script base presente: também no YouTube servido pelo service worker (antes: nenhum script); cosmética precoce já na 1.ª visita ao GitHub (0 → 1 estilo); nova aba 128 → 103 ms |
| F3 | Back/forward cache ligado: o CefSharp.Wpf põe `--disable-back-forward-cache` por omissão (por causa do modo fora da tela, que o Éteris não usa). Numa página reposta não há pedido nem fim de carregamento: a aba guarda a contagem do Escudo e o ícone das últimas 8 páginas deixadas (`BrowserTab.LeavePage`/`ReturnToPage`) e repõe-nos; cor do site, modo de leitura e miniatura atualizam-se no commit (`OnPageRestored`) | Voltar ×3 (wiki→wiki, BBC→wiki, GitHub→wiki): 1280/1335/1342 ms (base) e 205/301/336 ms (depois do F2) → **6/6/6 ms**, sem pedidos; BBC: 15 bloqueados antes de sair → 15 ao voltar; endereço e título certos |
| F4 | Barra de carregamento («aurora») fora da janela em camadas: passa a viver na janela principal, no fundo das barras, logo acima da página (fora dela); só com as barras todas escondidas volta à camada, no topo da página. A animação corre a 30 fps e para com a janela minimizada. A camada deixa de aparecer durante cada carregamento | A/B no mesmo build, 11 navegações ×2: CPU do processo principal 4077/3642 ms (na camada) → **3422/3032 ms** (−16%); latência máxima mediana da thread da janela por navegação 46/16 → **1/1 ms**; TTFB e 1.ª pintura sem regressão. Captura: barra visível e alinhada com a página |
| F5 | Histórico fora da thread da janela: `eteris.db` em registo antecipado (WAL, `synchronous=NORMAL`; depois de apagar histórico faz-se logo o checkpoint, para nada apagado ficar no `-wal`); visitas, títulos e ícones vão para uma fila com uma só tarefa (`Data/HistoryWriter.cs`), pela ordem; os títulos juntam-se por endereço (só o último de cada segundo é escrito); quem lê ou apaga despeja a fila primeiro; ao sair, escreve o que falta. Janela privada: nada escrito, como antes | Thread da janela por navegação (2–3 escritas): 3,0–3,6 ms (mediana) → **≤ 0,1 ms**; título que muda a cada segundo: 1 escrita com fsync por mudança → 1 escrita por segundo em segundo plano (teste); histórico certo depois de fechar e voltar a abrir |
| F6 | Investigado, sem alteração de código: com `--autoplay-policy=document-user-activation-required` o Chromium 156 do Éteris **deixa** tocar vídeo sem som sozinho (`AutoplayPolicy`: vídeo `muted` dispensa o gesto) e bloqueia o som, como o Chrome com a mesma política. Os YouTube Shorts ficam parados porque o leitor do YouTube pede reprodução **com** som e, recusada, não passa a sem som; no Chrome toca porque o índice de envolvimento com média (MEI, lista pré-carregada pelo component updater, que o Éteris desliga por privacidade) autoriza som no YouTube. Com «bloquear reprodução automática» ligado, os Shorts precisam de um clique — decisão do dono | Página local de teste (`scratchpad/autoplay/teste.html`): vídeo `muted autoplay` a tocar sem gesto (t=10,1 s) — **sim**; `play()` sem som sem gesto: ok; com som: parado / `NotAllowedError`. YouTube Shorts: 1.º fotograma sem clique — **não** (botão de play) |
| V1 | Deteção de «vídeo que não toca» reescrita (`Tabs/VideoEngine.cs`, WatchScript) com os sinais medidos no diagnóstico: erro no último `<source>` com o leitor em NETWORK_NO_SOURCE (MP4 aberto diretamente, `<video><source mp4>`); `addSourceBuffer` a lançar NotSupportedError para avc1/mp4a (logo, sem esperar); `canPlayType`/`isTypeSupported` (também `ManagedMediaSource`) com «não» para H.264/AAC + vídeo parado sem fonte ou com MediaSource vazio (`blob:`); vídeos dentro de shadow DOM (ouvinte no próprio elemento via `play`/`load`/`src` e procura recursiva); mensagens de leitor de uma lista curta, só como sinal secundário; verificações aos 3 s e 8 s (antes: uma, aos 10 s); `console.debug` guardado no arranque (o g1 substitui-o); HEVC/Dolby Vision deixam de contar (eram sondas). O relatório leva razão, força (`strong`/`weak`) e origem (topo/iframe). O 1.º documento de cada aba (nasce sem scripts precoces, F2) passa a ter a mesma deteção no fim do carregamento (`LateWatchScript`, pelo PageScript), com o vídeo parado só como sinal fraco | Medido ao vivo (profile-motor, sem som; tempos desde o pedido da página). MP4 direto (w3schools): nunca → **troca** (≤ 2,5 s desde o arranque, a tocar no motor de vídeo); `<source mp4>` local: nunca → **~0,7 s**; `addSourceBuffer` avc1 (como Vimeo/BBC): nunca → **~0,8 s**; vídeo em shadow DOM (como o Reddit): nunca → **~3,6 s**; hls.js demo: nunca → **~3,9 s**; g1 (vídeo do Copan): nunca → **~4,4 s**; BBC (vídeo): nunca → **~2,8 s**; sonda só de HEVC (como o Twitch offline): falso positivo aos 10,9 s → **nenhum aviso**; YouTube (AV1): sem aviso, como antes. X sem sessão: sem vídeo visível na página, nada a detetar |
| V2 | Política de troca (`VideoEngine.Decide`): troca automática só com sinal forte no documento de topo; sinal fraco (vídeo de fundo mudo em ciclo, fora de vista), leitor num iframe, DRM, Reddit (pede reCAPTCHA no motor de vídeo) ou site excluído pelo utilizador → botão «Tocar no motor de vídeo» na barra de endereço (também no menu da página, em qualquer site) e, nos casos relevantes, aviso curto. Lista embutida de sites só H.264 (`KnownH264Sites`: twitch.tv, kick.com, vimeo.com, dailymotion.com, tiktok.com, instagram.com) que abrem logo no motor de vídeo; o botão «Motor de vídeo» passa a gravar uma exclusão explícita (`video_sites_off`; numa janela privada só em memória), para a lista não voltar a forçar o site | Vídeo de fundo (página local como a da Amazon): trocava sozinho → **0 trocas, só o botão (~1 s)**; Reddit (post com vídeo): sem nada → **botão ~1,9 s, sem troca**. 1.ª visita: Twitch VOD troca aos 10,9–11,0 s e 1.º fotograma aos 15,0 s → **motor de vídeo a arrancar 2,6 s depois do arranque do Éteris** (vídeo a tocar na captura aos 16 s, com anúncio); Kick 10,9 s → **2,5 s**; Vimeo (Player error, sem troca) → **2,5 s**; TikTok (sem troca) → **2,5 s**; Dailymotion («Navegador não compatível») → **2,5 s** |
| V4 | `.m3u8` (HLS) e áudio AAC (`.m4a`/`.aac`) abertos diretamente: o motor principal transferia-os (aba a voltar a «Nova aba»). `DownloadHandler`: HLS pela extensão logo em `CanDownload`; o resto pelo tipo em `OnBeforeDownload` (lista de tipos de vídeo/áudio, sem `Content-Disposition: attachment`, endereço web) → cancela e abre no motor de vídeo, na mesma aba. «Guardar imagem» marca o endereço como pedido pelo utilizador (nunca desviado) | x36xhzz.m3u8: transferido → **a tocar no motor de vídeo** (motor a arrancar 2,5 s desde o arranque); sample1.m4a: transferido → **motor de vídeo, 2,5 s**; 0 itens novos em Transferências |
| V5 | Fase A: o WatchScript corre também nos iframes do mesmo processo (relatório com origem `frame`) e, no topo, um iframe visível de um leitor conhecido de outro site (player.vimeo.com, geo.dailymotion.com, players.brightcove.net, cdn.jwplayer.com, player.twitch.tv, fast.wistia.net…) sem vídeo próprio a tocar dá `frame-player` → botão, nunca troca automática. Fase B (auto-attach do DevTools aos iframes de outro site) não feita: fica para depois | Página local com iframes Vimeo e Dailymotion: 0 eventos, nada → **botão «Tocar no motor de vídeo» ~3,8 s** depois do pedido |
| V6 | DRM: `requestMediaKeySystemAccess` recusado para Widevine/PlayReady → relatório `drm` → só o botão e o aviso «Este site usa DRM (conteúdo protegido)…», nunca troca sozinho; o site só fica lembrado depois do clique. O motor principal continua sem Widevine | Página local que pede Widevine: sem aviso → **botão ~2,4 s, 0 trocas** |
| V7 | Motor de vídeo: `--disable-component-update` (componentes descarregados sem ação do utilizador no diagnóstico) e, com «bloquear reprodução automática», `--autoplay-policy=document-user-activation-required`; o bloqueador sai logo nos segmentos de vídeo/áudio (contexto Media), guarda o endereço da página na navegação em vez de ler `core.Source` a cada pedido e põe as decisões em cache; regras cosméticas do Escudo também no motor de vídeo (`Page.addScriptToEvaluateOnNewDocument` pelo DevTools do WebView2, protegidas pelo hostname, uma vez por site). `SetVideoMuted` respeita `ETERIS_TEST_MUTE`. Tentado e recusado: um filtro por contexto sem Media — o WebView2 deixava de entregar os pedidos (0 bloqueados) | A/B com a linha de base (mesmo perfil, sem som). Página local com 3 rastreadores no motor de vídeo: **1 → 3 bloqueados** (antes, durante o 1.º carregamento `core.Source` ainda era `about:blank` e os pedidos passavam). Twitch VOD a tocar: 4–5 → **9 bloqueados**. Thread da janela (WM_NULL a cada 10 ms, 30 s de Twitch a tocar): 0 respostas > 50 ms antes e depois (máx. 0,6–7,2 ms → 8,1–13,8 ms); os 221–225 bloqueios do diagnóstico eram do build de diagnóstico, não se reproduzem |
| V3 | Troca de motor sem intervalo vazio: a página do motor principal fica à vista (pausada pelo PageScript) até o WebView2 acabar de carregar o primeiro documento (recurso aos 5 s); só então o WebView2 aparece e, 200 ms depois, o motor principal sai. O WebView2 nasce com fundo branco opaco (antes transparente: via-se o Mica). Posição: o maior vídeo que já tocava retoma no mesmo segundo (`#t=` num ficheiro direto, script de uso único pelo DevTools nos outros; não medido ao vivo). Mostrar logo no `DOMContentLoaded` foi medido e recusado (220–250 ms de branco e, às vezes, um fotograma preto). Cópia de cookies para o motor de vídeo **não** feita: depende de decisão do dono | Captura a 60 fps da página durante a troca automática (página local com `<video src=mp4>`, A/B com a linha de base): antes, 4–5 mudanças de quadro, **3–4 fotogramas escuros** (Mica, luminância 40, 50–67 ms) e **~570 ms de página em branco**; depois, **2 mudanças** (página → vídeo a tocar), **0 fotogramas escuros, 0 ms em branco** (5/5 execuções) |
| V2b | Botões do motor de vídeo com nome acessível (`AutomationProperties.Name`: «Tocar no motor de vídeo», «Motor de vídeo: voltar ao motor principal»); a omnibox leva um site do motor de vídeo (lembrado ou da lista) logo para lá, sem o carregar também no motor principal | Ao vivo, por UI Automation: no Reddit, clique em «Tocar no motor de vídeo» → motor de vídeo a arrancar **0,2 s** depois (o Reddit responde ao WebView2 com «blocked by network security»: por isso fica só com o botão); Twitch: «Motor de vídeo» → volta ao motor principal e **2 visitas seguidas** abrem no motor principal (exclusão gravada; a lista embutida não a força); link de uma página para a Twitch VOD / Kick: motor de vídeo **~0,7 s** depois do clique (antes: ~11 s de deteção). Limite: num site excluído, o 1.º documento da aba nem sempre mostra o botão (o leitor da Twitch não deixa sinais depois do carregamento); o menu da página tem sempre «Tocar no motor de vídeo» |
| C1 | Motor com codecs da plataforma preparado no repositório, sem compilar (decisão do dono): alvos MSBuild condicionais que trocam o runtime completo da CEF por `build/cef-codecs/out/runtime`, `publish.ps1` a recusar tudo o que não seja o modo `platform-codecs` do commit `cf671c0` (com SHA256 por ficheiro), `collect.py` com os créditos do runtime que vai de facto, `.gitignore`, testes; secção «Motor com codecs» acima | Sem a pasta: saída igual (nada copiado). Com um runtime falso: `libcef.dll`, `locales\` e avisos (`licenses\cef-codecs`) substituídos, `manifest.json` fora; `publish.ps1`: modo `chrome` → recusado; versão errada → recusada |
| D1 | Fecho: suite completa (1374 testes), medição final comparada com a base e o «puro», `CHANGELOG.md` (secção por publicar, sem versão nem tag), `docs/paridade.md` (secção 1b) | Tabela «Resultado» no topo deste ficheiro |
| T1 | Troca de abas, aba nova e fecho da aba ativa sem fotograma vazio («dupla memória», `Browser/PageSwap.cs`): a página que entra aparece por baixo da que sai; a que sai só se esconde depois de a nova apresentar (rAF duplo pelo DevTools + 34 ms; uma aba nova espera primeiro pelo fim do 1.º documento; limite 1 s); a página de uma aba fechada que estava à vista fica, calada, até a vizinha aparecer | Fotogramas errados em 10 capturas (tema escuro + Mica): Ctrl+Tab clara↔escura 14 → **0**, Wikipédia 204 → 4, Ctrl+T 96 → **0**, botão + 97 → **0**, fechar a ativa 24 → 1; tema claro e sem Mica: todos → 0–1 (secção «Troca de abas sem piscar») |
| T2 | Foco do teclado na aba que entra: depois de trocar de aba, o foco do Windows ficava na janela (minimizada) da aba anterior e as teclas iam para uma página invisível; a página da aba ativa passa a recebê-lo (como no Chrome) quando o foco estava numa página que deixou de se ver | 8 Ctrl+Tab seguidos sem clicar: **2 → 8** trocas efetivas |
| T3 | Mica por baixo da página (rec. 4 do estudo): a static do CefSharp que aloja a janela do CEF deixa de pintar por GDI (`Browser/PageBrowser.cs`, `PageWindow.PaintStep`); na área de vidro o GDI escrevia alfa 0 e o DWM mostrava o Mica onde devia estar a cor de fundo da página. Vidro limitado às barras avaliado e recusado (secção «Troca de abas: o resto do estudo») | Medição ao vivo pendente (dono a usar o PC). Testes: `PageWindowTests` |
| T4 | Aba nova (rec. 5): a static nasce por baixo das outras páginas e a janela do CEF nasce já com o tamanho da área das páginas (antes 0×0 numa aba nova, redimensionada a seguir); mesma conta de píxeis do CefSharp (`PageWindow.PixelSize`), por isso o redimensionamento dele no fim da criação não muda nada. A espera pela 1.ª pintura continua a do T1 (teto 1 s) | Medição ao vivo pendente (dono a usar o PC). Testes: `PageWindowTests` |
| T5 | Aba fria (rec. 3): a janela do Chromium volta de minimizada num só passo e já no tamanho final (`SetWindowPlacement` com a área da página e `SW_SHOWNOACTIVATE`, antes de a static aparecer), em vez de `ShowWindow` + `SetWindowPos` (duas mudanças de tamanho com a página «hidden»); ao esconder, minimiza antes de o CefSharp encolher a janela a 0×0. Também nos painéis web da barra lateral | Medição ao vivo pendente (dono a usar o PC). Testes: `PageWindowTests` |
| T6 | Abas quentes (rec. 2): a página que sai fica tapada, no tamanho inteiro e sem minimizar, durante 8 s (no máximo 2, as mais recentes; `Tabs/WarmTabs.cs`), e voltar a ela é só subir a janela; nunca com vídeo a tocar, som, ecrã dividido, janela minimizada ou a passar para o motor de vídeo. Ordem das janelas das páginas explícita (`PageSwap.StackOrder`: à vista → a sair → a entrar fria → quentes). Custo: ~9–11 % de 1 núcleo por página animada quente, só durante a graça | Medição ao vivo pendente (dono a usar o PC). Testes: `WarmTabsTests`, `PageSwapTests` |
| T3–T6 | Lista do que falta medir ao vivo para T3–T6 (casos, critério de 0 fotogramas errados e ≤ ~120 ms, CPU de aba animada escondida depois da graça igual à do T1), na secção «Troca de abas: o resto do estudo» | Medição ao vivo pendente (dono a usar o PC) |
| T6b | Correções da revisão das abas quentes: (1) uma página que entrou fria e ainda não apresentou já não fica quente (`PagePresentation`: cada chegada fria tem um número e só o rAF duplo dessa chegada, depois do 1.º carregamento, a dá por apresentada; também nas chegadas sem página a segurar, e mais tarde, até 8 s, numa página que passe o teto de 1 s) — antes, Ctrl+Tab de ida e volta antes de a página apresentar, ou Ctrl+T e duas trocas, voltava a ela sem espera e mostrava branco ou conteúdo antigo; (7) o resultado da verificação de vídeo só se aplica à mesma saída da página; (8) a graça mede-se com um relógio monótono (`Environment.TickCount64`), não com a hora do sistema | Medição ao vivo pendente (dono a usar o PC). Testes: `PageSwapTests` (`Presentation_*`), `WarmTabsTests` |
