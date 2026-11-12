# Sincronização do Éteris («Entrar com o Google», opção B)

Estado: **publicada na 0.2.13** (ligada ao Éteris, fora do modo experimental; opcional e desligada por omissão). A
aplicação Google do Éteris está **«Em produção»** desde 2026-11-09 (passo 7 de «O que o dono tem de fazer»): qualquer
conta Google pode entrar, e a sessão já não expira ao fim de 7 dias. Página inicial, política de privacidade e termos
em <https://mrtjr.github.io/eteris/> (GitHub Pages, `docs/site/`, workflow `.github/workflows/pages.yml`). A
verificação da marca (nome e logótipo no ecrã de consentimento) continua opcional e por pedir. Decisão do dono
(opção B): o início de sessão na Google faz-se no **navegador do sistema** (OAuth 2.0 para apps de computador, PKCE e
redirecionamento por loopback `127.0.0.1`), os dados do Éteris ficam na **pasta oculta da aplicação** (`appDataFolder`)
do Google Drive do utilizador, com o âmbito `https://www.googleapis.com/auth/drive.appdata` (não sensível), e vão
**sempre cifrados ponta a ponta no PC**: a chave nunca vai para a Google nem para o projeto Éteris. A chave passa
entre computadores por aprovação num computador já ligado (desde a 0.2.15: o computador novo mostra um código de 6
algarismos e escreve-se esse código no computador ligado) ou pelo **kit de recuperação** (24 palavras; os códigos
antigos de 56 carateres continuam aceites), que se pode guardar, imprimir e voltar a ver. Base:
`C:\cef\notas\viabilidade\viab-sync.md`, `viab-login.md` e `viab-sintese.md` (D1–D7).

**0.2.15 «Um login e pronto».** Entra-se com um clique em «Entrar com o Google» em vários sítios (menu ⋯, boas-vindas,
paleta, oferta depois de guardar a primeira palavra-passe, páginas de palavras-passe e de preenchimento, Definições);
o pedido de um computador novo faz-se sozinho e aprova-se escrevendo um número; o código de recuperação passou a kit;
«Sair deste computador» deixou de revogar o acesso na Google e há «Retirar o acesso do Éteris à conta Google» e «Perdi
um computador / foi roubado»; pagamentos e documentos passaram a ser uma escolha da conta; e voltar à janela do Éteris
é mais um momento em que o Éteris procura pedidos no Drive. As secções abaixo descrevem o comportamento atual; onde
ajuda quem lê o código antigo, «(até à 0.2.14: …)».

## O que existe

| Peça | Onde | Estado |
|---|---|---|
| Biblioteca `Eteris.Sync` (net10.0, sem WPF, sem pacotes; partilhável com o Android) | `src/Eteris.Sync` | pronta |
| Modelo de registos, IDs estáveis e opacos, HLC, lápides, fusão (notas a três) | `Model/`, `Merge/`, `Engine/` | pronta |
| Formato cifrado (lotes, instantâneos, estado, perfil exportado) | `Format/` | pronta |
| Chaves: segredo de conta, kit de recuperação (24 palavras BIP39 em português, `RecoveryCode`; o código antigo de 56 carateres continua aceite), DEK embrulhada, «nova cadeia» entregue aos que ficam | `Crypto/`, `Engine/SyncEngine.cs` | pronta |
| Emparelhamento (ECDH efémero + código de verificação), pedido sozinho pelo computador novo e aprovado escrevendo o código no computador ligado (`PairingCode`, `PairingFatigue`, `PairingRenewal`) | `Pairing/` | pronto, com barras |
| Entrar, a barra do início de sessão, a página de fim e o aviso de sessão expirada | `Google/GoogleOAuth.cs`, `Google/SignInPage.cs`; `src/Eteris.Browser/Sync/GoogleSync.cs`, `SignInEntry.cs`, `SignInBarModel.cs`, `SessionExpiredPrompt.cs` | pronto (0.2.15) |
| Kit de recuperação: ver, guardar, imprimir, copiar, lembrete | `src/Eteris.Browser/Sync/RecoveryKit.cs`, `KitGate.cs`, `RecoveryReminder.cs` | pronto (0.2.15) |
| Saídas: «Sair deste computador», «Retirar o acesso…», «Perdi um computador», «Começar do zero…» | `src/Eteris.Browser/Sync/SyncExit.cs` | pronto (0.2.15) |
| O aparelho na conta: criar, entrar, juntar, remover, apagar, sair (`SyncAgent`) e o perfil local (`ProfileSync`) | `Engine/SyncAgent.cs`, `Engine/ProfileSync.cs` | pronto |
| Quando sincronizar (`SyncSchedule`) | `Engine/SyncSchedule.cs` | pronto |
| Transportes: pasta local e Google Drive `appDataFolder` (REST, multipart e resumable) | `Transport/`, `Google/` | prontos |
| OAuth de computador (PKCE + loopback, navegador do sistema, renovação, revogação) | `Google/GoogleOAuth.cs` | pronto |
| Tokens, conta e estado da sincronização cifrados com DPAPI no perfil | `src/Eteris.Browser/Sync/Dpapi.cs` | pronto |
| O perfil do Éteris para a sincronização (ler e aplicar) | `Sync/BrowserProfile.cs`, `MainWindow.Sync.cs` | pronto |
| O serviço no navegador (agente, agendador, estado para a interface) | `Sync/SyncService.cs` | pronto |
| Definições › **Sincronização** (sempre que há `google-oauth.json`) | `settings.html`, `SettingsPage.cs` | pronta |
| A conta no menu do navegador (nome, e-mail, avatar, estado da sincronização, pedidos) | `MainWindow.BrowserMenu.cs` | pronta |
| Exportar / importar o perfil cifrado num ficheiro `.eteris` (o cofre só com a caixa e o Windows Hello) | `Sync/ProfileTransfer.cs` | pronto |
| Coleções secretas do cofre de palavras-passe (selo próprio, perfil com segredos, escolha por aparelho, tópico «O que passa entre PCs») | `Secrets/`, `Engine/ProfileSync.cs`, `Model/SyncGroups.cs`; `Vault/VaultSyncProfile.cs`, `Sync/SyncChoices.cs` | pronto (ver «Coleções secretas») |
| `google-oauth.json` na publicação (`build/publish.ps1 -GoogleOAuthJson`) | `build/publish.ps1` | pronto |
| Prova com o Google Drive real | — | fase «Prova» |
| QR para o telemóvel, abas noutros aparelhos, histórico | — | versões seguintes |
| Escolher uma pasta (OneDrive/Dropbox) como transporte na interface | — | versões seguintes (hoje só `ETERIS_TEST_SYNC_FOLDER`, para testes) |

Sem `google-oauth.json` (ao lado do `Eteris.Browser.exe` ou no perfil) a secção e o menu dizem «indisponível nesta
compilação» e nada é pedido à Google. A opção experimental «syncPreview» (0.2.10–0.2.12) saiu: um `settings.json`
antigo com ela continua a abrir, o valor é ignorado, e uma sessão Google que já existisse fica (a sincronização liga-se
quando o utilizador carregar em «Ligar a sincronização»). Exportar/importar o perfil não usa rede nenhuma.

## O que sincroniza

| Dado | Sincroniza | Notas |
|---|---|---|
| Favoritos | sim | URL e título; apagar num computador apaga nos outros (lápide) |
| Espaços | sim | nome, cor, emoji e ordem (da primeira janela). **As abas de cada Espaço ficam em cada computador**: um Espaço apagado noutro só sai deste se estiver vazio e fora de vista |
| Definições | as que viajam | `Collections.SyncableSettings`: motor de pesquisa, arranque, adormecer abas, proteções (HTTPS primeiro, cookies de terceiros, links de rastreio, saltos, CNAME, notificações, DNS cifrado, apagar ao fechar), tema, cor, densidade, abas ao lado, nova aba, leitura; o prazo do arquivo e a língua dos sites só se o utilizador os escolheu. Nunca: Mica, esconder das capturas, barra lateral |
| Escudo por site | sim | desligado num site (`off`), modo estrito (`strict`), exceção ao HTTPS primeiro (`http`), elementos escondidos (`hide`), bloqueios por site do Observatório (`rule`) |
| Notas | sim | fusão a três por linhas; ao entrar num aparelho com notas diferentes, as dele ficam por baixo das da conta com um título; não se aplicam enquanto o utilizador está a escrever |
| Histórico | **não** (nesta versão) | — |
| Abas abertas | **não** (nesta versão) | a biblioteca tem a coleção `tabs` («abas noutros aparelhos»), mas o Éteris não a publica |
| Palavras-passe e preenchimento automático | sim (o cofre próprio do Éteris), com uma **segunda cifra** por registo | sete coleções **secretas** (`logins`, `loginsNever`, `cards`, `ibans`, `contacts`, `idDocs`, `travel`): ver «Coleções secretas». Cada computador escolhe no tópico «O que passa entre PCs»; Pagamentos e Documentos de identificação são uma escolha da conta (desligados numa conta nova; ver «Pagamentos e documentos: escolha da conta»). O gestor do Chromium continua desligado (`credentials_enable_service = false`, verificação de fugas desligada); a janela privada lê o cofre para preencher e não lhe muda nada, nem a «última utilização», que por isso nunca sobe da janela privada. O histórico local das palavras-passe não sobe |
| Chaves de acesso | **não** | ficam no Windows Hello ou na chave de segurança (o WebAuthn segue para a janela do Windows: `docs/palavras-passe.md`, E2) |
| Cookies, transferências, zoom por site, permissões, leitura para mais tarde, sessões guardadas | não | — |

O que é de cada computador fica: as abas, o zoom por site, as permissões dadas a sites, a barra lateral.

## Os fluxos

- **Entrar.** Um clique em «Entrar com o Google» chega: na linha da conta do menu ⋯ («Entrar com o Google…»,
  «Sincronize favoritos e palavras-passe, cifrados ponta a ponta»), no passo «Já usa o Éteris noutro computador?» das
  boas-vindas, na paleta (Ctrl+K, «Entrar com o Google (sincronização)»), no aviso depois de guardar a primeira
  palavra-passe nova («Ter as suas palavras-passe noutros computadores?», uma só vez, marca só local), nas páginas
  `eteris://passwords/` e `eteris://autofill/`, e nas Definições › Sincronização. **A Nova aba não oferece.** Nenhum
  destes pontos faz pedidos ao aparecer: todos chamam o mesmo `SignInEntry.Begin()` (ou a linha do menu e a barra,
  `GoogleSync.BeginSignIn`) só no clique (testes `SignInEntryTests.Begin_IsOnlyCalledFromUserActions`,
  `SettingsRoute_AndTheEntryPoints_ShareOneBegin`, `NewTab_HasNoSignInEntry`, `Deciding_OnlyReadsDisk`). Numa janela
  privada não há conta: nenhum ponto de entrada aparece e as rotas de conta respondem «recusado». Sem
  `google-oauth.json` na instalação a sincronização fica indisponível, nenhum ponto de entrada aparece e nada é pedido
  à Google.
  O ecrã da Google abre no navegador do sistema com a escolha de conta (`prompt=select_account`, ver «Início de
  sessão»). Enquanto se espera, uma **barra** na janela do Éteris diz o que está a acontecer: «À espera do início de
  sessão no seu navegador…» [Cancelar]; sem navegador, «Abra este endereço noutro navegador para entrar na Google.»
  [Copiar endereço]; depois «Sessão iniciada como *nome*. A sincronizar…» e «Sincronizado.», que some sozinha (4 s;
  12 s com uma ligação); os erros («O início de sessão demorou demasiado.», «A Google não deu autorização ao Éteris.»,
  «Entrou, mas sem a permissão do Drive o Éteris não sincroniza.», «Sem ligação à Google. Tente de novo.») ficam 20 s
  com «Tentar outra vez». A barra nunca rouba o foco do teclado, não tem animações e **nunca aparece em janelas
  privadas**. Há uma barra de cada vez: pedido de aprovação, conta substituída, início de sessão (ou o cartão do
  computador novo), sessão expirada, lembrete do kit e oferta de pagamentos e documentos, por esta ordem
  (`WindowBars.Pick`). No fim, a página no navegador do sistema diz o que falta (seis textos: «Pronto. O Éteris começou
  a sincronizar…», «Falta um passo no Éteris…», «O Éteris retomou a sincronização…», a falta da permissão do Drive, a
  neutra e a recusa), e a janela do Éteris pisca na barra de tarefas (e vem para a frente se o Windows deixar).
- **Primeiro computador.** Depois de entrar (ou de «Ligar a sincronização»), se a pasta da aplicação não tiver nenhuma
  conta (`eteris/<cadeia>/keys`), o Éteris cria-a e envia os dados deste computador. **Nada é pedido nem bloqueia ao
  criar a conta.** O segredo da conta fica guardado neste computador e vê-se como **kit de recuperação**: 24 palavras da
  lista BIP39 em português (`RecoveryCode.WordCount` = 24; `AccountSecret.ToWords()`). Em Definições › Sincronização ›
  «Kit de recuperação»: «Ver o kit» (pede **sempre** a confirmação do Windows, `ReauthPurpose.ShowRecoveryKit`; aberto,
  fica à vista no máximo 5 minutos, `KitUnlock.Limit`, e esconde-se ao mudar de aba), «Guardar como ficheiro…»,
  «Imprimir» e «Copiar». Copiar usa a área de transferência segura: o kit não fica no histórico nem na nuvem da área de
  transferência e sai ao fim de 60 s. A página diz «O seu kit atual termina em XXXX» (uma impressão digital de 4
  carateres, nunca o kit). O kit antigo deixa de valer quando se remove um computador ou se gera um kit novo: as
  Definições dizem-no e pedem que se guarde o novo.
  - **O ficheiro do kit.** O Éteris nunca envia o kit a ninguém: o ficheiro fica onde o guardar. Se essa pasta for
    sincronizada com uma nuvem (OneDrive, Google Drive, Dropbox), o kit vai com ela — o Éteris avisa antes de gravar
    numa pasta dessas, ou em qualquer pasta que não reconheça como deste computador (`RecoveryKit.Folder`,
    `RecoveryKit.FolderWarning`). O mesmo vale para «Imprimir para PDF» (é um ficheiro) e para uma impressora de rede.
  - **O lembrete.** Enquanto houver um só computador na conta e o kit não estiver guardado, uma barra na janela diz
    «Guarde o seu kit de recuperação. Se perder este computador, é com ele que recupera, na nuvem, os favoritos e as
    palavras-passe.» [Guardar o kit] [Mais tarde] [Não quero…]. O lembrete volta de 30 em 30 dias enquanto houver um só
    computador na conta, e tem a opção «Não quero: aceito que, sem este computador, a cópia na nuvem não se recupera».
    (`RecoveryReminder.SnoozeDays` = 30; «Não quero» grava-se e o lembrete não volta; uma troca de chave repõe-no.) A
    linha do menu diz «Guarde o kit de recuperação» enquanto o kit não estiver guardado nem recusado.
  - O kit sozinho não abre nada: também é preciso entrar na sua conta Google (o kit desembrulha a chave que está no
    Drive). E a Google sozinha também não (não tem a chave).
  - (Até à 0.2.14: o código de recuperação de 14 grupos de 4 aparecia uma vez, com «Copiar» e «Já guardei», e nunca
    mais.)
- **Computador novo.** Depois de entrar num computador **sem nenhum estado desta conta**, se a conta já existir no
  Drive, o pedido de aprovação abre-se sozinho na ronda do gesto (`SyncAgent.AutoPair`, `SyncAgent.MayAutoRequest`; uma
  ronda agendada nunca pede). Um computador que já esteve numa conta e encontra outra cadeia no Drive **não pede
  sozinho**: mostra o alerta «a conta foi apagada ou substituída» e pede uma decisão («Fui eu» pede o Windows com
  segredos ligados neste computador; «Não fui eu» leva a «Retirar o acesso»). O computador novo mostra um cartão:
  «Falta um passo no outro computador.» e, quando o outro responde, «Escreva este código no outro computador:» com o
  código de 6 algarismos; depois «Escreveu … no outro computador e ele aceitou?» [Sim] [Não] («Sim» só conta 2 s depois
  de o código aparecer). Enquanto espera, procura a resposta no Drive de 3 em 3 s (`SyncAgent.PairingPoll`); se ninguém
  responder em 10 minutos, renova o pedido sozinho só com o cartão ou a secção à vista, até 30 minutos e no máximo três
  sessões por gesto (`PairingRenewal.Window`, `MaxRenewals`); depois, «Ainda à espera?» com [Pedir outra vez].
  Nos computadores ligados, o pedido aparece numa **barra** («Um computador quer entrar na sua conta e receber as suas
  palavras-passe.» e «Se foi você, escreva o código que ele mostra:») ou nas Definições › Sincronização (a mesma caixa,
  só com o teclado: Tab até à caixa, os 6 algarismos e Enter). **Aprovar é escrever no computador ligado o código que o
  novo mostra**: o computador ligado nunca mostra o código (`SyncAgent.ApproveWithCodeAsync`). Três códigos errados
  recusam o pedido (`PairingCode.MaxWrong` = 3); um código incompleto não conta. «Não fui eu» recusa e aconselha a mudar
  a palavra-passe da Google. Com segredos em jogo, a confirmação do Windows vem **depois** do código certo e antes de a
  conta seguir (`SyncChoices.MayApproveAsync`). Depois de 3 pedidos recusados, expirados depois de respondidos ou com
  códigos errados em 24 h (`PairingFatigue.MaxBad`, `PairingFatigue.Window`) a barra deixa de abrir sozinha e o aviso
  fica nas Definições, com «Mostrar o pedido na barra». O nome do computador novo («Nome que o outro computador
  indicou: …») não é prova de nada: a barra mostra-o pequeno, sem algarismos e cortado. Escrever o código só protege se
  for o código que está a ver no seu outro computador: um código que alguém lhe dite por telefone ou envie por mensagem
  não deve ser escrito.
  Ou, sem o outro computador à mão: **«Usar o kit de recuperação»** (as 24 palavras, coladas ou abertas do ficheiro;
  os códigos antigos de 56 carateres continuam aceites; no corpo de um POST, nunca no endereço).
  Os ficheiros do pedido (`eteris/pair/<sessão>/0-request`, `1-host`, `2-guest`…, `x-host`) têm nomes legíveis e levam
  só a hora, chaves públicas de uso único e números aleatórios; o nome do computador novo vai cifrado com a chave da
  sessão. Dois computadores ligados que respondam ao mesmo pedido não se atrapalham: o novo diz a que chave de
  anfitrião se compromete (`for` no passo 2) e o outro desiste calado.
  Depois, **junta os dados sem perder nada**: o que só existia neste PC sobe; o que já existe na conta vale
  (definições, títulos, Espaços); notas diferentes juntam-se.
- **O nome do computador.** Por omissão «Éteris em *nome do Windows*» (`DeviceNameText.Default`); muda-se em
  Definições › Sincronização › Mais opções › «Nome deste computador». O nome viaja sempre cifrado: no passo 4 do
  emparelhamento (com a chave da sessão), no estado do aparelho e nos lotes (com a chave da conta); o `0-request` leva só
  a hora. Os outros computadores veem o nome novo na ronda seguinte.
- **Alterações** nos dois sentidos, com conflitos resolvidos por HLC (ganha a última ronda que as enviou) e lápides
  (o apagado não volta). O que o utilizador muda a meio de uma ronda que traz outra versão fica e segue na seguinte.
- **Aparelhos**: lista nas Definições › Sincronização › Mais opções (nome, «este computador», quando sincronizou, «·
  recebe pagamentos e documentos») com **«Remover»** (duas vezes, para confirmar). Remover faz **nova cadeia**: segredo
  e chaves novos, tudo recifrado, a cadeia antiga apagada, e um kit de recuperação novo (o antigo deixa de valer, em
  todos os computadores). Os aparelhos que ficam recebem a conta nova: cada aparelho tem uma
  chave estática P-256 (no estado local, DPAPI) cuja parte pública vai no seu estado cifrado; os outros fixam-na na
  primeira vez que a veem; a conta nova vai em `eteris/<cadeia antiga>/r/<aparelho>/<de>`, cifrada com ECDH entre as
  chaves estáticas de quem remove e de quem fica. Um aparelho que fica **segue sozinho só quando quem escreveu a
  mensagem é a única chave fixada nele** (a conta de dois computadores; `SyncEngine.RekeyIsUnambiguous`). Senão fica
  **em pausa, sem perder nada** (o estado e a base ficam guardados; não escreve nada em conta nenhuma), até se escrever
  nele o **identificador do kit novo**: os 4 caracteres de «O seu kit atual termina em…», lidos nas Definições do
  computador onde se fez a troca (`SyncAgent.ConfirmRekeyAsync`). O identificador deriva do segredo novo, que não está
  no Drive. O removido não tem mensagem para si: para — «a sincronização parou… este computador foi removido noutro
  aparelho». Consegue escrever uma mensagem sua para os outros (tem a chave deles fixada, e eles a dele) e apagar a
  verdadeira enquanto tiver acesso ao Drive, mas não conhece o identificador: a conta que deixar tem outro. Três
  identificadores errados param o aparelho, que se junta outra vez (kit novo ou aprovação); «Não tenho o identificador:
  juntar outra vez» faz o mesmo sem tentar. O aviso de quem fica usa o nome que este aparelho já conhecia do
  remetente, não o que vem na mensagem.
  Pormenores que a revisão do login completo acrescentou:
  - **Antes de trocar a chave, uma ronda completa** (o que os outros enviaram e este ainda não leu entra no estado que
    passa para a conta nova); e quem recebe a conta nova volta a pôr nela o que escreveu na antiga, com a hora original
    e as lápides (`RestageOwn`): o LWW decide contra o instantâneo de quem trocou a chave, e nada se perde nem ressuscita.
  - **Em dois passos**: a conta nova é criada sem tocar na antiga e **guardada** (segredo, estado e `RotationPending`)
    antes de se escreverem as mensagens `r/` e de se apagar a cadeia antiga — o registo de chaves em último lugar, porque
    é a falta dele que manda os outros procurar a mensagem. Uma falha a meio acaba na ronda seguinte.
  - **O registo de chaves presente não prende ninguém na cadeia antiga**: em cada ronda, quem fica olha também para as
    mensagens `r/` que lhe são dirigidas (na listagem que a ronda já faz, sem pedidos a mais quando não há nenhuma), e
    uma mensagem válida conta como a troca mesmo com o registo lá — o removido tem o segredo antigo e podia voltar a
    escrevê-lo. Por isso, numa troca interrompida antes de o registo sair, quem fica segue logo para a conta nova.
  - **Só os aparelhos que o utilizador viu na lista e com a chave fixada antes** recebem a conta nova (a troca não fixa
    chaves novas: um estado `a/` inventado à pressa pelo removido não entra); se aparecer outro, o aviso diz o nome.
    Depois da troca, os que ficaram continuam na lista (pela chave fixada) até escreverem o seu estado na conta nova.
  - A mensagem diz **quem foi removido e quem fica**: um remetente que outra mensagem válida dá como removido é recusado
    (o removido também tinha as chaves fixadas e podia deixar uma «conta nova» dele); remetentes que se contradizem, ou
    contas novas diferentes, **param sem escolher** (junta-se outra vez com o kit ou por aprovação); as chaves do
    removido não passam para a conta nova. As mensagens só saem do Drive **depois** de a conta nova ficar guardada, e um
    aparelho desligado durante **duas remoções seguidas** segue as duas (até 8 saltos).
  - **«Gerar um kit novo…»** (Definições, com confirmação; com segredos em jogo pede a confirmação do Windows): a mesma
    troca de chave, sem tirar ninguém. (Até à 0.2.14: «Gerar um código de recuperação novo».)
- **Parar não apaga a chave.** Conta desaparecida (dados apagados, este aparelho removido, outra conta Google), registo
  de chaves que não abre (alterado ou danificado) ou outra conta Google: a sincronização para ou fica em pausa, a chave
  fica (e, nos dois últimos casos, o estado todo). O estado guarda uma impressão digital da conta Google (não o e-mail):
  com outra sessão, «A sincronização está em pausa: a sessão é de outra conta Google…» e nada se mexe até o utilizador
  voltar à conta certa (retoma sem código) ou carregar em «Ligar outra vez» para usar esta.
- **Uma leitura que falha não apaga nada.** Favoritos, Escudo, notas ou Espaços que não se conseguem ler numa ronda
  (base de dados ocupada, ficheiro bloqueado pelo antivírus, sem janela nem sessão gravada) ficam de fora dessa ronda —
  sem remoções e com a base como estava — em vez de contarem como «vazios» (o que apagava em todos os aparelhos).
- **Escudo numa janela privada**: desligar o Escudo num site, o modo estrito e «esconder um elemento» numa janela
  privada ficam só em memória até a sessão privada acabar (como os bloqueios por site e a exceção ao HTTPS primeiro):
  nem vão para o perfil, nem sincronizam.
- **Dois computadores a criar a conta ao mesmo tempo** (Drive vazio para os dois): na primeira meia hora, enquanto
  estiver sozinho na conta que criou, cada um confere se há outra; a de identificador menor fica e o outro sai da sua
  (apaga-a do Drive) e volta a «escolher como entrar», sem perder nada deste computador.
- **«Sair deste computador»** (Definições › Sincronização › Mais opções e, desde a 0.2.17, o «Sair» à direita do
  cartão da conta no menu ⋯; as duas pela mesma função, `SyncService.SignOutHere`): a sincronização para neste computador e os
  tokens e a conta guardada **deste computador** são apagados. «Sair deste computador» não revoga nada e não faz
  nenhum pedido à Google (zero pedidos de rede; teste `GoogleSyncTests.SignOutRoute_NeverRevokes`): os outros
  computadores continuam ligados. Sai só deste computador. A Google continua a listar o Éteris na sua conta até usar
  «Retirar o acesso». A chave da sincronização e os dados ficam neste computador, a não ser que marque «apagar também a
  chave». A autorização dada à Google continua válida até a retirar (o token deste computador deixa de estar no disco,
  mas não é revogado). Com «Ao sair, apagar também a chave da sincronização deste computador», o estado local sai todo
  e voltar exige o kit ou outro computador (com o kit ainda por guardar, pede confirmação); sem ela, entrar outra vez
  retoma sem kit nem aprovação. Uma ronda a meio não volta a gravar a chave depois de «Sair». (Até à 0.2.14: «Sair»
  também revogava os tokens na Google.)
- **«Retirar o acesso do Éteris à conta Google»** (Definições › Sincronização › Mais opções, com confirmação na página;
  não existe na pasta de teste): revoga a autorização na Google (`oauth2.googleapis.com/revoke`) e desliga todos os
  computadores — os outros só dão por isso na ronda seguinte (não é instantâneo) e passam a «Sincronização em pausa:
  entre outra vez». Os dados no Drive e a chave deste computador ficam. O Éteris só diz que o acesso «foi retirado»
  quando a Google o confirma (`RevokeOutcome.Revoked`); se a sessão deste computador na Google já não era válida, diz que
  não conseguiu confirmar e manda entrar outra vez e repetir, ou usar myaccount.google.com/permissions
  (`SyncExit.RevokeUnconfirmed`); se a Google não responder, nada se apaga e diz para tentar outra vez.
- **«Perdi um computador / foi roubado»** (Definições › Sincronização › Mais opções › Aparelhos ligados; pede sempre a
  confirmação do Windows, `ReauthPurpose.LostDevice`): primeiro remove o aparelho e troca a chave da conta, e só com a
  troca acabada retira o acesso do Éteris na Google (`SyncExit.LostDeviceAsync`). O que já estava nesse computador
  continua lá: daqui em diante ele não recebe nada novo. O Éteris diz também: «O aparelho removido já tinha as suas
  palavras-passe: mude as dos sites importantes.» e, se tinha cartões ou documentos, «trate-os como vistos»; «Se a sua
  conta Google estava aberta num navegador desse computador, termine essa sessão na conta Google e mude a palavra-passe
  da Google»; e que este computador também saiu da conta Google e que o kit antigo deixou de valer: entra-se outra vez
  em cada computador e guarda-se o kit novo. Se a Google não confirmar a revogação, o Éteris diz que o aparelho foi
  removido mas o acesso não ficou confirmado como retirado (`SyncExit.LostNotRevoked`, `SyncExit.LostUnconfirmed`).
  Na pasta de teste só há o passo 1.
- **«Começar do zero…»** (no ecrã de entrada, só antes de este computador entrar numa conta; pede que se escreva
  «começar do zero» e sempre a confirmação do Windows, `ReauthPurpose.StartOver`): apaga a conta antiga da nuvem (o
  mesmo «Apagar os meus dados do Éteris no Google Drive») e cria uma nova a partir deste computador. O que só estava na
  conta antiga perde-se; o que está neste computador fica e passa para a conta nova. Os outros computadores dizem que a
  conta foi apagada ou substituída e não pedem nada sozinhos.
- **Pagamentos e documentos: escolha da conta.** «Também pagamentos e documentos de identificação» (Definições ›
  Sincronização › O que passa entre PCs) liga cartões, IBAN e documentos para a conta inteira: ligar pede a confirmação
  do Windows **no computador onde se liga** (`ReauthPurpose.SyncSecrets`) e grava a escolha, cifrada, num registo
  reservado da conta (`@account/secret-groups`, campos `value`, `since` e `by`; só `"on"` liga; um Éteris 0.2.14
  ignora-o — provado por imitação, `SyncAccountChoiceTests.OlderEteris_IgnoresTheRecord`). Um computador **novo** (sem
  estado de sincronização anterior) que entra depois herda a escolha sem pedir de novo e diz «Recebeu favoritos,
  palavras-passe, cartões e documentos». Um computador que já estava ligado — ou que já esteve numa conta e entra noutra
  (por exemplo depois do alerta de conta substituída) — recebe uma oferta única: «Os seus outros computadores
  sincronizam agora pagamentos e documentos. Receber também neste?» [Sim] [Não]; só recebe com «Sim» e a confirmação do
  Windows. Desligar a linha neste computador só o desliga aqui e não apaga nada. A escolha é de quem tem a chave da
  conta: a confirmação do Windows pedida num computador protege esse computador, não é verificável pelos outros. O
  estado de cada aparelho leva, cifrado, o campo `groups` (os grupos que recebe).
- **Sessão expirada.** Quando a Google deixa de aceitar a sessão (`invalid_grant`, autorização retirada), aparece na
  janela, uma vez por abertura do Éteris, «A sincronização está em pausa: a Google pediu para entrar outra vez.»
  [Entrar] [Agora não]; nunca em janelas privadas nem na pasta de teste. «Entrar» pré-escolhe a conta (`login_hint`, só
  na reentrada; ver «Início de sessão») e retoma com a chave guardada, sem kit nem aprovação. O aviso não faz nenhum
  pedido novo: é descoberto pela ronda que já existia.
- **«Apagar os meus dados do Éteris no Google Drive»** (com confirmação): apaga todos os ficheiros do Éteris da pasta
  da aplicação (todas as contas e pedidos, e os duplicados com o mesmo nome; lista outra vez até a pasta ficar vazia, no
  máximo 4 passagens), **sem revogar** nem sair da conta Google. A confirmação aparece por baixo, com «Cancelar» no
  lugar onde se carregou, não aceita o 2.º clique de um duplo clique e desarma-se ao fim de 10 s. Os dados deste computador ficam;
  a sincronização fica desligada aqui e, nos outros aparelhos, para na ronda seguinte (não recriam a conta sozinhos,
  o que desfaria o «apagar»). «Ligar outra vez» cria uma conta nova.
  A ordem protege o «apagar»: a rota grava logo (`SyncAgent.BeginCloudDeletion`, antes de responder à página e de
  qualquer pedido; o `sync.state` é escrito até ao disco antes de trocar) a sincronização desligada, a chave e o motor
  esquecidos e `deleting: true`; para o agendador e os emparelhamentos a meio; só depois `FinishCloudDeletionAsync`
  apaga (à espera de uma ronda em curso, que já não grava — `IsCurrent` — e cujo envio, se o houve, sai com o resto).
  Se o Éteris fechar a meio, ao abrir a sincronização continua desligada (nenhuma ronda, nem forçada, corre) e
  `AfterFirstPaint` acaba de apagar; «Ligar outra vez» acaba primeiro e só depois cria a conta nova. Se não conseguir
  acabar, diz-o na nota de «Apagar…» e tenta outra vez ao abrir.
  Qualquer falha do apagar fica à vista: `FinishCloudDeletionAsync` transforma toda a exceção (de rede, da sessão ou
  inesperada — uma lista ilegível, um estado estranho) numa `SyncUserException` com o porquê e guarda-o em
  `SyncStatus.DeleteError`; a página deixa de perguntar de 1,5 em 1,5 s, mostra o porquê com «Tentar de novo»
  (`/sync-delete-retry`, sem outra confirmação) e o menu diz «Por apagar do Drive: veja Definições › Sincronização».
  «Ligar outra vez» com o apagar por acabar tenta acabá-lo primeiro; se falhar, diz porquê e o botão passa a «Ligar sem
  acabar de apagar» (`/sync-start?anyway=1` → `AbandonCloudDeletion`): o que ficou no transporte fica lá e, se o
  registo de chaves da conta antiga ainda lá estiver, o computador pede para escolher como entrar (não há caminho sem
  saída). Nomes inesperados não travam o apagar: na pasta, as «cópias em conflito» dentro de `eteris/` («keys (1)»,
  «… (Cópia em conflito de …)») são do Éteris e apagam-se (`FolderTransport.DeleteAsync` aceita os nomes que ela própria
  lista dentro de `eteris/`, sem «..», separadores, «:» nem carateres proibidos; ler e escrever continuam só com os nomes
  do esquema); um nome que o transporte recuse apagar (`ArgumentException`) fica de fora, com registo, e o aviso final
  diz quantos ficaram. No Drive, uma resposta que não é JSON é uma falha de rede (passa sozinha). Uma entrada a meio
  (`JoinWithCodeAsync`, ou retomar com a chave guardada) confere, como as rondas, que o estado é o mesmo e que não
  começou um apagar nem «Sair» antes de gravar: depois de `BeginCloudDeletion` nada volta a gravar `on`, a chave ou o motor.

## Quando sincroniza

| Momento | Porquê |
|---|---|
| **30 s depois de a janela estar pronta** (1.ª página), só com a sincronização ligada — ou antes, 10 s depois de uma alteração feita nesses 30 s | o arranque fica para a janela e as páginas; quem abre o Éteris só para ver uma coisa e o fecha logo não fala com a Google; a promessa «No arranque, nada sai para a Google» mantém-se no sentido do motor e a exceção da sincronização fica dita (ver «Promessa») |
| **10 s depois da última alteração local** (favorito, definição, Escudo, Espaço, notas) | espera agregadora: várias alterações seguidas vão num lote; e só há rede se a comparação local (sem rede) encontrar alguma coisa por enviar |
| **De 10 em 10 minutos** com o Éteris aberto | para receber o que os outros aparelhos mudaram |
| **Ao entrar**, ao ligar, ao entrar num aparelho novo, e em **«Sincronizar agora»** | ação do utilizador |
| Ao abrir o menu do Éteris (⋯), ou com a secção Sincronização à vista (depois de um gesto; de 5 em 5 s, até 10 min depois do último gesto), ou em «Ver» na barra de um pedido contado | procura pedidos de computadores novos e responde-lhes (e relê a lista de aparelhos nas Definições); ao responder a um pedido, acompanha-o de 2 em 2 s até ele acabar (no máximo 10 min) |
| **Ao voltar à janela do Éteris** (no máximo uma vez a cada 5 minutos, ou logo se uma ronda já tiver encontrado um pedido) — só com sessão e a sincronização ligada, nunca antes da 1.ª ronda desta abertura, nunca numa janela privada | procura pedidos de computadores novos (`ActivationLook.Interval` = 5 min; decisão D8′) |
| Numa ronda agendada | os pedidos de computadores novos só se **contam** (para o menu dizer «Um computador novo pede para entrar» e a barra mostrar «Um computador novo quer entrar na sua conta.» com «Ver»); não se responde a nenhum sem um gesto |
| No computador novo, à espera de aprovação (parte da ação «entrar») | procura a resposta no Drive de 3 em 3 s; o pedido dura 10 min e renova-se sozinho só com o cartão ou a secção à vista, até 30 min e no máximo três sessões por gesto |
| Uma vez depois de cada entrada (ou ao abrir o menu ou a secção, com a sincronização ligada, se a conta ainda não estiver guardada) | o nome e o e-mail da conta (`about.get`) |
| Quando o token de acesso expira (cerca de 1 h), no próximo pedido | renovação da sessão em `oauth2.googleapis.com/token` |
| Nas ações que o utilizador pede: entrar, sincronizar agora, aprovar ou recusar um computador, remover um aparelho (e «Perdi um computador»), gerar um kit novo (troca a chave da conta no Drive), ligar uma linha de «O que passa entre PCs» ou aceitar pagamentos e documentos (fazem logo uma ronda), apagar os dados do Drive (e «Começar do zero…») e retirar o acesso do Éteris à conta Google | ação do utilizador. **Sair deste computador não fala com a Google.** Mudar o nome do computador só grava e segue na ronda seguinte |

Na última linha, «entrar» inclui ligar («Ligar a sincronização», «Ligar outra vez», «Ligar sem acabar de apagar»), usar
o kit de recuperação (escrito ou do ficheiro), «Aprovar noutro computador», «Pedir outra vez» e «Fui eu — juntar este
computador»; «apagar os dados do Drive» inclui «Tentar de novo» e «Começar do zero…». A exceção de `eteris://privacy/`
(`PrivacyPage.SyncException`) usa as mesmas palavras.

Esta lista é a do teste de varrimento `SyncIntegrationTests.NetworkMoments_ClosedListOfCallers` (quem chama
`SyncService.Look(` e `WindowActivated(`: a rota `sync-look` das Definições, o menu, a barra de aprovação e a ativação da
janela) e a de `PrivacyPage.SyncException` (a exceção que `eteris://privacy/` mostra com a sincronização ligada). Quem
acrescentar um momento de rede acrescenta-o a esse teste e à exceção (e a esta tabela); os números são os das
constantes (`SyncSchedule.StartupDelay`, `ChangeDelay`, `Period`, `PairingProtocol.Lifetime`, `PollInterval`,
`ActivationLook.Interval`), que `PrivacyPageTests.SyncException_NumbersMatchTheCode` confere.

Sem rede, com a Google em baixo ou a pasta inacessível: tenta outra vez sozinho, com espera crescente (30 s, 1, 2, 4,
8, 16 e depois de 30 em 30 min), sem avisos; as alterações locais não encurtam a espera; o que estava por enviar fica
na caixa de saída (no estado guardado) e segue depois. Uma compactação (instantâneo) por dia. Se a **autorização da
Google tiver expirado ou sido retirada** (`invalid_grant`, permissão desmarcada), não tenta mais: o agendador para, nem
procura pedidos nem relê os aparelhos, o menu diz «Sincronização em pausa: entre outra vez» e as Definições mostram «Entrar outra vez» (sem revogar nada nem
mexer na chave), como o «Sincronização em pausa · Iniciar sessão novamente» do Chrome.

**Estado visível:** Definições › Sincronização, à frente, «Ligada · última sincronização há 2 min» («A ligar…», «A
sincronizar…», «Ligada · a primeira sincronização está a caminho»; `SyncService.Headline`), o erro em claro e, em «Mais
opções», o resto; e a linha da conta no menu («Sincronizado há 2 min», «A sincronizar…», «Escreva o código 123 456 no
outro computador», «Um computador novo pede para entrar», «Guarde o kit de recuperação», «Por sincronizar: o Éteris
tenta outra vez sozinho», «Sincronização em pausa: entre outra vez»).

## Pasta de teste (`ETERIS_TEST_SYNC_FOLDER`)

Só para os testes ao vivo: `ETERIS_TEST_SYNC_FOLDER=<pasta>` no ambiente do processo troca o Google Drive por uma pasta
local (`FolderTransport`, nenhum pedido de rede). A «conta» é a pasta: «Ligar» e «Sair» sem Google; o nome do aparelho
leva o nome do perfil (dois perfis de teste no mesmo PC). **A versão instalada ignora-a** (`SyncService.TestFolderFor`:
há um `Update.exe` do Velopack na pasta de cima), como as outras `ETERIS_TEST_*` são só para testes.

**Prova ao vivo (2026-11-08, compilação do ramo `sync`, perfis `profile-sinc-a` e `profile-sinc-b` na mesma pasta):**
A liga e cria a conta (código de recuperação à vista, «Já guardei»); um favorito marcado em A segue 10 s depois (lote
novo na pasta); B liga e vê «Este Google Drive já tem dados do Éteris»; «Aprovar noutro computador» em B, o pedido
aparece em A com o mesmo código (304 964) nos dois; confirmado em B e aprovado em A, B entra e junta (os dois ficam com
os dois favoritos, densidade compacta de A; o tema claro de B dá lugar ao da conta); depois o tema mudado em B chega a
A; A remove B (código novo em A) e B para; B «Ligar outra vez» + código de recuperação novo (em minúsculas) entra outra
vez; um favorito apagado em A sai de B; «Apagar» em A deixa a pasta vazia e B para na ronda seguinte, com os favoritos
dele intactos. Reaberto, A só escreve na pasta **31,6 s** depois de o processo arrancar (30 s depois da 1.ª página).
Capturas em `scratchpad/sinc-live`. A prova com o Google Drive real fica para a fase «Prova».

## Desenho

### Registos e coleções

Cada dado sincronizado é um **registo**: coleção, ID opaco, chave natural, versão (HLC), lápide e campos (JSON). Os
campos desconhecidos (escritos por um Éteris mais recente) são guardados e reenviados tal e qual.

| Coleção | Chave natural | Fusão |
|---|---|---|
| `bookmarks` (favoritos) | URL | LWW por favorito, com lápides |
| `spaces` (Espaços) | nome do Espaço (até os Espaços terem ID próprio) | LWW (nome, cor, ícone, ordem) |
| `tabs` (abas abertas) | ID do aparelho | instantâneo por aparelho, nunca fundido («abas noutros aparelhos») |
| `settings` (definições) | nome da definição | LWW; só a lista `Collections.SyncableSettings` |
| `shield` (Escudo) | `tipo\|site\|valor` (`off`, `strict`, `hide`, `rule`, `http`) | conjunto com lápides (LWW por elemento) |
| `notes` (notas) | `notes` | fusão a três por linhas (diff3); conflito = as duas versões marcadas |

Definições que **não** viajam: Mica, esconder das capturas, barra lateral (aberta, painel, largura, lado), «Pedir o
Windows Hello ao preencher…» (palavras-passe e pagamentos) e o que só vale neste computador. «Oferecer para guardar
palavras-passe», «Mostrar sugestões ao clicar num campo» e o «Preencher automaticamente…» de cada secção do
preenchimento automático viajam. O prazo do arquivo e a língua dos sites só viajam se o utilizador os escolheu.

- **IDs opacos e estáveis:** `HMAC-SHA256(K_ids, coleção ‖ chave natural)`, 128 bits. O mesmo URL dá o mesmo ID em
  todos os aparelhos da conta; sem a chave não se liga um ID a um URL.
- **HLC** (relógio lógico híbrido: milissegundos + contador + aparelho): ordem total igual em todos os aparelhos e, ao
  contrário da hora pura, um aparelho com o relógio atrasado que já viu uma alteração escreve sempre «depois» dela.
- **Lápides** com prazo (90 dias). Na compactação saem as mais velhas; um aparelho ausente há mais do que o prazo
  recomeça dos instantâneos antes de enviar seja o que for, por isso não ressuscita o que os outros apagaram. Ao
  recomeçar, guarda o que ele próprio já enviou e nenhum instantâneo cobre (o motor lembra em que lote seguiu cada
  registo), e um registo só conta como «apagado» se faltar num instantâneo mais recente que viu tudo o que o mais
  antigo viu (de todos os aparelhos) — não só por o autor ainda não ter lido o lote que o criou.
- **Notas:** cada versão tem identidade = texto + de onde veio (como um commit do git). Edições concorrentes fundem-se
  a partir do antepassado comum mais recente; os dois lados entram por ordem de HLC, por isso todos os aparelhos chegam
  ao mesmo texto. Voltar a um texto antigo é uma versão nova (não «já visto»).

### Registo só de acréscimo

No transporte, debaixo de `eteris/<cadeia>/`:

```
keys                       registo de chaves (DEK embrulhada; único ficheiro com JSON em claro, sem segredos)
d/<aparelho>/<n.º>         lote imutável n.º 1, 2, 3… de cada aparelho
s/<aparelho>-<n.º>         instantâneo (compactação): estado inteiro + o que cobre + horizonte das lápides
a/<aparelho>               estado do aparelho (nome e tipo cifrados; o que já leu de cada um)
eteris/pair/<sessão>/…     mensagens de um emparelhamento (apagadas no fim)
```

- Cada aparelho só escreve ficheiros **seus**, e os lotes nunca são reescritos: o cliente da nuvem não vê duas escritas
  no mesmo ficheiro e não há «cópias em conflito» (as que aparecerem são ignoradas).
- Um lote que não se decifra (adulterado, a meio de chegar, ou de um formato mais recente) **nunca é marcado como lido**:
  fica pendente e é tentado na próxima ronda; os seguintes desse aparelho esperam por ele. Lotes que chegam fora de
  ordem (o OneDrive faz isso) esperam pelo que falta.
- Um envio que falha é repetido **igual** (mesmo número, mesmo conteúdo).
- Ao retomar, o transporte manda nos números: se o estado local veio de uma cópia antiga (cópia de segurança, outro
  disco), os lotes deste aparelho que ela não conhece são lidos de volta, o próximo número passa à frente deles e um
  lote pendente com número já usado segue com um número novo — nenhum número é reutilizado.
- A compactação apaga os lotes que todos os aparelhos conhecidos já leram, ou mais velhos do que o prazo das lápides
  (estes cobertos pelo instantâneo que acabou de escrever, que tem os registos deles).

### Cifra

- **Segredo de conta R:** 256 bits do gerador do sistema (nunca de uma senha). Escrito como **kit de recuperação**:
  24 palavras da lista BIP39 em português (256 bits + 8 de soma de controlo, 11 bits por palavra;
  `RecoveryCode.EncodeWords`, lista embutida em `Eteris.Sync.dll` com o SHA-256 fixado num teste), lidas com tolerância
  (maiúsculas, espaços, quebras de linha, vírgulas, numeração, acentos; cada palavra pelas 4 primeiras letras). O
  formato antigo continua aceite: **código de recuperação** de 13 grupos de 4 carateres base32 Crockford + 1 grupo de
  controlo (20 bits de SHA-256), lido com tolerância (minúsculas, espaços, O→0, I/L→1); uma gralha é recusada.
- `KEK = HKDF-SHA256(R, "kek")`, `cadeia = HKDF(R, "chain-id")` (nome opaco da pasta).
- **DEK** aleatória, embrulhada pela KEK com AES-256-GCM (cadeia e geração como dados associados).
- Por ficheiro: `chave = HKDF(DEK, "file:<tipo>:<contexto>")` — uma chave por ficheiro, nonce aleatório de 96 bits.
- **Ficheiro** (`Format/SealedFile.cs`): `"ETS" | tipo | versão | geração | opções | parte pública | nonce | cifra | etiqueta`.
  Conteúdo JSON → Brotli → **preenchido** (múltiplo de 1 KiB e, nos grandes, o «balde» Padmé do tamanho: só os bits de
  cima ficam, no máximo ~12 % a mais) → AES-256-GCM. Dados associados = cabeçalho + cadeia + nome lógico (aparelho e
  número): trocar, reordenar ou mudar de nome um ficheiro falha a verificação.
- **O que o tamanho ainda diz:** a ordem de grandeza. Um instantâneo tem o estado inteiro e cresce com o perfil
  (centenas de KiB num perfil grande), por isso quem vê os tamanhos sabe se o perfil é pequeno ou grande — não o número
  exato de favoritos, nem o tamanho de uma edição (os lotes pequenos ficam todos em 1–2 KiB).
- **Sem compressão com abas abertas:** um ficheiro que leve a coleção `tabs` não é comprimido. Os títulos das abas são
  escolhidos pelos sites e mudam a cada envio; comprimidos no mesmo bloco que URLs com segredos (tokens, ligações de
  redefinição de senha), abriam um canal lateral de compressão (tipo CRIME/BREACH) a quem vê os tamanhos. A opção vai
  no cabeçalho autenticado.
- **Versões:** um ficheiro de formato mais recente fica pendente (não é lido mal nem marcado como lido); registos com
  campos novos são preservados. A versão no cabeçalho de um ficheiro lê-se antes de o decifrar, por isso sozinha **não**
  faz o Éteris pedir uma atualização (quem controla o transporte podia mudá-la e usar isso como isco: «descarregue esta
  atualização»). «Atualize o Éteris» só aparece quando o registo de chaves anuncia um formato mais recente, e esse
  formato vai nos dados associados do embrulho da DEK: só um aparelho com o código o consegue escrever.
- **Nonces:** a chave de cada ficheiro deriva do seu nome lógico, mas não é «uma cifra por chave» (o estado do aparelho
  é recifrado a cada sincronização, e um lote que falhou é recifrado igual). Vale o nonce aleatório de 96 bits com
  poucas cifras por chave (milhares, longe do limite de 2^32).
- **Nova cadeia** (aparelho perdido ou kit exposto): segredo novo, DEK nova, tudo recifrado com IDs novos na cadeia
  nova, e a antiga apagada. Os outros aparelhos recebem a conta nova; o kit novo vê-se em cada um e o antigo deixa de
  valer.
- Tudo na biblioteca padrão do .NET 10 (`HKDF`, `AesGcm`, `HMACSHA256`, `ECDiffieHellman`, `BrotliStream`).

### Emparelhamento (aprovação num aparelho já ligado)

Na interface do PC, quem começa é o **aparelho novo** (`PairingRequests`): abre um pedido `0-request` (só a hora) e o
aparelho ligado responde como anfitrião na mesma sessão (`PairingHost.AnswerAsync`); daí em diante é o protocolo de
baixo. Desde a 0.2.15 (*number matching*, decisão D11) o anfitrião **nunca mostra o código**: o convidado mostra-o e a
pessoa escreve-o no anfitrião, que o compara em tempo constante (`SyncAgent.ApproveWithCodeAsync`, `PairingCode`). Um
pedido que ninguém fez (escrito por quem controla o transporte) aparece no aparelho ligado sem nada para copiar: o
código só existe no ecrã do convidado. Três códigos errados recusam o pedido (escreve-se `x-host`); «Não fui eu»
também. A aprovação faz-se na barra da janela ou em Definições › Sincronização (rota `POST /sync-request-code`, campos
`id` e `code`; recusa GET, janelas privadas e pedidos sem o testemunho da página); a rota `/sync-approve` (aprovar sem
código) saiu. Com a barra calada por pedidos em série (`PairingFatigue`), as Definições também não aprovam até
«Mostrar o pedido na barra». Um pedido com a hora mais de 2 minutos no futuro é ignorado (`PairingProtocol.ClockSkew`).
Uma sessão já respondida por um anfitrião não é aprovada por outro. O QR (para um telemóvel) continua na biblioteca.

```
QR no anfitrião:  eteris-par:1:<sessão 80 bits>:<chave pública efémera P-256 do anfitrião>   (ou só o código curto)
1 anfitrião → transporte  chave pública do anfitrião
2 convidado → transporte  chave pública do convidado + compromisso SHA-256(pkC ‖ pkA ‖ nC)
3 anfitrião → transporte  nonce nA (só depois do compromisso)
4 convidado → transporte  nonce nC (abre o compromisso) + nome do convidado cifrado com a chave da sessão
   o convidado mostra o código de verificação: 6 algarismos de SHA-256(sessão ‖ pkA ‖ pkC ‖ nA ‖ nC)
   o utilizador ESCREVE-O NO ANFITRIÃO, que o calcula e compara sem o mostrar (três erros recusam)
5 anfitrião → transporte  R cifrado com ECDH(pkA, pkC) → HKDF → AES-256-GCM (transcrito como dados associados)
   o utilizador confirma TAMBÉM NO CONVIDADO que o código é igual; só então o convidado abre o passo 5
```

- O QR só tem dados públicos: **fotografá-lo não dá nada**. Quem também controlar o transporte e se meter no meio tem
  de se comprometer com a sua chave e o seu nonce antes de ver o nonce do anfitrião, por isso acerta no código com
  probabilidade 1 em 1 000 000 por pedido — com no máximo 3 pedidos acompanhados de cada vez
  (`PairingRequests.MaxAnswered`), e a barra deixa de abrir sozinha ao 3.º pedido que acabe mal em 24 h
  (`PairingFatigue`) —, e o utilizador vê códigos diferentes e recusa. Sem a confirmação, o passo 5 não existe.
- Com QR, a chave do anfitrião vem do ecrã (autêntica) e tem de conferir com a do transporte. Sem câmara (PC↔PC),
  o código curto `XXXX-XXXX-XXXX-XXXX` dá só a sessão, e quem garante as chaves é o código de verificação.
- **Confirmação dos dois lados.** Com o código curto, quem controla o transporte pode pôr a sua chave no passo 1 e
  fazer de anfitrião só para o convidado, escrevendo logo o seu passo 5 (com a SUA conta, para o convidado passar a
  cifrar tudo com uma chave que o atacante tem). Por isso o convidado também pergunta «Escreveu … no outro computador e
  ele aceitou?» (`PairingGuest.ConfirmCode`; «Sim» só conta 2 s depois de o código aparecer) e só depois abre o segredo
  (`WaitForSecretAsync` recusa sem isso).
- **O limite do código escrito** (*number matching*): o anfitrião compara o que a pessoa escreve com o código do pedido,
  não sabe de onde ela o tirou. Escrever o código só protege se for o código que está a ver no seu outro computador: um
  código que alguém lhe dite por telefone ou envie por mensagem não deve ser escrito.
- **Nada do utilizador em claro no transporte:** o passo 2 leva só a chave pública e o compromisso; o nome do aparelho
  convidado segue no passo 4, cifrado com a chave da sessão (mesmo que o anfitrião feche de repente e os ficheiros
  fiquem lá, a Google não lê «PC da Ana»). As chaves públicas têm de ser P-256 (outra curva é recusada logo).
- A sessão expira em 10 minutos e os ficheiros são apagados no fim. O transporte é o mesmo da sincronização (os dois
  aparelhos com a mesma conta Google, ou a mesma pasta).
- **Falhas passageiras do transporte** (a rede cai um instante, o Drive responde tarde, a pasta não se lê): quem espera
  tenta outra vez no intervalo seguinte, até ao prazo de sempre, e as escritas dos passos repetem a mesma mensagem até
  3 vezes (`PairingProtocol.IsTransportFailure`). Só as mensagens que não conferem acabam com o pedido — e contam para
  os pedidos em série.
- **Um pedido pronto continua a ser acompanhado** enquanto espera pelo código: o aparelho ligado lê a recusa do outro
  lado de 2 em 2 s, até o pedido ser aprovado, recusado ou expirar. Se o aparelho novo desistir (ou pedir outra vez), o
  pedido velho sai da barra sozinho; e a entrega confere a recusa antes de escrever o passo 5.
- **No máximo 3 pedidos acompanhados de cada vez** (`PairingRequests.MaxAnswered`): os outros ficam só contados e são
  respondidos numa procura seguinte, quando houver vaga. Cada pedido acompanhado é uma tentativa em 1 000 000 de
  acertar no código que o aparelho novo verdadeiro mostra; uma vaga só se liberta com um pedido que acaba, e um pedido
  falso só acaba mal (pedidos em série).

### Transportes

- **Pasta local** (`FolderTransport`): OneDrive, Dropbox, Google Drive para computador, pasta de rede, pen. Nenhum
  pedido de rede do Éteris; escrita atómica (temporário + mudança de nome).
- **Google Drive** (`GoogleDriveTransport`): API REST v3, só `spaces=appDataFolder`; `files.list` (com páginas),
  `files.get?alt=media`, `files.delete`, envio `multipart/related` até 5 MiB e `resumable` acima disso, substituição do
  estado do aparelho por `PATCH uploadType=media`. 401 → renova o token uma vez; 429/5xx → espera e repete.

### Início de sessão (OAuth para apps de computador)

- A Google bloqueia o início de sessão dentro de motores embutidos como a CEF (`disallowed_useragent`). O Éteris abre o
  ecrã da Google no **navegador predefinido do sistema** (`Process.Start` com `UseShellExecute`).
- **Se o predefinido for o próprio Éteris**, usa outro navegador instalado (Firefox, Chrome, Brave… da lista do
  Windows); sem nenhum, mostra o endereço para copiar. O ecrã da Google nunca abre dentro do Éteris.
- **PKCE** (S256) e `state` aleatório; o redirecionamento chega a `http://127.0.0.1:<porta>/`, num recetor que escuta só
  na interface de loopback (nada de fora lhe chega e o Windows não pede regra na firewall). `access_type=offline` para
  o token de renovação. Se o utilizador desmarcar a permissão no ecrã da Google, o Éteris diz que sem ela não
  sincroniza.
- **Escolha de conta (0.2.15):** o pedido de autorização leva `prompt=select_account` (a Google mostra a escolha de
  conta, não o consentimento outra vez). Se a Google não devolver token de renovação, o Éteris repete **uma vez**, no
  mesmo separador (302) e com `state` e PKCE novos, com `prompt=consent`; se mesmo assim não vier, a entrada falha:
  **uma sessão sem token de renovação nunca é aceite**. Na reentrada (sessão expirada, ou a linha do menu em pausa) o
  pedido leva também `login_hint` com o e-mail da conta guardada, para a Google a pré-escolher: o e-mail vai **só** no
  endereço da Google, aberto no navegador do sistema (fica no histórico desse navegador), nunca num endereço `eteris://`
  nem em registos (`GoogleSync.ReentryHint()`, `GoogleOAuth.CleanLoginHint`; teste
  `SignInHint_NeverTravelsInAnEterisAddress`). Depois de «Sair» não há e-mail guardado e a entrada seguinte vai sem ele.
- **O recetor segura a resposta** até o Éteris trocar o código e ler a pasta (no máximo cerca de 10 s), para a página
  de fim dizer a verdade: seis textos (`SignInPage`: «Pronto.» com «O Éteris começou a sincronizar…» ou «…retomou a
  sincronização…», «Falta um passo no Éteris.», a falta da permissão do Drive, a neutra «Sessão iniciada. Volte ao
  Éteris para continuar.» e a recusa). A página não tem scripts, endereços externos nem o e-mail. Todas as respostas
  levam `Cache-Control: no-store`, `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff`, uma CSP fechada e
  `Connection: close`; a linha do pedido tem prazo de 5 s, um `state` errado leva 400 e a espera continua, e a porta
  fecha-se logo que chega a resposta certa. Com sucesso, a janela do Éteris pisca na barra de tarefas (e vem para a
  frente se o Windows deixar). A entrada inteira tem um prazo de 5 minutos; «Cancelar» na barra fecha a porta.
- **Tokens** guardados em `<perfil>\sync\google.tokens`, cifrados com DPAPI do utilizador do Windows.
- **«Sair deste computador»** apaga os tokens e a conta guardada deste computador e **não** revoga nada (zero pedidos;
  `GoogleSession.SignOut`). **«Retirar o acesso do Éteris à conta Google»** revoga em `oauth2.googleapis.com/revoke`
  (`GoogleSession.RevokeAccessAsync`) e só apaga os tokens daqui quando a Google responde (revogado, ou o token já não
  valia). (Até à 0.2.14, «Sair» revogava.)
- **Limite de 100 tokens de renovação.** A Google guarda no máximo 100 tokens de renovação por conta e por cliente
  OAuth. Como «Sair deste computador» já não revoga, cada entrada nova deixa um token válido na Google; ao passar o
  limite, a Google invalida o mais antigo sem aviso, e esse computador passa a «Sincronização em pausa: entre outra
  vez». «Retirar o acesso…» limpa todos. (Limite da documentação da Google; não foi medido.)
- **`client_id`:** lido de `google-oauth.json` ao lado do `Eteris.Browser.exe` (vai com a instalação) ou na pasta do
  perfil (testes). Aceita o JSON que a consola da Google descarrega (`{"installed": {…}}`). Sem ele: «indisponível nesta compilação»
  e nenhum pedido. O `client_secret` das apps de computador não é segredo (vai com o programa; a Google sabe-o); quem
  protege a troca é o PKCE.
- **Com o sandbox (0.2.14, `docs/sandbox.md`):** o `google-oauth.json` fica na pasta da instalação, que dá leitura às
  capacidades do AppContainer do serviço de rede (`Infrastructure/NetworkSandbox.cs`), por isso esse processo também o
  consegue ler. Aceite: é um cliente «installed», que a Google não trata como segredo (ver em cima). Os tokens
  (`google.tokens`, DPAPI) ficam no perfil (`%LocalAppData%\Eteris`), fora dessa pasta.

### A conta no menu

Como no Chrome, no Firefox e no Brave, a conta com que se entrou aparece no topo do menu do navegador (Alt+F) e nas
Definições › Sincronização («Ligado como *nome* (*e-mail*) ao Google Drive…»).

- **De onde vem:** um pedido ao Drive, `GET https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)`
  com o token de acesso que já existe (renovado pelo código de sempre se expirou; 401 → renova uma vez e repete). O
  `about.get` aceita o âmbito `drive.appdata` (documentação da Google, *about.get › Authorization scopes*:
  <https://developers.google.com/workspace/drive/api/reference/rest/v3/about/get>): **nenhum âmbito novo**, nenhum
  servidor novo (é o mesmo `www.googleapis.com` do Drive) e o `fields` limita a resposta ao nome e ao e-mail.
- **Sem foto:** seria outro pedido a outro servidor (`lh3.googleusercontent.com`). O avatar é a inicial do nome num
  círculo com o gradiente da íris.
- **Quando:** logo a seguir a cada início de sessão bem-sucedido; e, se já havia sessão sem conta guardada (quem entrou
  antes desta versão), **uma vez** quando o utilizador abre o menu do navegador ou a secção Sincronização das
  Definições (na página, só depois de um gesto da pessoa — clique, tecla, roda, toque — com a secção à vista: uma aba de
  Definições reposta no arranque não pede nada). Em segundo plano; a linha do menu atualiza-se se ainda estiver aberta.
  **Nunca no arranque** (a promessa da 0.2.11, «No arranque, nada sai para a Google», mantém-se), nunca num
  temporizador, e **no máximo uma tentativa falhada por sessão** (depois de falhar, só volta a tentar depois de sair e
  entrar, ou de reabrir o Éteris; qualquer falha conta, também a de gravar o token renovado). Enquanto não há conta, o
  menu diz «Conta Google ligada». O pedido corre fora do fio da interface (abrir o menu nunca espera pela rede nem
  pelo proxy do Windows), e o gesto que é o próprio clique em «Sair» não o lança.
- **Onde fica:** `<perfil>\sync\google.account`, cifrado com DPAPI do utilizador do Windows (como os tokens, com outra
  «entropia»). **Apagado deste computador ao «Sair».** O texto que vem da rede é limpo (sem caracteres de controlo, até
  200 caracteres) antes de ir para a interface. «Sair» a meio de um pedido: o pedido da conta é cancelado e a sessão e a
  conta mudam de geração, por isso uma resposta que chegue depois (a renovação do token ou o `about.get`) já não grava
  `google.tokens` nem `google.account`, nem mostra a conta antiga depois de se entrar com outra.
- **No menu**, numa janela normal e com o identificador da aplicação (`google-oauth.json`; sem ele as Definições
  dizem «indisponível nesta compilação» e o menu não oferece o que não se pode cumprir). Nas janelas privadas não
  aparece nada da conta. Com sessão, avatar (a letra com a tinta da cor de destaque), nome em negrito, e-mail e o estado
  da sincronização; sem sessão, «Entrar com o Google…» com «Sincronize favoritos e palavras-passe, cifrados ponta a
  ponta» e a dica «Abre o seu navegador para escolher a conta Google». Abrir o menu procura pedidos de aparelhos novos
  (ação do utilizador) e a linha atualiza-se se o menu ainda estiver aberto. **Desde a 0.2.15 a linha sem sessão entra
  logo** (abre o navegador do sistema, `GoogleSync.BeginSignIn`; a barra da janela segue o resto); com a sincronização
  em pausa por sessão expirada, entra com a conta guardada como pista (`login_hint`); só com a sessão em ordem a linha
  abre `eteris://settings/#sincronizacao` (`MainWindow.BrowserMenuAccountAction`). Na pasta de teste, a linha liga à
  pasta. As linhas têm nome para leitores de ecrã. (Até à 0.2.14 a linha sem sessão abria as Definições.) Pelo teclado (Alt+F) a
  linha da conta é a primeira paragem; aberto com o rato, o foco fica em «Nova aba», para o convite não aparecer
  realçado em cada abertura. O menu cresce até à altura da janela (sem o antigo teto fixo de 665 px, o menu inteiro
  com a linha da conta cabe numa janela normal) e, numa janela baixa, a barra de deslocamento tem as cores do tema.
  **Desde a 0.2.17, com sessão, há um «Sair» à direita do cartão** (ao lado, não dentro: o clique nele não abre as
  Definições). Não aparece no convite «Entrar», nas janelas privadas nem na pasta de teste, e fica desativado enquanto
  corre outra saída da conta (a dica e o leitor de ecrã dizem porquê). Sai em dois passos: o 1.º toque troca o texto
  por «Confirmar» (o menu fica aberto; um 2.º toque antes de meio segundo não conta, nem uma tecla mantida premida — a
  repetição do Windows, pelo WPF ou vinda da página) e o 2.º sai deste computador como nas Definições, mas **nunca
  apaga a chave** (`forgetKey: false`; para isso, as Definições), fecha o menu e avisa «Saiu da conta Google neste
  computador. A chave da sincronização e os dados ficaram aqui.» (sem a 2.ª frase quando ainda não havia chave). Se
  gravar o estado falhar, os tokens saem na mesma e o aviso di-lo. Nome acessível e dica: «Sair deste computador».
  Pelo teclado, o cartão e o «Sair» são uma linha: → vai ao «Sair», ← volta ao cartão, ↓ segue para «Nova aba»; o Tab
  e o leitor de ecrã vão do cartão para o «Sair»; a inicial chega ao cartão e não ao «Sair».
- **Nas Definições** (0.2.15, mais curtas): sem sessão, o título, a frase «Os seus favoritos, palavras-passe e
  definições em todos os seus computadores, cifrados ponta a ponta.», o botão «Entrar com o Google» e a ligação «Como
  protegemos os seus dados» (para `eteris://privacy/#sincronizacao`, o bloco «Se ligar a sincronização», sempre à vista,
  também antes de entrar); com sessão, a linha «Conta Google», o estado à frente («Ligada · última sincronização há…»)
  e o que pede uma decisão (pedidos, kit, oferta, alertas); o resto vive em «Mais opções» (sincronizar agora, nome do
  computador, aparelhos e «Perdi um computador», o tópico «O que passa entre PCs», «Sair deste computador» — anunciado
  aos leitores de ecrã como «Sair da conta Google» —, «Retirar o acesso…» e «Apagar os meus dados…»). Numa janela
  privada as Definições não mostram nada da conta e a secção Sincronização fica indisponível.

### Perfil cifrado num ficheiro

Definições › Sincronização › **Exportar perfil cifrado** grava em Transferências um `Perfil Éteris AAAA-MM-DD.eteris`
(favoritos, Espaços, definições que viajam, decisões do Escudo, notas) cifrado com um código de recuperação **novo**,
mostrado uma única vez na página. O cofre de palavras-passe só vai com a caixa «Incluir palavras-passe, pagamentos e
documentos», depois do Windows Hello, selado item a item (ver «Coleções secretas»). As **abas abertas não vão**: a importação não as usava, e os URLs de abas podem ter
tokens (ligações de redefinição de senha, IDs de sessão) que ficariam com quem tivesse o ficheiro e o código.
**Importar** lê o ficheiro escolhido e o código; o código e o ficheiro vão no corpo de um POST para
`eteris://settings/sync-import` (nunca no endereço). Junta ao perfil: o que falta entra, as definições do ficheiro passam
a valer, as notas diferentes ficam por baixo das atuais com um título, os Espaços em falta são criados na primeira
janela. Nenhuma rede. Recusado na janela privada.

- **Um ficheiro alheio não baixa as proteções sem o utilizador ver** (engenharia social do tipo «importa o meu perfil,
  o código é este»). Ficam por aplicar, e são listadas uma a uma, as definições que desligam proteções (HTTPS primeiro,
  cookies de terceiros, links de rastreio, redirecionamentos e sites de salto, rastreadores escondidos, pedidos de
  notificações, apagar ao fechar, DNS cifrado; ligar as sugestões enquanto escreve) e as decisões que desligam o Escudo
  ou o aviso de HTTP num site. Só entram se o utilizador carregar em «Aplicar também estas alterações»
  (`ProfileTransfer.WeakensSetting` / `WeakensShield`; testes em `SyncIntegrationTests`).

## Coleções secretas

O cofre de palavras-passe e do preenchimento automático (`docs/palavras-passe.md`) passa entre computadores em sete
coleções **secretas**: cada registo leva uma **segunda cifra** própria por dentro da cifra do ficheiro (o «selo»), e a
chave do registo no motor é opaca. Biblioteca: `Model/SyncRecord.cs` (`Collections.Secret`, `IsSecret`),
`Secrets/SecretSchemas.cs` (campos, partilhados com o Android), `Secrets/SecretSeal.cs`, `Engine/ISecretSource.cs`,
`Engine/ProfileSync.cs`, `Model/SyncGroups.cs`. Navegador: `Vault/VaultSyncProfile.cs`, `Vault/VaultSyncMap.cs`,
`Vault/VaultService.Sync.cs`, `Sync/SyncChoices.cs`.

| Coleção | Linha do tópico | Chave natural (dentro do selo) | Campos (dentro do selo) |
|---|---|---|---|
| `logins` | Palavras-passe | origem `\n` utilizador (uma gerada por confirmar: `gerada\n<ID>`) | `origin`, `username`, `password`, `note`, `created`, `changed`, `used`, `formAction`, `generated`, `realm` |
| `loginsNever` | Palavras-passe | origem | `origin`, `created` |
| `cards` | Pagamentos | ID do item | `name`, `number`, `expMonth`, `expYear`, `nickname`, `created`, `used` — **nunca** `cvc`/`csc`/`cvv` (recusados ao selar, em qualquer nível) |
| `ibans` | Pagamentos | ID do item | `iban`, `nickname`, `created` |
| `contacts` | Informações de contacto | ID do item | `label`, `givenName`, `familyName`, `organization`, `street`, `number`, `complement`, `neighborhood`, `city`, `region`, `postalCode`, `country`, `email`, `phone`, `created`, `used` |
| `idDocs` | Documentos de identificação | ID do item | `type` (`passport`, `driverLicense`, `nationalId`, `taxId`: passaporte, carta de condução/CNH, Cartão de Cidadão/RG, NIF/CPF), `number`, `name`, `country`, `issuer`, `issued`, `expires`, `note` |
| `travel` | Viagens | ID do item | `type` (`vehicle`, `loyalty`), `make`, `model`, `year`, `owner`, `plate`, `plateRegion`, `vin`, `company`, `program`, `number`, `name` |

Não vão: os candidatos por guardar, o histórico local das palavras-passe, a chave do cofre, a janela de graça do
Windows Hello, os interruptores «Pedir o Windows Hello…» (são deste PC) e a «última utilização» das janelas privadas
(nem chega a ser gravada).

**Cifra interna (o selo).**

- `K_seg = HKDF(DEK, "secrets:v1")` cifra e `K_skey = HKDF(DEK, "secret-keys:v1")` faz as chaves opacas
  (`KeyRing.SecretKey`/`SecretKeyId`). Derivam da cadeia: qualquer aparelho emparelhado as tem, e mudam com a geração e
  com a «nova cadeia».
- `Key` = `"s:" + hex(HMAC(K_skey, coleção ‖ chave natural))[0..32]`; o `Id` continua a ser o `OpaqueId` de sempre,
  calculado sobre essa `Key`.
- `Data` = `{"v":1,"n":<nonce de 96 bits>,"c":<AES-256-GCM(K_seg, {"k": chave natural, "d": campos})>}`, com os dados
  associados `eteris-secret|v1|<cadeia>|<geração>|<coleção>|<Id>`. Um selo movido para outro `Id`, coleção, cadeia ou
  geração não abre: esse registo fica de fora (não se aplica, conta como erro, nessa ronda nada dessa coleção se apaga
  aqui) e a ronda continua. Recusa selar mais de 64 KiB.
- O que isto dá, além da cifra do ficheiro: o estado do motor, os instantâneos, a base do `ProfileSync`, os lotes
  pendentes e o `.eteris` **nunca têm um segredo nem um site em claro**, mesmo dentro da DPAPI; o bloco selado é
  incompressível (nenhum canal lateral de compressão ao lado dos títulos que os sites escolhem); e um aparelho com uma
  linha desligada recebe os selos dessa linha e **nunca os abre**.
- O motor guarda no estado do aparelho (protegido por DPAPI, nunca no transporte) as chaves dos selos da cadeia atual,
  só para voltar a selar os registos que o próprio aparelho escreveu quando outro faz «nova cadeia» (a cadeia antiga já
  não existe quando ele dá por isso).

**O perfil com segredos.** O cofre dá à sincronização, de cada item, só uma chave **local** opaca e uma **impressão**
(HMAC com uma chave do próprio cofre, gravadas com o item: nada se decifra para comparar). A base guarda a impressão.
Quando a impressão muda, só esse item se decifra, sela e envia. No sentido contrário, o conteúdo vai ao cofre, que o
volta a cifrar com a chave local, tal como veio (sem eco). **Conflito**: ganha a versão mais recente (HLC); quem perde
guarda a palavra-passe anterior no histórico local (1 versão) e o estado avisa «A palavra-passe de *site* mudou noutro
computador; a anterior ficou no histórico…» (e «Mostrar» em `eteris://passwords/` mostra a anterior). **Primeira
junção** (aparelho novo, ou linha ligada outra vez): o que só existe aqui sobe; com a mesma chave natural vale a versão
da conta e a deste computador vai para o histórico. Apagar deixa uma lápide (com a chave natural selada). Itens gerados
à espera de confirmação também passam.

**Escolha por aparelho — o tópico «O que passa entre PCs»** (Definições › Sincronização; `SavedSync.Collections`):

| Linha | Coleções | Por omissão | O que diz |
|---|---|---|---|
| Favoritos | `bookmarks` | ligado | Os favoritos e as pastas |
| Espaços | `spaces` | ligado | Nome, cor e emoji; as abas de cada Espaço ficam em cada computador |
| Definições | `settings` | ligado | Só as que fazem sentido noutro computador |
| Decisões do Escudo | `shield` | ligado | Sites com o Escudo desligado ou estrito, elementos escondidos, bloqueios por site |
| Notas | `notes` | ligado | O texto das notas |
| Palavras-passe | `logins`, `loginsNever` | ligado | Palavras-passe, notas e sites onde nunca guardar |
| Também pagamentos e documentos de identificação (escolha da conta, 0.2.15) | `cards`, `ibans`, `idDocs` | **desligado** numa conta nova; ligar pede o Windows Hello uma vez e grava a escolha da conta | Cartões (sem o código de segurança), IBAN e documentos |
| Pagamentos | `cards`, `ibans` | **desligado** (segue a escolha da conta); ligar pede o Windows Hello | Cartões (sem o código de segurança) e IBAN |
| Informações de contacto | `contacts` | ligado | Nomes, moradas, e-mails e telefones |
| Documentos de identificação | `idDocs` | **desligado** (segue a escolha da conta); ligar pede o Windows Hello | Passaporte, carta de condução/CNH, Cartão de Cidadão/RG, NIF/CPF |
| Viagens | `travel` | ligado | Veículos e programas de fidelidade |
| Abas abertas | — | — | Não passam: ficam em cada computador (sem interruptor) |
| Chaves de acesso | — | — | Não passam: ficam no Windows Hello ou na chave de segurança (sem interruptor) |

Decisão D-1 do dono (até à 0.2.14: uma regra por computador, desligados por omissão) passou a **escolha da conta**
na 0.2.15 (D4′; ver «Os fluxos», «Pagamentos e documentos: escolha da conta»): numa conta nova vêm desligados; a linha
«Também pagamentos e documentos de identificação» liga-os para a conta com um Windows Hello (`ReauthPurpose.SyncSecrets`,
sempre que se pede, nunca com a janela de graça) e grava a escolha no registo reservado `@account/secret-groups`
(coleção `settings`, cifrado como os outros; nunca aplicado como definição nem apagado); um computador **novo** que
entra depois herda-a; um que já estava ligado recebe uma oferta única. Desligar uma linha neste computador para de
enviar e de aplicar essas coleções aqui e **não apaga nada**, nem aqui nem na conta, nem muda a escolha da conta; ligar
outra vez faz a primeira junção só dessa linha (`SavedSync.Joined`). Texto do topo (`SyncChoices.Intro`): «Tudo é
cifrado neste computador antes de sair. A Google só guarda ficheiros ilegíveis, os tamanhos e as horas, e a chave nunca
lá vai. As palavras-passe, os pagamentos e os documentos levam uma segunda cifra própria. Cada computador escolhe o que
recebe: desligar uma linha não apaga nada. Quem perder o kit de recuperação e todos os computadores perde estes dados:
não há «esqueci-me do kit».»

**Emparelhamento, nova cadeia e exportar.**

- **Aprovar um aparelho novo** com segredos em jogo (`SyncAgent.HasSecrets`: coleções do cofre ligadas aqui, registos
  secretos na conta, a escolha da conta ligada, ou ainda sem uma ronda completa que o diga): a barra diz «Um computador
  quer entrar na sua conta e receber as suas palavras-passe» (com a escolha da conta ligada, «…palavras-passe, cartões
  e documentos») e, depois do código certo, pede a confirmação do Windows (`SyncChoices.MayApproveAsync`; sem segredos
  em jogo, não pede).
- **Nova cadeia / remover aparelho:** os selos voltam a ser feitos com a `K_seg` nova (outra `Key`, outro selo; os
  dados associados com a cadeia nova); o aparelho removido não abre nada da cadeia nova. O aviso diz «O aparelho
  removido já tinha as suas palavras-passe: mude as dos sites importantes».
- **`.eteris` exportado:** sem o cofre por omissão. A caixa «Incluir palavras-passe, pagamentos e documentos» pede o
  Windows Hello (sempre) e leva o cofre inteiro, cada item selado com chaves derivadas da chave do ficheiro (o código
  novo, mostrado uma vez, abre também os selos). Ao importar, os itens entram no cofre e, com a mesma chave natural,
  vale o do ficheiro (a anterior fica no histórico).
- **Kit de recuperação:** o texto que o mostra diz que com ele (e a conta Google) se abre também o cofre de
  palavras-passe; por isso «Ver o kit» pede sempre a confirmação do Windows e «Gerar um kit novo…» pede-a com segredos
  em jogo.

## A exceção de rede

Regra do Éteris: **os pedidos de rede do Éteris passam só pelo motor**. A sincronização pela Google é uma **exceção
explícita**: fala com a Google por um cliente HTTP próprio (`GoogleEndpoints.CreateClient`), e só com:

| Endereço | Para quê |
|---|---|
| `https://accounts.google.com/o/oauth2/v2/auth` | ecrã de consentimento — aberto no **navegador do sistema**, nunca pedido pelo cliente HTTP |
| `https://oauth2.googleapis.com/token` | trocar o código por tokens e renová-los |
| `https://oauth2.googleapis.com/revoke` | revogar em «Retirar o acesso do Éteris à conta Google» (e no passo 2 de «Perdi um computador»); «Sair deste computador» não o usa |
| `https://www.googleapis.com/drive/v3/files…` | listar, ler e apagar na pasta oculta da aplicação |
| `https://www.googleapis.com/upload/drive/v3/files…` | enviar (multipart, resumable, PATCH) |
| `https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)` | nome e e-mail da conta, para o menu e as Definições (ver «A conta no menu»: só depois de uma ação do utilizador) |

- O `GoogleOnlyHandler` recusa qualquer outro endereço **antes de sair do processo** (só `https`, porta por omissão,
  sem utilizador no endereço; `www.googleapis.com` só com `/drive/v3/files`, `/upload/drive/v3/files` e
  `/drive/v3/about`; `oauth2` só
  `/token` e `/revoke`). Sem cookies, sem redirecionamentos automáticos, sem os cabeçalhos do motor; `User-Agent`
  `Eteris-Sync/1`.
- Só corre depois de o utilizador carregar em «Entrar com o Google» (em qualquer dos pontos de entrada); com a
  sincronização ligada, nos momentos de «Quando sincroniza» (30 s depois de abrir, salvo se o utilizador mudar alguma
  coisa antes: então 10 s depois dessa alteração). No arranque saem 0 pedidos para a Google, com ou sem sessão.
- O endereço do ecrã de consentimento leva `prompt=select_account` (escolha de conta), `login_hint` só na reentrada, e
  o recuo `prompt=consent` uma só vez quando a Google não devolve token de renovação (ver «Início de sessão»). O âmbito
  continua a ser só `drive.appdata`. A 0.2.15 não acrescentou nenhum endereço: a sessão expirada, a escolha de conta e
  voltar à janela usam os que já estavam nesta tabela.
- **DNS e proxy:** por não passar pelo motor, este cliente **não usa o «DNS cifrado»** escolhido nas Definições. Os
  nomes `oauth2.googleapis.com` e `www.googleapis.com` são resolvidos pelo **DNS do Windows**, em claro: o fornecedor
  de internet (ou quem gere a rede) vê que o computador fala com a API da Google e quando — não o que diz, que vai em
  HTTPS. Usa o **proxy do Windows**, se houver (aí é o proxy que resolve os nomes). Resolver pelo mesmo DoH seria mais
  um pedido fora do motor (ao fornecedor do DoH), por isso fica assim e fica dito.
- O recetor do redirecionamento (`127.0.0.1`) só aceita a resposta com o `state` deste início de sessão (comparação
  em tempo constante); outra qualquer — uma página local ou um processo que varra as portas — leva 400 e o recetor
  continua à espera até ao prazo.
- Testes: `GoogleSyncTests.HttpClient_OnlyReachesGoogleOAuthAndDrive` (lista de endereços),
  `HttpClient_RefusesAnythingElse_BeforeItLeavesTheProcess`, e
  `SyncLibrary_HttpOnlyInTheGoogleFolder_AndOnlyListedAddresses`, que varre o código da biblioteca: cliente HTTP e
  sockets só na pasta `Google/`, e os únicos endereços escritos no código são os desta tabela (mais `127.0.0.1`);
  `BrowserSyncFolder_HttpOnlyThroughTheGoogleClient` faz o mesmo à integração no navegador (`src/Eteris.Browser/Sync`):
  nenhum cliente HTTP, socket ou DNS próprio, só o de `GoogleEndpoints.CreateClient`.
  Se a exceção crescer, este documento e esses testes mudam juntos.

## Ameaças e privacidade

| Quem | O que consegue | O que não consegue |
|---|---|---|
| **A Google** (ou quem entrar na conta Google) | ver que a conta usa o Éteris, quando sincroniza (horas dos ficheiros, IP dos pedidos), quantos ficheiros e os tamanhos (preenchidos: dão a ordem de grandeza do perfil, não o número de favoritos); quando um computador novo pede para entrar e se foi recusado (os ficheiros dos pedidos têm nomes legíveis); apagar ficheiros ou não os entregar (o Éteris deixa-os pendentes) | ler favoritos, notas, abas, definições, nomes dos aparelhos ou URLs; alterar ou trocar ficheiros sem ser detetado; abrir a conta Éteris sem o kit |
| **Quem fotografa o QR** | nada | — |
| **Quem escreve um pedido falso de aparelho novo** (controla o transporte, ou entrou no Éteris com a conta Google da pessoa) | fazer aparecer «Um computador quer entrar na sua conta…» no computador ligado; depois de 3 pedidos maus em 24 h a barra cala-se e o aviso fica nas Definições | receber a conta: o computador ligado não mostra nenhum código para copiar, e o código só existe no ecrã do computador novo; três códigos errados recusam |
| **Quem dita um código à pessoa** (por telefone, por mensagem) | levá-la a escrever no computador ligado o código do computador dele | — limite do *number matching*: escrever o código só protege se for o código que está a ver no seu outro computador: um código que alguém lhe dite por telefone ou envie por mensagem não deve ser escrito. Remédio: «Perdi um computador» (remove e troca a chave) |
| **Quem apaga ou substitui a conta no Drive** | fazer um computador que já esteve na conta encontrar outra cadeia | que esse computador peça aprovação sozinho: mostra o alerta «a conta foi apagada ou substituída» e pede uma decisão (`SyncStatus.ChainReplaced`, `SavedSync.HadAccount`); «Fui eu» pede o Windows com segredos ligados neste computador |
| **Um aparelho removido** (tem o kit antigo) | ler o que já tinha (o que já estava nesse computador continua lá); enquanto o acesso do Éteris à conta Google não for retirado, apagar ou estragar os ficheiros no Drive (remover troca a chave, não retira esse acesso — remédio: «Perdi um computador / foi roubado», que o retira); deixar aos outros uma mensagem com uma conta dele: quem a recebe fica em pausa e pede o identificador do kit novo, que ele não conhece (1 em 1 048 576 por tentativa, 3 tentativas) — salvo um aparelho desligado desde antes de o outro entrar, que só fixou a chave do removido e segue sozinho a mensagem dele | abrir a conta nova ou escrever nela algo que os outros aceitem |
| **Um aparelho removido que repõe o registo de chaves da cadeia antiga** (tem o segredo antigo e ainda chega ao Drive) | manter viva a cadeia antiga só para si; se **também apagar a mensagem `r/`** deixada a um aparelho que ficou, antes de ele fazer uma ronda, esse aparelho não dá pela troca e continua a sincronizar com ele na cadeia antiga (nada no Drive o distingue de não ter havido troca; quem trocou a chave continua a vê-lo na lista sem ter passado para a conta nova) — remédio: «Perdi um computador / foi roubado», que retira o acesso na Google | prender na cadeia antiga um aparelho que ainda encontre a mensagem `r/` de quem trocou a chave: a mensagem conta como a troca mesmo com o registo reposto (segue sozinho, ou pede o identificador do kit novo, como na linha de cima) |
| **Quem se mete no meio do emparelhamento** (controla o transporte) | tentar a sua sorte: 1 em 1 000 000 por pedido, com no máximo 3 pedidos acompanhados de cada vez (`PairingRequests.MaxAnswered`), e a barra deixa de abrir sozinha ao 3.º pedido que acabe mal em 24 h (`PairingFatigue`); com o código curto, fazer de anfitrião falso para o convidado (que mostra um código que o anfitrião verdadeiro não mostra) | receber o segredo sem o utilizador escrever no anfitrião o código que o convidado verdadeiro mostra; pôr o convidado na SUA conta sem o utilizador confirmar códigos iguais no convidado; ler o nome dos aparelhos |
| **Quem rouba o PC com a sessão do Windows aberta** | o que esse Windows lê (tokens e estado protegidos por DPAPI do utilizador; o que já estava nesse computador) | — remédio: «Perdi um computador / foi roubado» noutro computador (remove, troca a chave e retira o acesso na Google) e mudar as palavras-passe importantes |
| **Quem tem um ficheiro `.eteris`** | nada sem o código | — |
| **Quem tem o kit de recuperação ou um computador emparelhado** | tudo o que a conta tem: **Quem tem o kit de recuperação ou um computador emparelhado tem também as palavras-passe, pagamentos e documentos** (das linhas que algum aparelho sincroniza); o kit sozinho não chega, é preciso também a conta Google | — remédio: «Nova cadeia» (remover o aparelho, «Perdi um computador» ou gerar um kit novo) **e mudar as palavras-passe** dos sites importantes, porque o que já foi lido não se desfaz |
| **O Éteris (o projeto)** | nada: não há servidor do Éteris | — |

- **Recuperação (D6):** sem o kit e sem nenhum computador ligado, a cópia na nuvem é irrecuperável. Não há
  «esqueci-me do kit». O lembrete do kit e a recusa «Não quero: aceito que, sem este computador, a cópia na nuvem não se
  recupera» dizem-no.
- **A confirmação do Windows.** Onde se lê «com o Windows Hello», é a confirmação do Windows: Windows Hello, PIN ou a
  palavra-passe do Windows. Se a conta do Windows não tiver nenhuma proteção, o Éteris avisa e não tem como confirmar
  que é você.
  (`ReauthGate.RequestAsync` passa com o conselho `ReauthGate.ProtectAccountAdvice`.) A confirmação pedida num
  computador protege esse computador; não é verificável pelos outros.
- **Promessa (D1):** o README diz «Sem telemetria. Por omissão tudo fica no computador; a sincronização é opcional,
  cifrada de ponta a ponta e desligada por omissão». Em `eteris://privacy/`, «No arranque, nada sai para a Google»
  continua verdadeira por omissão (é medida no motor) e, com a sincronização ligada, diz a exceção escolhida pelo
  utilizador (`PrivacyPage.SyncException`, os momentos de «Quando sincroniza», com a frase do que a Google vê,
  `PrivacyPage.SyncSees`; testes `NoGoogle_WithSyncOn_StaysKept_AndStatesTheException` e
  `SyncException_NumbersMatchTheCode`). A exceção aparece também com sessão Google e a sincronização desligada (uma
  sessão da 0.2.10–0.2.12); aí, abrir o menu já não pede a conta. Desde a 0.2.15, a página tem também o bloco «Se
  ligar a sincronização» (`eteris://privacy/#sincronizacao`, `PrivacyPage.SyncBlock`), **sempre à vista** — mesmo antes
  de entrar —, com o que vai para a Google, o que ela vê e onde fica a chave; não é uma promessa medida e não faz
  pedidos. É para ele que aponta «Como protegemos os seus dados» nas Definições.

### O que a Google vê

- que esta conta Google autorizou a aplicação «Éteris» (aparece em *myaccount.google.com › Segurança › Ligações a apps*),
  e quando houve início de sessão e renovação de tokens;
- que o Éteris pediu o nome e o e-mail da conta (`about.get`, uma vez depois de cada entrada — a Google já os conhece;
  nada do Éteris vai nesse pedido além do token); na reentrada, o e-mail vai no endereço do ecrã de consentimento
  (`login_hint`);
- Os pedidos para juntar um computador novo são a exceção: têm nomes legíveis e levam só a hora, chaves públicas de
  uso único e números aleatórios — nenhum dado seu. A Google fica a saber quando um computador novo pede para entrar e
  se foi recusado.
  (`eteris/pair/<sessão>/0-request` só com a hora, `1-host` com a chave pública do anfitrião, `x-host` numa recusa; o
  nome do computador novo vai cifrado com a chave da sessão.)
- nos ficheiros de dados, os pedidos à API do Drive: horas, endereço IP, quantos ficheiros, nomes opacos
  (`eteris/<hex>/d/<hex>/000000000012`) e tamanhos aproximados (preenchidos a 1 KiB e, nos grandes, a «baldes» Padmé: um
  instantâneo de um perfil grande pesa mais do que o de um pequeno; o número de palavras-passe também pesa no tamanho do
  instantâneo — a ordem de grandeza, não o número); o espaço ocupado conta na quota do Drive do utilizador (poucos MB);
- **nunca** o conteúdo: favoritos, URLs, títulos, notas, abas, definições, Espaços, decisões do Escudo, nomes dos
  aparelhos, palavras-passe, cartões, moradas, documentos — tudo dentro da cifra (os do cofre com uma segunda cifra
  própria). A chave nunca lá vai.

O utilizador pode apagar os dados da pasta oculta em *Google Drive › Definições › Gerir aplicações › Éteris › Eliminar
dados ocultos da app*, e retirar o acesso em *myaccount.google.com/permissions*.

## Por provar com conta real (0.2.15)

A 0.2.15 foi feita e testada com o Google falso dos testes (`FakeGoogle`) e com a pasta de teste; as provas com a
conta Google real não foram autorizadas durante a implementação. Os textos públicos descrevem o que o código faz
segundo a documentação da Google; estas provas confirmam-no, e se alguma falhar mudam os textos das afirmações
correspondentes. **Nenhuma se faz na instância do dono:** perfil de teste (`--user-data-dir`), em silêncio, repondo a
área de transferência.

| Prova | O quê | Tarefa que a prepara | Precisa da conta real? | Estado |
|---|---|---|---|---|
| 1 | Revogar num perfil põe o outro em «Sincronização em pausa» | T12 | sim | por fazer |
| 2 | Segunda autorização com `prompt=select_account`: vem token de renovação? (e o recuo com `prompt=consent`) | T1, T2 | sim | por fazer |
| 3 | `login_hint` com duas contas abertas: a certa vem pré-escolhida? | T1, T11 | sim | por fazer |
| 4 | Entrar com o Firefox, o Chrome e o Brave como navegador predefinido, e com o Éteris predefinido | T2, T4 | sim | por fazer |
| 5 | O piscar na barra de tarefas e a barra de sessão iniciada | T2, T4 | sim | por fazer |
| 6 | Segundo computador: aprovação pela barra (e nas Definições, só com o teclado), confirmação do Windows; pedido renovado | T7, T8 | sim | por fazer |
| 7 | Sessão expirada: retirar o acesso em myaccount → aviso → «Entrar» → retoma sem kit | T11 | sim | por fazer |
| 8 | Kit: guardar como ficheiro, imprimir para PDF, entrar no 2.º perfil com as palavras e com o formato antigo | T5, T6 | sim | por fazer |
| 9 | Pagamentos e documentos: ligar num perfil → o outro aprovado recebe a oferta | T10 | sim | por fazer |
| 10 | «Sair deste computador» num perfil não desliga o outro e deixa o Éteris listado em myaccount.google.com/permissions; «Retirar o acesso» desliga os dois e tira-o de lá | T12 | sim | por fazer |
| 11 | Reentrada sem `prompt`; consentimento granular com um só âmbito (desmarcar o Drive dá a página da permissão em falta) | T1, T4 | sim | por fazer |
| 12 | Código certo, código errado três vezes, pedido escrito à mão | T8, T8K | não: pasta de teste | por fazer |
| 13 | Cadeia substituída: «Começar do zero» num perfil, alerta no outro | T7 (alerta), T12c («Começar do zero…») | não: pasta de teste | por fazer |
| 14 | Remover um perfil: o kit do outro passa a «deixou de valer» | T5 | não: pasta de teste | por fazer |
| 15 | Depois de «Copiar», o Win+V não mostra o kit (repor a área de transferência no fim) | T5 | não | por fazer |

E ainda, sem prova numerada: o comportamento ao passar os 100 tokens de renovação (nunca medido); a contagem real de
pedidos ao voltar à janela, com um proxy numa sessão verdadeira; o que a Google mostra sobre a pasta da aplicação e
sobre os pedidos de emparelhamento; o corpo real do 400 de `/revoke` para um token já inválido; e um `invalid_grant`
real (só simulado).

## O que o dono tem de fazer (Google Cloud Console)

Tudo em <https://console.cloud.google.com/> com a conta Google do dono:

1. **Criar o projeto** (por exemplo «Eteris»).
2. **APIs e serviços › Biblioteca › Google Drive API › Ativar.**
3. **Google Auth Platform › Branding** (ecrã de consentimento OAuth): nome «Éteris», e-mail de apoio, (logótipo —
   opcional; com logótipo a verificação é obrigatória), **página inicial**, **política de privacidade**, termos
   (opcional), **domínios autorizados** (o domínio da página inicial) e contacto do programador.
4. **Audience (Público):** tipo «Externo»; estado **«Em teste»**; acrescentar os **utilizadores de teste** (o e-mail do
   dono; até 100). Em teste, os tokens de renovação expiram ao fim de 7 dias (é preciso entrar outra vez).
5. **Data access (Acesso aos dados):** acrescentar só o âmbito `https://www.googleapis.com/auth/drive.appdata`
   (classificado como **não sensível**: sem avaliação de segurança CASA).
6. **Clients › Criar cliente › Tipo de aplicação «App para computador»** (nome, por exemplo, «Éteris para Windows») →
   **Descarregar JSON** → guardar como **`google-oauth.json`**:
   - para testar: na pasta do perfil de teste (`--user-data-dir`), ou ao lado do `Eteris.Browser.exe` da compilação;
   - para publicar: `build/publish.ps1 -GoogleOAuthJson` põe-no junto do programa na instalação (passo 8; o `client_id`
     passa a ser público).
7. **Para sair de «Em teste»** (qualquer conta Google poder entrar, e os tokens deixarem de expirar aos 7 dias): com
   só o âmbito `drive.appdata` (não sensível), basta **Audience › Publicar aplicação** — a verificação da aplicação
   não é obrigatória e não aparece o aviso de aplicação não verificada. Depois de publicar, quem entrou em modo de
   teste tem de entrar outra vez. **Feito em 2026-11-09:** Branding com a página inicial, a política de privacidade,
   os termos e o domínio autorizado `mrtjr.github.io`; o único âmbito é `drive.appdata`; estado «Em produção».
   - A **verificação da marca** é opcional mas recomendada: sem ela o ecrã de consentimento não mostra o nome «Éteris»
     nem o logótipo. Pede a página inicial e a política de privacidade num domínio verificado no Google Search
     Console; as páginas estão em `docs/site/` (página inicial, política de privacidade — o que o âmbito
     `drive.appdata` permite, a cifra no PC com uma chave que a Google e o Éteris não têm, o que a Google vê, como
     apagar, e a declaração de conformidade com a *Google API Services User Data Policy*, incluindo os requisitos de
     «Limited Use» — e termos), para o GitHub Pages.
8. Publicar com o login: `.\build\publish.ps1 -Version X.Y.Z -GoogleOAuthJson <caminho do google-oauth.json>`
   (`docs/motor.md`, «Como publicar»). O ficheiro **não entra no repositório** (`.gitignore`); o `client_id` passa a
   ir com o programa (é público por natureza nas apps de computador; quem protege a troca é o PKCE). Desde a 0.2.14 o
   `build/pack.ps1` **recusa empacotar uma publicação sem ele** (só com `-SemLogin`, para a publicação de emergência
   pelo CI): uma release oficial nunca sai com a sincronização «indisponível nesta compilação» por esquecimento.

## Testes

`tests/Eteris.Browser.Tests/Sync/`:

- `SyncCryptoTests` — código de recuperação (ida e volta, tolerância, cada gralha recusada), chaves (código errado,
  envelope adulterado, formato novo), IDs opacos, ficheiro cifrado (preenchimento, contexto, cada byte protegido,
  versão nova), HLC e relógio atrasado.
- `SyncEngineTests` — **convergência com edições concorrentes em 3 aparelhos**, ordem de chegada indiferente, relógio
  atrasado, **nenhum texto em claro no transporte** (varre todos os bytes em Latin-1, UTF-8 e UTF-16), lote adulterado
  recusado e repetido, lotes fora de ordem, envio falhado repetido igual, **lápides** e prazo, aparelho ausente mais do
  que o prazo não ressuscita apagados, compactação, campos desconhecidos preservados, **formato mais recente pendente**,
  **recuperação pelo código**, **nova cadeia** depois de um aparelho perdido, estado depois de reiniciar, notas (linhas
  diferentes em 3 aparelhos; a mesma linha com conflito e resolução), abas por aparelho.
- `SyncPairingTests` — **emparelhamento** por QR e por código curto, nada no QR nem no transporte revela o segredo,
  homem no meio vê outro código e é recusado, QR com chave trocada, mensagem adulterada, expiração; fusão a três
  (casos limpos, conflito, textos enormes); perfil exportado (ida e volta, código errado, adulteração, sal por ficheiro).
- `GoogleSyncTests` — **Google Drive falso em memória** (OAuth com PKCE, list com páginas, multipart lido dos bytes,
  resumable, PATCH, 401 com renovação, 429/503 com novas tentativas), 3 aparelhos e emparelhamento pelo Drive falso,
  início de sessão de ponta a ponta pelo loopback, endereço para copiar, estado forjado, permissão em falta,
  `prompt=select_account` e o recuo `prompt=consent`, `login_hint` só na reentrada e nunca num endereço `eteris://`,
  a página de fim segurada e os cabeçalhos do recetor, «Sair deste computador» sem nenhum pedido
  (`SignOutRoute_NeverRevokes`) e «Retirar o acesso» com uma revogação, `google-oauth.json`, e a **exceção de rede**
  (lista de endereços, recusa antes de sair, varrimento do código).
- `GoogleAccountTests` — conta gravada cifrada com DPAPI (nada legível no ficheiro, outra «entropia» não abre), apagada
  ao sair, **nenhum pedido ao montar a sessão e ler o estado** (o arranque), o pedido só por `RequestIfMissing` (menu e
  rota `sync-account`) ou depois de entrar (varrimento do código: nada no `App.xaml.cs`/`MainWindow.xaml.cs`, nenhum
  temporizador, na página só depois de um gesto), uma única tentativa falhada por sessão, 401 com renovação, texto da
  rede limpo, e a linha do menu nas três situações (sem opção, sem sessão, com sessão) e nas janelas privadas.
- `SyncIntegrationTests` — a opção experimental saiu (um settings.json antigo abre), secção nas Definições e «indisponível
  nesta compilação», definições que viajam (e as que não), notas importadas, DPAPI (também o estado da sincronização),
  navegador do sistema, formulário POST, **o arranque só marca a 1.ª ronda para 30 s depois** (varrimento do
  `App.xaml.cs` e do `SyncService.AfterFirstPaint`), a pasta de teste ignorada na versão instalada, o resumo da linha
  do menu, que este documento regista a exceção, e `NetworkMoments_ClosedListOfCallers` — a lista fechada de quem
  procura pedidos (`SyncService.Look(` e `WindowActivated(`), a mesma de «Quando sincroniza» e de
  `PrivacyPage.SyncException`.
- `SyncAgentTests` — **dois aparelhos** com a pasta e com o **Drive falso**: criar a conta sem pedir nada (kit por
  guardar), entrar pelo **kit de recuperação** e pela **aprovação noutro computador** (o código escrito no anfitrião,
  nenhum nome em claro nos ficheiros do pedido, ficheiros apagados no fim), recusa, junção sem perder nada (o que só
  existia aqui sobe, a conta vale, notas juntas), alterações nos dois sentidos, **conflitos**, **lápides**, alteração a
  meio de uma ronda, alteração recusada que não ressuscita, campos desconhecidos preservados, **remover um aparelho**
  (os outros continuam — sozinhos com dois aparelhos, com o identificador do kit com mais —, o removido para), **apagar os dados** (pasta vazia, os outros param sem recriar), «Sair»
  com e sem a chave, **nenhum pedido ao montar o agente e ler o estado**, sem rede (falha calada, recupera), e o
  agendador (30 s depois da janela, 10 s depois de uma alteração, 10 min, espera crescente).
- `SyncEngineTests.RemovedDevice_IsLeftOut_TheOthersGetTheNewChain` — a conta nova entregue aos que ficam; o removido
  não tem mensagem e uma forjada por ele não abre.
- Revisão do login completo: `SyncEngineTests` (`Rotation_DoesNotLoseWhatAStayingDeviceSentButTheRotatorNeverRead`,
  `RemovedDevice_CannotHijackTheOthers_WithAnAccountOfItsOwn`, `RekeyIsUnambiguous_OnlyWhenTheSenderIsTheOnlyPinnedPeer`
  (auditoria, SYN-01; com `SyncAgentTests.RemovedDevice_DeletesTheRealMessage_OthersDoNotFollow`,
  `RemovedDevice_DeletesTheKeysOfTheNewAccount_OthersDoNotFollow` e
  `Rekey_ThreeWrongIdentifiers_StopForGood_AndRejoiningByKitWorks`; e, da revisão, o registo de chaves antigo reposto
  pelo removido: `RemovedDevice_RestoresTheOldKeys_OthersStillFindTheRekey`,
  `…_ADeviceThatOnlyPinnedTheSenderFollowsAlone_AndOnlyOnce`, `…_AndSwapsTheMessage_OthersDoNotFollow` e
  `AJunkRekeyFile_DoesNotStopTheRound`), `Rotation_GivesTheNewAccountOnlyToDevicesPinnedBefore`,
  `Rotation_InTwoSteps_OldAccountIntactUntilTheSecond_WhichCanBeRepeated`), `SyncAgentTests` (o que outro aparelho
  enviou e quem remove não leu fica; troca interrompida acaba na ronda seguinte; duas remoções seguidas; aparelho que
  aparece à pressa não recebe a chave; «Sair» a meio de uma ronda; outra conta Google; registo de chaves que não abre;
  pedidos só contados nas rondas; coleção ilegível não apaga; dois computadores a criar a conta; autorização retirada;
  código novo), `SyncPairingTests.TwoHostsAnswerTheSameRequest_TheOneNotChosenStepsAsideQuietly`,
  `GoogleSyncTests.Drive_CreateThatFailsAfterCreating_LeavesOneFile_AndDeleteRemovesDuplicates` e `PrivateShieldTests`.
- `PrivacyPageTests.NoGoogle_WithSyncOn_StaysKept_AndStatesTheException`, `SyncException_NumbersMatchTheCode` (os
  números da exceção cruzados com as constantes), `PrivacyPage_AlwaysHasTheSyncBlock` e
  `SignedOut_CanReadWhatGoesToGoogle_BeforeSigningIn` (o bloco «Se ligar a sincronização»).
- `PublicTextsTests` (raiz dos testes) — os textos públicos (política, sítio, README, este documento e
  `docs/palavras-passe.md`, Novidades e a entrada da versão no CHANGELOG) não voltam a dizer o que deixou de ser verdade.

Acrescentados na 0.2.15 («Um login e pronto»):

- Entrar: `SignInEntryTests` (os pontos de entrada, a Nova aba sem nenhum, nada antes do clique), `WelcomeSignInTests`
  (o passo das boas-vindas), `SignInTrackerTests`, `SignInBarModelTests` e `SignInBarTests` (a barra e o árbitro das
  barras), `SignInPageTests` (os seis textos da página de fim), `SignInFinishTests`, `WindowAttentionTests` (o piscar),
  `SessionExpiredPromptTests` (o aviso de sessão expirada, uma vez por abertura) e o auxiliar `TestStatus`.
- Kit de recuperação: `RecoveryWordsTests` (as 24 palavras, os vetores BIP39, a lista fixada por SHA-256, os códigos
  antigos aceites), `RecoveryKitTests` (ver, guardar, imprimir, copiar, a porta do Windows, as pastas na nuvem) e
  `RecoveryReminderTests` (o lembrete de 30 em 30 dias).
- Computador novo e aprovação: `AutoPairingTests` (o pedido sozinho só sem estado desta conta, a conta substituída),
  `PairingRenewalTests`, `GuestPairingModelTests`, `GuestPairingBarTests`, `GuestPairingServiceTests` (o cartão do
  computador novo), `PairingCodeTests`, `PairingNumberMatchingTests` (aprovar escrevendo o código, três erros, «Não fui
  eu», pedidos forjados), `PairingBarModelTests`, `PairingBarTests`, `PairingFatigueTests` e `ActivationLookTests`
  (voltar à janela, no máximo de 5 em 5 minutos).
- `DeviceNameTests` (o nome do computador, sempre cifrado), `SyncAccountChoiceTests` (pagamentos e documentos como
  escolha da conta, a herança e a oferta), `SyncExitTests` (sair, retirar o acesso, perdi um computador, começar do zero)
  e `SyncSettingsSectionTests` (Definições › Sincronização mais curta).
- Revisão final da 0.2.15 (o agente): `SyncAgentThreadingTests` (o estado lido e gravado de outra thread a meio de uma
  ronda: o estado guardado é sempre uma cópia, nunca o estado vivo do motor; um assinante de `Changed` que lança não
  estraga a operação), `PairingTransportFailureTests` (uma falha passageira do transporte em cada espera e em cada
  escrita do emparelhamento não acaba com o pedido; ficheiros selados e segredos que não conferem),
  `PairingFlowStateTests` (a entrega que falha, ligar outra vez com um pedido vivo, a pausa que para os pedidos, «Não
  fui eu» que diz se recusou, a primeira ronda falhada depois da aprovação) e `SecretFileTests` (apagar a chave e os
  tokens com o ficheiro preso por outro processo). Em `PairingNumberMatchingTests`: o pedido cancelado no computador
  novo que sai da barra sozinho e o teto de 3 pedidos acompanhados (`ManyRequests_OnlyKAreAnswered`). Os testes com
  vários agentes partilham o `SyncTestCloud` (transporte em memória com sinais; esperas sem orçamento de tempo).
- Revisão final da 0.2.15 (a interface e os textos): `FinalReviewUiTextsTests` (as palavras do kit não se selecionam e
  «Pesquisar» só aparece numa página web; uma janela fechada não volta a mostrar barras; as três barras descem por
  baixo do pedido de permissão e do procurar e anunciam-se ao leitor de ecrã; a captura do Éteris recusa com um segredo
  à vista; o ficheiro do kit marca o kit gravado e vai para o disco; a área de transferência segura esvazia-se se um
  formato de exclusão falhar; entrar com o kit na conta substituída pede o Windows; as âncoras das barras levam ao
  item). Em `PublicTextsTests`: o aparelho removido não perde a escrita no Drive sem «Perdi um computador» e a
  probabilidade por pedido do emparelhamento vem com o teto por série.
