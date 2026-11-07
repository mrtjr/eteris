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
