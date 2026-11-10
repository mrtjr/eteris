# Componentes de terceiros

O Éteris é software livre, distribuído sob a **GPL-3.0 ou posterior** (ficheiro `LICENSE`). O código do
motor de bloqueio (`src/Eteris.Shields`) é MPL-2.0.

O Éteris distribui os seguintes componentes de terceiros, cada um sob a sua própria licença. **Os textos
completos de todas as licenças estão na pasta `licenses/`** (índice em `licenses/INDEX.md`), que acompanha
a instalação. Essa pasta é gerada a partir dos pacotes exatos usados na compilação por
`build/licenses/collect.py`.

## Motor de bloqueio

| Componente | Origem | Licença |
|---|---|---|
| adblock-rust | https://github.com/brave/adblock-rust | MPL-2.0 |
| Public Suffix List (dados, pelo crate psl) | https://publicsuffix.org | MPL-2.0 |

Compilado em `eteris_shields.dll` (código em `src/Eteris.Shields`, também MPL-2.0). A Public Suffix List vai dentro
dele, sem alterações, e decide o «site» de Triturar, Esquecer ao fechar e do Escudo (texto da MPL-2.0 em
`licenses/PublicSuffixList-MPL-2.0.txt`).

## Listas de filtros

As listas são as que o uBlock Origin Lite ativa por omissão, mais a EasyList Portuguesa. São distribuídas
como ficheiros de dados separados em `Shields/lists/` e atualizadas semanalmente a partir das fontes abaixo.
Não são modificadas.

| Lista | Fonte | Licença |
|---|---|---|
As listas com licença dupla são usadas e redistribuídas sob a GPL-3.0.

| uBlock filters (filters, privacy, unbreak, badware, quick-fixes) | https://github.com/uBlockOrigin/uAssets | GPL-3.0 |
| EasyList | https://easylist.to | GPL-3.0 ou CC BY-SA 3.0 |
| EasyPrivacy | https://easylist.to | GPL-3.0 ou CC BY-SA 3.0 |
| EasyList Portuguesa | https://easylist-downloads.adblockplus.org/easylistportuguese.txt | GPL-3.0 ou CC BY-SA 3.0 |
| EasyList Cookie (via uBlock) | https://github.com/uBlockOrigin/uAssets | GPL-3.0 ou CC BY-SA 3.0 |
| AdGuard Cookie Notices (via uBlock) | https://github.com/uBlockOrigin/uAssets | GPL-3.0 |

## Scriptlets

`Shields/resources/scriptlets.json` contém as funções de scriptlet do uBlock Origin 1.75.4
(https://github.com/gorhill/uBlock, `src/js/resources/`), sem alterações ao código, convertidas para o
formato de recursos do adblock-rust por `build/scriptlets/build-scriptlets.mjs`. Licença: GPL-3.0.
O código-fonte corresponde à tag `1.75.4` desse repositório.

## Navegador

| Componente | Origem | Licença |
|---|---|---|
| Chromium Embedded Framework / CefSharp | https://github.com/cefsharp/CefSharp | BSD-3-Clause |
| Chromium (incluído no CEF) | https://www.chromium.org | BSD-3-Clause e licenças dos seus componentes |
| FFmpeg (dentro do `libcef.dll`; sem descodificadores com patentes) | https://chromium.googlesource.com/chromium/third_party/ffmpeg | LGPL-2.1-or-later |
| Velopack | https://github.com/velopack/velopack | MIT |
| CommunityToolkit.Mvvm | https://github.com/CommunityToolkit/dotnet | MIT |
| QRCoder (códigos QR) | https://github.com/Shane32/QRCoder | MIT |
| Microsoft.Data.Sqlite / SQLite | https://github.com/dotnet/efcore · https://sqlite.org | MIT · domínio público |
| SQLitePCLRaw | https://github.com/ericsink/SQLitePCL.raw | Apache-2.0 |
| Readability.js 0.6.0 (modo de leitura; sem alterações, em `Browser/Vendor/readability`) | https://github.com/mozilla/readability | Apache-2.0 |
| .NET e WPF (incluídos na instalação) | https://github.com/dotnet/runtime · https://github.com/dotnet/wpf | MIT, com os avisos de terceiros do .NET |
| Microsoft.Web.WebView2 (SDK do motor de vídeo: `Microsoft.Web.WebView2.*.dll`, `WebView2Loader.dll`; sem a projeção do Windows SDK, cuja licença é incompatível com a GPL) | https://www.nuget.org/packages/Microsoft.Web.WebView2 | BSD-3-Clause (Microsoft), com os avisos em `licenses/Microsoft.Web.WebView2-NOTICE.txt` |

Os créditos completos do Chromium (centenas de componentes) estão em `licenses/Chromium-credits.html`.

**Motor de vídeo.** As abas de sites que só têm vídeo em H.264/AAC usam o *WebView2 Runtime* da Microsoft, que faz
parte do Windows 10/11 e é atualizado pelo próprio Windows. O Éteris **não** distribui esse runtime nem os seus
codecs: usa o que já está instalado no sistema (componente de sistema, no sentido da secção 1 da GPL-3.0). As
licenças de patentes do H.264/AAC são, por isso, as do Windows/Edge e não do Éteris.

**Codecs da plataforma.** As versões do Éteris compiladas com o motor próprio (CEF 156.0.15 compilada pelo projeto
com `proprietary_codecs=true` e `ffmpeg_branding=Chromium`; ver `build/cef-codecs`) tocam H.264/HEVC e AAC no motor
principal sem os descodificar: o H.264 e o HEVC são descodificados por componentes do Windows, pela placa gráfica
(D3D11VideoDecoder), e o AAC pelo Media Foundation do Windows — componentes de sistema, com as licenças de patentes do
Windows e dos fabricantes. **Nenhum descodificador com patentes vai incluído**: nem os do FFmpeg nem o OpenH264. O
FFmpeg que vai ligado no `libcef.dll` (LGPL-2.1-or-later, texto em `licenses/FFmpeg-LICENSE.txt`) só tem
desmultiplexadores, parsers e descodificadores livres. Fonte correspondente: os patches e o script de compilação em
`build/cef-codecs` (também na instalação, em `licenses/cef-codecs/`), sobre os commits da CEF, do Chromium e do
FFmpeg indicados em `licenses/FFmpeg-SOURCE.txt`, que tem também a oferta escrita do código-fonte que a LGPL exige.
Quando o Windows não descodifica (sem descodificação por hardware, ou Windows N sem o Media Feature Pack), a aba usa o
motor de vídeo acima.

## Kit de recuperação

| Componente | Origem | Licença |
|---|---|---|
| Lista de palavras BIP39 em português (as 24 palavras do kit de recuperação; sem alterações, embutida no `Eteris.Sync.dll`) | https://github.com/bitcoin/bips (ficheiro `bip-0039/portuguese.txt`) | MIT |

`src/Eteris.Sync/Crypto/bip39-pt.txt` é a lista portuguesa do BIP-0039, sem alterações (2048 palavras, 15 671 bytes,
fins de linha LF), obtida em 2026-11-09 do ramo principal do repositório `bitcoin/bips`; SHA-256
`2685e9c194c82ae67e10ba59d9ea5345a23dc093e92276fc5361f6667d79cd3f` (o mesmo ficheiro, byte a byte, que o do
`trezor/python-mnemonic`). O Éteris usa só a codificação do BIP-0039 entre o segredo e as palavras, nada mais. Licença:
MIT, texto em `licenses/BIP39-wordlist-LICENSE.txt`.

## Motor de bloqueio: dependências Rust

O `eteris_shields.dll` inclui 59 crates de terceiros, todos sob licenças permissivas: MIT, Apache-2.0,
Unicode-3.0 e Unlicense (cada um com os seus ficheiros em `licenses/rust/`).

## Imagens e vídeo

Os fundos da nova aba (`Browser/Wallpapers/coast.jpg`, `mountains.jpg` e `coast-live.webm`) são imagens geradas por
IA com o ChatGPT (OpenAI) pelo autor do Éteris, em 2026-11-06; o vídeo foi codificado com o FFmpeg (VP9, sem som).
Não são fotografias nem vêm de bancos de imagens. Nos termos de uso da OpenAI o resultado pertence a quem o gerou;
imagens geradas por IA podem, ainda assim, não ter proteção de direitos de autor em várias jurisdições. Redistribuem-se
com o Éteris sob a GPL-3.0-or-later, como o resto do projeto. A ficha completa (dimensões, origem e termos) está em
`src/Eteris.Browser/Browser/Wallpapers/README.md` e acompanha a instalação em `licenses/Wallpapers-NOTICE.txt`.

## Listas próprias

`Shields/trackers.txt` (domínios de rastreio de reserva) foi escrita para o Éteris e segue a licença do projeto.
