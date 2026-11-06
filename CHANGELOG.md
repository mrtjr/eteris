# Alterações

## 0.2.0 — por publicar

Primeira ronda de 50 ciclos de melhorias.

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
