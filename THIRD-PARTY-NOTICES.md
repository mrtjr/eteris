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

Compilado em `eteris_shields.dll` (código em `src/Eteris.Shields`, também MPL-2.0).

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
