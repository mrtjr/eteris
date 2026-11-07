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
