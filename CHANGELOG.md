# Alterações

## 0.2.2 — 2026-11-06

Duas rondas de 50 ciclos de melhorias. (A 0.2.0 e a 0.2.1 não chegaram a ser publicadas: o fluxo de publicação falhava — ver em baixo.)

### Licenças e privacidade (preparação para distribuir)
- O Éteris passa a ser software livre sob a **GPL-3.0 ou posterior** (`LICENSE`).
- Pasta `licenses/` com os textos completos de todas as licenças de terceiros (Chromium, CEF, CefSharp, .NET, pacotes NuGet e os 59 crates do motor de bloqueio), gerada por `build/licenses/collect.py` e incluída na instalação. Acrescentados o SQLitePCLRaw (Apache-2.0) e os créditos completos do Chromium, que faltavam.
- Testes que impedem uma dependência de entrar sem o texto da sua licença.
- O motor deixa de descarregar componentes da Google em segundo plano (num perfil novo: 27 → 0, incluindo o módulo DRM Widevine e os modelos de IA); o Widevine que ficou de versões anteriores é apagado no arranque.

### Marca, correções e desempenho
- **Marca do Éteris**: ícone próprio (anel íris à volta de um núcleo luminoso) no executável, janelas, barra de tarefas, instalador e README; gerado por `build/brand/make-icon.ps1`.
- **Clicar numa aba não a ativava** (as abas não recebem foco e o Windows só seleciona itens com foco): corrigido na faixa do topo e na barra lateral.
- **Rolagem e animações a 60 quadros por segundo** (estavam limitadas a 30).
- Primeiro arranque aberto com um endereço mostrava «Novidades» em vez das boas-vindas.

### Leitura e vídeo
- **Modo de leitura com o Readability.js do Firefox** (Mozilla, Apache-2.0): extração e deteção de artigos muito mais fiáveis; a heurística própria fica como reserva (p. ex. em sites com Trusted Types).
- Teste que compila com o Node todos os scripts injetados nas páginas (apanhou um erro de sintaxe no leitor).
- **Motor de vídeo**: os sites que só têm vídeo em H.264/AAC (o Twitch, por exemplo) já tocam dentro do Éteris. A aba deteta sozinha que o vídeo não arranca e passa a usar o WebView2 — o motor do Edge que vem com o Windows e tem as licenças desses formatos —, sem sair da janela; o site fica lembrado. Selo «Motor de vídeo» na barra de endereço para voltar ao motor principal. Nessas abas: as mesmas listas de bloqueio, prevenção de rastreio estrita, sem SmartScreen nem autopreenchimento, e modo InPrivate nas janelas privadas. O YouTube (AV1/VP9) continua no motor principal.

### Bloqueio de anúncios
- **Anúncios pop-under travados**: alguns sites abriam um anúncio numa aba nova a qualquer clique na página (por formulário escondido ou pelo `window.open` de um iframe, para fugir aos bloqueadores). Agora uma aba de outro site só abre se o clique foi num link (ou formulário) para esse site; janelas de início de sessão e de partilha continuam a abrir se o clique foi num botão. Aviso «Anúncio em nova aba bloqueado».
- Os anúncios da PropellerAds (que abrem logo ao carregar no botão do rato, antes de o Éteris saber onde se clicou) também são travados: o pedido da aba espera pelo registo do clique.

### Publicação
- O fluxo de publicação no GitHub falhava no restauro do projeto de testes: a variável `VERSION` do fluxo era lida pelo MSBuild como a versão do projeto (`v0.2.0`, inválida). Renomeada, com um teste que o impede de voltar.
- Dois links abertos de outra aplicação quase ao mesmo tempo: o segundo podia perder-se (apanhava a janela aberta a reabrir o canal entre instâncias). O envio tenta de novo até ao fim do prazo.

### Novo (segunda ronda)
- **Privacidade**: DNS cifrado (Cloudflare, Quad9, Mullvad), links sem parâmetros de rastreio (ao navegar e ao copiar), modo rigoroso por site (sem scripts/frames de terceiros), pedidos de notificações recusados sem perguntar, apagar cookies e dados ao fechar, histórico interno do Chromium desligado.
- **Tema claro** (e «seguir o Windows») em toda a interface e nas páginas internas.
- **Grupos de abas** com nome e cor, encolhíveis, guardados na sessão; «Guardar grupo como sessão».
- **Sessões guardadas**: guardar as abas de uma janela e reabri-las (paleta, separador «Sessões» em `eteris://bookmarks`, painel «Continuar» na nova aba).
- **Arrumar abas**: fechar duplicadas, ordenar por site, filtrar abas na barra lateral, reabrir janela fechada, paleta com abas de todas as janelas.
- **Centro de multimédia**: o que toca em todas as abas, com tocar/pausar, silenciar e ir para a aba.
- **Modo de leitura**: «Ouvir» (leitura em voz alta com vozes locais), lista «Ler mais tarde», preferências de tema/tamanho/letra lembradas.
- **Janela flutuante** (Ctrl+Shift+M), **Notas** ao lado da página (Ctrl+Shift+E), **código QR** da página, **escurecer este site**.
- **Copiar**: link para um destaque (`#:~:text=`), endereço sem rastreio, link Markdown, título e endereço; «Colar e ir» (Ctrl+Shift+V).
- **Favoritos**: importar do Chrome/Edge/Brave e exportar para HTML; guardar página como PDF.
- **Páginas**: boas-vindas no primeiro arranque, «Novidades» uma vez por versão, `eteris://credits`, definições por site com «Repor», procurar nas definições.
- **Nova aba**: «Continuar» e uma dica por dia («Sabia que…?»).
- **Acessibilidade**: todos os botões de ícone com nome para leitores de ecrã.

### Corrigido (segunda ronda)
- O botão do modo de leitura deixava de aparecer (o endereço repetido apagava a deteção).
- O botão direito na etiqueta de um grupo abria o menu da aba (o menu do grupo era inalcançável).
- Sugestões da barra de endereço desalinhadas; sombras dos painéis cortadas em páginas claras.
- «Reabrir janela fechada» punha as abas pela ordem inversa.

### Primeira ronda

### Novo
- **Omnibox**: respostas instantâneas (contas, conversões), bangs (`!w`, `!yt`, `!gh`…), «Mudar para este separador», monogramas nos sites, sugestões do motor de pesquisa (opcionais, desligadas por omissão).
- **Painel do site**: estado da ligação, permissões revogáveis, apagar cookies e dados do site.
- **Zoom por site** com distintivo na omnibox e Ctrl + roda do rato.
- **Menu de contexto** escuro e em português (links, imagens, seleção, edição) e F12, Ctrl+U, Ctrl+P.
- **Páginas internas**: `eteris://history` (pesquisa, dias, apagar), `eteris://settings` / `eteris://about`, `eteris://bookmarks`.
- **Definições**: motor de pesquisa, arranque, abas a dormir, HTTPS primeiro, cookies de terceiros, Mica, posição das abas, vídeos com som automático, apagar dados de navegação.
- **Atualizações**: pílula «Atualizar para x.y.z» que reinicia guardando a sessão; estado na secção Sobre.
- **Aspeto**: material Mica do Windows 11, abas verticais encolhíveis, animação de entrada das abas, tooltips escuras em toda a app.
- **Ecrã dividido**, **modo de leitura** (F9), **ecrã inteiro** para vídeos, **captura da página inteira** (Ctrl+Shift+S).
- **Folha de atalhos** (F1), cartão da aba ao passar o rato, abas fechadas recentemente, «Esquecer» 15 min / 1 hora.
- **Esconder elemento**: escolher um elemento e escondê-lo para sempre nesse site.
- **Nova aba**: painel semanal de privacidade e personalização (5 temas, relógio, sites, painel).
- **Janelas**: tamanho e posição memorizados, links de outras apps abrem na janela aberta, lista de atalhos na barra de tarefas, mover aba para nova janela.
- **Transferências**: progresso no ícone da barra de tarefas e aviso a piscar ao terminar.
- **Robustez**: página «Esta página foi abaixo» quando o processo de uma aba termina.

### Segurança
- Ações das páginas internas protegidas por um token aleatório por sessão.
- Canal entre instâncias limitado ao mesmo utilizador; argumentos validados como num arranque normal.
- Seletores de «Esconder elemento» validados antes de virarem CSS.
