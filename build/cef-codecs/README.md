# CEF com codecs da plataforma

Esta pasta produz a compilação própria da CEF que o Éteris usa: **CEF 156.0.15**
(commit `cf671c072925b27823a61181152f4f288e8e4306`, ramo 8078, Chromium 156.0.8078.80), a mesma
versão que o CefSharp 156.0.150 espera, no modo **«codecs da plataforma»**.

| Ficheiro | Para que serve |
| --- | --- |
| `patches/platform-codecs.patch` | Alterações ao Chromium em `media/` (formato `git diff`, relativo a `src`). |
| `patches/ffmpeg-adts-demuxer.patch` | Alterações à configuração do FFmpeg (formato `git diff`, relativo a `src/third_party/ffmpeg`). |
| `build.ps1` | Verifica os requisitos, aplica os patches, gera os projetos, compila, faz a distribuição mínima e junta o runtime com um `manifest.json`. |
| `README.md` | Este documento. |

## O que é o modo «codecs da plataforma»

A CEF publicada pelo projeto (e usada pelo CefSharp) é compilada sem codecs proprietários: não
reproduz H.264, AAC nem HEVC, por isso muitos sites de vídeo não funcionam. Este modo liga o suporte
a esses formatos **sem incluir nenhum descodificador com patentes**:

- `proprietary_codecs=true` ativa no Chromium os desmultiplexadores (MP4, MPEG-2 TS/HLS, ADTS) e a
  lógica de H.264/AAC/HEVC;
- `ffmpeg_branding=Chromium` mantém o FFmpeg na configuração livre: **não contém descodificadores
  H.264, HEVC nem AAC** (só os de Opus, Vorbis, FLAC, MP3 e PCM; VP8/VP9/AV1 vêm do libvpx e do
  dav1d). O nosso segundo patch só lhe acrescenta o desmultiplexador ADTS (`.aac`) e o *parser* AAC,
  que leem cabeçalhos e cortam a sequência em tramas — não descodificam áudio;
- o **H.264 e o HEVC** são descodificados pela placa gráfica, através do `D3D11VideoDecoder` no
  processo GPU;
- o **AAC** (AAC-LC, HE-AAC v1/v2 e, no Windows 11 22H2 ou mais recente, xHE-AAC) é descodificado
  pelo descodificador AAC do Media Foundation que vem com o Windows, também no processo GPU;
- `media_use_openh264=false` desliga o OpenH264 (H.264 em software) e, com ele, o H.264 em software
  do WebRTC (`rtc_use_h264=false`).

No Windows, `proprietary_codecs=true` também liga sozinho, sem acrescentar descodificadores:
`enable_platform_hevc`, `enable_hevc_parser_and_hw_decoder`, `enable_hls_demuxer`,
`enable_platform_dolby_vision` e `enable_platform_encrypted_dolby_vision` (só desmultiplexação),
`platform_has_optional_hevc_encode_support` (codificação HEVC pelo Media Foundation, se o Windows a
tiver) e `rtc_use_h265` (*parsers* e empacotamento RTP). O `manifest.json` grava estes valores.

O Widevine, o *component updater*, a verificação do *host* do CDM, o *storage ID* e o RLZ ficam
como na CEF oficial: é a própria CEF que os impõe (`cef/tools/gn_args.py`). O seu uso continua a
ser controlado pelas definições do Éteris/CefSharp em tempo de execução.

A outra opção, `ffmpeg_branding=Chrome`, que põe os descodificadores H.264/AAC do FFmpeg dentro do
`libcef.dll`, **está fora de questão**: o `build.ps1` recusa continuar se a vir no `args.gn` ou se a
configuração do FFmpeg que vai ser compilada tiver descodificadores H.264/HEVC/AAC.

## Porque não se distribuem descodificadores com patentes

O H.264, o AAC e o HEVC estão cobertos por patentes licenciadas em *pools* (Via LA, Access Advance).
Quem distribui uma implementação destes codecs, mesmo de código aberto, pode ter de pagar licenças
por cópia. O Google licencia o H.264 e o AAC para o Chrome (o próprio Chrome só reproduz HEVC por
hardware); o Éteris (GPL-3.0, distribuído gratuitamente) não o pode fazer. As implementações que já
vêm com o Windows (Media Foundation) e com o controlador da placa gráfica (DXVA/D3D11) são
distribuídas pela Microsoft e pelos fabricantes. Ao chamá-las, o Éteris reproduz estes formatos sem
distribuir ele próprio um descodificador destes codecs — o mesmo modelo que o Firefox usa no
Windows. Isto não é aconselhamento jurídico.

O mesmo vale para o OpenH264: a licença de patentes da Cisco só cobre os binários que a própria Cisco
publica, não um OpenH264 compilado por nós.

## O que os patches alteram

Nenhum dos ficheiros é tocado pelos patches da CEF (o `build.ps1` confirma-o a cada execução). O
`platform-codecs.patch` não depende da pasta `cef/`: serve também para uma árvore Chromium sem a CEF.

`patches/platform-codecs.patch` (em `src`):

| Ficheiro | Alteração |
| --- | --- |
| `media/BUILD.gn` | Nova *build flag* `PLATFORM_PROPRIETARY_DECODERS_ONLY` (`proprietary_codecs && media_use_ffmpeg && ffmpeg_branding == "Chromium"`). O `assert` que proíbe esta combinação deixa de se aplicar no Windows (alteração própria do Éteris, na linha do que foi discutido no problema #3559 da CEF). Novo `assert`: este modo exige `enable_platform_hevc=true`, porque é com ele que o renderer recebe a lista de perfis de vídeo suportados pela GPU. |
| `media/base/supported_types.cc` | O H.264 deixa de ser «sempre suportado»: só os perfis que o processo GPU anunciou contam (como já acontecia com o HEVC). O AAC deixa de ser dado como suportado sem perguntar: consulta a lista de descodificadores de áudio do processo GPU. O FFmpeg deixa de contar como descodificador H.264 «embutido». |
| `media/gpu/windows/supported_profile_helpers.cc` | O D3D11/D3D12 só anuncia H.264 se a placa expuser o perfil DXVA H.264 (antes anunciava-o sempre, contando com o FFmpeg como reserva — em máquinas virtuais e adaptadores sem DXVA isso dava um «sim» sem descodificador). |
| `media/ffmpeg/ffmpeg_common.cc` | Sem descodificador AAC, o FFmpeg não preenche o formato de amostra; passa a usar-se 16 bits (como no AC3/EAC3 e como as faixas MP4 declaram). |
| `media/filters/ffmpeg_demuxer.cc` | Ficheiros `.aac` (ADTS) pelo FFmpeg: sem descodificador, o `avformat_find_stream_info()` deixa a faixa com 0 canais e taxa 0 (o *parser* AAC deixa-os de propósito para o descodificador) e os pacotes ficam sem duração nem PTS. Antes do `avformat_find_stream_info()`, lê-se o 1.º cabeçalho ADTS (o desmultiplexador deixa a leitura parada na 1.ª trama; volta-se lá a seguir) com o `ADTSStreamParser::ParseHeader()` do Chromium e preenchem-se a taxa base, os canais, o perfil, `frame_size=1024` e um AudioSpecificConfig de 2 bytes em `extradata`. O FFmpeg passa a calcular durações e PTS, e o `MediaFoundationAudioDecoder` recebe uma configuração com AudioSpecificConfig (entrada AAC «raw», sem o cabeçalho ADTS de cada trama — o mesmo caminho do MSE `audio/aac`). |
| `media/renderers/audio_renderer_impl.cc` | Para AAC aceita mudanças de taxa/canais a meio (como no AC3/EAC3): sem descodificador, o FFmpeg não resolve o SBR/PS implícito do HE-AAC e a GPU devolve o dobro da taxa; o conversor de áudio faz a reamostragem em vez de dar erro. |
| `media/filters/win/media_foundation_audio_decoder.{h,cc}` | Aceita AAC-LC/HE-AAC (antes só xHE-AAC); retira o cabeçalho ADTS que os *parsers* de MSE/HLS acrescentam quando há AudioSpecificConfig; aceita tipos de saída com metade ou o dobro da taxa e dos canais (SBR/PS, nos dois sentidos); novo `HasAacDecoder()` que confirma que o MFT AAC existe. |
| `media/base/win/mf_helpers.cc` | Sem AudioSpecificConfig (WebCodecs `mp4a.*` sem `description`) o MFT é configurado para entrada ADTS; o tipo de entrada AAC indica sempre 16 bits. |
| `media/mojo/services/gpu_mojo_media_client_win.cc` | O processo GPU anuncia AAC (perfis normais) sempre que o MFT AAC do Media Foundation existe. |

`patches/ffmpeg-adts-demuxer.patch` (em `src/third_party/ffmpeg`, só a configuração
`Chromium/win/x64`): liga `CONFIG_AAC_DEMUXER`, `CONFIG_AAC_PARSER` e `CONFIG_ADTS_HEADER`,
acrescenta `ff_aac_demuxer` e `ff_aac_parser` às listas e, em `ffmpeg_generated.gni`, as fontes
`aac_ac3_parser.c`, `aac_parser.c`, `adts_header.c`, `apetag.c`, `aacdec.c` (o desmultiplexador, em
`libavformat`) e `img2.c`. São exatamente os ficheiros não-descodificadores que a configuração
«Chrome» tem a mais; `codec_list.c` não muda. Sem isto, `<audio src="x.aac">` e as rádios ADTS por
HTTP diriam «sim» no `canPlayType('audio/aac')` mas não abririam (com o desmultiplexador, mas sem a
alteração ao `ffmpeg_demuxer.cc` descrita acima, davam `DEMUXER_ERROR_NO_SUPPORTED_STREAMS`).

Caminho de decisão resultante:

- `canPlayType()` / `MediaSource.isTypeSupported()` → `MimeUtil::IsCodecSupported` →
  `IsDecoderSupported{Video,Audio}Type` → listas enviadas pelo processo GPU. **Sem descodificação
  por hardware (ou num Windows «N» sem Media Feature Pack) a resposta é «não»**, e o Éteris recorre
  ao WebView2.
- Reprodução de vídeo: os descodificadores Mojo (GPU) vêm primeiro na lista; o
  `FFmpegVideoDecoder` já não se apresenta para H.264.
- Reprodução de áudio: o `FFmpegAudioDecoder` falha logo para AAC (não há descodificador) e o
  seletor passa ao `MojoAudioDecoder`, que no processo GPU cria o `MediaFoundationAudioDecoder`.

### Limitações conhecidas

- **Faixas descartadas em silêncio.** Num MP4 `<video src>` sem descodificação por hardware a faixa
  H.264/HEVC é descartada sem erro (toca só o som); num Windows «N» é a faixa AAC (vídeo sem som). O
  leitor HLS nativo responde «maybe» a `application/vnd.apple.mpegurl` sem olhar para o codec. Por
  isso o Éteris decide o recurso ao WebView2 **uma vez por sessão, no próprio CEF**
  (`src/Eteris.Browser/Tabs/PlatformCodecs.cs`), com
  `MediaSource.isTypeSupported('video/mp4; codecs="avc1.640028"')` **e**
  `MediaSource.isTypeSupported('audio/mp4; codecs="mp4a.40.2"')`, e não por página; e trata o vídeo
  sem imagem (metadados, `videoWidth` 0, motor a dizer «não» ao `avc1`) como «formato não suportado».
- `AudioContext.decodeAudioData()` de AAC/M4A não funciona (usa diretamente os descodificadores do
  FFmpeg).
- H.264 de 10 bits ou 4:4:4 e resoluções acima do que a GPU suporta não são reproduzidos (não há
  descodificação em software).
- O MFT AAC da Microsoft só vai até 6 canais e 48 kHz; AAC 7.1 ou a 96 kHz recebe «sim» e falha.
- Ficheiros `.aac` (ADTS) pelo FFmpeg: a configuração vem só do **1.º** cabeçalho ADTS. Não abrem os
  que usam `channel_configuration=0` (disposição de canais num `program_config_element`, raro); uma
  mudança de taxa ou de canais a meio do ficheiro não é seguida; as tramas com mais de um
  `raw_data_block` (raras; os codificadores habituais usam um por trama) não são descodificadas,
  porque o cabeçalho ADTS só é retirado de tramas com um bloco. No HE-AAC a taxa anunciada é a base
  (metade) até à 1.ª trama descodificada; o renderer aceita a mudança (ver `audio_renderer_impl.cc`).
- O MFT AAC da Microsoft não descodifica o perfil AAC Main (nem SSR/LTP): um `.aac` ADTS Main abre
  mas dá `DECODER_ERROR_NOT_SUPPORTED` (o mesmo acontece num MP4 com AAC Main). AAC-LC e HE-AAC v1/v2
  em ADTS tocam (validado com os ficheiros de `media/test/data`).
- Todo o AAC é descodificado no processo GPU: se este falhar a meio, a reprodução dá erro (não há
  reserva) e, até o canal GPU ser recriado, o AAC passa a «não».
- Codificação H.264 (WebRTC, MediaRecorder, WebCodecs `VideoEncoder`) só com o codificador por
  hardware. WebRTC: H.264 só por hardware e sem reserva em software (se o descodificador da GPU
  falhar a meio da chamada, o vídeo H.264 congela). WebCodecs com
  `hardwareAcceleration: "prefer-software"` diz «não» para H.264.
- Privacidade: as respostas de `canPlayType`/`isTypeSupported`/`MediaCapabilities` para `avc1` e
  `mp4a` passam a depender da GPU e da edição do Windows, o que acrescenta alguns bits de
  *fingerprinting* (o Chrome já faz o mesmo com o HEVC). É o preço de poder recorrer ao WebView2.

## Requisitos

- Windows 10/11 de 64 bits, com pelo menos **100 GB livres** no disco de `C:\cef` e 32 GB de RAM
  recomendados.
- Código já descarregado em `C:\cef\chromium_git\chromium\src` (tag 156.0.8078.80, com os *hooks*
  corridos e os perfis PGO) e a CEF em `src\cef` no commit acima; `depot_tools` em `C:\cef\depot_tools`.
- Visual Studio 2026 (18) com «Desenvolvimento para desktop com C++» e ATL
  (por omissão `C:\Program Files\Microsoft Visual Studio\18\Insiders`).
- Windows SDK 10.0.28000 com as Debugging Tools for Windows.

O `build.ps1` só altera o ambiente do próprio processo (`DEPOT_TOOLS_WIN_TOOLCHAIN=0`,
`DEPOT_TOOLS_UPDATE=0`, `DEPOT_TOOLS_METRICS=0`, `DEPOT_TOOLS_BUILD_TELEMETRY=0`, `vs2026_install`,
`GYP_MSVS_VERSION`, `PATH`, `GN_DEFINES`, `GN_OUT_CONFIGS` e os caminhos longos do git através de
`GIT_CONFIG_COUNT`), além da prioridade e da afinidade do próprio processo, que repõe no fim; não
muda a configuração global do git nem nada do sistema.

## Tempo

Numa máquina com 16 processadores lógicos, com 12 trabalhos e prioridade abaixo do normal (para o
computador continuar utilizável), a compilação oficial (ThinLTO + PGO) demora **cerca de 5 a 10
horas**. O limite vale também para a ligação ThinLTO final, que sozinha usaria todos os núcleos: o
`build.ps1` limita a afinidade do processo (herdada pelos filhos) a `-Jobs` processadores. A geração
dos projetos leva cerca de um minuto, o passo `Smoke` (só os ficheiros alterados) uns minutos e a
distribuição mínima poucos minutos. Uma recompilação depois de mudar só os patches é bem mais rápida
(minutos a uma hora).

## Como correr

Numa consola PowerShell, nesta pasta:

```powershell
# 1. Só verificar (não altera nada)
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Steps Check

# 2. Aplicar os patches, gerar os projetos e compilar só os ficheiros alterados (minutos)
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Steps Patch,Generate,Smoke

# 3. Tudo: verificar, aplicar, gerar, compilação rápida, compilação completa, distribuição e runtime
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

O passo `Build` corre, em `src`, o mesmo que o `automate-git.py` (`--no-update --force-build
--minimal-distrib --no-distrib-docs --x64-build --no-debug-build --with-pgo-profiles`) faria depois
de gerar os projetos, com o limite de trabalhos:

```
autoninja -C out\Release_GN_x64 -j 12 cefsimple bootstrap bootstrapc
```

e o passo `Package` corre `make_distrib.bat --ninja-build --x64-build --minimal --allow-partial
--no-archive --no-docs --no-symbols`.

Parâmetros úteis: `-Jobs 8` (menos trabalhos em paralelo), `-CefRoot D:\cef`, `-VsInstall <pasta>`,
`-OutDir <pasta>`, `-MinFreeGB 80`. Todos os passos podem ser repetidos: os patches só são aplicados
se ainda não estiverem, a compilação é incremental e a distribuição é refeita do zero.

Se o código for atualizado com o `automate-git.py` (por exemplo com `--force-clean` ou
`--discard-local-changes`) ou com `gclient sync`, os patches podem ser removidos; basta voltar a
correr o `build.ps1`.

## Resultado

Em `out\` (ao lado do `build.ps1`):

- `runtime\` — o runtime completo, numa só pasta como o CefSharp o espera (`libcef.dll`,
  `chrome_elf.dll`, `*.pak`, `icudtl.dat`, `v8_context_snapshot.bin`, `locales\`, …);
- `runtime\licenses\` — `LICENSE.txt` da CEF, `CREDITS.html` (avisos de licença do Chromium e das
  dependências) e `patches\` (os dois patches); ver «Licenças»;
- `runtime\manifest.json` — modo (`platform-codecs`), `args.gn`, valores GN derivados, commits da
  CEF, do Chromium e do FFmpeg, SHA256 dos patches e da configuração do FFmpeg e **SHA256 e tamanho
  de cada ficheiro** do runtime (caminhos relativos a `runtime\`, com `/`). No fim do `Package` o
  `build.ps1` relê o `manifest.json` e confirma que cada caminho existe com o tamanho e o SHA256
  gravados e que nenhum ficheiro do runtime ficou de fora;
- `distrib\cef_binary_…_windows64_minimal\` — a distribuição mínima da CEF (inclui `libcef.lib` e
  os cabeçalhos);
- `symbols\libcef.dll.pdb` — símbolos para analisar falhas (não se distribui);
- `logs\` — registos da geração, das compilações e da distribuição.

## No Éteris

O `src/Eteris.Browser/Eteris.Browser.csproj` usa `out\runtime` em vez do runtime da CEF do NuGet
sempre que lá existem `libcef.dll` e `manifest.json` (na compilação e na publicação). Leva o
runtime inteiro, menos o `bootstrap.exe`/`bootstrapc.exe` (o arranque dos processos filhos pelo
sandbox, ainda não usado pelo Éteris: `docs/sandbox.md`, passo D), põe `runtime\licenses\` e o
`manifest.json` em `licenses\cef-codecs\` na instalação e o `build/licenses/collect.py` gera a
pasta `licenses/` do repositório a partir dele. O `build/publish.ps1` recusa publicar se o
`manifest.json` não disser `mode = platform-codecs`, se o commit da CEF não for o `cf671c0…`, se o
`libcef.dll` não for a versão `156.0.15+gcf671c0+chromium-156.0.8078.80` ou se algum ficheiro não
bater com o SHA256 gravado. Sem `out\runtime`, o Éteris compila e publica com o runtime do NuGet,
como antes.

Para reconstruir: correr o `build.ps1` completo (ver «Como correr») e copiar a pasta `out\runtime`
inteira para `build/cef-codecs/out/runtime` no repositório do Éteris (se o `build.ps1` correr na
própria pasta do repositório, o resultado já fica no sítio certo); depois
`python build/licenses/collect.py`. O `build.ps1` e os patches estão no repositório tal como foram
validados (sem conversão de fins de linha: o SHA256 dos patches está no `manifest.json`).

### Publicação

As releases do Éteris **publicam-se localmente**, na máquina onde está este runtime: `build/sandbox/build-all.ps1`
(o CefSharp com o patch do sandbox e a DLL cliente do bootstrap; em cada release, depois do commit da versão, pelo menos
`-SkipCefSharp -SkipClient`, que corre os testes no modo com sandbox), `build/publish.ps1` (desde a 0.2.14 com sandbox
por omissão; `-NoSandbox` só em emergência), `vpk download github`, `build/pack.ps1`, teste do pacote, tag e
`vpk upload github` — passo a passo em `docs/motor.md`, «Publicação», e `docs/sandbox.md`, «Como publicar com o
sandbox». O fluxo `.github/workflows/release.yml` **não consegue compilar este motor** (110–150 GB de código e
objetos, 5–10 horas de compilação oficial, muito acima do disco, ~14 GB, e do tempo, 6 h, dos *runners* alojados do
GitHub): no push de uma tag `v*` só compila e corre os testes, sem pack nem upload. A publicação pelo CI existe só
para emergências, por `workflow_dispatch` com o input `sem_codecs` = true, e sai com o runtime do NuGet, sem codecs
e sem sandbox (`publish.ps1 -NoSandbox` e `pack.ps1 -NoSandbox`) nem login Google (`pack.ps1 -SemLogin`).

## Licenças

Quem redistribuir o runtime (o Éteris) tem de levar `runtime\licenses\` consigo. O `libcef.dll`
contém o FFmpeg (LGPL-2.1) ligado estaticamente: é o FFmpeg do Chromium na revisão fixada pelo `DEPS`
do commit indicado no `manifest.json` (`ffmpeg.commit`), com as alterações de
`ffmpeg-adts-demuxer.patch`. A fonte correspondente é esse código, que se obtém em
`https://chromium.googlesource.com/chromium/third_party/ffmpeg`, mais os patches e o `build.ps1`
desta pasta. O `CREDITS.html` traz os restantes avisos (BSD do Chromium, etc.).
