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
