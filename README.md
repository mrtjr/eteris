<p align="center"><img src="docs/brand/eteris-512.png" width="128" alt="Ícone do Éteris: um anel com as cores da íris à volta de um núcleo luminoso"></p>

# Éteris

Navegador web privado para Windows, com bloqueio de rastreadores e anúncios ativo por defeito, páginas internas cuidadas e uma interface que se adapta a cada site. Sem telemetria: por omissão tudo fica no computador; a sincronização entre computadores é opcional, cifrada de ponta a ponta e desligada por omissão.

## Instalar

Descarrega `EterisBrowser-win-Setup.exe` do [último release](https://github.com/mrtjr/eteris/releases/latest) e executa-o. Instala por utilizador, sem permissões de administrador, e atualiza-se sozinho.

## O que o Éteris faz

**Privacidade**
- Escudo com as listas do uBlock Origin (motor adblock-rust, ~200 mil regras), scriptlets e regras cosméticas; avisos de cookies removidos.
- Páginas isoladas (sandbox), como no Chrome, no Firefox e no Brave: cada página corre fechada, sem acesso aos seus ficheiros nem ao sistema; se o isolamento falhar num computador, o Éteris abre sem ele e avisa (`docs/sandbox.md`).
- HTTPS primeiro, cookies de terceiros bloqueados, WebRTC sem fuga de IP, Global Privacy Control (GPC).
- Vídeos com som automático bloqueados até um clique.
- «Esconder elemento»: clique num anúncio ou caixa e desaparece nesse site para sempre.
- Janelas privadas com perfil em memória; «Esquecer os últimos 15 min / 1 hora»; apagar histórico, cookies e cache.
- Painel semanal na nova aba: rastreadores bloqueados por dia e estimativa do que foi poupado.
- Observatório do site (Ctrl+Shift+U): quem está a observar a página, ao vivo, agrupado por empresa, com «Porque é que isto passou?» e bloquear um domínio só nesse site; no cadeado, «O que este site sabe de ti».
- `eteris://privacy/`: relatório dos últimos 7 e 30 dias (só números, neste computador, apagáveis), promessas verificadas no estado real e um auto-teste local do que um site vê.
- DNS cifrado, links sem rastreio, modo rigoroso por site, notificações recusadas sem perguntar, apagar dados ao fechar.

**Palavras-passe e preenchimento automático**
- Gestor de palavras-passe e preenchimento automático próprios (`eteris://passwords/`, `eteris://autofill/`), com o gestor do Chromium desligado: nada vai para a Google, o cofre fica cifrado neste computador, mostrar, copiar e exportar pedem o Windows Hello.
- Nas páginas: a lista do Éteris por baixo do campo preenche só quando escolhe; oferece guardar ou atualizar depois de entrar num site; gera palavras-passe fortes nos formulários de conta nova; na janela privada preenche mas não guarda nada.
- Importa o CSV do Chrome, do Brave, do Firefox, do Bitwarden e do 1Password e exporta no formato do Chrome; cartões (nunca o código de segurança), IBAN, moradas de Portugal e do Brasil, documentos (passaporte, carta de condução/CNH, Cartão de Cidadão/RG, NIF/CPF) e viagens.
- Com a sincronização ligada, passa para os outros computadores com uma segunda cifra por registo; Pagamentos e Documentos de identificação ficam de fora até os ligar (`docs/palavras-passe.md`).

**Sincronização (opcional)**
- «Entrar com o Google» (no navegador do sistema): favoritos, Espaços, definições, decisões do Escudo por site, notas e o cofre de palavras-passe passam de um computador para o outro, **cifrados de ponta a ponta** — a chave nunca sai dos seus aparelhos e a Google só vê ficheiros cifrados, os tamanhos e as horas, na pasta oculta da aplicação no seu Google Drive. Histórico, abas abertas, cookies e chaves de acesso não sincronizam.
- Um computador novo entra por aprovação noutro (o mesmo código nos dois ecrãs) ou pelo código de recuperação; remover um aparelho troca as chaves; «Apagar os meus dados do Éteris no Google Drive» apaga a cópia na nuvem. Desligada, nada sai para a Google (`docs/sincronizacao.md`).

**Barra de endereço**
- Contas e conversões instantâneas (`(12+8)*1,5`, `10 km em milhas`, `30 °C em F`).
- Bangs: `!w`, `!yt`, `!gh`, `!map`, `!r`, `!so`… (escreva `!` para ver a lista).
- Sugestões de histórico, favoritos e abas abertas («Mudar para este separador»); sugestões do motor opcionais.
- Painel do site (cadeado): ligação, permissões revogáveis, apagar cookies e dados do site.

**Abas e janelas**
- Abas verticais (encolhíveis) ou no topo; ecrã dividido lado a lado ou empilhado (abrir um link ao lado, arrastar uma aba até à margem, trocar lados), lembrado na sessão.
- Abas fixadas, a dormir (libertam memória; também sozinhas, uma de cada vez, quando falta memória ao Windows), silenciar, arrastar, cartão ao passar o rato; nome próprio, emoji, cor e nota «porque abri isto?» por aba (ficam na sessão e nas sessões guardadas); «Trancar» (não fecha por engano nem adormece), «Feito» (sai da faixa para «Feitas hoje» no Radar) e «Adiar até…» (volta à hora escolhida, com aviso; «Adiadas» no Radar); Arquivo automático das abas sem visita há N dias (desvanecem e, depois de escolher o prazo em Definições › Abas, vão para «Arquivo» no Radar, de onde se repõem).
- Multi-seleção de abas (Ctrl/Shift+clique) com ações em bloco: copiar links (texto e HTML), agrupar, mover para uma janela, adormecer, trancar, fechar.
- Espaços: ambientes de abas na mesma janela, cada um com nome, cor e emoji, a sua faixa de abas, aba ativa e ecrã dividido (chips na barra de título, Ctrl+Alt+1…9, «Mover para o Espaço…» no menu da aba ou arrastar até ao chip); as abas dos outros Espaços dormem (menos as trancadas e as que tocam som), a cor do Espaço tinge a barra e tudo fica na sessão; «Noutros Espaços» no Radar.
- Paleta de comandos (Ctrl+K), folha de atalhos (F1), abas fechadas recentemente.
- A janela lembra-se do tamanho e posição; links de outras apps abrem na janela aberta; lista de atalhos na barra de tarefas.
- Grupos de abas com nome e cor (pré-visualização ao pairar na etiqueta); sessões guardadas; fechar duplicadas e ordenar por site; reabrir janela fechada.
- Centro de multimédia (♫) com tudo o que toca; janela flutuante sempre por cima (Ctrl+Shift+M).

**Ler e ver**
- Espreitar um link (Alt+clique): título, imagem, tempo de leitura e primeiros parágrafos num cartão, sem abrir aba nem deixar rasto.
- Modo de leitura (F9) com temas escuro, sépia e claro, «Ouvir» em voz alta (vozes locais) e lista «Ler mais tarde».
- Barra lateral (Ctrl+Shift+B) com Notas, Para ler, Favoritos, Histórico e sites fixados que abrem dentro da barra.
- Notas ao lado da página (Ctrl+Shift+E), código QR da página, escurecer sites, links para um destaque.
- Zoom memorizado por site, ecrã inteiro para vídeos, captura da página inteira (Ctrl+Shift+S).
- Tema escuro, claro ou do Windows; material Mica do Windows 11, cor da interface adaptada ao site.
- Nova aba personalizável (5 temas de fundo), páginas `eteris://history`, `eteris://bookmarks`, `eteris://settings`.

## Desenvolvimento

```
dotnet build
dotnet test
```

## Licença

O Éteris é software livre: pode usá-lo, estudá-lo, alterá-lo e redistribuí-lo nos termos da
[GNU GPL versão 3 ou posterior](LICENSE). Os componentes de terceiros e as respetivas licenças estão em
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) e na pasta [licenses/](licenses/INDEX.md).

Design e decisões: [docs/superpowers/specs/2026-11-05-eteris-browser-design.md](docs/superpowers/specs/2026-11-05-eteris-browser-design.md).
Alterações: [CHANGELOG.md](CHANGELOG.md).
