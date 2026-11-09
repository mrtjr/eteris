# Sincronização do Éteris («Entrar com o Google», opção B)

Estado: **sincronização ligada ao Éteris, fora do modo experimental (ramo `sync`, por publicar).** Decisão do dono
(opção B): o início de sessão na Google faz-se no **navegador do sistema** (OAuth 2.0 para apps de computador, PKCE e
redirecionamento por loopback `127.0.0.1`), os dados do Éteris ficam na **pasta oculta da aplicação** (`appDataFolder`)
do Google Drive do utilizador, com o âmbito `https://www.googleapis.com/auth/drive.appdata` (não sensível), e vão
**sempre cifrados ponta a ponta no PC**: a chave nunca vai para a Google. A chave passa entre aparelhos por aprovação
num aparelho já ligado (o aparelho novo mostra um código, o outro mostra o pedido com o mesmo código) ou pelo código de
recuperação mostrado uma vez. Base: `C:\cef\notas\viabilidade\viab-sync.md`, `viab-login.md` e `viab-sintese.md`
(D1–D7).

## O que existe

| Peça | Onde | Estado |
|---|---|---|
| Biblioteca `Eteris.Sync` (net10.0, sem WPF, sem pacotes; partilhável com o Android) | `src/Eteris.Sync` | pronta |
| Modelo de registos, IDs estáveis e opacos, HLC, lápides, fusão (notas a três) | `Model/`, `Merge/`, `Engine/` | pronta |
| Formato cifrado (lotes, instantâneos, estado, perfil exportado) | `Format/` | pronta |
| Chaves: segredo de conta, código de recuperação, DEK embrulhada, «nova cadeia» entregue aos que ficam | `Crypto/`, `Engine/SyncEngine.cs` | pronta |
| Emparelhamento (ECDH efémero + código de verificação), também pedido pelo aparelho novo | `Pairing/` | pronto, com ecrã |
| O aparelho na conta: criar, entrar, juntar, remover, apagar, sair (`SyncAgent`) e o perfil local (`ProfileSync`) | `Engine/SyncAgent.cs`, `Engine/ProfileSync.cs` | pronto |
| Quando sincronizar (`SyncSchedule`) | `Engine/SyncSchedule.cs` | pronto |
| Transportes: pasta local e Google Drive `appDataFolder` (REST, multipart e resumable) | `Transport/`, `Google/` | prontos |
| OAuth de computador (PKCE + loopback, navegador do sistema, renovação, revogação) | `Google/GoogleOAuth.cs` | pronto |
| Tokens, conta e estado da sincronização cifrados com DPAPI no perfil | `src/Eteris.Browser/Sync/Dpapi.cs` | pronto |
| O perfil do Éteris para a sincronização (ler e aplicar) | `Sync/BrowserProfile.cs`, `MainWindow.Sync.cs` | pronto |
| O serviço no navegador (agente, agendador, estado para a interface) | `Sync/SyncService.cs` | pronto |
| Definições › **Sincronização** (sempre que há `google-oauth.json`) | `settings.html`, `SettingsPage.cs` | pronta |
| A conta no menu do navegador (nome, e-mail, avatar, estado da sincronização, pedidos) | `MainWindow.BrowserMenu.cs` | pronta |
| Exportar / importar o perfil cifrado num ficheiro `.eteris` | `Sync/ProfileTransfer.cs` | pronto |
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
| Palavras-passe | **não** | o Éteris não guarda palavras-passe (o gestor do Chromium está desligado: `credentials_enable_service = false`) |
| Cookies, transferências, zoom por site, permissões, leitura para mais tarde, sessões guardadas | não | — |

O que é de cada computador fica: as abas, o zoom por site, as permissões dadas a sites, a barra lateral.

## Os fluxos

- **Primeiro aparelho.** Depois de entrar (ou de «Ligar a sincronização»), se a pasta da aplicação não tiver nenhuma
  conta (`eteris/<cadeia>/keys`), o Éteris cria-a, envia os dados deste computador e mostra o **código de recuperação**
  com «Copiar» e «Já guardei» e o aviso: *sem este código e sem outro computador ligado, os dados na nuvem não se
  recuperam*. O código fica à vista (Definições e linha do menu: «Guarde o código de recuperação») até «Já guardei» —
  também depois de reabrir o Éteris — e nunca mais aparece.
- **Aparelho seguinte.** Se já houver conta, a secção oferece:
  - **«Aprovar noutro computador»**: este aparelho abre um pedido (`eteris/pair/<sessão>/0-request`, só a hora) e diz
    «abra o menu (Alt+F) ou Definições › Sincronização no outro computador». Numa ronda agendada, o aparelho ligado só
    conta o pedido (o menu diz «Um computador novo pede para entrar»); responde ao protocolo de emparelhamento quando o
    utilizador abre o menu do Éteris (⋯) ou tem a secção Sincronização à vista (a página volta a procurar de 5 em 5 s
    enquanto estiver à vista, até 10 minutos depois do último gesto). Dois aparelhos ligados que respondam ao mesmo
    pedido não se atrapalham: o aparelho novo diz a que chave de anfitrião se compromete (`for` no passo 2) e o outro
    desiste calado. Responder mostra «*nome do aparelho* quer juntar-se à sincronização» com o **código de verificação** de 6 algarismos; o
    aparelho novo mostra o mesmo código e o botão «O outro computador mostra este código». O utilizador confirma nos
    dois («Aprovar» no ligado); só então a conta segue, cifrada para o aparelho novo. Sem QR (sem bibliotecas novas).
  - **«Usar o código de recuperação»** (no corpo de um POST, nunca no endereço).
  Depois, **junta os dados sem perder nada**: o que só existia neste PC sobe; o que já existe na conta vale
  (definições, títulos, Espaços); notas diferentes juntam-se.
- **Alterações** nos dois sentidos, com conflitos resolvidos por HLC (ganha a última ronda que as enviou) e lápides
  (o apagado não volta). O que o utilizador muda a meio de uma ronda que traz outra versão fica e segue na seguinte.
- **Aparelhos**: lista nas Definições (nome, «este computador», quando sincronizou) com **«Remover»** (duas vezes, para
  confirmar). Remover faz **nova cadeia**: segredo e chaves novos, tudo recifrado, a cadeia antiga apagada, um código
  de recuperação novo mostrado uma vez. Os aparelhos que ficam recebem a conta nova sozinhos: cada aparelho tem uma
  chave estática P-256 (no estado local, DPAPI) cuja parte pública vai no seu estado cifrado; os outros fixam-na na
  primeira vez que a veem; a conta nova vai em `eteris/<cadeia antiga>/r/<aparelho>/<de>`, cifrada com ECDH entre as
  chaves estáticas de quem remove e de quem fica. O removido não tem mensagem para si e não consegue forjar uma (não tem
  as chaves privadas dos outros): para — «a sincronização parou… este computador foi removido noutro aparelho».
- **«Sair»** (Definições, botão da conta): a sincronização para neste aparelho e os tokens são apagados e revogados na
  Google (como antes). Com «Ao sair, apagar também a chave da sincronização deste computador», o estado local sai todo
  e voltar exige o código ou outro aparelho; sem ela, entrar outra vez retoma sem código.
- **«Apagar os meus dados do Éteris no Google Drive»** (com confirmação): apaga todos os ficheiros do Éteris da pasta
  da aplicação (todas as contas e pedidos), **sem revogar** nem sair da conta Google. Os dados deste computador ficam;
  a sincronização fica desligada aqui e, nos outros aparelhos, para na ronda seguinte (não recriam a conta sozinhos,
  o que desfaria o «apagar»). «Ligar outra vez» cria uma conta nova.

## Quando sincroniza

| Momento | Porquê |
|---|---|
| **30 s depois de a janela estar pronta** (1.ª página), só com a sincronização ligada — ou antes, 10 s depois de uma alteração feita nesses 30 s | o arranque fica para a janela e as páginas; quem abre o Éteris só para ver uma coisa e o fecha logo não fala com a Google; a promessa «No arranque, nada sai para a Google» mantém-se no sentido do motor e a exceção da sincronização fica dita (ver «Promessa») |
| **10 s depois da última alteração local** (favorito, definição, Escudo, Espaço, notas) | espera agregadora: várias alterações seguidas vão num lote; e só há rede se a comparação local (sem rede) encontrar alguma coisa por enviar |
| **De 10 em 10 minutos** com o Éteris aberto | para receber o que os outros aparelhos mudaram |
| **Ao entrar**, ao ligar, ao entrar num aparelho novo, e em **«Sincronizar agora»** | ação do utilizador |
| Ao abrir o menu do Éteris (⋯), ou com a secção Sincronização à vista (depois de um gesto; de 5 em 5 s, até 10 min depois do último gesto) | procura pedidos de aparelhos novos e responde-lhes (e relê a lista de aparelhos nas Definições); ao responder a um pedido, acompanha-o de 2 em 2 s até ele acabar (no máximo 10 min) |
| Numa ronda agendada | os pedidos de aparelhos novos só se **contam** (para o menu dizer «Um computador novo pede para entrar»); não se responde a nenhum sem o utilizador abrir o menu ou a secção |
| Uma vez, depois de entrar (ou ao abrir o menu ou a secção, com a sincronização ligada, se a conta ainda não estiver guardada) | o nome e o e-mail da conta (`about.get`) |
| Quando o token de acesso expira (cerca de 1 h), no próximo pedido | renovação da sessão em `oauth2.googleapis.com/token` |

Sem rede, com a Google em baixo ou a pasta inacessível: tenta outra vez sozinho, com espera crescente (30 s, 1, 2, 4,
8, 16 e depois de 30 em 30 min), sem avisos; as alterações locais não encurtam a espera; o que estava por enviar fica
na caixa de saída (no estado guardado) e segue depois. Uma compactação (instantâneo) por dia. Se a **autorização da
Google tiver expirado ou sido retirada** (`invalid_grant`, permissão desmarcada), não tenta mais: o agendador para, o
menu diz «Sincronização em pausa: entre outra vez» e as Definições mostram «Entrar outra vez» (sem revogar nada nem
mexer na chave), como o «Sincronização em pausa · Iniciar sessão novamente» do Chrome.

**Estado visível:** Definições › Sincronização («Sincronizado há 2 min», «A sincronizar…», o erro em claro) e a linha
da conta no menu («Sincronizado há 2 min», «A sincronizar…», «Pedido de *nome* · código 123 456», «Um computador novo
pede para entrar», «Guarde o código de recuperação», «Por sincronizar: o Éteris tenta outra vez sozinho»,
«Sincronização em pausa: entre outra vez»).

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

Definições que **não** viajam: Mica, esconder das capturas, barra lateral (aberta, painel, largura, lado), e o que só
vale neste computador. O prazo do arquivo e a língua dos sites só viajam se o utilizador os escolheu.

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

- **Segredo de conta R:** 256 bits do gerador do sistema (nunca de uma senha). Escrito como **código de recuperação**:
  13 grupos de 4 carateres base32 Crockford + 1 grupo de controlo (20 bits de SHA-256), lido com tolerância
  (minúsculas, espaços, O→0, I/L→1); uma gralha é recusada.
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
- **Nova cadeia** (aparelho perdido ou código exposto): segredo novo, DEK nova, tudo recifrado com IDs novos na cadeia
  nova, e a antiga apagada. Os outros aparelhos recebem o código novo.
- Tudo na biblioteca padrão do .NET 10 (`HKDF`, `AesGcm`, `HMACSHA256`, `ECDiffieHellman`, `BrotliStream`).

### Emparelhamento (aprovação num aparelho já ligado)

Na interface do PC, quem começa é o **aparelho novo** (`PairingRequests`): abre um pedido `0-request` (só a hora) e o
aparelho ligado responde como anfitrião na mesma sessão (`PairingHost.AnswerAsync`); daí em diante é o protocolo de
baixo. Um pedido que ninguém fez (escrito por quem controla o transporte) aparece no aparelho ligado, mas sem um ecrã
com o mesmo código ao lado o utilizador não o aprova: o texto do pedido diz «só aprove se estiver a ligar esse
computador agora e ele mostrar este mesmo código». O QR (para um telemóvel) continua na biblioteca.

```
QR no anfitrião:  eteris-par:1:<sessão 80 bits>:<chave pública efémera P-256 do anfitrião>   (ou só o código curto)
1 anfitrião → transporte  chave pública do anfitrião
2 convidado → transporte  chave pública do convidado + compromisso SHA-256(pkC ‖ pkA ‖ nC)
3 anfitrião → transporte  nonce nA (só depois do compromisso)
4 convidado → transporte  nonce nC (abre o compromisso) + nome do convidado cifrado com a chave da sessão
   os dois ecrãs mostram o código de verificação: 6 algarismos de SHA-256(sessão ‖ pkA ‖ pkC ‖ nA ‖ nC)
   o utilizador confirma NO ANFITRIÃO que são iguais
5 anfitrião → transporte  R cifrado com ECDH(pkA, pkC) → HKDF → AES-256-GCM (transcrito como dados associados)
   o utilizador confirma TAMBÉM NO CONVIDADO que o código é igual; só então o convidado abre o passo 5
```

- O QR só tem dados públicos: **fotografá-lo não dá nada**. Quem também controlar o transporte e se meter no meio tem
  de se comprometer com a sua chave e o seu nonce antes de ver o nonce do anfitrião, por isso acerta no código com
  probabilidade 1 em 1 000 000, e o utilizador vê códigos diferentes e recusa. Sem a confirmação, o passo 5 não existe.
- Com QR, a chave do anfitrião vem do ecrã (autêntica) e tem de conferir com a do transporte. Sem câmara (PC↔PC),
  o código curto `XXXX-XXXX-XXXX-XXXX` dá só a sessão, e quem garante as chaves é o código de verificação.
- **Confirmação dos dois lados.** Com o código curto, quem controla o transporte pode pôr a sua chave no passo 1 e
  fazer de anfitrião só para o convidado, escrevendo logo o seu passo 5 (com a SUA conta, para o convidado passar a
  cifrar tudo com uma chave que o atacante tem). Por isso o convidado também pergunta «o código é igual ao do outro
  ecrã?» (`PairingGuest.ConfirmCode`) e só depois abre o segredo (`WaitForSecretAsync` recusa sem isso). Depois de o
  receber, o ecrã mostra o nome do anfitrião e o código outra vez antes de juntar o aparelho à conta.
- **Nada do utilizador em claro no transporte:** o passo 2 leva só a chave pública e o compromisso; o nome do aparelho
  convidado segue no passo 4, cifrado com a chave da sessão (mesmo que o anfitrião feche de repente e os ficheiros
  fiquem lá, a Google não lê «PC da Ana»). As chaves públicas têm de ser P-256 (outra curva é recusada logo).
- A sessão expira em 10 minutos e os ficheiros são apagados no fim. O transporte é o mesmo da sincronização (os dois
  aparelhos com a mesma conta Google, ou a mesma pasta).

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
  na interface de loopback (nada de fora lhe chega e o Windows não pede regra na firewall), aceita um único pedido e
  fecha. `access_type=offline` para o token de renovação. Se o utilizador desmarcar a permissão no ecrã da Google, o
  Éteris diz que sem ela não sincroniza.
- **Tokens** guardados em `<perfil>\sync\google.tokens`, cifrados com DPAPI do utilizador do Windows.
- **Sair** apaga os tokens e a conta guardada deste computador e revoga os tokens na Google
  (`oauth2.googleapis.com/revoke`).
- **`client_id`:** lido de `google-oauth.json` ao lado do `Eteris.Browser.exe` (vai com a instalação) ou na pasta do
  perfil (testes). Aceita o JSON que a consola da Google descarrega (`{"installed": {…}}`). Sem ele: «indisponível nesta compilação»
  e nenhum pedido. O `client_secret` das apps de computador não é segredo (vai com o programa; a Google sabe-o); quem
  protege a troca é o PKCE.

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
- **Quando:** logo a seguir a um início de sessão bem-sucedido; e, se já havia sessão sem conta guardada (quem entrou
  antes desta versão), **uma vez** quando o utilizador abre o menu do navegador ou a secção Sincronização das
  Definições (na página, só depois de um gesto da pessoa — clique, tecla, roda, toque — com a secção à vista: uma aba de
  Definições reposta no arranque não pede nada). Em segundo plano; a linha do menu atualiza-se se ainda estiver aberta.
  **Nunca no arranque** (a promessa da 0.2.11, «No arranque, nada sai para a Google», mantém-se), nunca num
  temporizador, e **no máximo uma tentativa falhada por sessão** (depois de falhar, só volta a tentar depois de sair e
  entrar, ou de reabrir o Éteris; qualquer falha conta, também a de gravar o token renovado). Enquanto não há conta, o
  menu diz «Conta Google ligada». O pedido corre fora do fio da interface (abrir o menu nunca espera pela rede nem
  pelo proxy do Windows), e o gesto que é o próprio clique em «Sair» não o lança.
- **Onde fica:** `<perfil>\sync\google.account`, cifrado com DPAPI do utilizador do Windows (como os tokens, com outra
  «entropia»). **Apagado ao «Sair».** O texto que vem da rede é limpo (sem caracteres de controlo, até 200 caracteres)
  antes de ir para a interface. «Sair» a meio de um pedido: o pedido da conta é cancelado e a sessão e a conta mudam de
  geração, por isso uma resposta que chegue depois (a renovação do token ou o `about.get`) já não grava `google.tokens`
  nem `google.account`, nem mostra a conta antiga depois de se entrar com outra.
- **No menu**, numa janela normal e com o identificador da aplicação (`google-oauth.json`; sem ele as Definições
  dizem «indisponível nesta compilação» e o menu não oferece o que não se pode cumprir). Nas janelas privadas não
  aparece nada da conta. Com sessão, avatar (a letra com a tinta da cor de destaque), nome em negrito, e-mail e o estado
  da sincronização; sem sessão, «Entrar com o Google…». Abrir o menu procura pedidos de aparelhos novos (ação do
  utilizador) e a linha atualiza-se se o menu ainda estiver aberto. As duas abrem `eteris://settings/#sincronizacao` e têm nome para leitores de ecrã. Pelo teclado (Alt+F) a
  linha da conta é a primeira paragem; aberto com o rato, o foco fica em «Nova aba», para o convite não aparecer
  realçado em cada abertura. O menu cresce até à altura da janela (sem o antigo teto fixo de 665 px, o menu inteiro
  com a linha da conta cabe numa janela normal) e, numa janela baixa, a barra de deslocamento tem as cores do tema.
- **Nas Definições**, com sessão a linha chama-se «Conta Google» e o botão «Sair» anuncia-se como «Sair da conta
  Google». Numa janela privada as Definições não mostram nada da conta e a secção Sincronização fica indisponível.

### Perfil cifrado num ficheiro

Definições › Sincronização › **Exportar perfil cifrado** grava em Transferências um `Perfil Éteris AAAA-MM-DD.eteris`
(favoritos, Espaços, definições que viajam, decisões do Escudo, notas) cifrado com um código de recuperação **novo**,
mostrado uma única vez na página. As **abas abertas não vão**: a importação não as usava, e os URLs de abas podem ter
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

## A exceção de rede

Regra do Éteris: **os pedidos de rede do Éteris passam só pelo motor**. A sincronização pela Google é uma **exceção
explícita**: fala com a Google por um cliente HTTP próprio (`GoogleEndpoints.CreateClient`), e só com:

| Endereço | Para quê |
|---|---|
| `https://accounts.google.com/o/oauth2/v2/auth` | ecrã de consentimento — aberto no **navegador do sistema**, nunca pedido pelo cliente HTTP |
| `https://oauth2.googleapis.com/token` | trocar o código por tokens e renová-los |
| `https://oauth2.googleapis.com/revoke` | revogar ao «Sair» |
| `https://www.googleapis.com/drive/v3/files…` | listar, ler e apagar na pasta oculta da aplicação |
| `https://www.googleapis.com/upload/drive/v3/files…` | enviar (multipart, resumable, PATCH) |
| `https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)` | nome e e-mail da conta, para o menu e as Definições (ver «A conta no menu»: só depois de uma ação do utilizador) |

- O `GoogleOnlyHandler` recusa qualquer outro endereço **antes de sair do processo** (só `https`, porta por omissão,
  sem utilizador no endereço; `www.googleapis.com` só com `/drive/v3/files`, `/upload/drive/v3/files` e
  `/drive/v3/about`; `oauth2` só
  `/token` e `/revoke`). Sem cookies, sem redirecionamentos automáticos, sem os cabeçalhos do motor; `User-Agent`
  `Eteris-Sync/1`.
- Só corre depois de o utilizador carregar em «Entrar com o Google»; com a sincronização ligada, nos momentos de
  «Quando sincroniza» (30 s depois de abrir, salvo se o utilizador mudar alguma coisa antes: então 10 s depois dessa
  alteração).
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
| **A Google** (ou quem entrar na conta Google) | ver que a conta usa o Éteris, quando sincroniza (horas dos ficheiros, IP dos pedidos), quantos ficheiros e os tamanhos (preenchidos: dão a ordem de grandeza do perfil, não o número de favoritos); apagar ficheiros ou não os entregar (o Éteris deixa-os pendentes) | ler favoritos, notas, abas, definições, nomes dos aparelhos ou URLs; alterar ou trocar ficheiros sem ser detetado; abrir a conta Éteris sem o código |
| **Quem fotografa o QR** | nada | — |
| **Quem escreve um pedido falso de aparelho novo** (controla o transporte) | fazer aparecer «*nome* quer juntar-se» no aparelho ligado | receber a conta sem o utilizador carregar em «Aprovar» com um ecrã ao lado a mostrar o mesmo código |
| **Um aparelho removido** (tem o código antigo) | ler o que já tinha | ler ou escrever na conta nova; forjar a mensagem com a conta nova para os outros (precisava das chaves privadas deles) |
| **Quem se mete no meio do emparelhamento** (controla o transporte) | tentar a sua sorte: 1 em 1 000 000; com o código curto, fazer de anfitrião falso para o convidado (que mostra um código que o anfitrião verdadeiro não mostra) | receber o segredo sem o utilizador confirmar códigos iguais no anfitrião; pôr o convidado na SUA conta sem o utilizador confirmar códigos iguais no convidado; ler o nome dos aparelhos |
| **Quem rouba o PC com a sessão do Windows aberta** | o que esse Windows lê (tokens e estado protegidos por DPAPI do utilizador) | — remédio: «Nova cadeia» noutro aparelho e retirar o acesso na conta Google |
| **Quem tem um ficheiro `.eteris`** | nada sem o código | — |
| **O Éteris (o projeto)** | nada: não há servidor do Éteris | — |

- **Recuperação (D6):** sem o código e sem nenhum aparelho ligado, a cópia na nuvem é irrecuperável. Não há
  «esqueci-me do código». Isto tem de ser dito ao utilizador quando o código aparece.
- **Promessa (D1):** o README diz «Sem telemetria. Por omissão tudo fica no computador; a sincronização é opcional,
  cifrada de ponta a ponta e desligada por omissão». Em `eteris://privacy/`, «No arranque, nada sai para a Google»
  continua verdadeira por omissão (é medida no motor) e, com a sincronização ligada, diz a exceção escolhida pelo
  utilizador: os pedidos da sincronização, cifrados, para o Drive dele, 30 s depois de abrir (ou 10 s depois de uma
  alteração, se for antes), 10 s depois de uma alteração, de 10 em 10 minutos e ao abrir o menu do Éteris (⋯) ou com a
  secção Sincronização à vista (pedidos de outros computadores, acompanhados de 2 em 2 s quando se responde a um), a
  renovação da sessão em `oauth2.googleapis.com`, o nome e o e-mail da conta, e as ações que o utilizador pede
  (`PrivacyPage.SyncException`; teste `NoGoogle_WithSyncOn_StaysKept_AndStatesTheException`). A exceção aparece também
  com sessão Google e a sincronização desligada (uma sessão da 0.2.10–0.2.12); aí, abrir o menu já não pede a conta.

### O que a Google vê

- que esta conta Google autorizou a aplicação «Éteris» (aparece em *myaccount.google.com › Segurança › Ligações a apps*),
  e quando houve início de sessão e renovação de tokens;
- que o Éteris pediu o nome e o e-mail da conta (`about.get`, uma vez depois de entrar — a Google já os conhece; nada
  do Éteris vai nesse pedido além do token);
- os pedidos à API do Drive: horas, endereço IP, quantos ficheiros, nomes opacos (`eteris/<hex>/d/<hex>/000000000012`)
  e tamanhos aproximados (preenchidos a 1 KiB e, nos grandes, a «baldes» Padmé: um instantâneo de um perfil grande
  pesa mais do que o de um pequeno); o espaço ocupado conta na quota do Drive do utilizador (poucos MB);
- **nunca** o conteúdo: favoritos, URLs, títulos, notas, abas, definições, Espaços, decisões do Escudo, nomes dos
  aparelhos — tudo dentro da cifra. A chave nunca lá vai.

O utilizador pode apagar os dados da pasta oculta em *Google Drive › Definições › Gerir aplicações › Éteris › Eliminar
dados ocultos da app*, e retirar o acesso em *myaccount.google.com/permissions*.

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
   - para publicar: junto do programa na instalação (decidir no `publish.ps1`; o `client_id` passa a ser público).
7. **Para sair de «Em teste»** (qualquer pessoa poder entrar): **verificação da marca** da Google:
   - **página inicial** num domínio **verificado no Google Search Console**, que descreva o Éteris e a sincronização
     (não só um botão de entrar) — sugestão: **GitHub Pages** (`<utilizador>.github.io/eteris`, verificável no Search
     Console por meta-tag; um domínio próprio pode vir depois);
   - **política de privacidade** na mesma página/domínio, com link na página inicial e no ecrã de consentimento:
     o que o âmbito `drive.appdata` permite, que tudo é cifrado no PC com uma chave que a Google e o Éteris não têm, o
     que a Google vê (secção acima), como apagar, e a declaração de conformidade com a *Google API Services User Data
     Policy* (incluindo os requisitos de «Limited Use»);
   - **Publicar aplicação** e pedir a verificação (âmbitos não sensíveis: só a verificação da marca, sem CASA).
8. Publicar com o login: `.\build\publish.ps1 -Version X.Y.Z -GoogleOAuthJson <caminho do google-oauth.json>`
   (`docs/motor.md`, «Como publicar»). O ficheiro **não entra no repositório** (`.gitignore`); o `client_id` passa a
   ir com o programa (é público por natureza nas apps de computador; quem protege a troca é o PKCE).

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
  início de sessão de ponta a ponta pelo loopback, endereço para copiar, estado forjado, permissão em falta, «Sair»
  revoga, `google-oauth.json`, e a **exceção de rede** (lista de endereços, recusa antes de sair, varrimento do código).
- `GoogleAccountTests` — conta gravada cifrada com DPAPI (nada legível no ficheiro, outra «entropia» não abre), apagada
  ao sair, **nenhum pedido ao montar a sessão e ler o estado** (o arranque), o pedido só por `RequestIfMissing` (menu e
  rota `sync-account`) ou depois de entrar (varrimento do código: nada no `App.xaml.cs`/`MainWindow.xaml.cs`, nenhum
  temporizador, na página só depois de um gesto), uma única tentativa falhada por sessão, 401 com renovação, texto da
  rede limpo, e a linha do menu nas três situações (sem opção, sem sessão, com sessão) e nas janelas privadas.
- `SyncIntegrationTests` — a opção experimental saiu (um settings.json antigo abre), secção nas Definições e «indisponível
  nesta compilação», definições que viajam (e as que não), notas importadas, DPAPI (também o estado da sincronização),
  navegador do sistema, formulário POST, **o arranque só marca a 1.ª ronda para 30 s depois** (varrimento do
  `App.xaml.cs` e do `SyncService.AfterFirstPaint`), a pasta de teste ignorada na versão instalada, o resumo da linha
  do menu, e que este documento regista a exceção.
- `SyncAgentTests` — **dois aparelhos** com a pasta e com o **Drive falso**: criar a conta e o código só até «Já
  guardei», entrar pelo **código de recuperação** e pela **aprovação noutro computador** (o mesmo código nos dois,
  nenhum nome em claro nos ficheiros do pedido, ficheiros apagados no fim), recusa, junção sem perder nada (o que só
  existia aqui sobe, a conta vale, notas juntas), alterações nos dois sentidos, **conflitos**, **lápides**, alteração a
  meio de uma ronda, alteração recusada que não ressuscita, campos desconhecidos preservados, **remover um aparelho**
  (os outros continuam sozinhos, o removido para), **apagar os dados** (pasta vazia, os outros param sem recriar), «Sair»
  com e sem a chave, **nenhum pedido ao montar o agente e ler o estado**, sem rede (falha calada, recupera), e o
  agendador (30 s depois da janela, 10 s depois de uma alteração, 10 min, espera crescente).
- `SyncEngineTests.RemovedDevice_IsLeftOut_TheOthersGetTheNewChain` — a conta nova entregue aos que ficam; o removido
  não tem mensagem e uma forjada por ele não abre.
- `PrivacyPageTests.NoGoogle_WithSyncOn_StaysKept_AndStatesTheException`.
