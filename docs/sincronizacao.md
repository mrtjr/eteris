# Sincronização do Éteris («Entrar com o Google», opção B)

Estado: **núcleo pronto e testado, tudo desligado por omissão.** Decisão do dono (opção B): o início de sessão na Google
faz-se no **navegador do sistema** (OAuth 2.0 para apps de computador, PKCE e redirecionamento por loopback
`127.0.0.1`), os dados do Éteris ficam na **pasta oculta da aplicação** (`appDataFolder`) do Google Drive do utilizador,
com o âmbito `https://www.googleapis.com/auth/drive.appdata` (não sensível), e vão **sempre cifrados ponta a ponta no
PC**: a chave nunca vai para a Google. A chave passa entre aparelhos por QR com aprovação num aparelho já ligado, ou
pelo código de recuperação mostrado uma vez. Uma pasta na nuvem local (OneDrive, Dropbox… sem conta Google) é o
transporte alternativo. Base: `C:\cef\notas\viabilidade\viab-sync.md`, `viab-login.md` e `viab-sintese.md` (D1–D7).

## O que existe

| Peça | Onde | Estado |
|---|---|---|
| Biblioteca `Eteris.Sync` (net10.0, sem WPF, sem pacotes; partilhável com o Android) | `src/Eteris.Sync` | pronta |
| Modelo de registos, IDs estáveis e opacos, HLC, lápides, fusão (notas a três) | `Model/`, `Merge/`, `Engine/` | pronta |
| Formato cifrado (lotes, instantâneos, estado, perfil exportado) | `Format/` | pronta |
| Chaves: segredo de conta, código de recuperação, DEK embrulhada, «nova cadeia» | `Crypto/`, `Engine/SyncEngine.cs` | pronta |
| Emparelhamento por QR / código curto (ECDH efémero + código de verificação) | `Pairing/` | pronto (sem ecrã ainda) |
| Transportes: pasta local e Google Drive `appDataFolder` (REST, multipart e resumable) | `Transport/`, `Google/` | prontos |
| OAuth de computador (PKCE + loopback, navegador do sistema, renovação, revogação) | `Google/GoogleOAuth.cs` | pronto |
| Tokens cifrados com DPAPI no perfil | `src/Eteris.Browser/Sync/Dpapi.cs` | pronto |
| Definições › **Sincronização** (opção experimental desligada) | `settings.html`, `Sync/` | pronta |
| Exportar / importar o perfil cifrado num ficheiro `.eteris` | `Sync/ProfileTransfer.cs` | pronto |
| «Entrar com o Google» / «Sair» | `Sync/GoogleSync.cs` | pronto, **por configurar** (falta o `client_id`) |
| Sincronização automática com o Drive (ligar o motor aos dados do Éteris) | — | próxima fase |
| Ecrã de emparelhamento (QR no PC, leitor no telemóvel) | — | próxima fase |
| Escolher uma pasta (OneDrive/Dropbox) como transporte na interface | — | próxima fase |

Nada disto faz pedidos de rede enquanto a opção estiver desligada, e mesmo ligada só depois de o utilizador carregar
em «Entrar com o Google». Exportar/importar o perfil não usa rede nenhuma.

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
  recomeça dos instantâneos antes de enviar seja o que for, por isso não ressuscita o que os outros apagaram.
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
- A compactação apaga os lotes que todos os aparelhos conhecidos já leram, ou mais velhos do que o prazo das lápides.

### Cifra

- **Segredo de conta R:** 256 bits do gerador do sistema (nunca de uma senha). Escrito como **código de recuperação**:
  13 grupos de 4 carateres base32 Crockford + 1 grupo de controlo (20 bits de SHA-256), lido com tolerância
  (minúsculas, espaços, O→0, I/L→1); uma gralha é recusada.
- `KEK = HKDF-SHA256(R, "kek")`, `cadeia = HKDF(R, "chain-id")` (nome opaco da pasta).
- **DEK** aleatória, embrulhada pela KEK com AES-256-GCM (cadeia e geração como dados associados).
- Por ficheiro: `chave = HKDF(DEK, "file:<tipo>:<contexto>")` — uma chave por ficheiro, nonce aleatório de 96 bits.
- **Ficheiro** (`Format/SealedFile.cs`): `"ETS" | tipo | versão | geração | opções | parte pública | nonce | cifra | etiqueta`.
  Conteúdo JSON → Brotli → **preenchido até ao múltiplo de 1 KiB** → AES-256-GCM. Dados associados = cabeçalho + cadeia
  + nome lógico (aparelho e número): trocar, reordenar ou mudar de nome um ficheiro falha a verificação.
- **Versões:** um ficheiro de formato mais recente é recusado com «atualize o Éteris» e fica pendente (não é lido mal
  nem marcado como lido); registos com campos novos são preservados.
- **Nova cadeia** (aparelho perdido ou código exposto): segredo novo, DEK nova, tudo recifrado com IDs novos na cadeia
  nova, e a antiga apagada. Os outros aparelhos recebem o código novo.
- Tudo na biblioteca padrão do .NET 10 (`HKDF`, `AesGcm`, `HMACSHA256`, `ECDiffieHellman`, `BrotliStream`).

### Emparelhamento (QR com aprovação num aparelho já ligado)

```
QR no anfitrião:  eteris-par:1:<sessão 80 bits>:<chave pública efémera P-256 do anfitrião>   (ou só o código curto)
1 anfitrião → transporte  chave pública do anfitrião
2 convidado → transporte  chave pública do convidado + compromisso SHA-256(pkC ‖ pkA ‖ nC)
3 anfitrião → transporte  nonce nA (só depois do compromisso)
4 convidado → transporte  nonce nC (abre o compromisso)
   os dois ecrãs mostram o código de verificação: 6 algarismos de SHA-256(sessão ‖ pkA ‖ pkC ‖ nA ‖ nC)
   o utilizador confirma NO ANFITRIÃO que são iguais
5 anfitrião → transporte  R cifrado com ECDH(pkA, pkC) → HKDF → AES-256-GCM (transcrito como dados associados)
```

- O QR só tem dados públicos: **fotografá-lo não dá nada**. Quem também controlar o transporte e se meter no meio tem
  de se comprometer com a sua chave e o seu nonce antes de ver o nonce do anfitrião, por isso acerta no código com
  probabilidade 1 em 1 000 000, e o utilizador vê códigos diferentes e recusa. Sem a confirmação, o passo 5 não existe.
- Com QR, a chave do anfitrião vem do ecrã (autêntica) e tem de conferir com a do transporte. Sem câmara (PC↔PC),
  o código curto `XXXX-XXXX-XXXX-XXXX` dá só a sessão, e quem garante as chaves é o código de verificação.
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
- **Sair** apaga os tokens deste computador e revoga-os na Google (`oauth2.googleapis.com/revoke`).
- **`client_id`:** lido de `google-oauth.json` ao lado do `Eteris.Browser.exe` (vai com a instalação) ou na pasta do
  perfil (testes). Aceita o JSON que a consola da Google descarrega (`{"installed": {…}}`). Sem ele: «por configurar»
  e nenhum pedido. O `client_secret` das apps de computador não é segredo (vai com o programa; a Google sabe-o); quem
  protege a troca é o PKCE.

### Perfil cifrado num ficheiro

Definições › Sincronização › **Exportar perfil cifrado** grava em Transferências um `Perfil Éteris AAAA-MM-DD.eteris`
(favoritos, Espaços, abas deste computador, definições que viajam, decisões do Escudo, notas) cifrado com um código de
recuperação **novo**, mostrado uma única vez na página. **Importar** lê o ficheiro escolhido e o código; o código e o
ficheiro vão no corpo de um POST para `eteris://settings/sync-import` (nunca no endereço). Junta ao perfil: o que falta
entra, as definições do ficheiro passam a valer, as notas diferentes ficam por baixo das atuais com um título, os
Espaços em falta são criados na primeira janela. Nenhuma rede. Recusado na janela privada.

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

- O `GoogleOnlyHandler` recusa qualquer outro endereço **antes de sair do processo** (só `https`, porta por omissão,
  sem utilizador no endereço; `www.googleapis.com` só com `/drive/v3/files` e `/upload/drive/v3/files`; `oauth2` só
  `/token` e `/revoke`). Sem cookies, sem redirecionamentos automáticos, sem os cabeçalhos do motor; `User-Agent`
  `Eteris-Sync/1`.
- Só corre com a opção experimental ligada **e** depois de o utilizador carregar em «Entrar com o Google».
- Testes: `GoogleSyncTests.HttpClient_OnlyReachesGoogleOAuthAndDrive` (lista de endereços),
  `HttpClient_RefusesAnythingElse_BeforeItLeavesTheProcess`, e
  `SyncLibrary_HttpOnlyInTheGoogleFolder_AndOnlyListedAddresses`, que varre o código da biblioteca: cliente HTTP e
  sockets só na pasta `Google/`, e os únicos endereços escritos no código são os desta tabela (mais `127.0.0.1`).
  Se a exceção crescer, este documento e esses testes mudam juntos.

## Ameaças e privacidade

| Quem | O que consegue | O que não consegue |
|---|---|---|
| **A Google** (ou quem entrar na conta Google) | ver que a conta usa o Éteris, quando sincroniza (horas dos ficheiros, IP dos pedidos), quantos ficheiros e os tamanhos (arredondados a 1 KiB); apagar ficheiros ou não os entregar (o Éteris deixa-os pendentes) | ler favoritos, notas, abas, definições, nomes dos aparelhos ou URLs; alterar ou trocar ficheiros sem ser detetado; abrir a conta Éteris sem o código |
| **Quem fotografa o QR** | nada | — |
| **Quem se mete no meio do emparelhamento** (controla o transporte) | tentar a sua sorte: 1 em 1 000 000 | receber o segredo sem o utilizador confirmar códigos iguais |
| **Quem rouba o PC com a sessão do Windows aberta** | o que esse Windows lê (tokens e estado protegidos por DPAPI do utilizador) | — remédio: «Nova cadeia» noutro aparelho e retirar o acesso na conta Google |
| **Quem tem um ficheiro `.eteris`** | nada sem o código | — |
| **O Éteris (o projeto)** | nada: não há servidor do Éteris | — |

- **Recuperação (D6):** sem o código e sem nenhum aparelho ligado, a cópia na nuvem é irrecuperável. Não há
  «esqueci-me do código». Isto tem de ser dito ao utilizador quando o código aparece.
- **Promessa (D1):** quando a sincronização for anunciada, o README e `eteris://privacy` passam a dizer «Sem contas,
  sem telemetria. Por omissão tudo fica no computador; a sincronização é opcional, cifrada de ponta a ponta e desligada
  por omissão». Até lá a promessa atual mantém-se verdadeira (nada liga sem o utilizador).

### O que a Google vê

- que esta conta Google autorizou a aplicação «Éteris» (aparece em *myaccount.google.com › Segurança › Ligações a apps*),
  e quando houve início de sessão e renovação de tokens;
- os pedidos à API do Drive: horas, endereço IP, quantos ficheiros, nomes opacos (`eteris/<hex>/d/<hex>/000000000012`)
  e tamanhos aproximados (preenchidos a 1 KiB); o espaço ocupado conta na quota do Drive do utilizador (poucos MB);
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
8. Depois disso: decidir se o `google-oauth.json` entra na instalação e quando a sincronização automática é ligada
   (próxima fase), e atualizar a promessa (D1).

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
- `SyncIntegrationTests` — opção desligada por omissão, secção nas Definições, definições que viajam (e as que não),
  notas importadas, DPAPI, navegador do sistema (reconhece o Éteris, prefere outro navegador), formulário POST, e que
  este documento regista a exceção.
