# Escudos: páginas que ficavam incompletas

Estado: **corrigido para a 0.2.17** (2026-11-12). Um sub-recurso bloqueado deixa de ser cancelado
(`CefReturnValue.Cancel` → `net::ERR_ABORTED`) e passa a falhar com `net::ERR_BLOCKED_BY_CLIENT` por um tratador local,
sem ir à rede: `Shields/BlockedByClientHandler.cs` e `PrivacyResourceHandler.Settle`/`ContinueAfterBlock` em
`Shields/PrivacyRequestHandler.cs` (testes em `BlockedByClientTests`). Os frames e os websockets continuam cancelados.
A marca do pedido é o identificador e o endereço (o identificador do CEF é o `request_id` do Blink, único só dentro
de cada processo da página), e nos bloqueios tardios (CNAME, rede local) só se marca um pedido ainda à espera.

**Prova ao vivo** (build de desenvolvimento, perfil de teste, servidor local com contagem de ligações, nomes reais de
rastreadores mapeados para 127.0.0.1): nenhum de ~20 tipos de pedido bloqueado chegou ao servidor (script, fetch,
XHR, imagem, `sendBeacon`, CSS, fonte, prefetch, EventSource, workers, service worker, redireções 302, https); todos
falham com `ERR_BLOCKED_BY_CLIENT`, e o iframe e o `<object>` com `ERR_ABORTED`, como antes. O contador do Escudo deu
o mesmo total que antes. Com o Escudo ligado: ge Flamengo 2939 → 14444 px, ge 2797 → 14544 px, g1 3197 → 14573 px,
com os rastreadores bloqueados; www.globo.com, uol, folha e publico.pt iguais.

**Fica por corrigir (anterior a esta mudança, igual na 0.2.16):** um **WebSocket** (`ws://`) para um rastreador
bloqueado chegou ao servidor na prova, e não entra na contagem do Escudo — o tratador ou não vê o pedido ou deixa-o
passar. A investigar à parte.

O resto deste registo é a investigação, tal como ficou quando estava em pausa.

## O sintoma

O dono viu `https://ge.globo.com/futebol/times/flamengo/` incompleta no Éteris: cabeçalho, jogos do dia e destaques,
mas depois quase nada. Medido na 0.2.16 publicada, num perfil de teste, com o DevTools do motor por CDP
(`ETERIS_TEST_DEVTOOLS_PORT`), comparando com o Edge headless:

| Página | Éteris, Escudos ligados | Éteris, Escudos desligados | Edge |
|---|---|---|---|
| ge.globo.com (Flamengo) | 2 939 px, 586 links | 14 447 px, 648 links | 12 881 px, 648 links |
| ge.globo.com (início) | 2 797 px, 590 links | 11 759 px, 665 links | 14 294 px, 658 links |
| g1.globo.com | 3 197 px, 686 links | 10 919 px, 774 links | 10 908 px, 781 links |

Com os Escudos ligados aparecem só 5 notícias e o «Mostrar mais»; faltam a coluna da direita (classificações, jogos,
blogs), o carrossel de vídeos, o «Meu time do coração» e as dezenas de notícias seguintes.

Sem diferença que se veja (altura e links parecidos com e sem Escudos): www.globo.com, oglobo.globo.com,
gshow.globo.com, uol.com.br, terra.com.br, estadao.com.br, folha.uol.com.br, publico.pt, record.pt e abola.pt.

## O que já se sabe sobre a causa

1. **Não é a lista do que se bloqueia, é a forma de bloquear.** Os Escudos cancelam o pedido em
   `src/Eteris.Browser/Shields/PrivacyRequestHandler.cs` (`return CefReturnValue.Cancel`), e a página vê-o como
   `net::ERR_ABORTED`. Um bloqueador de extensão (e o Edge com os mesmos pedidos bloqueados) falha-o com
   `net::ERR_BLOCKED_BY_CLIENT`.
2. **Prova A/B no ge.globo.com, com os Escudos desligados** e os mesmos 27 pedidos de anúncios e medição falhados à mão
   por CDP (`Fetch.failRequest`):
   - com `errorReason: Aborted` (como os Escudos): 3 479 px, 586 links — **incompleta**, igual aos Escudos ligados;
   - com `errorReason: BlockedByClient`: 14 953 px, 645–648 links — **completa**.
   A mesma lista bloqueada no Edge também dá a página completa (15 219 px).
3. **No g1.globo.com basta um pedido:** com os Escudos desligados, falhar só `https://horizon-track.globo.com/id`
   - como `Aborted`: 3 987 px — incompleta (o pedido é tentado 2 vezes);
   - como `BlockedByClient`: 10 819 px — completa (o pedido é tentado 7 vezes e a página segue em frente).
   Ou seja, o código da Globo trata um `ERR_ABORTED` neste pedido de identificação como «esperar» e não continua.
4. Não são a causa (medido no ge.globo.com, tudo continua incompleto): desligar o CNAME uncloak, aceitar cookies de
   terceiros, e desligar as camadas do Éteris por cima do motor (`ETERIS_TEST_PURO=1`: sem sessões DevTools, logo sem
   scriptlets, e sem mídia, Mica, janela em camadas e animações) — só o bloqueio de pedidos continuava ligado.
5. A bissecção dos 27 pedidos no ge.globo.com (com `Aborted`) ficou a meio: há subconjuntos que, sozinhos, já deixam a
   página incompleta; não se chegou ao pedido exato. É provável que seja o mesmo `horizon-track.globo.com/id` (está na
   lista), mas **não está provado** para o ge.

Os 27 pedidos que os Escudos bloqueiam no ge.globo.com são de anúncios e medição: `ads-library.globo.com`,
`horizon.globo.com/auth-session/activity/…`, `horizon-track.globo.com/configs` e `/id`,
`trackid.globoid.globo.com`, `securepubads.g.doubleclick.net`, `pagead2.googlesyndication.com`,
`imasdk.googleapis.com`, `www.googletagmanager.com`, `analytics.google.com`, `connect.facebook.net`,
`static.chartbeat.com`, `sb.scorecardresearch.com`, `sdk.mrf.io`, `www.clarity.ms`, `sentry.globoi.com`,
`*.edge.permutive.app` e outros do mesmo tipo.

## Onde retomar

1. Confirmar no ge.globo.com que falhar só `horizon-track.globo.com/id` como `Aborted` chega para a deixar incompleta
   (o mesmo A/B do g1).
2. Ver como fazer o CEF/CefSharp 156 devolver `ERR_BLOCKED_BY_CLIENT` (-20) em vez de `ERR_ABORTED` (-3) a um pedido
   bloqueado, sem o deixar sair para a rede. Candidatos a medir: no `GetResourceRequestHandler`/`OnBeforeResourceLoad`
   em vez de `Cancel`, um `IResourceHandler` que falha com `ERR_BLOCKED_BY_CLIENT`; ou responder localmente (sem rede)
   com um corpo vazio. Cada opção tem de manter o pedido fora da rede e a contagem do Escudo.
3. A correção fica provada por um teste unitário do código de erro escolhido e pela prova ao vivo: ge.globo.com e
   g1.globo.com completos com os Escudos ligados, e os mesmos pedidos bloqueados (nenhum chega à rede).
4. Repetir a medição nos outros sites da tabela, para ver que nada piora.

Método usado: Éteris publicado com `--user-data-dir` temporário, `ETERIS_TEST_DEVTOOLS_PORT` e `ETERIS_TEST_MUTE`,
uma sonda por CDP (scroll até ao fim, `scrollHeight`, contagem de links, `Network.loadingFailed`) e, para o A/B,
`Fetch.enable` + `Fetch.failRequest` com o `errorReason` em teste; Escudos desligados com uma exceção do site no
perfil de teste. O Edge headless serviu de referência.
