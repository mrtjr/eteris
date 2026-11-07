# Programa de 100 ciclos — experiência do utilizador

Pedido do dono do projeto (2026-11-07): «Continue evoluindo o navegador. Inove: pense na experiência do utilizador e
pense fora da caixa. Mudanças arrojadas que melhorem a experiência ou o aspeto geral, não pequenos ajustes. Pelo
menos uma alteração por ciclo; 100 ciclos.»

Regras do programa:
- Cada ciclo termina num commit com os testes verdes (`cycle.sh`); as verificações ao vivo usam só o perfil de teste.
- Referências de produto: Chrome, Firefox e Brave (abertos no computador do dono para comparação por capturas).
- Tudo offline, sem contas, sem telemetria, sem IA remota; licenças registadas (ver `THIRD-PARTY-NOTICES.md`).
- JavaScript nas páginas só via `Browser/PageScript.cs` (regra do sandbox); nada por cima da página fora da camada
  de sobreposição (`MainWindow.Overlay.cs`).
- A cada 25 ciclos: revisão adversarial das alterações e teste ao vivo das funcionalidades novas.

Ponto de partida: v0.2.8 publicada (commit `ad41fc0`).

## Roteiro

Fusão dos três roteiros dos juízes (116 ideias recolhidas em seis lentes: referências Chrome/Firefox/Brave,
navegadores inovadores, privacidade, o Éteris hoje, acessibilidade e desempenho, fora da caixa). Os ciclos são
estimativas; cada ciclo é um commit.

### Bloco A — arrojado e visível (ciclos 1–30)
| # | Item | Ciclos |
|---|---|---|
| A1 | Faixa de abas que nunca perde abas: rolagem, setas, lista ▾ e salto para a aba | 2 |
| A2 | Modo compacto: abas e endereço numa só linha; interface que desaparece com zonas quentes | 3 |
| A3 | Cor do Éteris: destaque à escolha, extraída da paisagem ou do Windows | 2 |
| A4 | Espreitar: um link num cartão por cima da página (Alt+clique / segurar), com título, imagem, tempo de leitura e primeiros parágrafos; Abrir / Abrir ao lado / Ler mais tarde | 4 |
| A5 | Ecrã dividido 2.0: abrir ao lado, trocar lados, empilhar, arrastar à margem | 3 |
| A6 | Pré-visualização dos grupos de abas ao pairar | 1 |
| A7 | Abas com nome próprio, emoji, cor e nota («porque abri isto?») | 2 |
| A8 | Abas como tarefas: Feito, Adiar até…, Trancar; Arquivo automático de abas esquecidas | 3 |
| A9 | Multi-seleção de abas (Ctrl/Shift+clique) e «Copiar links» em bloco | 1 |
| A10 | Espaços: ambientes de abas com cor, ícone e estado retomável | 5 |
| A11 | Barra lateral unificada com painéis web (Notas, Para ler, Favoritos, Histórico, sites fixados) | 4 |

### Bloco B — privacidade visível (ciclos 31–65)
| # | Item | Ciclos |
|---|---|---|
| B1 | HTTPS primeiro com aviso (sem recuar para HTTP em silêncio) + página de erro com diagnóstico e nova tentativa | 2 |
| B2 | Avisos com ação («Anular», «Abrir», «Mostrar na pasta») e Ctrl+Z global | 1 |
| B3 | «Esquecer este site ao fechar», botão «Triturar» e «Terminar sessão privada» | 2 |
| B4 | Promessas verificáveis: GPC visível em JS, Privacy Sandbox desligado, Recall desligado | 2 |
| B5 | Fundações de rede: frascos de cookies por site, referrer só origem, Accept-Language mínimo | 2 |
| B6 | Permissão para a rede local: sites públicos não falam com o PC nem com a rede | 2 |
| B7 | Saltar e esquecer redirecionamentos de rastreio (bounce tracking) | 2 |
| B8 | Desmascarar CNAME: rastreadores escondidos atrás de subdomínios do próprio site | 2 |
| B9 | Observatório do site: quem está a observar ao vivo e «o que este site sabe de ti» no cadeado | 3 |
| B10 | Relatório de privacidade legível (eteris://privacy) e auto-teste de privacidade | 3 |
| B11 | Avisos de cookies: carregar em «Rejeitar tudo» por ti, não só esconder | 3 |
| B12 | Contêineres: identidades por cor com cookies separados | 5 |
| B13 | Impressão digital borrada («farbling») com seed por sessão e por site, medida com testes públicos | 4 |
| B14 | Abas efémeras na janela normal («Request-OTR») | 2 |

### Bloco C — fluxos do dia a dia e paridade (ciclos 66–94)
| # | Item | Ciclos |
|---|---|---|
| C1 | Transferências a sério: faixa de progresso, «Guardar como», página própria, pausar/retomar | 3 |
| C2 | Favoritos com pastas, barra de favoritos e a estrela que fala | 3 |
| C3 | Omnibox unificada: âmbitos @abas/@fav/@hist/@notas, comandos com «>» | 2 |
| C4 | Primeiros 5 minutos: navegador predefinido, importar do Firefox (com histórico) | 3 |
| C5 | Definições que faltam (motor personalizado, pasta de transferências, …) + atalhos personalizáveis | 3 |
| C6 | Abas do motor de vídeo de primeira classe (um só contrato de motor: Ctrl+F, zoom, imprimir) | 3 |
| C7 | Vídeo destacável (PiP) real e automático ao mudar de aba | 2 |
| C8 | Leitor de PDF do Éteris (pdf.js) com tema, pesquisa e anotações | 4 |
| C9 | Pré-visualização de impressão com «Imprimir só o artigo» | 2 |
| C10 | Instalar sites como apps na barra de tarefas | 2 |
| C11 | Pequeno Éteris e rotas de links: links de outras apps numa janelinha; «este site abre ali» | 2 |

### Bloco D — conhecimento local, aspeto, acessibilidade (ciclos 95–100 e folga)
| # | Item | Ciclos |
|---|---|---|
| D1 | Histórico como Viagens: miniaturas, sessões agrupadas, vista por site | 3 |
| D2 | Procurar em tudo: índice local (FTS5) do texto das abas e das páginas visitadas | 3 |
| D3 | Destaques persistentes, notas presas às páginas, «Recortar» (Markdown) e «Congelar» (MHTML) | 3 |
| D4 | Afinar este site: cores, fontes, CSS próprio e filtros por aba | 2 |
| D5 | Moldura 100 % por teclado e falada: anel de foco, F6 por zonas, leitores de ecrã, alto contraste, escala | 3 |
| D6 | Movimento: tokens únicos, «reduzir movimento», micro-interações, desfoque real atrás da paleta | 2 |
| D7 | Arranque abaixo de 1 s e poupança de memória sob pressão | 2 |
| D8 | Pausa e Foco: ecrã calmo, sons de foco sintetizados, sessão com temporizador; tempo no ecrã | 2 |
| D9 | Enviar para o telemóvel: partilha por QR na rede local, sem nuvem | 2 |

Deixado de fora por agora (decisão a tomar com o dono): palavras-passe guardadas localmente (DPAPI) — útil,
mas sensível; ver depois do bloco B.

## Diário

| Ciclo | Alteração | Commit | Verificação |
|---|---|---|---|
| 1 | A faixa de abas deixa de encolher as abas até ficarem ilegíveis: a partir de 120 px por aba passa a rolar (roda do rato, setas nas pontas que só aparecem quando há abas escondidas, sombra a indicar «há mais»), a aba ativa é sempre trazida à vista e arrastar uma aba junto à orla rola a faixa. | — | 15 testes `TabLayoutTests`; ao vivo no perfil de teste: 15 abas abertas com Ctrl+T (todas legíveis, seta esquerda ativa, direita inativa no fim), Ctrl+1 rola até à primeira aba (capturas `c1-many-tabs.png`, `c1-first-tab.png`). |
| 2 | Botão «▾» no fim da faixa com a lista de todas as abas pela ordem da faixa: pesquisa sem acentos, a aba ativa já selecionada e à vista («Em uso»), setas e Enter para saltar, Esc para fechar; a partir de 20 abas sugere passar para abas verticais com um clique. | — | 7 testes `TabListMenuTests`; ao vivo no perfil de teste: 21 abas, clique no «▾» (lista com a ativa marcada e a sugestão), ↑↑ Enter salta para a aba dois lugares atrás e a faixa rola até ela (capturas `c2-list.png`, `c2-jumped.png`). |
| 3 | Modo compacto (Definições › Aspeto › Densidade, ou «Modo compacto» na paleta): as abas e a barra de endereço partilham uma só linha de 44 px e a página ganha a altura inteira da barra de título; a aba ativa vira pílula, a barra de endereço tem largura fixa e cresce ao receber o foco, os botões da janela vêm para a mesma linha. A decisão da disposição (topo/lateral/compacto/escondido) passou para `Theme/ChromeLayout.cs`. | — | 8 testes `ChromeLayoutTests` (prioridades, alturas, largura da barra de endereço, definição `density`); ao vivo no perfil de teste: paleta → «Modo compacto», 2 abas, clique na página (capturas `c3-compact.png`, `c3-compact-final.png`), volta a «normal» pela paleta. |
| 4 | Interface que desaparece: em modo foco (F11) e no ecrã inteiro escolhido no menu, as barras aparecem quando o rato toca no topo da janela e escondem-se 0,9 s depois de o deixar (ficam enquanto a barra de endereço tiver o foco ou um menu estiver aberto); Ctrl+L e Ctrl+T mostram-nas por 3 s. O ecrã inteiro pedido por um vídeo fica como está (só Esc). Correção de passagem: uma janela nunca arranca vazia (sessão sem abas e nada para mostrar abria sem aba nenhuma). | — | 8 testes `AutoHideBarsTests` + 2 em `ChromeLayoutTests` (peek) + 1 em `StartupPagesTests`; ao vivo no perfil de teste: F11, rato no topo (barras aparecem, `c4-focus-peek.png`), rato afastado 1,6 s (escondem-se, `c4-focus-hidden-again.png`). |
| 5 | Polimento do modo compacto e da interface que desaparece: as barras deslizam (170 ms) ao aparecer, esconder-se e mudar de densidade em vez de saltar; Alt sozinho mostra-as em modo foco; as sugestões da barra de endereço têm largura mínima (a barra compacta é estreita); folha de atalhos (F1) e «Novidades» descrevem a faixa que rola, o modo compacto e a interface que desaparece. | — | Bateria completa (ShortcutSheetTests garante que o atalho anunciado nas Novidades existe na folha); ao vivo no perfil de teste: F11 → esconder → rato no topo → afastar → F11 → paleta «Modo compacto» (capturas `c5-peek.png`, `c5-hidden-again.png`, `c5-compact.png`), volta a «normal». |
| 6 | Cor do Éteris (Definições › Aspeto): nove cores de íris em amostras redondas, «Da paisagem» e «Do Windows» (lida do registo DWM, sem WinRT); a faixa íris partilhada por toda a interface (logótipo, aba ativa, pílulas, escudo, barra aurora, menus) muda de cor com uma transição de 0,4 s, com tons derivados no círculo cromático e contraste ≥ 3:1 contra a moldura do tema (clareia no escuro, escurece no claro). Comando «Cor do Éteris» na paleta. | — | 14 testes `AccentTests` (paleta, validação da definição, gradiente visível nos dois temas, contraste do texto, cor dominante de uma imagem, cor do Windows em ABGR, HSL); ao vivo no perfil de teste: Coral e «Do Windows» tingem o anel do logótipo (capturas `c6-coral.png`, `c6-windows.png`); volta a Íris. |
| 7 | Cor do Éteris em todo o lado: «Da paisagem» tira a cor dominante da paisagem da nova aba (imagem reduzida a 32 px, lida uma vez), «Do Windows» acompanha as mudanças de cor do sistema na hora, as páginas internas (eteris://) recebem a cor ao serem servidas (variáveis CSS `--iris`/`--accent`) e as já abertas mudam ao vivo sem recarregar (script pelo DevTools), a barra aurora de cada janela acompanha; destaque nas «Novidades». | — | 7 testes `AccentPagesTests`/`WallpaperAccentTests` (injeção só quando a cor não é a original, script sem HTML, as duas paisagens embutidas dão cor, «none» não); ao vivo no perfil de teste: «Da paisagem» tinge o anel e o título «Definições» da página aberta sem recarregar (captura `c7-wallpaper.png`), volta a Íris (`c7-iris.png`). |
| 8 | Fundações do «Espreitar»: `Browser/LinkPreview.cs` extrai de uma página o título, a descrição, a imagem (Open Graph/Twitter/`<title>`/`image_src`, endereços relativos resolvidos, entidades HTML descodificadas), o nome do site, os três primeiros parágrafos legíveis (orçamento de 700 caracteres, corte numa palavra) e o tempo de leitura (230 palavras/min); o script que corre na aba escondida devolve o cabeçalho e o artigo do Readability em JSON. Ainda sem interface. | — | 14 testes `LinkPreviewTests` sobre HTML fixo e JSON fixo; o script entrou em `InjectedScriptsSyntaxTests` (compilado pelo Node). |
| 9 | «Espreitar o link» no menu de contexto de qualquer link web: a página abre numa aba escondida de pré-visualização (sem JavaScript nem imagens, contexto só em memória sem cookies, pop-ups/transferências/menus mudos, nada no histórico) e o Readability corre pelo DevTools; o cartão na camada de sobreposição mostra site, tempo de leitura, título, autor, descrição, imagem og:image (um pedido sem cookies, só se o escudo deixar) e os primeiros parágrafos, com «Ler mais tarde» (escondido na janela privada), «Abrir ao lado» e «Abrir»; Esc, Enter, clique fora ou × fecham, e a página pré-visualizada é largada (`about:blank`). | — | 1 teste `PageMenuTests` (só links http/https, também na janela privada); ao vivo no perfil de teste com páginas locais: menu com «Espreitar o link» (`c9-menu.png`), cartão com imagem/autor/parágrafos (`c9-card.png`), Esc fecha (`c9-closed.png`), «Abrir ao lado» divide o ecrã (`c9-d3.png`). |
| 10 | Alt+clique num link espreita-o: um script do Éteris, registado antes dos da página e em captura na janela, intercepta o clique com Alt (e só Alt, com o botão esquerdo) em links http(s), impede a navegação/transferência que o Chromium faria e avisa a janela, que abre o cartão «Espreitar» (só da página à vista). Enter no cartão abre o link numa aba. O menu do link mostra o gesto «Alt+clique» e a folha de atalhos (F1) ganhou a entrada. | — | 5 testes novos em `LinkPreviewTests` (aviso lido só com endereços web; script só reage a Alt sem outros modificadores, em captura) + 1 em `PageMenuTests`; o script entrou em `InjectedScriptsSyntaxTests`; ao vivo no perfil de teste: Alt+clique no link abre o cartão sem navegar (`c10-altclick.png`), Enter abre o artigo numa aba nova (`c10-enter.png`). |
| 11 | Polimento do «Espreitar»: teclas no cartão (Esc fecha, Enter abre, Shift+Enter abre ao lado, Ctrl+Enter guarda para ler mais tarde — não na janela privada), decididas em `LinkPreview.ActionFor`; o cartão nunca é mais alto do que a área da página (janelas pequenas rolam); mudar de aba fecha-o; dicas dos botões com as teclas; nome para leitores de ecrã; destaque nas «Novidades» e linha no README. Os avisos do script passaram a usar o escape `` (sem caracteres de controlo no código). | — | 8 testes `ActionFor` em `LinkPreviewTests`; `ShortcutSheetTests` garante que o «Alt+clique» anunciado nas Novidades existe na folha; ao vivo no perfil de teste: Alt+clique → Shift+Enter abre o artigo ao lado (`c11-shift-enter.png`). |
| 12 | Ecrã dividido 2.0 (parte 1): «Abrir link ao lado (ecrã dividido)» no menu de qualquer link web; a divisão ganhou orientação — lado a lado ou empilhada (linhas e divisor horizontal) — e «Trocar lados»; selo «Dividido»/«Empilhado» na barra de endereço com menu por baixo (Trocar lados · Empilhar/Lado a lado · Desfazer); os mesmos comandos na paleta. `Tabs/SplitView.cs` guarda a orientação, troca as metades e dá nome à orientação para a sessão. | — | 3 testes novos em `SplitViewTests` (troca, orientação, nome ↔ sessão) + 1 em `PageMenuTests`; ao vivo no perfil de teste: «Abrir link ao lado» divide e mostra o selo (`c12-side.png`), menu do selo (`c12-badge-menu.png`), «Empilhar» (`c12-stacked.png`), «Trocar lados» (`c12-swapped.png`). |
| 13 | O ecrã dividido fica na sessão: `session.json` guarda as duas abas (índices) e a orientação (`Split`), gravado a cada mudança (abrir, trocar, empilhar, desfazer); ao arrancar com «restaurar», a divisão volta como estava, com a mesma ordem das metades (as duas abas acordam). Índices que não sobrevivem à filtragem da sessão (abas não restauráveis) deitam a divisão fora; sessões antigas sem o campo continuam a abrir. Menu do selo encostado por baixo do selo (`DarkMenu.OpenBelow`), sem sair do ecrã. | — | 6 testes novos em `SessionStoreTests` (ida e volta com orientação, remapeamento dos índices, divisão perdida, índices inválidos, compatibilidade, validade); ao vivo no perfil de teste: dividir → empilhar → fechar → `session.json` com `"Split":{"Left":0,"Right":1,"Orientation":"stacked"}` → reabrir restaura empilhado (`c13-restored.png`); menu do selo por baixo (`c13-menu.png`). |
| 14 | Ecrã dividido 2.0 (parte 3): arrastar uma aba até à margem direita da página cria a divisão lado a lado (a arrastada fica à direita) e até à margem inferior cria-a empilhada; uma faixa íris acende na camada de sobreposição enquanto a aba está sobre a margem (`Tabs/SplitDropZone.cs`, lógica pura). O contorno da metade ativa passou para a camada de sobreposição (até aqui ficava por baixo da página — pendente em `docs/paridade.md`, agora resolvido) e acompanha o divisor e a janela. Folha de atalhos, «Novidades» e README atualizados. | — | 12 testes `SplitDropZoneTests` (faixas, cantos, fora da área, orientação, abas iguais/nulas); ao vivo no perfil de teste: arrastar a 2.ª aba até à margem direita acende a faixa (`c14-hint.png`) e larga em ecrã dividido com o contorno íris visível por cima da página (`c14-split.png`); clicar na metade esquerda move o contorno (`c14-ring-left.png`). |
| 15 | Pré-visualização dos grupos ao pairar: passar o rato na etiqueta de um grupo (encolhido ou não) abre, ao fim de 0,35 s, um cartão na camada de sobreposição com o nome e a cor do grupo, o resumo («2 abas · encolhido · 1 a dormir») e um mosaico (até 3 colunas, 12 abas) com a miniatura ou o monograma de cada aba, o ícone e o título — a ativa com contorno íris; clicar num mosaico ativa a aba; o cartão fica enquanto o rato estiver na etiqueta ou nele e desaparece 0,28 s depois de sair, ao clicar na etiqueta ou ao rolar a faixa. A dica da etiqueta passou a linha de ajuda do cartão. Lógica em `Tabs/GroupPreview.cs`. | — | 12 testes `GroupPreviewTests` (mosaicos pela ordem da faixa e com a ativa marcada, limite de 12, colunas, largura, posição dentro da área, resumo); ao vivo no perfil de teste: grupo com 2 abas, pairar na etiqueta abre o cartão (`c15-hover.png`), clicar no mosaico ativa a aba (`c15-tile-click.png`), grupo encolhido mostra «2 abas · encolhido» (`c15-collapsed-hover.png`), afastar o rato fecha (`c15-hidden.png`). |
| 16 | Abas com nome próprio, emoji e cor (parte 1): «Mudar o nome…» no menu da aba (e «Mudar o nome da aba…» na paleta) abre uma caixa na camada de sobreposição por baixo da aba com o nome, 16 emojis e as 8 cores da paleta dos grupos; emoji e cor aplicam-se ao clicar (o ícone da aba passa a ser o emoji — ou a letra do site — sobre a cor), o nome guarda-se com Enter, Esc ou clicar fora fecha, «Repor» limpa tudo. O nome sobrevive a navegações no mesmo site (e à primeira navegação a partir da nova aba) e cai ao mudar de site; o cartão da aba mostra o nome e, por baixo, o título da página; o Radar, a lista «▾», o filtro lateral e a paleta mostram o nome e continuam a encontrar a aba pelo título real. Tudo fica na sessão (`Metas` em `session.json`, só quando há algo). Regras em `Tabs/TabIdentity.cs`. Correção de passagem: `AddToOverlay` passa a mostrar a camada quando o elemento já entra visível. | — | 11 testes `TabIdentityTests` (normalização, emojis/cores só conhecidos, nome mantido no mesmo site e perdido noutro, nome dado na nova aba fica, Radar mostra o nome e encontra pelo título, sessão guarda/alinha/omite o campo, restauro e fotografia); ao vivo no perfil de teste: menu → «Mudar o nome…», ⭐ e Rosa aplicados na hora, nome escrito e Enter (`c16-filled.png`, `c16-named.png`), reaberto com a identidade restaurada da sessão, navegar dentro do mesmo site mantém o nome (`c16-same-site.png`), ir para eteris://settings volta ao título (`c16-other-site.png`). |
