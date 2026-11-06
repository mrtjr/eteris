# Componentes de terceiros

O Éteris distribui os seguintes componentes de terceiros, cada um sob a sua própria licença.

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
| QRCoder (códigos QR) | https://github.com/codebrainz/QRCoder | MIT |
| Microsoft.Data.Sqlite / SQLite | https://github.com/dotnet/efcore · https://sqlite.org | MIT · domínio público |
| .NET | https://github.com/dotnet/runtime | MIT |
