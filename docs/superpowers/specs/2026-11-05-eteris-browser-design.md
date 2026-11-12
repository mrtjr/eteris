# Éteris — navegador web privado + Atenix (metabusca) — Design

> **Documento histórico (2026-11-05).** Descreve o desenho inicial; partes foram abandonadas (o motor de pesquisa Atenix, o «DNT: 1», o «sem contas», a sincronização e o gestor de palavras-passe fora do âmbito). O estado atual está em `docs/motor.md`, `docs/sandbox.md`, `docs/palavras-passe.md`, `docs/sincronizacao.md` e `docs/paridade.md`.

Data: 2026-11-05
Estado: aprovado em conversa; aguarda revisão escrita

## 1. Objetivo

Um navegador para Windows, distribuível por instalador, com identidade própria,
que amigos e público possam instalar e usar no dia a dia **sem rastreio**.
Pesquisar a partir do navegador vai para um motor de metabusca privado com a
nossa marca.

- Navegador: **Éteris** (Éter + Íris — luz celestial + mensageira/arco-íris).
- Motor de busca: **Atenix** (Atena + Nike — sabedoria + vitória).
- Pasta do projeto: `%USERPROFILE%\Desktop\navegador` (repositório git próprio).

Referências de produto: Chrome (omnibox, abas), Firefox (privacidade por
defeito), Brave (Shields com contador, bloqueio em Rust). **São as únicas
referências** em comparações, metas de desempenho e decisões de design — o Edge
não é referência (decisão do dono do projeto, 2026-11-06). Menções técnicas
continuam: o motor de vídeo usa o WebView2 da Microsoft e os favoritos podem ser
importados de perfis do Edge.

### Restrições

- Orçamento: **zero** por agora. Tudo tem de correr em níveis gratuitos.
- Equipa: Claude Code implementa; o dono do projeto revê e decide.
- Plataforma: Windows 10/11 x64 apenas no primeiro lançamento.
- Toolchains presentes: .NET SDK 10, Node 22, git. **Rust (rustup) tem de ser
  instalado antes da fase Shields.**

### Critérios de sucesso do MVP

1. Instalar com um `Setup.exe` sem permissões de administrador e receber
   atualizações automáticas.
2. Navegar com abas, barra de endereço, favoritos, histórico e downloads.
3. Bloqueio de rastreadores/anúncios ligado por defeito, com contador visível.
4. Pesquisar na barra abre resultados no Atenix.
5. Nenhum dado do utilizador sai da máquina além dos pedidos que o próprio
   utilizador faz (páginas que visita, pesquisas que escreve).

## 2. Abordagem escolhida

Replicar a arquitetura do Brave (Chromium + bloqueador em Rust + páginas
internas em tecnologias web) **sem fazer fork do Chromium**:

| Camada | Escolha | Porquê |
|---|---|---|
| Motor de renderização | Chromium via **CEF / CefSharp.Wpf.NETCore** | Blink + V8 iguais ao Chrome; atualizações mensais via NuGet, sem compilar Chromium |
| Shell do navegador | **C# / .NET 10 / WPF** | Ecossistema já usado pelo dono; instalador e integração Windows nativos |
| Páginas internas | **HTML/CSS/TypeScript** servidas por esquema `eteris://` dentro do CEF | Mesmo padrão WebUI do Chrome/Brave |
| Bloqueio | **adblock-rust** (motor do Brave Shields) embrulhado numa DLL com C-ABI | Filtragem em milissegundos, listas EasyList/EasyPrivacy |
| Motor de busca | **SearXNG** com tema Atenix, num VM **Oracle Cloud Always Free** | Metabusca privada provada, sem APIs pagas |
| Instalador / updates | **Velopack** + GitHub Releases | Gratuito, instalação por utilizador, auto-update |

Alternativas rejeitadas: fork do Chromium (builds de horas, C++ pesado,
re-sincronização mensal); Electron (sai do C#, +RAM); motor de busca próprio
desde o início (scraping frágil, semanas para igualar o SearXNG).

**Pós-lançamento (aprovado):** migrar o Atenix de SearXNG para um serviço
próprio em ASP.NET Core (ver §11).

## 3. Estrutura do repositório

```
navegador/
├── Eteris.sln
├── src/
│   ├── Eteris.Browser/      # WPF + CefSharp — o navegador
│   ├── Eteris.Shields/      # Rust crate → eteris_shields.dll (C-ABI)
│   └── Eteris.WebUI/        # Vite + TypeScript — páginas eteris://
├── atenix/                  # SearXNG: settings.yml, tema, docker-compose, deploy
├── tests/
│   ├── Eteris.Browser.Tests/   # xUnit
│   └── Eteris.WebUI.Tests/     # Playwright
├── .github/workflows/       # CI + release
└── docs/superpowers/specs/  # esta spec e futuras
```

Cada componente compila e testa-se sozinho. O browser depende do artefacto
dos outros dois (DLL e pasta `dist/` do WebUI), copiados para o output no
build.

## 4. Eteris.Browser (C# / WPF / CefSharp)

### 4.1 Janela principal

- Barra de abas no topo (estilo Chrome), botão `+`, fecho por aba, arrastar
  para reordenar.
- Barra de ferramentas: voltar, avançar, recarregar/parar, **omnibox**,
  ícone **Shields** com contador de bloqueios da aba atual, botão favoritos
  (estrela), menu `⋮` (nova janela, histórico, favoritos, downloads,
  definições, sobre, sair).
- Atalhos: Ctrl+T/W/Tab/Shift+Tab, Ctrl+L, Ctrl+D, Ctrl+H, Ctrl+J, Ctrl+R,
  F5, Alt+←/→, Ctrl+1..9, Ctrl+Shift+T (reabrir aba fechada), F11.
- Restaura a última sessão (abas abertas) ao arrancar; opção para começar
  limpo.

### 4.2 Abas

- Cada aba é um `ChromiumWebBrowser`; o CEF isola renderizadores por
  processo. Crash de um renderizador afeta só essa aba (mostra página de erro
  interna com botão recarregar).
- Modelo: `TabViewModel { Id, Title, Url, Favicon, IsLoading, CanGoBack,
  CanGoForward, BlockedCount }`. A UI faz binding (MVVM, CommunityToolkit.Mvvm).
- Popups (`window.open`, `target=_blank`) abrem como nova aba, nunca como
  nova janela nativa.

### 4.3 Omnibox

- Entrada única. Regras, por ordem:
  1. Tem esquema (`http://`, `https://`, `eteris://`, `file://`) → navega.
  2. Parece host (`algo.tld`, `localhost`, IP, com ou sem caminho) sem espaços
     → navega com `https://`.
  3. Caso contrário → pesquisa: `{motor.urlTemplate}` com `{q}` codificado.
- Sugestões apenas de **histórico e favoritos locais** (prefixo e
  subsequência no título/URL, máx. 8). **Nunca envia teclas a servidor
  nenhum.**
- Mostra cadeado/aviso conforme o estado TLS que o CEF reporta.

### 4.4 Dados locais

- SQLite em `%LocalAppData%\Eteris\eteris.db` (Microsoft.Data.Sqlite).
  Tabelas: `history(id, url, title, visited_at)`, `bookmarks(id, url, title,
  folder, position, created_at)`, `downloads(id, url, path, size, state,
  started_at)`, `settings(key, value)`, `shield_exceptions(host)`,
  `session_tabs(position, url)`.
- Cache, cookies e localStorage ficam no cache do CEF em
  `%LocalAppData%\Eteris\profile\`.
- "Limpar dados" apaga histórico/cookies/cache/downloads (lista), mantém
  favoritos e definições.

### 4.5 Privacidade por defeito (configuração do CEF)

- Envia `DNT: 1` e `Sec-GPC: 1`.
- Cookies de terceiros bloqueados (política via `IRequestHandler` +
  `CookieManager`).
- WebRTC com política `disable_non_proxied_udp` (não revela IP local).
- Sem telemetria, sem crash reporting externo, sem contas.
- User-Agent padrão do Chromium (não alterado, para não ser identificável).
- Motor de busca padrão: Atenix. Trocável nas definições (DuckDuckGo, Brave,
  Startpage como presets; personalizado por URL com `{q}`).

### 4.6 Downloads

- `IDownloadHandler` do CEF → diálogo "Guardar como" na primeira vez (ou
  pasta padrão `Transferências`), barra de progresso na página
  `eteris://downloads`, abrir ficheiro / abrir pasta ao terminar.

### 4.7 Instalador e atualizações

- Velopack: `vpk pack` com `packId` **`EterisBrowser`** gera
  `EterisBrowser-win-Setup.exe` (instala em `%LocalAppData%\EterisBrowser`,
  sem admin) e pacotes delta. O `packId` é deliberadamente diferente da
  pasta de dados `%LocalAppData%\Eteris`: o Velopack apaga a pasta de
  instalação ao desinstalar, e os dados do utilizador têm de sobreviver.
- Ao arrancar, verifica GitHub Releases do repositório; se houver versão
  nova, descarrega em segundo plano. O Velopack **não aplica sozinho**: ao
  sair, a app chama `WaitExitThenApplyUpdates` e o `Update.exe` troca a
  pasta `current\` depois de o processo terminar, pelo que o próximo
  arranque já é a versão nova (aviso discreto no menu `⋮`, com
  "Reiniciar para atualizar" via `ApplyUpdatesAndRestart`).
- Falha de rede/update → silenciosa; tenta na próxima abertura.

## 5. Eteris.Shields (Rust)

### 5.1 Crate

- `src/Eteris.Shields/` — crate `eteris_shields`, `crate-type = ["cdylib", "rlib"]`,
  dependência `adblock` 0.13 (Brave) **sem** a feature `single-thread` (o CEF
  consulta o motor a partir de várias threads). Target `x86_64-pc-windows-msvc`.
- O Rust só é preciso para compilar: a build do `Eteris.Browser` chama o
  `cargo` e copia `eteris_shields.dll` para a saída e para o instalador. Os
  utilizadores não instalam nada.
- C-ABI exportada (nenhum pânico atravessa a fronteira; em erro: não bloquear / NULL):

```c
Shields* shields_new(const char* rules_utf8);                       // listas concatenadas
int   shields_check(Shields*, const char* url, const char* source_url,
                    const char* resource_type);                     // 1 = bloquear
char* shields_url_css(Shields*, const char* url);                   // regras ##específicas do site
char* shields_generic_css(Shields*, const char* url,
                          const char* classes, const char* ids);    // regras ##.classe/###id genéricas
void  shields_string_free(char*);
void  shields_free(Shields*);
```

- O motor carrega ~174 mil regras em ~75 ms; não é preciso cache serializada.

- `resource_type` segue os nomes do adblock-rust (`script`, `image`,
  `stylesheet`, `xmlhttprequest`, `sub_frame`, `font`, `media`, `other`),
  mapeados a partir de `CefSharp.ResourceType`.

### 5.2 Listas

- As listas que o **uBlock Origin Lite** ativa por omissão — uBlock filters
  (filters, privacy, unbreak, badware, quick-fixes), EasyList, EasyPrivacy —
  mais a EasyList Portuguesa. Ficam de fora a URLhaus (11,8 MB) e a de Peter
  Lowe (termos de uso pouco claros); licenças em `THIRD-PARTY-NOTICES.md`.
- Uma cópia vai junto do exe (`Shields/lists/`) para o primeiro arranque offline.
- Atualização semanal em segundo plano para `%LocalAppData%\Eteris\filters\`;
  respostas inválidas (páginas de erro, ficheiros curtos) nunca substituem a
  cópia anterior. Nunca arranca sem escudo: até o motor carregar (ou se a DLL
  faltar) usa a lista mínima em C# (`TrackerBlocker`).

### 5.3 Integração no browser

- `IResourceRequestHandler.OnBeforeResourceLoad` → P/Invoke `shields_check`
  com o tipo de recurso e o URL do documento que fez o pedido (pode ser um
  iframe). Bloqueado → `CefReturnValue.Cancel` + `BlockedCount++` na aba.
- Regras específicas do site: injetadas em `OnContextCreated` (antes de os
  anúncios aparecerem). Regras genéricas: no `FrameLoadEnd`, a partir das
  classes e ids presentes na página, e de novo 2,5 s depois.
- Pop-ups: sem gesto do utilizador são sempre bloqueados; com gesto, o destino
  passa pelo motor como navegação (o adblock-rust ainda não implementa `$popup`).
- **Scriptlets** do uBlock (`##+js(...)`): os recursos são gerados por
  `build/scriptlets/build-scriptlets.mjs` a partir do código do uBlock Origin
  (excluindo os "trusted") e vão no instalador; mudam só com novas versões do
  Éteris, nunca por download em tempo de execução (são código injetado nas
  páginas). São registados via DevTools `Page.addScriptToEvaluateOnNewDocument`
  e o pedido do frame principal fica suspenso (`ContinueAsync`, máx. 1 s) até
  o registo estar confirmado, para correrem antes do primeiro script da página.
  O invólucro define `scriptletGlobals` (como o uBlock) e só corre no frame de
  topo do domínio para o qual foi calculado.
- Para regenerar os scriptlets: descarregar a tag do uBlock Origin e correr
  `node build/scriptlets/build-scriptlets.mjs <pasta> src/Eteris.Browser/Shields/resources/scriptlets.json`.
- Exceções por host (`shield_exceptions`) desligam a verificação nesse host;
  toggle no popup do ícone Shields.
- Níveis: **padrão** (listas acima), **agressivo** (+ listas de anúncios
  regionais e anti-anti-adblock), **desligado**.

## 6. Eteris.WebUI (HTML/CSS/TypeScript)

- Vite + TypeScript + Preact (leve). Build para `dist/`, embebido no exe e
  servido por `ISchemeHandlerFactory` no esquema `eteris://`.
- Páginas:
  - `eteris://newtab` — caixa de pesquisa Atenix, 8 atalhos "mais visitados"
    (do histórico), contador "rastreadores bloqueados hoje", relógio.
  - `eteris://settings` — motor de busca padrão, tema (sistema/claro/escuro),
    nível Shields, página inicial, restaurar sessão, limpar dados, sobre.
  - `eteris://history` — lista por dia, pesquisa, apagar item/dia/tudo.
  - `eteris://bookmarks` — pastas, arrastar, editar, apagar.
  - `eteris://downloads` — lista com progresso, abrir, mostrar na pasta.
  - `eteris://error` — página de erro (DNS, TLS, renderizador caiu, Atenix
    indisponível) com ações.
- Ponte JS↔C#: objeto `window.eteris` registado via `JavascriptObjectRepository`
  (CefSharp), API tipada em `eteris.d.ts`:
  `history.list/search/delete`, `bookmarks.*`, `downloads.*`,
  `settings.get/set`, `shields.stats`, `navigate(url)`.
- O esquema `eteris://` só é acessível a partir de páginas `eteris://`
  (verificação de origem no handler) — páginas da web não conseguem chamar
  `window.eteris`.

## 7. Atenix (SearXNG)

- `atenix/` contém: `settings.yml` (nome "Atenix", idioma padrão `pt-PT`,
  motores ativos: Google, Bing, DuckDuckGo, Brave, Wikipedia, Wikidata;
  categorias gerais/imagens/notícias; `image_proxy: false`; sem Matomo/stats
  externas), tema `atenix` (cores e logo da família Éteris, derivado do tema
  `simple`), `docker-compose.yml` (SearXNG + Caddy com HTTPS automático +
  Valkey para rate-limit), `deploy.sh` (idempotente; instala Docker, sobe os
  serviços, configura firewall).
- Hospedagem: VM Oracle Cloud Always Free (Ampere A1, até 4 OCPU/24 GB).
- Domínio: começar com **DuckDNS** (`atenix.duckdns.org`, grátis); o URL do
  Atenix no browser é uma definição, por isso mudar para um `.com` depois é
  só trocar o valor padrão.
- Privacidade: SearXNG não guarda pesquisas; `limiter` ligado para evitar
  abuso; sem cookies de rastreio.
- Formato JSON ativo (`/search?q=…&format=json`) para o smoke test e para a
  futura migração (§11).

## 8. Fluxo de dados principal

1. Utilizador escreve "receita de bacalhau" → Omnibox classifica como
   pesquisa → aba navega para
   `https://atenix.duckdns.org/search?q=receita+de+bacalhau`.
2. Cada pedido de rede passa por `OnBeforeResourceLoad` → `shields_check` →
   cancela ou deixa passar → contador da aba atualiza.
3. Frame principal termina de carregar → CSS cosmético injetado → histórico
   gravado (`url, title, visited_at`).
4. `eteris://newtab` lê os mais visitados do SQLite via `window.eteris`.

Nada deste fluxo contacta outro servidor além do site visitado e do Atenix.

## 9. Erros e casos-limite

| Situação | Comportamento |
|---|---|
| Atenix em baixo / timeout | `eteris://error?kind=search` com botão "pesquisar no motor de reserva" (DuckDuckGo por defeito, configurável) |
| Listas de filtros não descarregam | usa cópia anterior ou embutida; regista aviso em log local |
| Renderizador de uma aba cai | só essa aba mostra `eteris://error?kind=crash`; as outras continuam |
| Atualização falha | mantém versão atual; tenta na próxima abertura |
| DB SQLite corrompida | renomeia para `eteris.db.bak-<data>` e cria nova; favoritos perdidos são avisados |
| URL inválida na omnibox | tratada como pesquisa |
| Download interrompido | estado `failed` na lista, botão repetir |

## 10. Testes

- **Shields (Rust)**: `cargo test` — `doubleclick.net/script` bloqueado,
  `pt.wikipedia.org` permitido, CSS cosmético não vazio para um domínio com
  regras conhecidas; teste de FFI com strings UTF-8 e `NULL`.
- **Browser (xUnit)**: classificador da Omnibox (tabela de casos),
  repositórios SQLite (histórico/favoritos/sessão), `FilterListUpdater`
  (falha de rede mantém cópia), mapeamento `ResourceType`.
- **Fumo (xUnit, categoria `Smoke`)**: arranca o browser headless-ish, abre
  uma página HTML local que referencia `https://doubleclick.net/x.js`,
  confirma `BlockedCount == 1`.
- **WebUI (Playwright)**: cada página contra o `vite dev` com `window.eteris`
  falso; nova aba mostra atalhos; definições persistem via mock.
- **Atenix**: workflow pós-deploy faz `GET /search?q=teste&format=json` e
  exige ≥1 resultado.
- **CI (GitHub Actions)**: `build.yml` em push/PR — dotnet build+test, cargo
  build+test, npm build+test. `release.yml` em tag `v*` — gera instalador com
  Velopack e publica em GitHub Releases.

## 11. Fases de entrega

1. **Esqueleto** — repositório, `Eteris.sln`, projeto WPF + CefSharp a abrir
   uma aba, CI a compilar, Velopack a gerar `Setup.exe` com auto-update
   apontado ao GitHub Releases. *Marco: instalar e abrir uma página.*
2. **Navegador usável** — abas, omnibox com sugestões locais, histórico,
   favoritos, downloads, sessão, atalhos, privacidade por defeito (§4.5).
3. **Shields** — instalar Rust, crate, FFI, interceção, contador, exceções,
   listas embutidas + atualização semanal.
4. **WebUI** — esquema `eteris://`, ponte `window.eteris`, páginas nova
   aba/definições/histórico/favoritos/downloads/erro.
5. **Atenix** — SearXNG temado, deploy Oracle Free + DuckDNS, smoke test,
   Atenix como motor padrão no browser, motor de reserva.
6. **Polimento + lançamento** — tema escuro, ícone/logo, página "sobre",
   README, política de privacidade, primeiro release público `v1.0.0`.

**Pós-lançamento (aprovado): Atenix próprio em ASP.NET Core** — serviço C#
que agrega fontes (Brave Search API quando houver orçamento, Wikipedia,
DuckDuckGo) com cache, mantendo o mesmo contrato `/search?q=&format=json`
para que o browser não mude. Terá spec própria quando chegar a vez.

## 12. Fora de escopo no MVP

Extensões Chrome, sincronização entre dispositivos, gestor de palavras-passe,
perfis múltiplos, modo leitor, tradução, macOS/Linux, pesquisa por voz.
