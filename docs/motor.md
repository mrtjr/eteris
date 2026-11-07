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
