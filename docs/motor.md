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
- `ETERIS_TEST_SYNC_FOLDER=<pasta>`: a sincronização usa esta pasta em vez do Google Drive (nenhum pedido de rede;
  dois perfis de teste na mesma pasta). Ignorada pela versão instalada. Ver `docs/sincronizacao.md`.
- `ETERIS_TEST_REAUTH=aceitar|recusar`: a confirmação com o Windows do cofre de palavras-passe (Windows Hello ou
  palavra-passe do Windows) responde sozinha, sem janela, para as provas ao vivo não precisarem do PIN de ninguém.
  Ignorada pela versão instalada. Ver `docs/palavras-passe.md`.

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

## Motor com codecs (compilado e integrado)

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
- `build/licenses/collect.py`: com a compilação própria, `CEF-LICENSE.txt` e `Chromium-credits.html` são o
  `LICENSE.txt` e o `CREDITS.html` dela; juntam-se `FFmpeg-LICENSE.txt` (LGPL-2.1 completa, tirada dos créditos) e
  `FFmpeg-SOURCE.txt` (commits da CEF, do Chromium e do FFmpeg do manifesto, patches com SHA256, argumentos GN e a
  oferta escrita por três anos); o índice diz «Chromium Embedded Framework 156.0.15 (compilação própria, codecs do
  Windows)». Sem a compilação, sai igual ao de antes. `THIRD-PARTY-NOTICES.md`: FFmpeg na tabela e a nota «Codecs da
  plataforma».
- `.gitignore`: `build/cef-codecs/out/`. `.gitattributes`: o `build.ps1` e os patches sem conversão de fins de linha
  (o SHA256 dos patches está no manifesto).
- `CefCodecsRuntimeTests`: o alvo nunca pode ficar incondicional; o `bootstrap.exe`/`bootstrapc.exe` (sandbox, passo D,
  ainda não usado) fica fora da saída; o `manifest.json` vai para `licenses\cef-codecs` (é a marca da compilação
  própria na instalação).

**Fontes (no repositório desde C2):** `build/cef-codecs/build.ps1` (passos Check, Patch, Generate, Smoke, Build,
Package; ASCII), `README.md` (como reconstruir), `patches/platform-codecs.patch` (10 ficheiros de `media/`) e
`patches/ffmpeg-adts-demuxer.patch` (desmultiplexador ADTS e parser AAC na configuração Chromium do FFmpeg, sem
descodificadores). O runtime (238 ficheiros, `manifest.json`, `licenses\`) fica em `build/cef-codecs/out/runtime`, fora
do git; a cópia de trabalho durável está em `C:\cef\eteris-cef-codecs`.

**Escolha do motor no arranque (`Tabs/PlatformCodecs.cs`).** Sem `licenses\cef-codecs\manifest.json` na instalação
(runtime do NuGet) o motor principal não tem codecs e tudo fica como antes (V1–V8). Com ele, uma sonda **uma vez por
sessão**, na 1.ª página interna que acaba de carregar (pelo PageScript: nas páginas `eteris:` não há WatchScript nem
site a ver a pergunta), pergunta `MediaSource.isTypeSupported` para `avc1.640028` **e** `mp4a.40.2`:
- «sim» aos dois (ou ainda sem resposta): a lista `KnownH264Sites` deixa de abrir sites no motor de vídeo; os sites
  lembrados passam a uma lista própria (`video_sites_codecs`: os lembrados só por faltarem os codecs não contam); os
  avisos do WatchScript só contam se o motor respondeu «não» — cada aviso leva no fim `no` quando a página ouviu «não»
  a H.264/AAC ou quando o próprio script, com o `isTypeSupported` original guardado no arranque, ouve «não»
  (`VideoReport.EngineSaidNo`). Um MP4 estragado, um leitor parado ou o leitor do Vimeo num iframe já não mandam para
  o WebView2. O DRM continua a ser só uma pergunta;
- «não» a algum (sem descodificação por hardware, controlador básico, máquina virtual; Windows N sem o Media Feature
  Pack): tudo como no motor sem codecs. Sinal novo para o caso medido sem GPU: o FFmpeg descarta em silêncio a faixa
  H.264 de um MP4 progressivo e só se ouve o som — um `<video>` de MP4/MOV/M3U8 com metadados, `videoWidth` 0 e o motor
  a dizer «não» ao `avc1` dá `format`, e a aba troca.

### Como publicar

**Publicação (decidido na 0.2.9: localmente, nesta máquina).** O `.github/workflows/release.yml` **não** consegue
compilar este motor: são 110–150 GB de fonte e objetos, 5–10 h de compilação oficial (ThinLTO + PGO) e o código
descarregado à parte; os runners alojados do GitHub têm ~14 GB de disco e 6 h por trabalho. Uma publicação pelo CI sai
com o runtime do NuGet (sem codecs) e os utilizadores atualizados pelo Velopack voltariam a um motor sem H.264/AAC.
Por isso as releases normais publicam-se **localmente**, com o runtime próprio em `build/cef-codecs/out/runtime`:
1. Subir a versão (csproj, padrão do `build/publish.ps1`, `CHANGELOG.md`, `Browser/WhatsNewPage.cs`) e fazer commit.
   **Desde a 0.2.14 a publicação sai com o sandbox** (`docs/sandbox.md`, «Como publicar com o sandbox»): antes da 1.ª
   publicação, e de novo quando mudar o motor, o CefSharp, o patch ou `build/sandbox/client`, correr
   `powershell -ExecutionPolicy Bypass -File build\sandbox\build-all.ps1` — recompila o CefSharp com o patch v2 para o
   feed local `build/cefsharp/feed` e a DLL cliente para `build/sandbox/client/out`, verifica os dois e corre os testes
   no modo com sandbox (pastas ignoradas pelo git; o feed e a DLL ficam para as publicações seguintes). Precisa da
   distribuição mínima do motor em `build/cef-codecs/out/distrib` (se foi apagada:
   `build/cef-codecs/build.ps1 -Steps Package`, sem recompilar) ou de `-CefDistrib <pasta>`. **Em cada release**, depois
   do commit da versão: `build\sandbox\build-all.ps1 -SkipCefSharp -SkipClient` — só os testes no modo com sandbox (o CI
   só testa o modo sem), que gravam a marca `build/sandbox/out/tests-ok.json` para esse commit; sem ela o passo 2 recusa.
2. `.\build\publish.ps1 -Version X.Y.Z -GoogleOAuthJson <caminho do google-oauth.json>` — o `-GoogleOAuthJson` copia
   o identificador da aplicação Google («App para computador», descarregado da Google Cloud; nunca no repositório, está
   no `.gitignore`) para `publish\google-oauth.json`, depois de verificar (antes de compilar) que é um cliente
   `installed` com `client_id` e endereços da Google; sem ele publica **sem login** (a secção Sincronização diz
   «indisponível nesta compilação») e avisa — e o passo 4 só empacota essa publicação com `-SemLogin`. Recusa um
   runtime que não seja o validado (modo, commit, versão do `libcef.dll`, SHA256 de cada ficheiro do manifesto);
   com o sandbox (a omissão) recusa também a falta do feed ou da DLL cliente,
   verifica a DLL contra o motor (`verify-client.ps1`) e confirma a forma publicada (`Eteris.Browser.exe` =
   bootstrap com a marca e a versão, `Eteris.Browser.dll` = DLL cliente, `Eteris.App.dll`, CefSharp com
   `WindowsSandboxInfo` e o SHA256 do feed, sem `CefSharp.BrowserSubprocess.exe`); recusa também sem a marca dos testes
   para o HEAD atual ou com alterações por gravar. A pasta `publish` é limpa antes, apagada se alguma verificação
   falhar, e no fim leva `eteris-build.json` (versão, modo, commit, o CefSharp com o SHA256 das montagens e
   `google_oauth`, se levou o login Google, nunca o `client_id`).
   Nos dois modos recusa também binários publicados com o caminho do perfil de quem compila (`docs/sandbox.md`,
   «Como publicar com o sandbox», passo 3).
   `-NoSandbox` só em emergência (o Éteris corre com `--no-sandbox`, como até à 0.2.13). Confirmar que `publish\libcef.dll` tem o SHA256
   do `manifest.json`, que não há `bootstrap.exe`/`bootstrapc.exe` com esse nome (o bootstrap vai como
   `Eteris.Browser.exe`) e as verificações que o CI fazia (`eteris_shields.dll`, ≥ 10 listas em `Shields\lists`,
   `Shields\resources\scriptlets.json`, `licenses\FFmpeg-LICENSE.txt` e `FFmpeg-SOURCE.txt`).
3. `vpk download github --repoUrl https://github.com/mrtjr/eteris --token … --outputDir Releases` — o pacote delta tem
   de partir do pacote `full` **publicado**: a pasta `Releases` não pode ter um `full` local da mesma versão que não
   seja o do GitHub (na 0.2.9 o `0.2.8-full` local era outro; os pacotes locais antigos foram guardados à parte).
4. `.\build\pack.ps1 -Version X.Y.Z` → `full`, `delta`, `Setup`, `Portable`, `releases.win.json` e `RELEASES`. Só
   empacota uma `publish\` com o `eteris-build.json` dessa versão e com sandbox (uma publicação `-NoSandbox` só com
   `-NoSandbox` também aqui). **Recusa uma publicação sem login Google** (desde a 0.2.14): se a marca disser
   `google_oauth` = false, pára antes de empacotar e diz para publicar de novo com
   `publish.ps1 -Version X.Y.Z -GoogleOAuthJson <caminho>` (passo 2) — uma release oficial sem ele deixaria o
   «Entrar com o Google» «indisponível nesta compilação» para toda a gente. `-SemLogin` empacota assim mesmo, com um
   aviso (só para a publicação de emergência pelo CI ou um pacote de teste); numa publicação que leva o login, o
   `-SemLogin` é recusado, como o `-NoSandbox` numa publicação com sandbox. O `pack.ps1` só vê se o ficheiro existe,
   nunca o lê. **A marca e o ficheiro têm de bater**: se a marca disser que a publicação leva o login e o
   `publish\google-oauth.json` não estiver lá, ou se ele lá estiver sem a marca o dizer (a pasta foi mexida depois de
   publicar), recusa sempre, com ou sem `-SemLogin`, e manda publicar de novo — um ficheiro posto à mão, que o
   `publish.ps1` não verificou, sairia num pacote anunciado como «sem login». **E a publicação tem de ser a do commit
   que se empacota**: a marca guarda o commit de onde a `publish\` saiu e o `pack.ps1` compara-o com o HEAD
   (`git rev-parse HEAD`); se forem diferentes — uma pasta que ficou de uma medição, ou um commit feito depois de
   publicar —, ou se não conseguir ler o HEAD, recusa e manda publicar de novo. Por isso o passo 2 faz-se depois do
   último commit (o da versão), e nada se grava entre publicar e empacotar.
5. Testar o executável de `publish\` num perfil de teste (abre, navega, `eteris://whatsnew/` com a versão nova, um MP4
   H.264 toca no motor principal, sem processos `msedgewebview2` filhos; `chrome://sandbox` com os renderers
   Lockdown/Untrusted e a GPU Low, e `build/sandbox/sandbox-check.ps1 -ProcessId <pid>` sem filhos com `--no-sandbox`;
   fora de uma instalação a pasta não tem a ACL do serviço de rede, por isso aí ele aparece «Not Sandboxed» — normal,
   os ganchos do Velopack dão-na ao instalar e ao atualizar; com `-GoogleOAuthJson`, Definições › Sincronização mostra
   «Entrar com o Google» e não «indisponível»). Nunca instalar o `Setup` na máquina do dono.
6. `git push`, `git tag vX.Y.Z` e `git push origin vX.Y.Z` — o push da tag só põe o `release.yml` a **verificar**
   (compilar e testes, sem pack nem upload).
7. `vpk upload github --repoUrl https://github.com/mrtjr/eteris --releaseName "Éteris vX.Y.Z" --tag vX.Y.Z
   --token … --outputDir Releases`, sem publicar: a release **fica em rascunho**. O token só no ambiente do processo
   (`$env:GH_TOKEN = (gh auth token)`), nunca em ficheiros.
8. `.\build\release.ps1 -Version X.Y.Z -Key <chave.pem>` (docs/atualizacoes.md): recusa enquanto o CI do commit da
   etiqueta não estiver verde (o fluxo `build` incluído), confere o `releases.win.json` do rascunho (o que o
   `vpk upload github` gerou, só com os pacotes desta versão) com os pacotes de `Releases\` desta máquina, assina os bytes
   dele tal como vieram, sobe o `.sig`, volta a descarregar e verificar os dois e só então publica. Depois, as notas da release
   (`gh release edit`), com a nota do motor próprio e da oferta do código-fonte do FFmpeg
   (`licenses/FFmpeg-SOURCE.txt`).

A publicação pelo CI ficou só para **emergências** (esta máquina indisponível): `workflow_dispatch` do `release.yml`
com a tag e o input obrigatório `sem_codecs` = true. Também fica em rascunho; publica-se com
`build\release.ps1 -Version X.Y.Z -Key <chave.pem> -IndiceDaRelease`, que assina o índice que o CI lá pôs (o CI nunca
tem a chave). Sai com o runtime do NuGet, sem codecs e sem sandbox
(`publish.ps1 -NoSandbox`: o CI não tem o motor próprio, que traz o bootstrap, nem o CefSharp com o patch) e sem login
Google (`pack.ps1 -NoSandbox -SemLogin`: o `google-oauth.json` nunca está no repositório): a sonda do arranque
(`Tabs/PlatformCodecs.cs`) não encontra o manifesto e o H.264/AAC volta a ir para o motor de vídeo (WebView2), como
antes da 0.2.9. Alternativas estudadas e não seguidas: guardar o runtime (~370 MB, 238 ficheiros) num armazenamento do
dono e o CI descarregá-lo e verificá-lo pelo SHA256 do manifesto; um runner próprio com o checkout em `C:\cef`.

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
(V2) só vale quando a sonda diz «não» (ver «Escolha do motor no arranque»).

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

## Primeira aba nova da sessão (medição ao vivo do main)

A verificação ao vivo do main deu 4–6 fotogramas brancos (~70–100 ms, 95–100 % da área) na **1.ª** aba nova de cada
sessão (Ctrl+T, botão +, 1.ª vez que se mostra `eteris://newtab`), em 6/6 sessões; 0 no build do T1. Mesmo método
(`abasrun2.ps1` + `swap.py`, 60 fps), 1.ª aba nova de sessões novas. Três causas, por A/B:
1. **A sonda de codecs** (`PlatformCodecs`) corria no fim do carregamento da 1.ª página interna — a própria aba nova, a
   meio da troca. A 1.ª pergunta ao motor de média da sessão atrasa a apresentação: com a sonda 5 fotogramas (2/2
   sessões), sem ela (manifesto retirado) 1–2. Agora corre `ProbeDelay` (1,5 s) depois, e nunca a meio de uma troca.
2. **`UpdateLayout` logo a seguir a criar a aba (T6d, `2333846`)**: bissecção no ramo motor — T4 `c949bb1` 0/4,
   `d617f7b` 0/4, `2333846` 2/3 sessões com branco. Retirado.
3. **O rAF duplo não prova que o 1.º fotograma chegou ao ecrã.** Registo dos tempos (build de diagnóstico): nas
   sessões más o rAF duplo resolvia ~20 ms depois do fim do carregamento e o 1.º fotograma só aparecia ~150 ms
   depois; nas boas o rAF levava ~150 ms. Com 1 e 2 corrigidos ainda havia branco em 2/9 sessões (e 3/6 com o runtime
   do NuGet: não é a compilação própria da CEF). No 1.º documento a troca espera agora pela `first-contentful-paint`
   (`PageSwap.FirstPaintScript`): o Chromium só a regista quando recebe a hora da apresentação (chega à página
   ~16 ms depois dela), por isso a página que entra já está no compositor do Windows e dispensa a margem de 34 ms. Sem
   ela em 400 ms (página sem conteúdo), o rAF duplo e a margem, como antes.

| 1.ª aba nova da sessão (Ctrl+T da clara) | Fotogramas brancos | Latência |
|---|---|---|
| main antes | 5 em 2/2 sessões (6/6 na verificação) | 1.ª 355–501 ms (com branco); seguintes 181–196 ms |
| só com 1 e 2 | 5–6 em 2/9 sessões | 1.ª ~340–450 ms |
| 1 + 2 + 3 | **0 em 8 sessões × 3 abas novas** | 1.ª 358–375 ms (502 na 1.ª sessão depois de compilar); seguintes 190–206 ms |

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
  redimensionamento que ele faz no fim da criação não mude nada. Esse redimensionamento (`OnAfterBrowserCreated`, na
  thread do CEF) usa sempre o `ActualWidth`/`ActualHeight` do WPF: antes da 1.ª disposição dava 0×0 e desfazia o
  `BirthSize`, por isso a aba é disposta logo a seguir a ser criada (`UpdateLayout`; a criação no CEF é assíncrona).
  **Revertido depois da medição ao vivo** (ver «Primeira aba nova da sessão» abaixo): com o `UpdateLayout` a 1.ª aba
  nova da sessão ficava branca 2–3 fotogramas (2 em 3 sessões; 0 em 4 sem ele). A janela do CEF volta a passar por
  0×0 no fim da criação e ao tamanho final na disposição seguinte, antes da 1.ª pintura.

Não feito: um browser «sobressalente» já com a nova aba carregada (o estudo dá-o como opcional). Custa um processo de
página sempre vivo; fica para decisão do dono depois de medir o T4.

**T5 — Aba fria restaurada num só passo (rec. 3).** Uma aba escondida há mais do que o período de graça (T6) está
minimizada e a sua janela do CEF, em geral, encolhida a 0×0 (o CefSharp encolhe-a ao esconder, mas também lhe devolve
o tamanho inteiro, escondida, em cada `SizeChanged` e ao restaurar a janela principal; o tamanho de partida não é
fiável, e o `SetWindowPlacement` não depende dele). Ao voltar, a janela do Chromium era restaurada com `ShowWindow`
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
que entra até esta apresentar) → a entrar fria → quentes (a mais recente primeiro) → a que saiu e vai ser escondida
agora (`Layer.Cooling`: sem ela empatava com a que entra na camada «à vista» e podia ficar por cima enquanto se
esconde), num só `DeferWindowPos` e só se a ordem atual for outra. As quentes que deixam de poder ficar são arrefecidas
antes de mexer na ordem (ainda tapadas), e todas arrefecem quando nenhuma outra página fica à vista por cima. Antes bastava pôr a que entra em `HWND_BOTTOM`; com páginas quentes visíveis por baixo, a que
entra ficaria tapada por elas.

Nunca fica quente (`MainWindow.CanStayWarm` + `WarmTabs.Keep`): no ecrã dividido (as metades não tapam a área toda),
com a janela minimizada ou escondida, sem uma página à vista por cima, numa página que entrou fria e ainda não
apresentou um fotograma desde essa chegada (`PagePresentation`: Ctrl+Tab de ida e volta antes de a página apresentar,
ou Ctrl+T seguido de duas trocas; voltaria sem espera, com a superfície em branco ou antiga), numa aba com som
(`IsAudible` ou multimédia a tocar no documento principal, lido a cada 2 s por `MediaInfo.Script`; arrefece quando o
vê), numa aba a passar para o motor de vídeo, numa aba fechada a sair, e numa página com `<video>` a tocar
(verificação ao sair, `WarmTabs.PlayingVideoScript`: documento, raízes de sombra abertas e iframes do mesmo site, até
250 ms; sem resposta conta como «a tocar»). Também arrefece, por cautela, uma página com um iframe de outro site com
ar de leitor (pelo menos 200×150 CSS px, com `autoplay` ou ecrã inteiro permitidos: YouTube, Vimeo, Spotify ou
SoundCloud embutidos), mesmo parado. Limite conhecido: o `<video>` e o som de um iframe de outro site não se deixam
ver (o `IsAudible` só lê o documento principal), nem o Web Audio (`AudioContext`, jogos); uma página com um leitor de
outro site sem esse aspeto, ou com som só por Web Audio, pode ficar quente até 8 s a descodificar e desenhar. O sono
das abas por tempo e por pressão tem o mesmo limite: o som de um iframe de outro site e o Web Audio não se veem, por
isso não seguram a aba acordada (decisão D1 do plano 4 da auditoria: `SleepProbe.PlayerFrameKeepsAwake`), e só as
caixas de texto de várias linhas e o editor com o foco contam como texto por enviar — os campos de uma linha não. Outro:
se a página que entra fria não apresentar dentro do teto de 1 s do T1, por baixo dela pode ver-se uma página quente em
vez da cor de fundo.

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
| F5 | Histórico fora da thread da janela: `eteris.db` em registo antecipado (WAL, `synchronous=NORMAL`; depois de apagar histórico faz-se logo o checkpoint, para nada apagado ficar no `-wal`; o histórico apaga-se com `secure_delete` em todas as ligações dos armazéns com endereços e o ficheiro compacta-se com `VACUUM` depois de «desde sempre» e uma vez ao sair, para o que versões anteriores deixaram; o checkpoint repete-se até 3 vezes quando vem ocupado); visitas, títulos e ícones vão para uma fila com uma só tarefa (`Data/HistoryWriter.cs`), pela ordem; os títulos juntam-se por endereço (só o último de cada segundo é escrito); quem lê ou apaga despeja a fila primeiro; ao sair, escreve o que falta. Janela privada: nada escrito, como antes | Thread da janela por navegação (2–3 escritas): 3,0–3,6 ms (mediana) → **≤ 0,1 ms**; título que muda a cada segundo: 1 escrita com fsync por mudança → 1 escrita por segundo em segundo plano (teste); histórico certo depois de fechar e voltar a abrir |
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
| C2 | Motor com codecs integrado: fontes em `build/cef-codecs` (byte a byte as validadas), runtime próprio na saída sem o `bootstrap.exe`/`bootstrapc.exe`, escolha do motor no arranque (`Tabs/PlatformCodecs.cs`: sonda única na 1.ª página interna; com codecs, `KnownH264Sites` não abre sites no motor de vídeo e só contam os avisos de um motor que disse «não»; sinal novo para o vídeo sem imagem sem GPU), licenças da compilação própria (CREDITS.html e LICENSE.txt dela, LGPL do FFmpeg, oferta do código-fonte), `THIRD-PARTY-NOTICES.md`. `publish.ps1` confirmado com o runtime real | Saída Release: `libcef.dll` `4d5097ce…f20c` (o do manifesto; o do NuGet é `07168eac…`), 236/236 ficheiros com o SHA256 do manifesto, sem `bootstrap*.exe`; `publish.ps1` (verificação): «238 ficheiros verificados». Ao vivo (profile-codecs, sem som, Release): MP4 da w3schools, Twitch VOD e YouTube no motor principal — **0** processos `msedgewebview2` filhos do Éteris de teste (antes: MP4 e Twitch no WebView2). Licenças: `collect.py` sem o runtime gera `licenses/` igual ao anterior (0 diferenças) |
| D1 | Fecho: suite completa (1374 testes), medição final comparada com a base e o «puro», `CHANGELOG.md` (secção por publicar, sem versão nem tag), `docs/paridade.md` (secção 1b) | Tabela «Resultado» no topo deste ficheiro |
| M1 | Ramo fundido no main (bloco B, ciclos 31–51): o `ScriptletInjector` do motor (registos fora da navegação, por site) ganha o GPC do main registado à parte quando a sessão nasce e nunca retirado; o `PrivacyRequestHandler` junta todas as decisões do bloco B (saltos, limpeza, HTTPS primeiro, rede local, modo rigoroso, regras do utilizador, listas, CNAME, observatório, referrer, cookies de terceiros, client hints, Accept-Language) dentro do `Decide` que nunca lança, com `SafeFrame` em todas as leituras de frames. A única espera que resta no tratador é a do CNAME, só em sub-recursos (`CnameUncloak.MaxWait`); o pedido da página nunca espera (`ScriptRegistrationTests`) | — |
| V8 | Motor de vídeo: os scripts nas páginas do WebView2 passam por um só canal (`Browser/VideoPageScript.cs`). O WebView2 não tem o processo filho .NET do CefSharp (a razão da regra do sandbox não se aplica), mas `SandboxRulesTests` recusa `CallDevToolsProtocolMethodAsync`, `ExecuteScriptAsync` e `AddScriptToExecuteOnDocumentCreatedAsync` fora dele | — (sem efeito para o utilizador) |
| T1 | Troca de abas, aba nova e fecho da aba ativa sem fotograma vazio («dupla memória», `Browser/PageSwap.cs`): a página que entra aparece por baixo da que sai; a que sai só se esconde depois de a nova apresentar (rAF duplo pelo DevTools + 34 ms; uma aba nova espera primeiro pelo fim do 1.º documento; limite 1 s); a página de uma aba fechada que estava à vista fica, calada, até a vizinha aparecer | Fotogramas errados em 10 capturas (tema escuro + Mica): Ctrl+Tab clara↔escura 14 → **0**, Wikipédia 204 → 4, Ctrl+T 96 → **0**, botão + 97 → **0**, fechar a ativa 24 → 1; tema claro e sem Mica: todos → 0–1 (secção «Troca de abas sem piscar») |
| T2 | Foco do teclado na aba que entra: depois de trocar de aba, o foco do Windows ficava na janela (minimizada) da aba anterior e as teclas iam para uma página invisível; a página da aba ativa passa a recebê-lo (como no Chrome) quando o foco estava numa página que deixou de se ver | 8 Ctrl+Tab seguidos sem clicar: **2 → 8** trocas efetivas |
| T3 | Mica por baixo da página (rec. 4 do estudo): a static do CefSharp que aloja a janela do CEF deixa de pintar por GDI (`Browser/PageBrowser.cs`, `PageWindow.PaintStep`); na área de vidro o GDI escrevia alfa 0 e o DWM mostrava o Mica onde devia estar a cor de fundo da página. Vidro limitado às barras avaliado e recusado (secção «Troca de abas: o resto do estudo») | Medição ao vivo pendente (dono a usar o PC). Testes: `PageWindowTests` |
| T4 | Aba nova (rec. 5): a static nasce por baixo das outras páginas e a janela do CEF nasce já com o tamanho da área das páginas (antes 0×0 numa aba nova, redimensionada a seguir); mesma conta de píxeis do CefSharp (`PageWindow.PixelSize`), por isso o redimensionamento dele no fim da criação não muda nada. A espera pela 1.ª pintura continua a do T1 (teto 1 s) | Medição ao vivo pendente (dono a usar o PC). Testes: `PageWindowTests` |
| T5 | Aba fria (rec. 3): a janela do Chromium volta de minimizada num só passo e já no tamanho final (`SetWindowPlacement` com a área da página e `SW_SHOWNOACTIVATE`, antes de a static aparecer), em vez de `ShowWindow` + `SetWindowPos` (duas mudanças de tamanho com a página «hidden»); ao esconder, minimiza antes de o CefSharp encolher a janela a 0×0. Também nos painéis web da barra lateral | Medição ao vivo pendente (dono a usar o PC). Testes: `PageWindowTests` |
| T6 | Abas quentes (rec. 2): a página que sai fica tapada, no tamanho inteiro e sem minimizar, durante 8 s (no máximo 2, as mais recentes; `Tabs/WarmTabs.cs`), e voltar a ela é só subir a janela; nunca com vídeo a tocar, som, ecrã dividido, janela minimizada ou a passar para o motor de vídeo. Ordem das janelas das páginas explícita (`PageSwap.StackOrder`: à vista → a sair → a entrar fria → quentes). Custo: ~9–11 % de 1 núcleo por página animada quente, só durante a graça | Medição ao vivo pendente (dono a usar o PC). Testes: `WarmTabsTests`, `PageSwapTests` |
| T3–T6 | Lista do que falta medir ao vivo para T3–T6 (casos, critério de 0 fotogramas errados e ≤ ~120 ms, CPU de aba animada escondida depois da graça igual à do T1), na secção «Troca de abas: o resto do estudo» | Medição ao vivo pendente (dono a usar o PC) |
| T6b | Correções da revisão das abas quentes: (1) uma página que entrou fria e ainda não apresentou já não fica quente (`PagePresentation`: cada chegada fria tem um número e só o rAF duplo dessa chegada, depois do 1.º carregamento, a dá por apresentada; também nas chegadas sem página a segurar, e mais tarde, até 8 s, numa página que passe o teto de 1 s) — antes, Ctrl+Tab de ida e volta antes de a página apresentar, ou Ctrl+T e duas trocas, voltava a ela sem espera e mostrava branco ou conteúdo antigo; (7) o resultado da verificação de vídeo só se aplica à mesma saída da página; (8) a graça mede-se com um relógio monótono (`Environment.TickCount64`), não com a hora do sistema | Medição ao vivo pendente (dono a usar o PC). Testes: `PageSwapTests` (`Presentation_*`), `WarmTabsTests` |
| T6c | Ordem das janelas e cobertura das quentes (revisão): (4) a página que sai e não pode ficar quente (som, motor de vídeo) passa para uma camada de fundo (`Layer.Cooling`) antes de se esconder — antes empatava com a quente que voltava (ou com a que acabou de entrar) e, conforme a ordem do dicionário, ficava por cima dela enquanto era minimizada e escondida; (5) as quentes são revistas antes de ordenar as janelas, por isso ao entrar no ecrã dividido arrefecem ainda tapadas; (6) a revisão verifica se há uma página à vista por cima (sem nenhuma, arrefecem todas; também quando a página de uma aba fechada sai pelo recurso de tempo) | Medição ao vivo pendente (dono a usar o PC). Testes: `PageSwapTests` (`Stack_APageBeingHidden_GoesUnderEverything`) |
| T6d | Resto da revisão: (2) a aba nova é disposta logo a seguir a ser criada (`UpdateLayout`), porque o CefSharp 156 redimensiona sempre a janela do CEF pelo `ActualWidth` no fim da criação (`OnAfterBrowserCreated`) e, antes da 1.ª disposição, isso dava 0×0 e desfazia o `BirthSize`; (3) o «som» só se vê no documento principal (`MediaInfo.Script`): comentários e limite corrigidos, e um iframe de outro site com ar de leitor (≥ 200×150, `autoplay`/ecrã inteiro) passa a arrefecer a página; Web Audio continua por ver (limite documentado); (9) os painéis web da barra lateral minimizam antes de esconder no WPF, como as abas; frase do T5 sobre o 0×0 corrigida | Medição ao vivo pendente (dono a usar o PC): no T4, registar o tamanho da janela do CEF da aba nova. Testes: `WarmTabsTests` (`PlayingVideoScript_AnEmbeddedPlayerFromAnotherSiteCountsAsPlaying`) |
| FP1 | **Por fazer na próxima compilação do motor** (medição de impressão digital, `docs/paridade.md` #6): `libcef/browser/media_access_query.cc:353-359`, `CheckMediaAccessPermission` responde sempre «sim» (`// Always allowed here.`), e o Chromium usa essa resposta para decidir se `enumerateDevices` mostra os nomes e os `deviceId` dos dispositivos. Patch: responder «sim» só às origens a que o utilizador deu câmara/microfone (as decisões do Éteris estão em `Permissions/SitePermissionHandler.cs`; o CEF pode perguntar ao cliente, ou guardar as origens aceites em `RequestMediaAccessPermission`). Até lá, paliativo por script (`PrivacySignals.DevicesScript`, registado com o GPC em todas as páginas): uma entrada por tipo, sem nome nem ID, até haver permissão ou um `getUserMedia` aceite. Limite do paliativo: um iframe de outro site (processo próprio) a quem a página delega `camera`/`microphone` pelo atributo `allow` não recebe o script e continua a ver os nomes; sem `allow` o próprio Chromium já os esconde | Página (example.com, wikipedia.org): 8 dispositivos com nome e `deviceId` persistente → **3 entradas vazias** (`audioinput`, `videoinput`, `audiooutput`), igual ao Chrome. Iframe de outro site com `allow="camera; microphone"`: continua com nomes (patch do motor) |
| FP2 | **Por fazer na próxima compilação do motor**: dicas do cliente reduzidas no próprio Chromium (`GetUserAgentMetadata` do cliente de conteúdo da CEF / `embedder_support::GetUserAgentMetadata`: Windows `10.0.0`, versão `<maior>.0.0.0`). Hoje a redução vem dos metadados da sessão DevTools de cada aba (`Shields/ReducedClientHints.cs`), que cobrem a página, os frames do mesmo processo, os workers dedicados e os cabeçalhos, mas não os service workers nem os shared workers (não pertencem à aba) | Worker dedicado: `19.0.0`/`156.0.8078.80` → **`10.0.0`/`156.0.0.0`** (igual à página). Shared worker e service worker (o que o CreepJS lê): continuam `19.0.0`/`156.0.8078.80` |
| MOT-01 | Prova ao vivo dos certificados de cliente (auditoria, 2026-11-12, motor 156.0.15, sem `OnSelectClientCertificate` no `PrivacyRequestHandler`): servidor https em 127.0.0.1 que pede certificado de cliente sem o exigir, um certificado de cliente de teste na loja pessoal do Windows, perfil de teste. Resultado **C**: o motor corta a ligação TLS logo a seguir ao pedido do certificado, não aparece nenhuma janela de escolha e nenhum certificado é enviado (40 s de espera, janela normal e privada); a aba fica parada sem título. Não se confirmou o envio sozinho do certificado: a gravidade fica baixa, e o que falta é compatibilidade (os sites que pedem certificado de cliente não abrem). Não provado: o site fixado na barra lateral. A escolha explícita do Éteris é a Task 17 do plano 3 da auditoria | sites com certificado de cliente parados → por corrigir |
