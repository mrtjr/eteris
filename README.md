# Éteris

Navegador web privado para Windows, com bloqueio de rastreadores e anúncios ativo por defeito, páginas internas cuidadas e uma interface que se adapta a cada site. Tudo fica no computador: sem contas, sem telemetria.

## Instalar

Descarrega `EterisBrowser-win-Setup.exe` do [último release](https://github.com/mrtjr/eteris/releases/latest) e executa-o. Instala por utilizador, sem permissões de administrador, e atualiza-se sozinho.

## O que o Éteris faz

**Privacidade**
- Escudo com as listas do uBlock Origin (motor adblock-rust, ~200 mil regras), scriptlets e regras cosméticas; avisos de cookies removidos.
- HTTPS primeiro, cookies de terceiros bloqueados, WebRTC sem fuga de IP, «Não rastrear» e GPC.
- Vídeos com som automático bloqueados até um clique.
- «Esconder elemento»: clique num anúncio ou caixa e desaparece nesse site para sempre.
- Janelas privadas com perfil em memória; «Esquecer os últimos 15 min / 1 hora»; apagar histórico, cookies e cache.
- Painel semanal na nova aba: rastreadores bloqueados por dia e estimativa do que foi poupado.

**Barra de endereço**
- Contas e conversões instantâneas (`(12+8)*1,5`, `10 km em milhas`, `30 °C em F`).
- Bangs: `!w`, `!yt`, `!gh`, `!map`, `!r`, `!so`… (escreva `!` para ver a lista).
- Sugestões de histórico, favoritos e abas abertas («Mudar para este separador»); sugestões do motor opcionais.
- Painel do site (cadeado): ligação, permissões revogáveis, apagar cookies e dados do site.

**Abas e janelas**
- Abas verticais (encolhíveis) ou no topo; ecrã dividido com duas abas lado a lado.
- Abas fixadas, a dormir (libertam memória), silenciar, arrastar, cartão ao passar o rato.
- Paleta de comandos (Ctrl+K), folha de atalhos (F1), abas fechadas recentemente.
- A janela lembra-se do tamanho e posição; links de outras apps abrem na janela aberta; lista de atalhos na barra de tarefas.

**Ler e ver**
- Modo de leitura (F9) com temas escuro, sépia e claro.
- Zoom memorizado por site, ecrã inteiro para vídeos, captura da página inteira (Ctrl+Shift+S).
- Material Mica do Windows 11, cor da interface adaptada ao site, tooltips e menus escuros.
- Nova aba personalizável (5 temas de fundo), páginas `eteris://history`, `eteris://bookmarks`, `eteris://settings`.

## Desenvolvimento

```
dotnet build
dotnet test
```

Design e decisões: [docs/superpowers/specs/2026-11-05-eteris-browser-design.md](docs/superpowers/specs/2026-11-05-eteris-browser-design.md).
Alterações: [CHANGELOG.md](CHANGELOG.md).
