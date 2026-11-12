# Alterações

## 0.2.16 — por publicar

### Segurança

- A cor do tema que um site declara deixa de passar pelo conversor de cores do Windows: um valor forjado podia levar o Éteris a abrir um endereço fora do motor.
- Uma página que faz cair processos do motor já não consegue desligar o isolamento das páginas: as quedas só contam até haver conteúdo web, e depois de um arranque falhado a sessão volta com as abas a dormir.
- As atualizações passam a ser conferidas: o Éteris só descarrega e instala pacotes que constem de uma lista de versões assinada pelo autor, com uma chave que não está no GitHub, e volta a conferir cada pacote depois de o descarregar.

### Privacidade

- Esquecer uma permissão no cadeado, triturar um site ou responder sem «Lembrar» passa a valer também no motor: o site volta mesmo a perguntar. Antes, o motor guardava para sempre cada «Permitir» e cada «Bloquear», incluindo a recusa automática dos pedidos de notificações, que continuava depois de desligar essa opção. «Permitir» sem «Lembrar» vale até fechar o Éteris para notificações e várias transferências, e uma só vez para câmara, microfone, ecrã e localização. Fica de fora a área de transferência: o motor continua a lembrar a decisão dela, mesmo depois de a esquecer no cadeado.

### Conta e sincronização

- **Trocar a chave com mais de dois computadores pede o identificador do kit.** Um aparelho removido que ainda chegasse ao Drive conseguia deixar aos outros uma conta sua; agora, quando há mais de um remetente possível, o computador que ficou espera que escreva os 4 caracteres do kit novo antes de continuar.
- Um aparelho removido que voltasse a pôr no Drive o registo de chaves antigo conseguia manter outro computador a sincronizar com ele, sem aviso. Agora a mensagem da troca de chave vale mesmo com esse registo presente. Se o aparelho removido também apagar essa mensagem antes de o outro computador a ler, isto não chega: use «Perdi um computador / foi roubado», que lhe retira o acesso na Google.
- Um cofre cuja base de dados desapareceu (com a chave no sítio) deixa de ser lido como vazio: a sincronização já não apaga as palavras-passe na conta por causa disso.

### Correções

- O gerador de palavras-passe já não desiste num computador ocupado: os 100 ms do padrão do site contam só as tentativas, não a preparação, e a primeira tentativa faz-se sempre (antes podia aparecer «Este campo não aceita uma palavra-passe gerada» num campo que a aceita).
- Abrir o Éteris outra vez, ou um link noutra aplicação, com uma atualização já descarregada deixou de fechar a janela que estava aberta: a atualização aplica-se ao sair, ou no arranque seguinte quando não há outro Éteris a correr.

## 0.2.15 — 2026-11-12

A versão do **login simples**: «Entrar com o Google» com um clique, a partir do menu, das boas-vindas ou das
Definições, e a sincronização cifrada começa sozinha. Um computador novo pede para entrar sozinho e aprova-se
escrevendo no computador ligado o código que o novo mostra; o código de recuperação passa a **kit de recuperação** de 24
palavras, que se guarda, imprime e volta a ver; «Sair deste computador» deixa de desligar os outros, e há «Retirar o
acesso do Éteris à conta Google» e «Perdi um computador / foi roubado». A chave continua a nunca ir para a Google nem
para o projeto.

### Conta e sincronização
- **Entrar em qualquer sítio.** «Entrar com o Google» na linha da conta do menu ⋯ (que agora entra logo, em vez de abrir
  as Definições), num passo novo das boas-vindas («Já usa o Éteris noutro computador?»), na paleta (Ctrl+K), no aviso
  depois de guardar a primeira palavra-passe nova (uma só vez), nas páginas de palavras-passe e de preenchimento e nas
  Definições. Não na Nova aba e nunca numa janela privada; sem o identificador da aplicação na instalação, nenhum
  aparece. Uma barra na janela do Éteris diz o que está a acontecer («À espera do início de sessão no seu navegador…»,
  «Sessão iniciada como *nome*. A sincronizar…», «Sincronizado.», ou o erro com «Tentar outra vez»), sem roubar o foco
  do teclado.
- **A conta escolhe-se no seu navegador.** O ecrã da Google abre com a escolha de conta (`prompt=select_account`); se a
  Google não devolver a autorização para manter a sessão, o Éteris pede o consentimento uma vez e, sem ela, não aceita
  a sessão. No fim, a página no navegador só responde depois de o Éteris trocar o código e diz o que falta («Pronto.»,
  «Falta um passo no Éteris.», «O Éteris retomou a sincronização.»…); a janela do Éteris pisca na barra de tarefas.
- **Sessão expirada, um aviso.** Quando a Google pede para entrar outra vez, aparece na janela, uma vez por abertura,
  «A sincronização está em pausa: a Google pediu para entrar outra vez.» [Entrar] [Agora não]; «Entrar» pré-escolhe a
  conta e retoma sem kit nem aprovação.
- **Computador novo, aprovado com um número.** Ao entrar num computador sem nenhum estado desta conta, o pedido de
  aprovação abre-se sozinho e o computador novo mostra um código de 6 algarismos; nos computadores ligados aparece uma
  barra (ou, só com o teclado, a mesma caixa nas Definições) onde se escreve esse código. O computador ligado nunca mostra
  o código; três códigos errados recusam; «Não fui eu» recusa e aconselha a mudar a palavra-passe da Google; depois de
  3 pedidos maus em 24 h a barra deixa de abrir sozinha. Com palavras-passe ou outros dados do cofre em jogo, aprovar
  pede a confirmação do Windows depois do código certo. Um computador que já esteve na conta e encontra outra no Drive
  não pede sozinho: avisa que a conta foi apagada ou substituída.
- **Kit de recuperação de 24 palavras.** Criar a conta já não mostra nem bloqueia nada: o kit (24 palavras em
  português) vê-se em Definições › Sincronização com a confirmação do Windows, e guarda-se como ficheiro, imprime-se ou
  copia-se (fora do histórico e da nuvem da área de transferência). O Éteris avisa antes de gravar numa pasta que vai
  para a nuvem. Um lembrete na janela volta de 30 em 30 dias enquanto houver um só computador na conta, com «Não quero:
  aceito que, sem este computador, a cópia na nuvem não se recupera». Os códigos de recuperação antigos continuam
  aceites.
- **O nome do computador** passa a «Éteris em …» nos computadores novos e muda-se em Definições › Sincronização; viaja
  sempre cifrado. Os computadores já ligados mantêm o nome até o mudarem.
- **Pagamentos e documentos são uma escolha da conta.** «Também pagamentos e documentos de identificação» liga cartões,
  IBAN e documentos para a conta, com a confirmação do Windows no computador onde se liga; um computador novo que entra
  depois recebe-os sem pedir de novo; um que já estava ligado recebe uma oferta única («Receber também neste?»).
- **«Sair deste computador», «Retirar o acesso…» e «Perdi um computador».** Sair apaga só deste computador a sessão e a
  conta guardada, sem falar com a Google: os outros continuam ligados, e a chave e os dados ficam aqui (a não ser que
  marque «apagar também a chave»). «Retirar o acesso do Éteris à conta Google» revoga a autorização e desliga todos (o
  Éteris só diz «retirado» quando a Google o confirma). «Perdi um computador / foi roubado» remove-o, troca a chave e
  retira o acesso na Google; o que já estava nesse computador continua lá, e o kit antigo deixa de valer. E, antes de
  entrar, «Começar do zero…» apaga a conta antiga da nuvem e cria uma nova a partir deste computador.
- **Definições › Sincronização mais curta.** Sem sessão: uma frase, «Entrar com o Google» e «Como protegemos os seus
  dados»; com sessão: o estado à frente («Ligada · última sincronização há…») e o resto em «Mais opções».

### Privacidade
- **O que mudou no que o Éteris pede à Google.** Ao voltar à janela do Éteris procura pedidos de computadores novos, no
  máximo uma vez a cada 5 minutos (ou logo, se uma ronda já tiver encontrado um pedido), só com a sincronização ligada e
  nunca antes da primeira ronda; o computador novo, enquanto espera pela aprovação, procura a resposta no Drive de 3 em
  3 s (cada pedido dura 10 minutos e só se renova, até 30 minutos, com o pedido à vista). «Sair deste computador» já não
  revoga e não fala com a Google: a Google continua a listar o Éteris até «Retirar o acesso…». A lista completa está em
  `eteris://privacy/` e em `docs/sincronizacao.md`, «Quando sincroniza».
- **O que vai, o que a Google vê e onde fica a chave**, sempre à vista em `eteris://privacy/#sincronizacao`, mesmo antes
  de entrar. A Google vê o que já via (que usa o Éteris, quando sincroniza, quantos ficheiros e o tamanho aproximado) e
  ainda quando um computador novo pede para entrar e se foi recusado; nunca o conteúdo.
- **O que não mudou.** A chave nunca vai para a Google nem para o projeto; o único âmbito pedido é `drive.appdata`; no
  arranque saem 0 pedidos para a Google; nada é pedido à Google antes de carregar em «Entrar com o Google».
- A política de privacidade e a página do projeto (`docs/site/`) dizem o mesmo, em português e em inglês.

### Por provar com conta real
- A escolha de conta e o token de renovação sem repetir o consentimento, o `login_hint` na reentrada, a página de fim
  nos três navegadores, «Sair deste computador» a deixar o Éteris listado na conta Google e «Retirar o acesso» a
  desligar os outros, e o limite de 100 tokens de renovação por conta: feito e testado com o Google falso dos testes;
  a lista completa está em `docs/sincronizacao.md`, «Por provar com conta real».

## 0.2.14 — 2026-11-09

A versão das **páginas isoladas**. Como no Chrome, no Firefox e no Brave, cada página passa a correr **fechada num
sandbox**, sem acesso aos seus ficheiros nem ao sistema: uma falha explorável no código de uma página deixa de ficar
com os acessos do utilizador. Se o isolamento falhar num computador, o Éteris abre sem ele e avisa. É também uma versão
de **desempenho medido** contra a 0.2.13 e as três referências: menos memória (−11% a −16%), quase nada de processador
com 20 abas paradas e uma janela minimizada que deixa de desenhar. E lima arestas: o menu do Éteris já não deixa
passar o `Alt` para a página e um link aberto logo a seguir ao arranque já não se perde.

### Segurança: páginas isoladas (sandbox)
- **O sandbox sai ligado.** Estava pronto desde a 0.2.12 e desligado até à 0.2.13. Os processos das páginas e do
  armazenamento correm sem privilégios (Untrusted) e a placa gráfica e o áudio com integridade baixa (Low), como no
  Chrome; o serviço de rede corre num AppContainer (no Chromium 156 esta proteção vem desligada por omissão: o Chrome
  só a tem com a experiência ligada); nenhum processo do motor corre com `--no-sandbox`
  (`docs/sandbox.md`; `docs/paridade.md`, secção 2).
- **O Éteris abre sempre.** Se o .NET não arrancar com o sandbox, o Éteris volta a arrancar sem ele; dois arranques
  falhados seguidos (uma queda antes da 1.ª página, ou três processos do motor a cair nos primeiros 30 s) desligam o
  sandbox até à versão seguinte, com um aviso na nova aba. O `sandbox-fallback.json` do perfil guarda de onde veio cada
  falha contada (hora, versão, tipo do processo e código de saída; nunca a linha de comandos nem caminhos).
- **Atualizar não muda nada do que é seu:** de uma versão sem sandbox para uma com ele, os atalhos do menu Iniciar e
  do ambiente de trabalho, o perfil e o navegador predefinido ficam iguais, e a desinstalação não deixa restos (provado
  com um pacote de teste pelo Velopack; o ícone fixado na barra de tarefas não entrou nessa prova). A pasta de
  instalação recebe a permissão de que o serviço de rede precisa ao instalar e ao atualizar.
- **Custo medido:** cerca de 40 ms a mais no arranque (medianas 703 → 735 ms até à 1.ª página; +39 ms na diferença por
  pares, 20 rondas) e nenhuma memória a mais. Para o desligar à mão num computador: `--eteris-no-sandbox` no atalho.
- Por ver ao vivo: o proxy do sistema e a autenticação integrada com o serviço de rede no AppContainer (a máquina de
  testes não tem domínio nem proxy; com proxy por argumentos do motor já passou).

### Desempenho
Medido na mesma máquina e na mesma noite contra a 0.2.13, o Chrome, o Brave e o Firefox (medianas; método, tabelas e
limites em `docs/paridade.md`, «Medição da 0.2.14»; ferramentas: `build/perf/` — a medição usou uma cópia com
correções ainda por trazer para o repositório). Os números são do código medido (commit `8046e32`); o ajuste da
janela minimizada foi revisto depois da medição e o seu valor não voltou a ser medido no código final.
- **Menos memória:** 251 → 223 MB com 1 aba, 377 → 322 MB com 5 e 1239 → 1037 MB com 20. Com 20 abas fica abaixo do
  Chrome (1279 MB) e 11% acima do Brave (931 MB); com poucas abas ainda fica 60 a 80 MB acima dos dois.
- **Parado, quase nada de processador:** com 20 abas abertas e paradas, 1,72% → 0,13% de um núcleo (Chrome 0,61%,
  Brave 0,21%, Firefox 1,56%).
- A central de multimédia deixou de acordar todas as abas de 2 em 2 segundos (só as que estão à vista, as que saíram
  há pouco e as que têm som; as outras de 30 em 30), o ponto «a tocar» só anima enquanto se vê e o modo foco deixa de
  ler o rato com a janela inativa.
- **Janela minimizada deixa de desenhar:** com uma página animada, 6,77% de um núcleo na 0.2.13 (6,47% na 0.2.14 sem
  o ajuste) → 0,59% (com 20 abas, 9,31% sem o ajuste → 1,25% com ele), como nas três referências. Vale também noutra
  área de trabalho virtual e com a sessão bloqueada; nunca numa página com som, multimédia, câmara, microfone ou ecrã
  em uso, ou Picture-in-Picture. Com a janela só tapada por outra, a página continua a desenhar (o Chrome, o Firefox e
  o Brave param): fica em aberto.
- **Com pouca memória no Windows, adormece uma aba de cada vez** — a usada há mais tempo; nunca as fixadas, as
  trancadas, as que tocam som nem as que usam a câmara, o microfone ou o ecrã — e, ao acordar, a página volta ao mesmo
  ponto. Opção nova em Definições › Abas, «Adormecer também quando falta memória» (ligada). Com o sinal simulado e 8
  abas: 10 → 4 processos de página e 404 → 253 MB.
- **Troca de abas como no Chrome e no Brave:** 44 a 49 ms até a aba nova estar completa no ecrã (Chrome e Brave: 50 a
  56 ms), sem fotogramas errados em 36 trocas (numa passagem anterior apareceu 1 fotograma branco em 24: a vigiar).
  A 1.ª troca para uma aba que ainda não tinha sido mostrada demora 108 a 116 ms (no Chrome e no Brave não se distingue
  das outras: no máximo 62 a 73 ms); a 0.2.13 não foi medida nestes casos.
- A bolha do endereço de um link (ao passar o rato) passa para uma janela do tamanho do texto, em vez de ligar a
  camada do tamanho da janela inteira.
- Desenhar e rolar custam praticamente o mesmo que na 0.2.13 (animação 8,05% → 7,87%; rolagem 4,51% → 4,20%).
- Em aberto: em quatro cenários curtos (janelas de 30 s que começam 8 s depois de abrir) a 0.2.14 ficou 1,4 a 2,9
  pontos acima da 0.2.13; falta confirmar se é custo real, cauda do arranque ou o peso dos contadores de teste
  (`docs/paridade.md`).
- Os outros ajustes preparados nesta versão (cache dos shaders no disco, cache de Voltar/Avançar mais pequena, motor
  a arrancar em paralelo, funcionalidades do motor, poupança de energia do Chromium) ficam **desligados**: os que se
  mediram não mostraram ganho ou têm um custo (a poupança de energia corta para 30 fotogramas por segundo). Cada
  ajuste liga-se ou desliga-se pela linha de comandos (`--eteris-ajustes=-e8`, por exemplo) e `--eteris-sem-ajustes`
  desliga todos.

### Privacidade
- **O que muda:** com as páginas isoladas, um processo de página comprometido deixa de conseguir ler o cofre das
  palavras-passe (`cofre.db`, `cofre.key`) e de chamar a proteção de dados do Windows (DPAPI) do utilizador; o cofre só
  se abre no processo do Éteris (`docs/palavras-passe.md`). Num computador em que a rede de segurança desligou o
  sandbox, ou com `--eteris-no-sandbox`, volta o limite da 0.2.13.
- **O que não muda: no arranque continuam a sair 0 pedidos para a Google** com a sincronização desligada (a omissão).
  O que o Éteris corta no motor para isso é o mesmo da 0.2.13 e continua fixado por testes (a última medição do
  arranque com um proxy é da 0.2.11; nesta versão, em que mudam o executável de arranque e o isolamento do serviço de
  rede, não foi repetida), e a promessa «No arranque, nada sai para a Google» continua em `eteris://privacy/`, com ✔
  ou ✘. Com a sincronização ligada, tudo como na 0.2.13: só os pedidos dela, cifrados, a partir de 30 s depois de abrir.
- **Nada de novo sai do computador.** Os ajustes de desempenho só olham para o estado do próprio Windows (a janela
  minimizada, o aviso de pouca memória), e a rede de segurança do sandbox guarda o registo das falhas só no perfil
  (`sandbox-fallback.json`), sem o enviar a ninguém.
- **Duas linhas novas em `eteris://privacy/`:** «A cache da GPU vai-se com os seus dados» (nesta versão os shaders da
  GPU continuam a não ficar no disco) e «As afinações de desempenho não mudam o que os sites medem» (nenhuma afinação
  do motor está ligada; se uma delas mudasse a impressão digital, a linha dizia-o).
- Com o sandbox, o identificador da aplicação Google (`google-oauth.json`) fica legível pelo serviço de rede isolado,
  por estar na pasta da instalação. Não é um segredo (vai com o programa, como em qualquer aplicação de computador);
  os tokens da sessão Google continuam protegidos no perfil, fora dessa pasta (`docs/sincronizacao.md`).
- Os interruptores das medições de desempenho (`ETERIS_TEST_PURO`, `ETERIS_TEST_CONTADORES`,
  `ETERIS_TEST_HOST_RULES`, `ETERIS_TEST_PRESSAO`, `ETERIS_TEST_ENERGIA` e `ETERIS_TEST_AJUSTES`; `build/perf/README.md`)
  são ignorados por um Éteris instalado.

### Novidades
- `eteris://whatsnew/` abre com 4 destaques desta versão: as páginas isoladas, a memória e o processador com muitas
  abas, a janela minimizada e o sono das abas com pouca memória. Os dois últimos só se anunciam enquanto o ajuste
  respetivo estiver ligado por omissão.

### Correções
- Com o menu do Éteris aberto, nenhuma tecla — nem `Alt`, `Ctrl` ou `Shift` sozinhos — chega à página, como no Chrome,
  no Firefox e no Brave.
- Abrir um link no Éteris logo depois de ele arrancar já não se perde: o canal por onde um segundo lançamento entrega
  o link passa a existir assim que o Éteris começa a ouvir (havia um intervalo em que o link se perdia).

### Sincronização
- A aplicação Google do Éteris passou a «Em produção»: qualquer conta Google pode entrar e a sessão já não expira ao
  fim de 7 dias. A página inicial, a política de privacidade e os termos estão em `https://mrtjr.github.io/eteris/`.

### Para quem compila e publica
- Os programas instalados já não levam o caminho da pasta de quem os compilou (no .NET, no Escudo e nas peças nativas
  do sandbox), e o `build/publish.ps1` recusa publicar binários com ele.
- `build/publish.ps1` publica com o sandbox por omissão (`-NoSandbox` só para emergências, como a publicação pelo CI)
  e só com os testes do modo com sandbox aprovados para o próprio commit (`build/sandbox/build-all.ps1`, que compila
  o CefSharp com o patch e a DLL cliente a partir do repositório); `-GoogleOAuthJson` leva o login Google.
- `build/pack.ps1` só empacota uma publicação completa, da versão e do modo pedidos, feita a partir do commit que se
  empacota e com o login Google (sem ele só com `-SemLogin`).
- Testes: as janelas WPF correm sem paralelismo e as threads STA têm prazo; no CI, os jobs têm prazo e um teste preso
  deixa um dump e os resultados.

## 0.2.13 — 2026-11-09

A versão do **login completo**. Com **«Entrar com o Google»** (opcional e desligado por omissão), os favoritos, os
Espaços, as definições, as decisões do Escudo, as notas e as palavras-passe passam de um computador para o outro, como
no Chrome, no Firefox e no Brave — mas **cifrados de ponta a ponta no computador** antes de irem para a pasta oculta da
aplicação no Google Drive do utilizador: a chave nunca sai dos seus aparelhos e a Google só guarda ficheiros que não
consegue ler. Um computador novo entra pela aprovação noutro computador (o mesmo código nos dois ecrãs) ou pelo código
de recuperação; nas Definições há a lista de aparelhos, «Sair» e «Apagar os meus dados do Éteris no Google Drive».
Chega também o **gestor de palavras-passe e preenchimento automático** do próprio Éteris: guardar, preencher, gerar,
importar (Chrome, Brave, Firefox, Bitwarden, 1Password) e exportar palavras-passe; pagamentos (nunca o código de
segurança), informações de contacto, documentos de identificação e viagens; o Windows Hello para ver, copiar e
exportar. E fica cortado um pedido que o motor fazia à Google sempre que se entrava num site.

### Privacidade
- **Para o Google Drive só vai alguma coisa se ligar a sincronização.** Por omissão está desligada e nada muda: no
  arranque continuam a sair 0 pedidos para a Google (a promessa da 0.2.11). Sem `google-oauth.json` na instalação, a
  sincronização fica «indisponível nesta compilação» e nada é pedido à Google.
- **O que vai, com ela ligada:** favoritos, Espaços (nome, cor, emoji e ordem — não as abas), as definições que fazem
  sentido noutro computador, as decisões do Escudo por site e as notas; e, por omissão, palavras-passe, sites onde
  nunca guardar, informações de contacto e viagens. **Pagamentos e Documentos de identificação ficam desligados por
  omissão** e ligá-los pede o Windows Hello. Cada linha desliga-se em Definições › Sincronização › «O que passa entre
  PCs». **Não vão:** histórico, abas abertas, cookies, chaves de acesso, zoom e permissões dos sites.
- **Tudo é cifrado no computador antes de sair** (AES-256-GCM), e o que vem do cofre leva uma segunda cifra própria
  por registo. A chave fica só nos seus aparelhos — nem a Google nem o Éteris a têm; sem o código de recuperação e sem
  outro computador ligado, a cópia no Drive não se recupera. O Éteris só pede acesso à pasta oculta da aplicação
  (`drive.appdata`): não vê nem toca nos outros ficheiros do seu Drive.
- **O que a Google vê:** que a sua conta autorizou o Éteris, quando há pedidos (hora e endereço IP), quantos ficheiros
  há, com nomes opacos, e o tamanho aproximado de cada um; nunca o conteúdo. O Éteris pede à Google o nome e o e-mail
  da conta, uma vez depois de entrar, para os mostrar no menu (ficam cifrados neste computador). Com a sincronização
  ligada, a 1.ª ronda sai 30 s depois de abrir, e `eteris://privacy/` diz tudo o que sai e quando. O Éteris não tem
  servidor: não recebe nada.
- **Sair e apagar:** «Sair» revoga a sessão Google; «Apagar os meus dados do Éteris no Google Drive» apaga os ficheiros
  da pasta da aplicação; o acesso também se retira em *myaccount.google.com/permissions*.
- **O gestor de palavras-passe nunca envia nada à Google** (nem a ninguém): não faz pedidos de rede, o cofre está
  cifrado neste computador (AES-256-GCM, chave protegida pelo Windows) e o gestor do Chromium continua desligado.
  Importar e exportar são ficheiros locais. Só a sincronização, se a ligar, leva o cofre — cifrado como acima. Promessa
  nova em `eteris://privacy/`: «As suas palavras-passe não vão para ninguém», verificada no estado real.
- **Cortado: o motor mandava à Google cada início de sessão.** Medido com um proxy: ao enviar um formulário de início
  de sessão, o Chromium mandava o utilizador e a palavra-passe (um prefixo do hash dos dois) a
  `passwordsleakcheck-pa.googleapis.com` — a verificação de fugas corria mesmo com o gestor dele desligado. Desligada
  no perfil; o servidor de previsões de formulários (`content-autofill.googleapis.com`) fica também cortado no motor.
  Depois: 0 pedidos (`docs/paridade.md`, secção 7).
- O isolamento das páginas (sandbox) continua desligado nesta versão.

### Sincronização entre computadores («Entrar com o Google»)
- **Login completo:** entra-se com a conta Google no navegador do sistema e os **favoritos, Espaços (nome, cor, emoji
  e ordem), definições que viajam, decisões do Escudo por site (desligado, modo estrito, elementos escondidos,
  bloqueios por site, exceções ao HTTPS primeiro) e notas** passam de um computador para o outro, como no Chrome, no
  Firefox e no Brave — mas **sempre cifrados de ponta a ponta**: os dados vão para a pasta oculta da aplicação no
  Google Drive do utilizador (âmbito `drive.appdata`), a chave nunca sai dos aparelhos e a Google só vê ficheiros
  cifrados, os tamanhos e as horas. As palavras-passe e o preenchimento passam também, com uma segunda cifra (ver
  «Palavras-passe e preenchimento automático»). Histórico, abas abertas, cookies e chaves de acesso não sincronizam.
- **Primeiro computador:** a conta cria-se sozinha e aparece o **código de recuperação** com «Copiar» e «Já guardei» e
  o aviso de que, sem ele e sem outro computador, os dados na nuvem não se recuperam.
- **Computador seguinte:** «Aprovar noutro computador» (este mostra um código; o outro mostra o pedido com o nome do
  computador e o mesmo código, no menu da conta e nas Definições, e aprova-se lá) ou «Usar o código de recuperação».
  Os dados juntam-se sem se perder nada: o que só existia nesse computador também sobe.
- **Quando:** 30 s depois de abrir (só com a sincronização ligada), 10 s depois de uma alteração, de 10 em 10 minutos
  e em «Sincronizar agora»; sem rede, volta a tentar sozinho com espera crescente. O estado está nas Definições
  («Sincronizado há 2 min», «A sincronizar…», o erro) e na linha da conta do menu.
- **Aparelhos:** lista nas Definições com «Remover» — troca as chaves (código de recuperação novo), os outros
  computadores continuam sozinhos e o removido deixa de ler e escrever.
- **«Sair»** para a sincronização neste computador, apaga e revoga a sessão Google e, se se escolher, apaga também a
  chave deste computador. **«Apagar os meus dados do Éteris no Google Drive»** apaga os ficheiros da pasta da
  aplicação (com confirmação), sem sair da conta. A sincronização fica desligada (gravado no disco) **antes** de se
  apagar o primeiro ficheiro: se o Éteris fechar a meio, ao abrir continua desligada, nada volta a ser enviado e o
  apagar acaba sozinho («Ligar outra vez» também o acaba primeiro); uma ronda que estava a correr já não grava nem
  envia depois do apagar; «A apagar os dados do Éteris do Google Drive…» fica à vista enquanto corre. Se o apagar
  não acabar (sem rede, sessão da Google acabada ou um erro inesperado), as Definições e o menu dizem porquê, com
  **«Tentar de novo»**, em vez de ficarem em «A apagar…»; «Ligar outra vez» passa a **«Ligar sem acabar de apagar»**
  (o que ficou no Drive fica lá), para nunca ficar preso. As «cópias em conflito» que um cliente de nuvem deixe na
  pasta do Éteris («keys (1)») também saem, e uma entrada (com o código ou com a chave guardada) que esteja a meio
  quando se carrega em «Apagar…» já não volta a ligar a sincronização nem a gravar a chave.
- A secção Sincronização e a conta no menu deixam de estar atrás da opção experimental: aparecem sempre que a
  compilação tem o identificador da aplicação Google (sem ele: «indisponível nesta compilação»).
- `eteris://privacy/`: «No arranque, nada sai para a Google» continua verdadeira por omissão e, com a sincronização
  ligada, diz a exceção escolhida (só os pedidos da sincronização, cifrados, para o seu Drive, e quando).
- Publicação: `build/publish.ps1 -GoogleOAuthJson <ficheiro>` põe o identificador da aplicação Google na instalação
  (verificado; nunca no repositório); sem ele publica sem login e avisa.
- **Mais seguro ao remover um aparelho:** antes de trocar a chave faz uma ronda completa e os outros computadores
  voltam a pôr na conta nova o que tinham mudado, por isso nada do que já tinham enviado se perde; a troca resiste a
  uma falha de rede a meio (acaba na ronda seguinte); o aparelho removido não consegue desviar os outros para uma conta
  dele (não era verdade em todos os casos: corrigido na versão seguinte à 0.2.15), e só recebem a chave nova os aparelhos que estavam na lista. «Gerar um código de recuperação novo» nas
  Definições.
- **Parar nunca apaga a chave:** dados apagados noutro aparelho, um registo de chaves danificado ou entrar com outra
  conta Google param (ou põem em pausa) a sincronização sem apagar a chave deste computador; de volta à conta certa,
  continua sem código.
- **Uma leitura que falha não apaga nada** nos outros computadores (favoritos, Escudo, notas ou Espaços que não se
  conseguiram ler ficam de fora dessa ronda).
- **Janela privada:** desligar o Escudo num site, o modo estrito e esconder um elemento ficam só nessa sessão privada
  (não vão para o perfil nem sincronizam), como no Chrome, no Firefox e no Brave.
- **Autorização da Google retirada ou expirada:** «Sincronização em pausa: entre outra vez» no menu e «Entrar outra
  vez» nas Definições, em vez de tentar para sempre.
- **Pedidos de computadores novos:** respondidos só quando abre o menu (⋯) ou com a secção Sincronização à vista (que
  agora os mostra sem recarregar); dois computadores ligados a responder ao mesmo pedido já não o fazem falhar.
- **Definições › Sincronização mais fáceis de usar pelo teclado e com leitor de ecrã:** os pedidos e a lista de
  aparelhos já não perdem o foco a cada atualização; «Remover» anuncia a confirmação; as mensagens são anunciadas;
  depois de cada ação o foco vai para o que apareceu; «Escolher ficheiro…» é um botão. Um código de recuperação com uma
  gralha deixa tentar outra vez sem o escrever de novo. «Apagar…» e «Remover» não se confirmam com um duplo clique, e
  «Apagar…» tem «Cancelar». «Sair» com «apagar a chave» pede confirmação se o código ainda não foi guardado. Quem perdeu
  o código e não tem outro computador vê a saída («apagar os dados do Éteris no Drive e começar de novo»).
- `eteris://privacy/`: a exceção da sincronização diz tudo o que sai para a Google (o menu ⋯, a renovação da sessão, o
  nome e o e-mail da conta e as ações que pede) e aparece também com sessão Google e a sincronização desligada; com ela
  desligada, abrir o menu já não pede nada à Google.

### Palavras-passe e preenchimento automático
- **Canal do gestor com as páginas, sem deixar rasto** (`docs/palavras-passe.md`, C1): o script do gestor corre num
  mundo isolado de cada página `http(s)` (a página não o vê nem à ligação com o Éteris), fala com o Éteris por
  mensagens validadas, nunca escreve na página por si e não usa o `Runtime.enable` que os anti-robôs procuram (medido:
  0 deteções, com controlo positivo).
- **Classificador de formulários** (C2): reconhece inícios de sessão, criação de conta, mudança de palavra-passe,
  códigos de uma utilização (nunca se guardam), moradas de Portugal e do Brasil, cartões (o CVC só para nunca o tocar),
  IBAN, NIF/CPF, documentos e matrícula/placa, em português, inglês e espanhol, também em campos soltos, raízes sombra
  e iframes da mesma origem; campos escondidos de rastreadores ficam de fora.
- **Correção:** os pedidos internos do Éteris ao motor (DevTools) podiam falhar de vez em quando quando dois saíam ao
  mesmo tempo (numeração fora de ordem); agora saem sempre por ordem.
- **Preencher palavras-passe nas páginas** (C3): ao clicar (ou com Tab) no campo do utilizador ou da palavra-passe de
  um site com palavras-passe guardadas, aparece a lista do Éteris por baixo do campo (as do próprio site primeiro, as
  do mesmo domínio marcadas; nunca as de `https` numa página `http`). ↑ ↓ e Enter escolhem e preenchem, Esc fecha — a
  página não vê essas teclas; um clique também preenche. Só preenche quando escolhe, nunca ao carregar a página;
  funciona com o zoom, no ecrã dividido e em sites feitos com React. «Pedir o Windows Hello ao preencher
  palavras-passe» (desligado por omissão) pede a confirmação antes.
- **Guardar e atualizar palavras-passe** (C4): depois de entrar num site (a página muda, ou numa aplicação web o
  formulário desaparece), a bolha do Éteris sai da barra de endereço com «Guardar a palavra-passe de *site*?» — ou
  «Atualizar…» quando a palavra-passe mudou, também num formulário de mudança de palavra-passe. O utilizador pode
  corrigir o nome e ver a palavra-passe antes; «Agora não» deixa um ícone de chave para guardar mais tarde, e ⋯ ›
  «Nunca neste site». Um erro de login não oferece nada; a janela privada nunca oferece; nada fica guardado sem
  escolher «Guardar».
- **Gerador de palavras-passe** (C5): num campo de palavra-passe nova (criar conta, mudar a palavra-passe), a lista do
  Éteris propõe «Usar palavra-passe forte» — 20 carateres aleatórios sem carateres que se confundem, a respeitar o
  tamanho e o formato que o site pede. Também no menu de contexto: «Gerar palavra-passe forte». A palavra-passe fica
  logo guardada («Não confirmada») para não se perder e é confirmada quando o formulário é enviado com sucesso. Nunca
  na janela privada.
- **Preencher moradas, cartões, IBAN, NIF/CPF, documentos e viagens** (C6): num formulário de morada aparece a lista
  das suas informações de contacto (Portugal e Brasil: código postal/CEP, bairro, distrito/estado, país em listas…);
  num formulário de pagamento, os cartões (nunca o código de segurança: o CVC fica vazio e com o cursor); num campo de
  NIF/CPF, os seus números fiscais. Cartões, IBAN e documentos só em ligações seguras e com o Windows Hello. Passaporte,
  carta de condução/CNH, Cartão de Cidadão/RG, matrícula/placa e fidelidade: pelo menu de contexto do campo,
  «Preencher com… ›», que também deixa usar uma palavra-passe de outro site (com confirmação).
- **Janela privada** (C7): preenche palavras-passe, moradas, cartões e documentos quando escolhe, mas não guarda nada:
  nem a «última utilização», nem a oferta de guardar, nem o gerador; o gestor abre numa janela normal. Medido: o
  ficheiro do cofre fica exatamente igual depois de uma janela privada o usar.
- **As palavras-passe passam entre computadores** (D1–D5): com a sincronização ligada, as palavras-passe, os sites onde
  nunca guardar, as informações de contacto e as viagens passam para os outros computadores da conta, cada registo com
  uma **segunda cifra própria** por dentro da cifra de sempre (nem dentro do Éteris, nem no Google Drive, nem num perfil
  exportado há um site ou uma palavra-passe fora desse selo). Pagamentos e Documentos de identificação vêm desligados
  e ligá-los pede o Windows Hello. Se a mesma palavra-passe mudar em dois computadores, fica a mais recente e a outra
  vai para o histórico desse computador (o «Mostrar» das Palavras-passe mostra-a) com um aviso.
- **Definições › Sincronização › «O que passa entre PCs»**: uma linha por tipo de dado (Favoritos, Espaços, Definições,
  Decisões do Escudo, Notas, Palavras-passe, Pagamentos, Informações de contacto, Documentos de identificação, Viagens)
  com o interruptor deste computador, e as que não passam (abas abertas, chaves de acesso). Desligar não apaga nada;
  ligar outra vez junta sem perder nada.
- **Aprovar um computador novo** com palavras-passe na conta diz que ele as vai receber e pede o Windows Hello;
  **remover um computador** volta a selar tudo com chaves novas e lembra de mudar as palavras-passe dos sites
  importantes; **exportar o perfil cifrado** pode levar o cofre (caixa «Incluir palavras-passe, pagamentos e
  documentos», com o Windows Hello); o código de recuperação diz que também abre o cofre.
- **Menu do Éteris › «Palavras-passe e preenchimento automático ›»**, a seguir a «Favoritos», com o submenu do Chrome:
  Gestor de palavras-passe, Pagamentos, Informações de contacto, Documentos de identificação e Viagens. Pelo teclado
  como no Chrome (→ ou Enter abre, ← ou Esc volta, a inicial salta) e anunciado pelo leitor de ecrã (aberto/fechado,
  «n de N»). Na janela privada as entradas abrem numa janela normal. Também nas Definições (secção nova, com «Oferecer
  para guardar palavras-passe», «Mostrar sugestões ao clicar num campo» e «Pedir o Windows Hello ao preencher
  palavras-passe») e na paleta (Ctrl+K).
- **`eteris://passwords/` — o gestor de palavras-passe:** lista com pesquisa, agrupada por site; «Mostrar» e «Copiar»
  com o Windows Hello (ou a palavra-passe do Windows), com a janela fora das capturas enquanto a palavra-passe está à
  vista e a cópia fora do histórico da área de transferência; adicionar, editar (a anterior fica no histórico) e apagar
  com «Anular»; «Mudar palavra-passe» abre a página de mudança do site só ao clicar; verificação de palavras-passe
  fracas e repetidas feita neste computador; «Sites onde nunca guardar»; importar o CSV do Chrome, do Brave, do
  Firefox, do Bitwarden ou do 1Password (o ficheiro é lido pelo Éteris, não pela página, com a escolha nos conflitos e a
  oferta de o enviar para a Reciclagem) e exportar no formato do Chrome (pede sempre o Windows Hello e avisa numa pasta
  do OneDrive, do Dropbox ou do Google Drive). Nenhuma palavra-passe vai no HTML da página; nada sai do computador.
- **`eteris://autofill/` — Pagamentos, Informações de contacto, Documentos de identificação e Viagens:** cartões
  (nunca o código de segurança) e IBAN, moradas com os campos de Portugal (código postal, distrito) e do Brasil (CEP,
  bairro, estado), passaporte, carta de condução/CNH, Cartão de Cidadão/RG e NIF/CPF, veículos (matrícula/placa, VIN)
  e programas de fidelidade. Números sempre mascarados; vê-los ou editá-los pede o Windows Hello. Validações com o
  dígito de controlo (cartão, IBAN, NIF, CPF) e os formatos de cada país. «Pedir o Windows Hello ao preencher
  pagamentos» ligado por omissão (desligá-lo também pede).
- **«Apagar dados de navegação» › «Palavras-passe e preenchimento automático»**, desmarcada por omissão e com o
  Windows Hello (recusado, nada se apaga). «Esquecer este site», «Triturar» e «Apagar ao fechar» não tocam nas
  palavras-passe. **Promessa nova em `eteris://privacy/`: «As suas palavras-passe não vão para ninguém»**, verificada no
  estado real (gestor do Chromium desligado no perfil, sem verificação de fugas nem servidor de formulários da Google).
- **Nada dos formulários vai para a Google:** medido ao vivo, o motor mandava o utilizador e a palavra-passe (prefixo
  de um hash) de cada início de sessão enviado a `passwordsleakcheck-pa.googleapis.com` — a verificação de fugas do
  Chromium corria mesmo com o gestor dele desligado. Desligada nas preferências do perfil; o servidor de previsões de
  formulários da Google (`content-autofill.googleapis.com`) fica também cortado no motor. `eteris://privacy/` › «Nada
  sai do computador sem um pedido seu» diz os dois e verifica-os no estado real (`docs/palavras-passe.md`).
- **Gestor de palavras-passe próprio, base local** (`docs/palavras-passe.md`, A1–A5):
  cofre `cofre.db` com cada item cifrado em AES-256-GCM (metadados e segredo em separado) e a chave protegida pela DPAPI
  do Windows, aberto só quando é preciso (o arranque não muda); palavras-passe, cartões (nunca o código de segurança),
  IBAN, contactos, documentos de identificação (passaporte, carta de condução/CNH, Cartão de Cidadão/RG, NIF/CPF) e
  viagens, com as validações de cada um (Luhn, IBAN, NIF e CPF, código postal/CEP); sugestões só para a mesma origem
  ou o mesmo domínio registável, nunca entre domínios, `http` e `https` separados; confirmação com o Windows Hello ou a
  palavra-passe do Windows (janela de graça de 5 minutos por janela, que recomeça ao bloquear o Windows; exportar
  pede sempre); importar CSV do Chrome, Brave, Firefox, Bitwarden e 1Password e exportar no formato do Chrome; copiar
  um segredo fica fora do histórico da área de transferência (Win+V) e da nuvem e é limpo ao fim de 60 s; com um
  segredo à vista, o Éteris sai das capturas de ecrã.

## 0.2.12 — 2026-11-08

Uma versão de correções no **menu do navegador**: volta a ler-se (o título, o subtítulo e o «Zoom» saíam em
quadrados) e **funciona pelo teclado como no Chrome, no Firefox e no Brave** — `Alt`+`F` ou `Alt`+`E` abrem-no,
as setas, `Home`, `End` e a inicial escolhem a linha, `Enter` ativa, `Esc` fecha e devolve o foco, e as teclas já não
chegam à página. Nas Definições, a secção escolhida na navegação fica realçada.

### Menu do navegador
- O título «Éteris», o subtítulo e «Zoom» deixam de sair em quadrados: o menu, criado em código e sem pai, herdava a
  fonte de ícones do botão «⋯»; passa a fixar a fonte da interface, também no popup de mudar o nome do grupo e nos
  menus de contexto escuros.
- O teclado volta a funcionar no menu: `Esc`, `Alt`+`F` e `Alt`+`E` fecham-no e devolvem o foco a quem o tinha
  (página, barra de endereço ou «⋯»); `↑`, `↓`, `Home` e `End` movem entre as linhas (com volta ao início e ao fim);
  `Enter` e `Espaço` ativam; `Tab` e `Shift`+`Tab` percorrem; uma letra salta para a linha com essa inicial; `←` e `→`
  só andam dentro da linha do zoom, que `↑` e `↓` contam uma vez. Com o foco na página, as teclas do menu deixavam de chegar à janela e iam para a página (as setas
  rolavam-na): agora ficam no menu e nunca chegam à página.
- Como no Chrome, no Firefox e no Brave: aberto pelo rato, nenhuma linha fica realçada até se usar o teclado, e o rato
  que se mexe leva o realce consigo (uma só seleção).
- O menu fecha ao perder a ativação (`Alt`+`Tab`, clique noutra aplicação), ao mudar de aba ou fechar a aba e ao abrir outra
  janela, e o teclado volta a quem o tinha — nunca a uma página escondida.
- Folha de atalhos (`F1`) aberta a partir da página: fica com o teclado e as setas já não rolam a página.
- Leitor de ecrã: o menu anuncia-se como «Menu do Éteris», cada linha como item de menu com a posição («2 de 17»), e a
  lista da folha de atalhos chama-se «Atalhos de teclado».

### Definições
- O item clicado na navegação fica realçado até se rolar a página à mão (as últimas secções são curtas e o realce pela
  posição escolhia outra); também com `#âncora`. O realce solta-se quando uma rolagem tira a secção da vista (`Tab`,
  Procurar na página), segue o foco do teclado e já não se perde com um clique num botão ou interruptor.
- Na pesquisa, o realce só conta as secções à vista e, no fim da página, realça a última delas.
- O item realçado é anunciado ao leitor de ecrã como a secção atual (`aria-current`).

## 0.2.11 — 2026-11-08

Uma versão de privacidade: **no arranque, nada sai para a Google** — o «relógio de rede» do Chromium e a pergunta
pelo «Modo IA» da pesquisa Google estão desligados no motor (0 pedidos medidos com um proxy, promessa nova em
`eteris://privacy/`) — e o **Escudo passa a valer também nos pedidos sem aba**: service workers, shared workers e
extensões. O isolamento das páginas (sandbox) continua pronto mas desligado.

### Privacidade
- No arranque, nada sai para a Google. Medido com um proxy local que regista todos os pedidos (`docs/paridade.md`,
  secção 7): apesar de o tráfego de fundo do Chromium já estar desligado, cada arranque fazia 2 pedidos sem nenhuma
  ação do utilizador — a hora a `clients2.google.com` (o «relógio de rede» do Chromium) e, a `www.google.com` e com
  os cookies da Google, se o utilizador pode usar o «Modo IA» da pesquisa Google. Os dois estão desligados no motor
  (`NetworkTimeServiceQuerying`, `AimEnabled`): agora são 0 pedidos nos primeiros 60 s com a nova aba e nos 60 s
  seguintes com uma página aberta, e 0 em 8 minutos parado. Pesquisar e abrir sites da Google continua igual.
- Promessa nova em `eteris://privacy/`: «No arranque, nada sai para a Google», lida da linha de comandos do motor
  (fica ✘ e diz qual interruptor falta se algum sair).
- O Escudo passa a valer também nos pedidos sem aba: os do service worker e do shared worker de um site (incluindo o
  truque de um service worker reencaminhar os pedidos da própria página, que contornava o bloqueio, e os scripts que
  ele importa) e os do service worker de uma extensão. Antes chegavam aos rastreadores sem listas, sem GPC, sem filtro
  de cookies de terceiros nem bloqueio da rede local; agora decidem-se como os da aba, contam no Escudo da aba que
  mostra o site e no relatório de privacidade. Provado com páginas locais: 5 caminhos chegavam ao rastreador, 0 agora
  (`docs/paridade.md`, secção 8). Nenhum custo para os pedidos das abas.

## 0.2.10 — 2026-11-08

Uma versão de afinação, medida contra Chrome, Brave e Firefox (`docs/paridade.md`): o **arranque ficou ~45% mais
rápido** (a 1.ª página em ~0,6 s, eram ~1,1 s), o Éteris gasta **~50 MB a menos de memória**, a **impressão digital
ficou ao nível do Chrome** (dispositivos escondidos sem autorização, a língua do Windows, sem «Não rastrear», dicas do
cliente reduzidas e iguais na página e nos workers) e entram as correções da verificação ao vivo e da noite. O
**isolamento das páginas (sandbox)** está pronto mas ainda desligado: liga na 0.2.11 (`docs/sandbox.md`).

### Arranque e memória (medidos contra Chrome, Brave e Firefox; `docs/paridade.md`, secção 1c)
- A 1.ª página aparece em ~0,6 s num arranque normal (eram ~1,1 s na mesma máquina e sessão; o Firefox
  leva ~1,1 s e o Chrome ~0,5 s): o motor do Chromium, o motor de desenho da janela e a base de dados arrancam em
  paralelo com a janela, o JIT do arranque usa vários núcleos guiado pelo arranque anterior, e a lista de atalhos da
  barra de tarefas e a procura de atualizações ficam para depois da 1.ª página.
- Menos memória (~47 MB a menos com 1 aba e ~56 MB a menos com 5, quase toda no processo principal): o Escudo lê as listas de filtros diretamente
  e guarda o motor já compilado no perfil, por isso o arranque seguinte não volta a compilar as listas e o bloqueio
  fica ativo mais cedo.

### Impressão digital (medida contra Chrome, Brave e Firefox; `docs/paridade.md`, secção 6)
- Os sites deixam de ver os nomes e os identificadores da câmara, do microfone e dos altifalantes sem pedir
  autorização: antes da permissão, a lista dá uma entrada vazia por tipo, como o Chrome (a correção definitiva no
  motor fica para a próxima compilação, `docs/motor.md`).
- A língua enviada aos sites passa a ser, por omissão, a do Windows (era sempre pt-PT, o que num Windows em pt-BR
  distinguia quem usa o Éteris); quem a escolheu nas Definições mantém a escolha. As datas e os números das páginas
  seguem a mesma língua.
- Deixa de ser enviado o «Não rastrear» (DNT), que o Chrome e o Brave não enviam e que só servia para distinguir o
  Éteris; o Global Privacy Control (GPC) continua em todos os pedidos e no JavaScript.
- A versão do Windows e do Chromium que os sites leem passa a ser a mesma na página e nos seus workers (antes, os
  workers davam a versão exata).

### Correções
- Um vídeo ou áudio aberto diretamente depois de outro site deixa de ficar com o ícone desse site, nos dois motores.
- O mesmo ficheiro de média fica no histórico com o mesmo título nos dois motores: o nome do ficheiro, como no Chrome.
- Espreitar: depois de fechar o cartão com `Esc`, o `Alt`+clique seguinte volta a abri-lo à primeira, sem mostrar
  por um instante o cartão anterior.
- Ao sair do ecrã dividido para outra aba, as duas metades ficam no lugar até a nova aparecer (antes a da esquerda
  via-se por um instante em ecrã inteiro).

## 0.2.9 — 2026-11-08

A maior versão até agora. O programa de experiência do utilizador fica encerrado nos 50 ciclos (ciclos 1–51 feitos,
com duas revisões adversariais, aos 25 e aos 50; diário em `docs/ciclos-ux.md`). O Éteris passa a usar um **motor
próprio com os codecs do Windows**: H.264 e HEVC descodificados pela placa gráfica e AAC pelo Media Foundation, sem
nenhum descodificador com patentes no Éteris (fontes e patches em `build/cef-codecs`). A navegação ficou ~0,9 s mais
rápida por página e o Voltar instantâneo, as páginas e as abas deixaram de piscar, e o bloco B pôs a privacidade à
vista: Observatório, relatório e auto-teste, promessas verificáveis, rede local, HTTPS primeiro e rastreio escondido.

### Abas e interface (bloco A, ciclos 1–30)
- **Faixa de abas que rola** em vez de as esmagar (roda do rato, setas nas pontas) e a lista «▾» de todas as abas, pesquisável.
- **Modo compacto** (abas e barra de endereço numa só linha) e **interface que desaparece** em modo foco (`F11`).
- **Cor do Éteris** (Definições › Aspeto): nove cores, a da paisagem da nova aba ou a do Windows.
- **Espreitar um link** (`Alt`+clique ou menu do link): título, imagem e primeiros parágrafos num cartão, sem abrir aba nem deixar rasto no histórico.
- **Ecrã dividido 2.0**: «Abrir link ao lado», arrastar a aba até à margem (lado a lado ou empilhado), trocar lados; a divisão fica na sessão.
- **Abas com nome, emoji, cor e nota**, pré-visualização dos grupos ao pairar e multi-seleção de abas (`Ctrl`/`Shift`+clique).
- **Abas como tarefas**: Trancar, Adiar até…, Feito («Feitas hoje») e Arquivo automático das abas esquecidas, no Radar.
- **Espaços** (`Ctrl+Alt+1 … 9`): ambientes de abas na mesma janela, com nome, cor e emoji; as abas dos outros Espaços dormem.
- **Barra lateral** (`Ctrl+Shift+B`): Notas, Para ler, Favoritos, Histórico e sites fixados (com vista móvel), à esquerda ou à direita.

### Privacidade à vista (bloco B, ciclos 31–51)
- **HTTPS primeiro com aviso**: sem recuo silencioso para http://, com «Continuar sem segurança» só desta vez ou sempre; páginas de erro com diagnóstico.
- **Anular**: «Anular» nos avisos e `Ctrl+Z` fora da página desfazem a última ação do Éteris.
- **Esquecer e triturar** no cadeado («Esquecer este site ao fechar», «Triturar agora…») e «Terminar sessão privada».
- **Promessas verificáveis** em `eteris://privacy/`: GPC, APIs de anúncios do Chromium desligadas, um frasco por site, referrer só com a origem, uma só língua, dicas do cliente mínimas também no JavaScript — cada uma com ✔/✘ lido do estado real. Opção para esconder o Éteris do Recall e das capturas.
- **A rede local é sua**: uma página da Internet não fala com este computador, o router, a impressora ou o NAS sem perguntar.
- **Rastreio escondido**: links por l.facebook.com, out.reddit.com e afins vão direto ao destino (os avisos anti-phishing ficam), os sites de salto são esquecidos e os rastreadores atrás de um subdomínio do site (CNAME) são desmascarados, também com o DNS cifrado.
- **Observatório do site** (`Ctrl+Shift+U`), **relatório** dos últimos 7 e 30 dias e **auto-teste** de privacidade em `eteris://privacy/`.

### Mais rápido (ver `docs/motor.md` e `docs/paridade.md`, secção 1b)
- **Cada página abre ~1 s mais depressa**: o pedido da página deixou de esperar pelo registo dos scripts do Escudo (que o Chromium só entregava depois da navegação). 1.ª pintura ~0,3 s depois do clique (era ~1,25 s).
- **Voltar/Avançar instantâneos** (back/forward cache, como no Chrome): ~6 ms em vez de ~1,3 s, com a contagem do Escudo da página reposta.
- A barra de carregamento deixou de pôr uma janela transparente por cima da página: −16% de processador ao carregar.
- O histórico é escrito em segundo plano (WAL, fila, títulos juntos): a janela deixa de parar 3–6 ms por página.
- **Voltar a uma aba deixada há pouco é imediato**: as 1–2 páginas deixadas há menos de 8 s ficam prontas por baixo da aba à vista (nunca com vídeo a tocar, som ou ecrã dividido).

### Correções
- A página deixa de «piscar» (ficar branca e minúscula por instantes) ~1 s depois de carregar: a miniatura da aba é tirada sem mexer na página à vista.
- Sem clarão do fundo da janela entre páginas: a área da página tem fundo opaco.
- **Trocar de aba, abrir uma nova e fechar a ativa sem piscar**: a página que entra aparece por baixo da que sai, já no tamanho final, e só sobe depois de desenhada (antes: branco, o fundo da janela ou, em páginas pesadas, até ~0,3 s de página por desenhar). Sem o Mica a aparecer por trás de uma página ainda vazia.
- Depois de trocar de aba, o teclado segue a aba ativa (as teclas podiam ir para a página escondida).
- O Éteris deixa de fechar inteiro quando um iframe desaparece a meio de um pedido.
- Abrir o Éteris com um endereço já não abre duas abas iguais.
- As regras cosméticas e os scriptlets do Escudo passam a valer logo na 1.ª página de cada site, e também nas páginas servidas por service worker (YouTube).
- A sessão guarda todas as abas: com mais de 100 (na faixa ou num Espaço na reserva), as seguintes perdiam-se em silêncio no arranque seguinte.
- O cartão do cadeado fecha ao navegar, ao mudar de aba e ao ir para a barra de endereço (ficava aberto com o site anterior).

### Vídeo
- **H.264 e AAC no motor principal** (Twitch, MP4 direto, HLS, g1, BBC, Reddit, TikTok, Dailymotion, `.m4a`/`.aac`), com uma compilação própria do motor com os codecs do Windows: o vídeo é descodificado pela placa gráfica e o som AAC pelo Windows, sem nenhum descodificador com patentes no Éteris (~2,4% de processador a 1080p). Os sites deixam de mudar para o motor de vídeo, com o Escudo, as regras cosméticas e todas as funções da aba.
- Sem descodificação por hardware (máquina virtual, controlador básico) ou num Windows N, o Éteris percebe-o no arranque e usa o motor de vídeo como antes — também num MP4 que tocaria só o som.
- Licenças: créditos do motor próprio, LGPL do FFmpeg e a oferta do código-fonte em `licenses/`.
- **Vídeo que não toca passa a tocar** (motor de vídeo do Windows): a deteção apanha MP4 abertos diretamente, `<video><source mp4>`, leitores MSE (hls.js, Vimeo, BBC, g1), vídeos dentro de componentes (Reddit) e leitores conhecidos em iframes — em 1–4 s, em vez de nunca ou ~11 s.
- **Twitch, Kick, Vimeo, Dailymotion, TikTok e Instagram abrem logo no motor de vídeo** (1.º fotograma ~4 s mais cedo na 1.ª visita); o botão «Motor de vídeo» exclui o site de vez.
- Novo botão **«Tocar no motor de vídeo»** (barra de endereço e menu da página) quando a troca não é automática: vídeo de fundo, leitor noutro site, Reddit e **DRM** (Netflix, Spotify…: sempre com pergunta).
- Ligações diretas a **HLS (`.m3u8`) e áudio AAC (`.m4a`/`.aac`) tocam** em vez de irem para as Transferências.
- A troca de motor já não mostra o fundo da janela nem uma página em branco; o vídeo retoma onde estava.
- Depois da troca para o motor de vídeo, **Voltar** (e Alt+←) regressa à página anterior no motor principal, e Avançar volta ao vídeo; um ficheiro de vídeo aberto diretamente tem como título o nome do ficheiro (ficava o da página anterior, também no histórico).
- Motor de vídeo: sem o «component updater» do Edge, com a política de reprodução automática do Éteris, o bloqueador já vale desde o 1.º pedido da página e as regras cosméticas do Escudo também se aplicam.

## 0.2.8 — 2026-11-07

(As versões 0.2.4 a 0.2.7 foram pacotes locais de teste, nunca publicados.)

### Novo
- **Menu do Éteris** (`Alt+F`): as ações por categoria e o zoom no próprio menu.
- **Radar de abas** (`Ctrl+Shift+A`): todas as abas numa só vista, com pesquisa, âmbitos, ordenações, duplicadas e abas fechadas recentes.
- **Ícones dos sites** no histórico e nos atalhos da nova aba, guardados no perfil (nunca na janela privada).
- **Paisagens na nova aba**: duas imagens e um fundo «ao vivo» opcional (vídeo sem som, só quando escolhido), geradas por IA com o ChatGPT pelo autor; origem e termos em `licenses/Wallpapers-NOTICE.txt`.

### Privacidade
- Os ícones dos sites passam a ser pedidos pelo próprio motor (contexto da aba): passam pelo Escudo, pelo DNS cifrado e pelo HTTPS-first, e a janela privada não partilha sessão com o perfil normal.

### Sandbox (preparação, ver `docs/sandbox.md`)
- Todo o JavaScript que o Éteris corre nas páginas passou para o DevTools (`Browser/PageScript.cs`): nada depende já do processo filho .NET do CefSharp (passo A), o Éteris funciona com um processo filho nativo só com CEF (passo B) e o CefSharp 156 compila com a informação do sandbox (passo C). O sandbox em si ainda não está ligado.
- Teste `SandboxRulesTests` impede o regresso das APIs do processo filho.

### Correções
- Com o menu do Éteris aberto, os atalhos e o `Esc` passam a fechá-lo em vez de agir por trás dele.
- Sair do ecrã inteiro de um vídeo deixa de desfazer o ecrã inteiro escolhido no menu ou com `F11`.
- A aba do segundo painel do ecrã dividido já não adormece no minuto seguinte a fechar o ecrã dividido.
- A miniatura da aba deixa de desaparecer em navegações dentro da mesma página (SPAs, âncoras).
- Apagar uma visita ou «esquecer desde» deixou de percorrer o histórico inteiro.
- Licenças: todos os ficheiros de imagem e vídeo embutidos têm de estar registados (teste novo).

## 0.2.3 — 2026-11-06

Paridade de desempenho com o Chrome e o Brave (ver `docs/paridade.md`).

- **Páginas desenhadas pelo próprio Chromium numa janela** (como no Chrome, Firefox e Brave), em vez de copiadas para a interface a cada quadro: uma aba a 60 fps passa de 28–36% para 8–11% de um núcleo do processador (Chrome 6,7%, Brave 4,8–8,0%). Menos bateria e rolagem mais leve.
- **Abas escondidas deixam de gastar processador**: a página passa a «escondida» e para de desenhar, como nas abas de fundo das outras referências (antes, cada aba animada escondida custava ~30% de um núcleo); continua a receber mensagens e notificações.
- Avisos, paleta, pesquisa na página e painéis aparecem por cima de qualquer página, incluindo as do motor de vídeo.
- **AltGr deixou de disparar atalhos**: AltGr+W (que em alguns teclados escreve um carácter) fechava a aba.

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
