# Gestor de palavras-passe e preenchimento automático do Éteris

Estado: **bloco A (base local: A0–A5), bloco B (interfaces de gestão: B1–B4) e bloco C (na página: C1–C7 — canal,
classificador, sugestões, preencher palavras-passe, guardar e atualizar depois do envio, gerador, preencher contactos,
pagamentos, documentos e viagens, janelas privadas) e bloco D (sincronização: D1–D5) feitos no ramo `sync`, por
publicar** (o bloco E — fecho — do desenho está por fazer, salvo a prova E2 das chaves de acesso). O gestor é próprio, em
C#, com a interface do Éteris; o do Chromium continua desligado. Desenho completo (arquitetura, ameaças, plano de 25
tarefas e decisões do dono): fora do repositório, no documento de desenho de 08/11/2026. Este ficheiro diz o que já
existe no código e como foi provado.

Decisões do dono em vigor (as recomendadas no desenho):

| # | Decisão |
|---|---|
| D-1 | Pagamentos e Documentos de identificação aparecem no tópico «O que passa entre PCs», **desligados por omissão**, com Windows Hello para ligar; os outros ligados |
| D-2 | O número fiscal (**NIF** em Portugal, **CPF** no Brasil) entra nos Documentos de identificação |
| D-3 | Janela privada: preenche a pedido, mas **não oferece guardar nem gerar** |
| D-4 | Verificação de fugas (Pwned Passwords por prefixo) fica **para depois da v1** |
| D-5 | «Pedir o Windows Hello ao preencher palavras-passe» **desligado** por omissão; para pagamentos e documentos, **ligado** |

## Promessas

- **Nada sai sem uma ação do utilizador.** O gestor não faz nenhum pedido de rede.
- **Nada dos formulários vai para a Google** (A0, abaixo): nem a verificação de fugas do Chromium nem o servidor de
  previsões de formulários.
- **Os segredos aparecem o mínimo possível:** cifrados no disco campo a campo, decifrados só para usar, nunca em
  registos nem diagnósticos, nunca no HTML inicial das páginas de gestão.
- **As suas palavras-passe não vão para ninguém** (B4): promessa própria em `eteris://privacy/`, verificada no estado
  real.

## A0. Nada dos formulários vai para a Google

Medido ao vivo (08–09/11/2026) com o método da `docs/paridade.md`, secção 7: proxy local que regista todos os pedidos e
não encaminha nenhum, passado ao motor só por argumento, NetLog do motor (`--log-net-log`), perfil de teste novo e uma
página local em `127.0.0.1` com três formulários (início de sessão com `autocomplete="username"`/`current-password`,
morada com `given-name`…`postal-code`/`tel` e um campo NIF, cartão com `cc-number`/`cc-exp`/`cc-csc`), preenchidos
pelo pipeline de entrada do motor e enviados com Enter (dados inventados; cartão de teste 4111 1111 1111 1111).

| Pedido | Antes | Depois | Origem no Chromium 156 | Corte |
|---|---|---|---|---|
| `CONNECT passwordsleakcheck-pa.googleapis.com:443` → `POST /v1/leaks:lookupSingle` (logo depois de enviar o início de sessão) | **sim**, mesmo com `credentials_enable_service=false` | **não** | verificação de fugas do gestor de palavras-passe (`leak_detection_delegate.cc`: corre no envio mesmo sem oferecer para guardar; `leak_detection_request.cc`). Vai o prefixo de um hash do utilizador e da palavra-passe | preferência `profile.password_manager_leak_detection=false` (`PrivacyPreferences.LeakCheckPreference`, lida de volta do perfil) |
| `content-autofill.googleapis.com` (previsões de formulários) | não | não | `autofill_crowdsourcing_manager.cc` (funcionalidade `AutofillServerCommunication`) | `--disable-features=AutofillServerCommunication` (`EngineHardening.NoGoogleOnPages`), preventivo: desde o primeiro processo, sem depender das preferências |

Testes: `Vault/PrivacyPreferencesGuardTests` (as quatro preferências do gestor e do preenchimento do Chromium
continuam `false`, a verificação de fugas desligada e lida do perfil, o interruptor do motor, a promessa),
`EngineHardeningTests.Build_FixesEverySwitchThatKeepsTheEngineQuiet` (lista inteira do `disable-features`) e
`EngineFeatureNamesTests` (`AutofillServerCommunication` é uma `base::Feature` do `libcef.dll` em uso).
`eteris://privacy/` › «Nada sai do computador sem um pedido seu» passa a dizer também os formulários e fica ✘ (a dizer
o quê) se a verificação de fugas estiver ligada no perfil ou se faltar o interruptor.

## A1. Modelos, origens e validações

`src/Eteris.Browser/Vault/VaultModels.cs` e `Vault/Origins.cs` (puros, sem interface).

- **Coleções** com os nomes da sincronização: `logins`, `loginsNever`, `cards`, `ibans`, `contacts`, `idDocs`, `travel`.
- **Itens:** palavra-passe (origem, utilizador, palavra-passe, nota, datas, ação do formulário, «gerada à espera de
  confirmação», `httpRealm`), «nunca neste site», cartão (titular, número, validade, alcunha — **nunca o código de
  segurança**: o modelo não tem onde e um conjunto de campos com `cvc`/`csc`/`cvv`/`cid` é recusado, a mensagem diz o
  nome do campo e nunca o valor), IBAN, contacto (com número, complemento e bairro, distrito/estado e código postal/CEP),
  documento (passaporte, carta de condução/CNH, Cartão de Cidadão/RG, **NIF/CPF** — com o país, só o nome desse país) e
  viagem (veículo ou programa de fidelidade). O `ToString()` de um item diz só o tipo e o ID.
- **Validações:** cartão por Luhn e comprimento (12–19), marca pelo prefixo (Elo e Hipercard antes das gerais),
  validade («08/29», «8/2029», «0829»; vale até ao fim do mês); IBAN por mod-97 e comprimento do país; NIF com o dígito
  de controlo e os prefixos válidos; CPF com os dois dígitos; código postal de Portugal (`0000-000`) e CEP do Brasil
  (`00000-000`); matrícula de Portugal e placa do Brasil (antiga e Mercosul).
- **Máscaras:** «Visa •••• 1111 · 08/29», «PT50 •••• 0154», «CPF •••• 4725», «NIF •••• 6789».
- **Origens:** esquema + anfitrião (IDN em ASCII, minúsculas, sem ponto final) + porta (sem a por omissão); só `http`
  e `https`; `android://` guarda-se tal como vem e nunca se sugere. Correspondência: **exata**, depois **o mesmo
  domínio registável** (Public Suffix List, como o Chromium; com o mesmo esquema e a mesma porta, e só para nomes, não
  IPs), **nunca entre domínios**: `g1thub.com` não recebe as de `github.com`, `alice.github.io` não recebe as de
  `bob.github.io`, `http` e `https` são separados. Seguro para cartões, IBAN e documentos: `https` e o próprio
  computador (`localhost`, `*.localhost`, `127.0.0.1`, `[::1]`).

Testes: `Vault/VaultModelsTests`, `Vault/OriginsTests` (dados de teste inventados: cartões de teste públicos, IBAN de
exemplo do registo SWIFT, NIF e CPF de exemplo).

## A2. Cofre local cifrado

`Vault/VaultKey.cs`, `Vault/VaultStore.cs`, `Vault/VaultService.cs` (só armazenamento; ainda nada no navegador o usa).

- **Ficheiros:** `<raiz>\cofre.db` (SQLite) e `<raiz>\cofre.key`, separados do `eteris.db` e fora da pasta do Chromium
  (`<raiz>\profile`). A chave do cofre são 256 bits aleatórios, criados uma vez e guardados embrulhados pela **DPAPI do
  utilizador** (sem `LOCAL_MACHINE`, sem interface, entropia `Eteris.Sync:cofre`). Uma chave que não abre (outro
  utilizador, outra entropia, ficheiro estragado) ou que desapareceu com o cofre no disco não é substituída: o cofre
  fica como estava e não abre.
- **Cifra:** cada item tem os **metadados** (origem, utilizador, alcunha, datas, máscaras) e o **segredo** (palavra-
  passe, nota, número do cartão, IBAN, número do documento, VIN) cifrados em separado com **AES-256-GCM** (chave
  derivada por HKDF), com dados associados `eteris-cofre|v1|coleção|id|campo|versão`: um blob trocado de item, de campo
  ou de versão não abre (`VaultCorruptException`). O segredo só se decifra para usar (`GetLogin`, `GetCard`…); as
  listas (`Logins`, `List`) só decifram os metadados.
- **Procura por origem sem a origem no disco:** `origin_key = HMAC(K_idx, origem)` e `site_key = HMAC(K_idx, domínio
  registável)`; as sugestões são a origem exata e depois as do mesmo domínio registável que passam a correspondência
  (mesmo esquema e porta). Em claro no disco ficam só o nome da coleção, o ID aleatório e as horas.
- **Histórico de 1 versão:** mudar a palavra-passe guarda a anterior (`PreviousPassword`); mudar só a nota não mexe.
  A mesma origem e o mesmo utilizador são o mesmo item.
- **Lápides:** apagar tira os dados e os índices e deixa o ID e a hora (para a sincronização); `Purge` tira-a de vez.
  **Apagar tudo** esvazia a tabela e faz `VACUUM`; com `secure_delete` o SQLite escreve zeros por cima do que apaga.
- **Abertura preguiçosa:** o cofre só se abre quando alguém o pede (`VaultService.Profile`); o arranque do Éteris não
  o abre (teste que varre `App.xaml.cs`, `MainWindow.xaml.cs` e `Program.cs`). Nada em `Vault/` escreve registos
  (`Debug`, `Trace`, `Console`, `StartupTrace`: teste de guarda).
- **Limite honesto:** a DPAPI do utilizador não protege de programas que correm como o mesmo utilizador (o nível do
  Chrome sem App-Bound e do Firefox sem palavra-passe principal). Os segredos passam por `string` em memória (o .NET não
  as apaga); os buffers intermédios são apagados com `CryptographicOperations.ZeroMemory`.

Testes: `Vault/VaultStoreTests` (nenhum texto em claro em `cofre.db` em Latin-1, UTF-8 e UTF-16; blob trocado de item
ou de campo e versão alterada falham; outra entropia não abre e não substitui a chave; procura por HMAC; histórico;
lápides; apagar tudo; abertura preguiçosa; sem registos).

Prova ao vivo (perfil de teste novo, compilação de depuração): itens inventados criados por um comando de teste fora do
repositório (o `VaultService` do Éteris, com a mesma DPAPI) — 2 palavras-passe, 1 «nunca», 1 cartão, 1 IBAN,
1 contacto, NIF e CPF, 1 veículo. `cofre.db` (20 KiB): nenhum dos 21 textos procurados (palavras-passe, utilizadores,
`127.0.0.1`, `exemplo.test`, números, nomes, «Visa») em Latin-1, UTF-8 nem UTF-16; as colunas são blobs. Arranque
(marca `aba.apresentada`, ms desde o início do processo): 802 e 802 antes de haver cofre, 753, 798 e 742 depois; o
`cofre.db` ficou igual (mesmo hash) depois de três arranques: o Éteris não o abre.

## A3. Reautenticação do Windows

`Vault/WindowsReauth.cs`. Só APIs do sistema por P/Invoke e COM, sem WinRT projetado nem pacotes (teste de guarda).

- **Windows Hello** (PIN, rosto, impressão digital): `UserConsentVerifier` por **COM puro** — `RoGetActivationFactory`
  (combase.dll), `IUserConsentVerifierInterop::RequestVerificationForWindowAsync` com a janela do Éteris como dona e
  `IUserConsentVerifierStatics::CheckAvailabilityAsync`; a operação assíncrona é esperada consultando
  `IAsyncInfo::get_Status` numa thread MTA própria (cancelada com `IAsyncInfo::Cancel`). Os IIDs parametrizados
  (`IAsyncOperation<UserConsentVerificationResult>` = `fd596ffd-2318-558f-9dbe-d21df43764a5`,
  `IAsyncOperation<UserConsentVerifierAvailability>` = `ddd384f3-d818-5d83-ab4b-32119c28587c`) estão fixos e o
  algoritmo `pinterface` que os calcula confere com IIDs conhecidos do Windows (`IAsyncOperation<Boolean>`,
  `IReference<Int32>`). É o caminho do Chrome (`authenticator_win.cc`).
- **Sem Hello:** a palavra-passe da conta do Windows — `CredUIPromptForWindowsCredentialsW` com
  `CREDUIWIN_ENUMERATE_CURRENT_USER`, `CredUnPackAuthenticationBufferW` e `LogonUserW(LOGON32_LOGON_NETWORK)`, até 3
  tentativas, com o buffer da palavra-passe apagado logo a seguir (o caminho do Chrome, `password_manager_util_win.cc`).
  Uma palavra-passe em branco que o Windows recusa por política (`ERROR_ACCOUNT_RESTRICTION`) quer dizer **conta sem
  palavra-passe**: não se pede nada (como o Firefox) e a página explica «Proteja a sua conta do Windows com um PIN…».
  Nunca se experimenta uma palavra-passe sem o utilizador a escrever (contaria como tentativa falhada).
- **Janela de graça** (`ReauthGate`): 5 minutos **por janela do Éteris**, contados desde a verificação (não se renova
  a cada uso); recomeça quando o Windows bloqueia (`SystemEvents.SessionSwitch`), quando o computador suspende e quando
  a janela fecha. Dois pedidos ao mesmo tempo dão uma só janela do Windows. Um pedido cancelado (a aba saiu da frente)
  não conta, mesmo que o Windows responda «verificado» depois.
- **Quando pede** (`ReauthPolicy`, decisão D-5): mostrar, copiar ou editar um segredo e ver números completos — sim;
  preencher palavras-passe — não por omissão (interruptor deste PC); preencher cartões, IBAN e documentos — sim por
  omissão (desligar o interruptor também pede); **exportar, pôr segredos no perfil exportado, ligar Pagamentos ou
  Documentos na sincronização e aprovar um aparelho — sempre, sem janela de graça**.
- **Via de teste:** `ETERIS_TEST_REAUTH=aceitar|recusar` responde sem janela nenhuma, para as provas ao vivo; a versão
  instalada ignora-a, como as outras `ETERIS_TEST_*` (`TestSwitches.ReauthFor`).

Testes: `Vault/WindowsReauthTests` (graça de 5 min por janela sem renovação; bloqueio, suspensão e fecho; «sempre»
sem graça; recusa; cancelamento quando a aba sai da frente; conta sem proteção; pedidos simultâneos; política D-5;
IIDs fixos; mapeamento dos resultados do Windows e dos erros do `LogonUser`; a disponibilidade real, que não pede nada;
a via de teste só fora da versão instalada).

Ao vivo (sem pedir nada a ninguém): `CheckAvailabilityAsync` neste PC de testes deu `DeviceNotPresent` (1) em 67 ms
— o mesmo valor que a projeção WinRT do Windows PowerShell dá —, por isso aqui o Éteris pediria a palavra-passe do
Windows (CredUI). **Provas manuais que ficam para o dono** (precisam do PIN ou da palavra-passe dele): a janela do
Windows Hello real (`RequestVerificationForWindowAsync`) num PC com Hello, a janela da palavra-passe do Windows
(CredUI + `LogonUser`), e o recomeço da graça depois de bloquear o Windows.

## A4. CSV: importar e exportar (núcleo)

`Vault/PasswordCsv.cs` (sem interface: os diálogos nativos, a confirmação do Windows ao exportar e a proposta de enviar
o CSV para a Reciclagem chegam com `eteris://passwords`, B2). Nunca se leem os ficheiros de senhas de outros
navegadores (o `v20` App-Bound do Chrome, o `key4.db` do Firefox): só o CSV que o utilizador exportou.

- **Ler** como o Chromium e o Firefox leem: RFC 4180 (aspas, `""`, quebras de linha dentro das aspas, CRLF ou LF, BOM),
  cabeçalho na primeira linha não vazia, nomes de coluna sem maiúsculas e espaços — origem `url|website|origin|
  hostname|login_uri`, utilizador `username|user|login|account|login_username`, palavra-passe `password|
  login_password`, nota `note > notes > comment > comments`, e do Firefox `httpRealm`, `formActionOrigin` e as horas
  (`timeCreated`, `timeLastUsed`, `timePasswordChanged`). Assim entram os CSV do **Chrome, Brave, Firefox, Bitwarden e
  1Password**.
- **Limites do Chromium:** ficheiro ≤ 1000 KiB e ≤ 3000 palavras-passe (senão nada entra); palavra-passe, utilizador e
  nota ≤ 1000 carateres, endereço ≤ 2048. Linhas recusadas (sem palavra-passe, sem endereço, endereço que não é `http(s)`
  nem `android://`, demasiado longas, aspas por fechar) contam-se com o número da linha, o endereço e o utilizador —
  nunca a palavra-passe. `android://` guarda-se sem sugestão; `httpRealm` guarda-se (autenticação HTTP, v2).
- **Importar para o cofre:** duplicado igual (no cofre ou antes no próprio ficheiro) ignora-se; a mesma origem e
  utilizador com outra palavra-passe é um **conflito** que o utilizador decide — «a do ficheiro» (a atual fica no
  histórico do item, por isso nenhuma se perde) ou «a atual». Resumo: «312 importadas, 4 em conflito, 2 inválidas»
  (e «N já existiam», «N substituídas»).
- **Exportar** no formato do Chrome (`name,url,username,password,note`, ordenado como o Chromium), que o Chrome, o Brave
  e o Firefox importam. `CloudFolder` reconhece pastas do OneDrive (pelas variáveis do Windows e pelo nome), do Dropbox e
  do Google Drive («O meu disco», «Meu Drive», «My Drive») para o aviso antes de gravar.

Testes: `Vault/PasswordCsvTests` (um ficheiro sintético de cada um dos cinco, aspas, quebras de linha, BOM e CRLF,
prioridade das notas, linhas recusadas sem a palavra-passe, limites, ida e volta, importação com duplicados, conflitos e
resumo, pastas da nuvem).

## A5. Área de transferência segura e capturas

`Vault/SecureClipboard.cs` e `Infrastructure/CaptureShield.cs`.

- **Copiar um segredo** (`SecureClipboard`) faz-se no processo do navegador — a página nunca o vê —, pela API do
  Windows, com os três formatos que o Windows respeita: `ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory = 0` (fora do histórico, Win+V) e `CanUploadToCloudClipboard = 0` (fora da nuvem). Ao
  fim de **60 s**, se o número de sequência da área de transferência não mudou (o segredo ainda lá está), é limpo; o
  que o utilizador copiar entretanto fica. Uma segunda cópia tem o seu próprio prazo. A área de transferência abre-se
  com uma janela só de mensagens criada e destruída na mesma chamada (nenhuma janela sem ciclo de mensagens fica dona
  dela) e os bytes do segredo em memória gerida são apagados.
- **Um segredo à vista** (`SecretOnScreen`): todas as janelas do Éteris saem das capturas de ecrã, gravações,
  partilhas e do Recall (`WDA_EXCLUDEFROMCAPTURE`; `WDA_MONITOR` antes do Windows 10 2004), mesmo com «Esconder do
  Recall e de capturas» desligado, até o esconder ou, no máximo, **30 s** (então avisa quem o mostrou para o esconder).
  Vários segredos ao mesmo tempo contam-se; cada um liberta uma vez.

Testes: `Vault/SecureClipboardTests` (formatos de exclusão; limpo aos 60 s só se for o mesmo; segunda cópia;
área de transferência ocupada; nada de cópias sem exclusão em `Vault/`; afinidade com um segredo à vista; contagem e
prazo de 30 s). Os testes nunca tocam na área de transferência real.

Prova ao vivo (comando de teste fora do repositório, com as classes do Éteris; a área de transferência do dono — uma
imagem — guardada e reposta):
- `SecureClipboard.Copy`: o texto chegou à área de transferência e os três formatos estavam lá
  (`CanIncludeInClipboardHistory`, `CanUploadToCloudClipboard`, `ExcludeClipboardContentFromMonitorProcessing`: os três
  presentes). **Neste PC o histórico da área de transferência (Win+V) está desligado** (`Clipboard.IsHistoryEnabled()` =
  falso): a prova de que a entrada não aparece no Win+V fica para um PC com o histórico ligado (ligá-lo aqui seria
  mudar uma definição do sistema). O teste não abre o painel Win+V (mostraria o histórico do dono numa captura):
  compara só o item mais recente do histórico com o marcador, sem o mostrar, e retira o item de controlo que pôs.
- `SecretOnScreen` numa janela WPF fora do ecrã: afinidade `0x0` → `0x11` com o segredo à vista → `0x0` ao esconder;
  outra vez `0x11` e, sem esconder, o aviso e `0x0` aos 30 s.

## B1. Menu, Definições e paleta

`MainWindow.BrowserMenu.cs`, `BrowserMenuKeys.cs`, `MainWindow.Vault.cs`, `Vault/VaultPages.cs`, `Data/UserSettings.cs`,
`Browser/settings.html`, `Browser/SettingsPage.cs`, `MainWindow.Palette.cs`.

- **Menu do Éteris (⋯ / Alt+F):** a linha **«Palavras-passe e preenchimento automático ›»** fica logo a seguir a
  «Favoritos» e abre o submenu com as cinco entradas do Chrome: «Gestor de palavras-passe» (`eteris://passwords/`),
  «Pagamentos», «Informações de contacto», «Documentos de identificação» e «Viagens» (`eteris://autofill/#…`). O
  submenu abre no lugar do menu, com «‹ Palavras-passe e preenchimento automático» no topo para voltar (como as
  subvistas do menu do Firefox), por isso as setas, as letras, o Enter, o realce pelo rato e o leitor de ecrã são os
  do menu. Teclado como no Chrome: → (ou Enter, ou Espaço) na linha abre; ← ou Esc voltam à linha; ↑ ↓ andam; a
  primeira letra salta (as cinco têm iniciais diferentes); Alt+F e Alt+E fecham o menu inteiro. Vindas da página
  (foco do Win32 no Chromium) as teclas passam pelo mesmo passo (`BrowserMenuKeys.Submenu`).
- **Leitor de ecrã:** a linha é um item de menu com o padrão ExpandCollapse (fechado/aberto) e conta na posição
  («6 de 18»); as entradas do submenu são «1 de 5» … «5 de 5» (o «Voltar» não conta).
- **Janela privada:** a linha aparece, mas as páginas recusam-se lá; as entradas abrem numa janela normal (a primeira
  que houver, ou uma nova). A aba de `eteris://autofill/` já aberta é reaproveitada e muda só de secção.
- **Definições › Palavras-passe e preenchimento automático:** as cinco ligações e os interruptores gerais —
  «Oferecer para guardar palavras-passe» (ligado), «Mostrar sugestões ao clicar num campo» (ligado) e «Pedir o
  Windows Hello ao preencher palavras-passe» (desligado, D-5). «Pedir o Windows Hello ao preencher pagamentos» (ligado)
  muda-se em `eteris://autofill/`, porque desligá-lo também pede o Windows Hello: a rota genérica das Definições
  (`eteris://settings/set`) recusa-o (`SettingsPage.CanSetFromPage`, `UserSettings.NeedsReauth`).
- **Definições novas** (`UserSettings`): `pwOffer`, `pwSuggest`, `afPayments`, `afContacts`, `afDocuments`,
  `afTravel` viajam entre computadores (`Collections.SyncableSettings`); `pwAskHello` e `payAskHello` são de cada
  computador e nunca viajam.
- **Paleta (Ctrl+K):** «Palavras-passe», «Pagamentos», «Informações de contacto», «Documentos de identificação» e
  «Viagens». «Gerar palavra-passe» entra com o gerador (tarefa C5).
- De passagem: a folha de estilo das Definições tinha uma marca solta (`>>>`) que anulava a regra seguinte
  (`.syncform .filename`); saiu.

Testes: `Vault/VaultMenuTests` (as cinco entradas e as páginas; janela privada; →/Enter/Espaço abrem, ←/Esc voltam,
Alt+F fecha, as mesmas decisões vindas da página; ExpandCollapse e «n de N»; a linha a seguir a «Favoritos»; valores
por omissão e só booleanos; o que viaja e o que não; desligar o Hello dos pagamentos recusado na rota das Definições;
a secção das Definições com as ligações e os interruptores) e `Sync/SyncIntegrationTests` (todas as definições que
viajam voltam iguais).

Prova ao vivo (`pp\vivo\b1`, perfis de teste novos, tema escuro e claro, teclas reais por `SendInput` e estado por UI
Automation): Alt+F → a linha «Palavras-passe e preenchimento automático» é «6 de 18», `expand=Collapsed`; P salta para
ela; → abre o submenu com «Gestor de palavras-passe» realçado («1 de 5»); ↓ e V levam a «Viagens»; ← volta à linha;
Enter abre outra vez com a primeira entrada realçada; Esc volta à linha; outro Esc fecha o menu. Pelo rato: um clique
na linha abre o submenu e um clique em «Gestor de palavras-passe» abre `eteris://passwords`. Capturas: menu, submenu
pelo teclado e pelo rato, nos dois temas, e a secção das Definições (`definicoes-dark.png`, `definicoes-light.png`).

## B2. `eteris://passwords/`

`Browser/PasswordsPage.cs`, `Browser/passwords.html`, `Vault/VaultRoutes.cs`, `Vault/PasswordHealth.cs`,
`Browser/VaultResourceHandler.cs`, `MainWindow.Vault.cs` (`WindowsVaultHost`), rotas em
`Browser/EterisSchemeHandlerFactory.cs`.

- **Página** com o estilo das Definições (navegação à esquerda com realce fixo, tema claro e escuro, Cor do Éteris):
  «Palavras-passe» (pesquisa; lista agrupada por domínio registável, com o ícone da cache de favicons ou a inicial;
  etiquetas «Ligação não segura» para `http`, «Não confirmada» para geradas, «App Android»), «Verificação» e
  «Definições», e atalhos para as quatro secções do preenchimento automático.
- **Nenhum segredo no HTML inicial:** a página recebe só o site, o utilizador e as datas. A palavra-passe **e a nota**
  chegam só por `eteris://passwords/reveal`, depois da reautenticação (Windows Hello ou palavra-passe do Windows, com a
  janela de graça de 5 min), e ficam à vista no máximo 30 s; com um segredo à vista as janelas saem das capturas
  (`SecretOnScreen`), até «Esconder» ou aos 30 s.
- **Copiar:** a palavra-passe pede a reautenticação e vai pela área de transferência segura no processo do navegador
  (A5) — nunca passa pela página; o utilizador copia-se sem pedir.
- **Adicionar** não pede nada; **editar** um item existente pede (é ver e mudar a palavra-passe), e a anterior vai para
  o histórico de 1 versão. Validações: endereço `http(s)`, palavra-passe não vazia, limites do Chromium (1000 carateres).
- **Apagar com «Anular»:** o item sai logo da lista e só se apaga no cofre ao fim de 10 s (ou ao fechar o Éteris), para
  «Anular» não precisar de guardar nenhum segredo decifrado em memória.
- **«Mudar palavra-passe»:** nenhum pedido até ao clique; ao clicar, o processo do navegador abre numa aba nova
  `/.well-known/change-password` da origem guardada (RFC 8615). O endereço nunca está na página.
- **Verificação local** (`PasswordHealth`): fracas (menos de 8 carateres, das mais comuns em Portugal e no Brasil com ou
  sem números no fim, sequências do teclado ou do alfabeto, um só carácter repetido, menos de 12 carateres de uma só
  classe) e repetidas (SHA-256 em memória). Calculada ao abrir a secção; só os IDs saem para a página. Nenhum pedido de
  rede (a verificação de fugas fica para depois, D-4).
- **Definições da página:** os três interruptores de B1 (só esses: a rota recusa outras chaves), «Sites onde nunca
  guardar» com «Remover», «Importar…», «Exportar…» e «Apagar todas as palavras-passe…».
- **Importar:** o diálogo «Abrir» é nativo e do processo do navegador — o CSV nunca entra na página; limite de 1000 KiB
  antes de ler; conflitos (mesmo site e utilizador, outra palavra-passe) perguntam «Ficar com as do ficheiro» (a atual
  vai para o histórico) ou «Manter as atuais»; resumo «6 importadas, 1 substituída, 1 inválida»; no fim, «Enviar o
  ficheiro CSV para a Reciclagem» (`SHFileOperation` com anular). A escolha «manter as duas» do desenho não existe
  (o cofre tem um item por site e utilizador).
- **Exportar:** dois cliques (o segundo diz «Ficheiro em claro»), reautenticação **sempre** (sem janela de graça),
  diálogo «Guardar como» nativo, aviso nativo se a pasta for do OneDrive, do Dropbox ou do Google Drive (e nada se grava
  se o utilizador disser que não), formato do Chrome em UTF-8.
- **«Apagar todas as palavras-passe…»:** dois cliques e reautenticação; apaga as palavras-passe e os «nunca» (com
  lápides para a sincronização); os pagamentos, contactos, documentos e viagens ficam.
- **Regras das rotas:** todas as ações exigem o token desta sessão; a janela privada recebe «Abra numa janela normal»
  e as rotas recusam; os dados vão no corpo de um POST (nunca no endereço); as respostas chegam por iframes da mesma
  origem (a CSP da página não deixa `fetch` nem pedidos de rede); a reautenticação desiste quando a aba sai da frente
  ou a janela fecha (e a graça dessa janela acaba). O cofre continua a abrir-se só na primeira vez que é preciso.
- `eteris://passwords/` e `eteris://autofill/` passam a poder abrir-se como endereço de arranque (como
  `eteris://privacy/`), só na raiz e sem parâmetros.

Testes: `Vault/PasswordsPageTests` (nenhum segredo no HTML inicial; a página fechada à rede e sem o endereço
`/.well-known`; cada ação recusada sem o token; janela privada; mostrar com e sem reautenticação, na graça e cancelado
quando a aba sai da frente; copiar; adicionar sem pedir e editar a pedir, com histórico; validações; apagar com
«Anular» e ao fechar; «nunca» e «Remover»; «Mudar palavra-passe» só ao clicar e da origem guardada; verificação só com
IDs; importar com e sem conflitos, diálogo cancelado, ficheiro grande, Reciclagem; exportar sempre com reautenticação,
diálogo nativo e aviso da nuvem; apagar tudo só as palavras-passe; interruptores; metadados da lista com IDN e
`http`), `Vault/PasswordHealthTests`, `InjectedScriptsSyntaxTests` (o script da página no Node), `ThemedPagesTests`,
`StartupArgsTests`.

Prova ao vivo (`pp\vivo\b2`, `pp\vivo\b2-registo.txt`; perfil de teste novo; `ETERIS_TEST_REAUTH=aceitar`; CSV com
sete palavras-passe inventadas, uma em conflito e uma linha inválida; a área de transferência do dono — uma imagem —
guardada e reposta):
- página vazia; nenhum segredo no HTML antes nem depois de importar;
- importar pelo diálogo nativo (teclas reais no campo do nome): «1 palavra-passe do ficheiro é diferente da guardada…
  Qual fica?» → «Ficar com as do ficheiro» → «6 importadas, 1 substituída, 1 inválida»; seis linhas, `intranet` com
  «Ligação não segura»;
- pesquisar «loja» → uma linha;
- «Mostrar» → a palavra-passe inventada à vista e a afinidade da janela `0x11` (fora das capturas; por isso não há
  captura desse momento); «Esconder» → `••••` e afinidade `0x0`;
- «Copiar» → «Palavra-passe copiada. A área de transferência é limpa daqui a 60 s…», a área de transferência com a
  palavra-passe e os três formatos de exclusão presentes;
- «Apagar» → 5 linhas e o aviso com «Anular» → «Anular» → 6 linhas;
- «Verificação» → «1 palavra-passe fraca · 2 repetidas» com os sites;
- «Exportar…» (dois cliques, Hello falso, diálogo «Guardar como» nativo) → «6 palavras-passe exportadas…», ficheiro
  com 7 linhas e o cabeçalho do Chrome;
- `cofre.db` sem «Inventada», «loja.exemplo» nem «ana.teste» em Latin-1, UTF-8 ou UTF-16.

## B3. `eteris://autofill/`

`Browser/AutofillPage.cs`, `Browser/autofill.html`, `Vault/VaultRoutes.Autofill.cs`.

- **Quatro secções com contagem**, como o «Your saved info» do Chrome, e endereços diretos
  (`#pagamentos`, `#contacto`, `#documentos`, `#viagens`) para o submenu de B1:
  - **Pagamentos:** cartões («Visa •••• 1111 · validade 08/29 · titular · alcunha», marcados «Fora da validade»)
    e IBAN («PT50 •••• 0154»); «Preencher automaticamente pagamentos»; «Pedir o Windows Hello ao preencher pagamentos»
    (ligado por omissão, D-5; desligá-lo pede o Windows Hello e, recusado, fica ligado); a ligação «Sincronizar entre
    computadores» diz que os pagamentos ficarão desligados por omissão (D-1) e que a sincronização do cofre ainda não
    existe.
  - **Informações de contacto:** moradas com etiqueta; o formulário muda com o país (`AutofillPage.AddressForm`):
    Portugal (Apelido, Morada, Andar/porta, Código postal 0000-000, Localidade, Distrito), Brasil (Sobrenome,
    Logradouro, Complemento, Bairro, CEP 00000-000, Cidade, Estado) e os outros (Código postal, Região).
  - **Documentos de identificação:** passaporte, carta de condução/CNH, Cartão de Cidadão/RG e o número fiscal
    NIF/CPF (D-2), com o nome do país quando há país («NIF», «CPF», «CNH», «Cartão de Cidadão») e os dois quando não
    há; números mascarados; validade.
  - **Viagens:** veículos (marca, modelo, ano, proprietário, matrícula/placa e país, VIN) e programas de fidelidade
    (companhia, programa, número mascarado, nome).
- **Nenhum número completo no HTML:** a página recebe só máscaras e metadados. «Mostrar» e «Editar» de um cartão,
  IBAN, documento ou viagem pedem a reautenticação (com a janela de graça) e o número fica à vista no máximo 30 s, com
  as janelas fora das capturas. As moradas não têm segredos e editam-se sem pedir.
- **Validações** (as de A1): Luhn e marca do cartão; validade MM/AA; IBAN por mod-97; NIF e CPF pelo dígito de
  controlo; código postal de Portugal e CEP do Brasil (o CEP guarda-se com hífen); e-mail; matrícula de Portugal e
  placa do Brasil (antiga e Mercosul); ano do veículo; datas pelo calendário. Mensagens em português, no formulário.
- **Nunca o código de segurança:** um formulário com um campo chamado `cvc`, `cvv`, `csc` ou «security code» é
  recusado inteiro, sem guardar nada; a página não tem esse campo.
- **Apagar com «Anular»** e as mesmas regras de B2 (token, POST, iframes da mesma origem, janela privada recusada).
- O JSON das duas páginas sai com os acentos e o «•» como são (só `<`, `>`, `&` e aspas escapados).

Testes: `Vault/AutofillPageTests` (só máscaras no HTML; secções, tema e nenhum campo de código de segurança; os nomes
dos documentos nas duas formas; mostrar com e sem reautenticação nas quatro coleções com segredo; `cvc`, `cvv`, `csc`
e `card-security-code` recusados; dezasseis validações; um de cada guardado e normalizado; editar com segredo pede e
morada não; apagar e anular; o interruptor do Hello dos pagamentos; formulários de Portugal e do Brasil; cartão fora da
validade; janela privada e token), `InjectedScriptsSyntaxTests` e `ThemedPagesTests`.

Prova ao vivo (`pp\vivo\b3`, `pp\vivo\b3-registo.txt`; perfil de teste novo; Hello falso; tudo inventado): um
cartão com o Luhn errado recusado («Número de cartão inválido…»); depois, pelos formulários da página, dois cartões
(um fora da validade), um IBAN, uma morada de Portugal e uma do Brasil (o CEP `01310100` guardado `01310-100`), NIF,
CPF, CNH, Cartão de Cidadão, passaporte, dois veículos (a placa `abc1d23` guardada `ABC1D23`) e um programa de
fidelidade, todos «Guardado.»; nenhum número completo no HTML; «Mostrar» o cartão → o número à vista e a afinidade
`0x11`, «Esconder» → `0x0`; editar a alcunha do IBAN (Hello falso) → «Alterações guardadas.»; apagar o programa →
«Apagado [Anular]» → «Anular» → de volta; desligar o Hello dos pagamentos (Hello falso) → «Guardado»; `cofre.db` sem
números, nomes nem moradas em claro. Capturas das quatro secções (`dark-pagamentos.png`, `dark-contacto.png`,
`dark-documentos.png`, `dark-viagens.png`), sem nenhum número completo à vista.

## B4. Apagar dados e a promessa nova

`Browser/ClearDataRequest.cs`, `Browser/settings.html`, `Browser/EterisSchemeHandlerFactory.cs`,
`Browser/PrivacyPage.cs`, `Shields/PrivacyPreferences.cs`, `MainWindow.Forget.cs`, `README.md`.

- **«Apagar dados de navegação»** ganha a caixa **«Palavras-passe e preenchimento automático»**, desmarcada por omissão
  («tudo, desde sempre; pede o Windows Hello»). Marcada, primeiro pede a reautenticação: recusada, **nada** se apaga
  (nem o histórico nem os cookies) e o resultado diz «Não foi confirmado com o Windows: nada foi apagado.», sem ✓ e com
  as escolhas à vista; confirmada, apaga o resto do que foi escolhido e o cofre inteiro (as cinco áreas, `EraseAll`
  com `secure_delete` e `VACUUM`). Nunca a partir da janela privada.
- **«Esquecer este site ao fechar», «Triturar» e «Apagar cookies e dados de sites ao fechar» não mexem no cofre**, e os
  textos dizem-no («As palavras-passe guardadas ficam», «As palavras-passe e o preenchimento automático ficam»).
- **Promessa nova em `eteris://privacy/`: «As suas palavras-passe não vão para ninguém»**, verificada no estado real:
  as quatro preferências do gestor do Chromium (`credentials_enable_service`, `credentials_enable_autosignin`,
  `autofill.profile_enabled`, `autofill.credit_card_enabled`) lidas de volta do perfil a falso
  (`PrivacyPreferences.ChromiumPasswordsOff`), a verificação de fugas desligada (A0) e `AutofillServerCommunication`
  no `disable-features` do motor; diz que o cofre está cifrado neste computador, não faz pedidos de rede e ainda não
  sincroniza. Falha (✘) com o motivo se uma das três coisas não se verificar.
- `README.md`: o gestor de palavras-passe e o preenchimento automático na lista do que o Éteris faz.

Testes: `Vault/VaultClearDataTests` (a caixa só quando pedida; o resumo; a caixa desmarcada e os textos nas
Definições; recusado sem ✓; apagar o cofre pede a reautenticação e, recusada, nada se apaga; esquecer e apagar ao fechar
nunca chamam o apagar do cofre; a promessa cumprida e as três maneiras de falhar; as quatro preferências lidas de volta),
`ClearDataTests`, `PrivacyPageTests`.

Prova ao vivo (`pp\vivo\b4`, `pp\vivo\b4-registo.txt`; cópia do perfil de teste do B2, com seis palavras-passe
inventadas): `eteris://privacy/` com «As suas palavras-passe não vão para ninguém» ✔ (`privacy-promessa.png`); com o
Hello falso a recusar, «Apagar agora» com histórico, cache e a caixa nova → «Não foi confirmado com o Windows: nada foi
apagado.» e as seis palavras-passe continuam lá; com o Hello falso a aceitar → «✓ Histórico e cache apagados ·
palavras-passe e preenchimento automático também» e `eteris://passwords/` vazio (`passwords-vazio.png`). Captura da
caixa nas Definições: `apagar-history-cache-vault.png`.

## C1. Canal isolado e guardas

`Vault/VaultChannel.cs`, `Vault/VaultScripts.cs`, `Vault/Scripts/cofre.js` (recurso embutido), `MainWindow.Vault.cs`
(ligação às abas), `MainWindow.xaml.cs` (commit e fim de carregamento), `MainWindow.Sleep.cs` (libertar).

- **Uma sessão DevTools própria por aba** (só depois do 1.º documento `http(s)`; o arranque não muda e o cofre não
  abre), com `Page.enable` e o `Page.frameNavigated`. Para cada documento `http(s)` com a origem que o **processo do
  navegador** conhece para a página de topo (o frame de topo e os iframes da mesma origem; nunca iframes de outros
  sites, `about:`, `data:`, `file:` nem `eteris:`): `Page.createIsolatedWorld(frameId, "eteris-cofre")` →
  `Runtime.addBinding(nome aleatório por sessão, executionContextId)` → `Runtime.evaluate(cofre.js, contextId)`.
- **Sem `Runtime.enable`.** O `Runtime.bindingCalled` chega sem ele (medido: risco R1 do desenho afastado; o plano B da
  espera longa não foi preciso).
- **O 1.º documento de cada aba** nasce antes de a sessão poder ouvir o commit dele: o commit e o fim de carregamento do
  frame de topo (`FrameLoadStart`/`FrameLoadEnd`) pedem também a árvore de frames (`Page.getFrameTree`). Cada documento
  (frame + loader) prepara-se uma vez; quando o frame navega, os contextos dos documentos que saíram esquecem-se, e um
  pedido atrasado não chega a outro documento.
- **Mensagens do script → navegador** pela ligação, validadas por um esquema estrito (`VaultMessages`): JSON até 16 KiB,
  tipos `ready`, `focus`, `input`, `blur`, `scroll`, `submit`, `formGone`, `generate`, `context`, propriedades
  conhecidas com o tipo certo (IDs efémeros curtos, tipos de campo e de formulário de uma lista fechada, retângulos
  finitos, textos com limite); o que não encaixa é descartado. Só o `submit` traz segredos e o `ToString` nunca os
  mostra. Uma mensagem só vale se o contexto for conhecido; a origem é a do contexto (a do navegador), confirmada outra
  vez no momento de usar (`IsCurrent`).
- **Navegador → script** só por `Runtime.evaluate` no `contextId` do documento, com métodos de uma lista (`fill`,
  `rect`, `forget`) e o argumento serializado pelo `JsonSerializer` (nada é concatenado à mão).
- **O script** apanha a ligação e tira-a logo do objeto global; define `__cofre` como propriedade própria, não
  configurável (um `<div id="__cofre">` da página não o substitui); não escreve no DOM.

Testes: `Vault/VaultChannelTests` (esquema das mensagens: aceites, descartadas, tamanho, textos longos, segredos fora
do `ToString`; contextos por documento, navegação do topo e de um iframe, preparação atrasada ou repetida; origens
elegíveis e recusadas; a expressão das chamadas com texto hostil; o nome da ligação), `Vault/VaultChannelGuardTests`
(nenhum `Runtime.enable` no código; avaliações do canal só com `contextId`; o mundo isolado e a ligação com o
`executionContextId`; o `cofre.js` só sai de `VaultScripts` para o canal; nada no arranque; o script não escreve no
DOM fora da zona «preencher»: atributos, classes, estilos, nós, foco, eventos, rede ou armazenamento),
`InjectedScriptsSyntaxTests` (o `cofre.js` compila no Node).

Prova ao vivo (`pp\vivo\c1.ps1`, `pp\vivo\c1b.ps1`, `pp\vivo\c1\`; perfil de teste novo, página local em
`127.0.0.1:8897`, com um registo **temporário** das mensagens que não foi para o código):

- **A mensagem chega:** `ready` do contexto do mundo isolado, origem `http://127.0.0.1:8897`, com o documento
  preparado uma vez apesar de o commit e o fim do carregamento o pedirem os dois (`mensagens.txt`).
- **A página não vê nada:** `typeof window.__cofre` = `undefined`, `'__cofre' in window` = falso, nenhuma propriedade
  com o nome da ligação no mundo da página; o `MutationObserver` da página não vê mutações do cofre (as 2 que vê são do
  próprio analisador da página, iguais sem o canal) (`deteccao.txt`, `c1-pagina.png`).
- **Deteção do `Runtime.enable` negativa,** com controlo positivo: as sondas conhecidas (getters de `stack`, `name`,
  `message`, `cause` num `Error` registado na consola, `Proxy` registado, `toString`) correm 40 vezes em 10 s. No motor
  156 a consola lê `name` e `message` uma vez por mensagem mesmo sem DevTools (o texto para o registo do navegador);
  com um `Runtime.enable` ligado noutra sessão de teste, o `name` passa a ser lido duas vezes (29 deteções em 21 sondas).
  Com o canal do cofre: 0 deteções em 40 sondas.
- **Custo, 20 carregamentos** (`medir.html`, primeiro carregamento frio fora da conta, duas rondas): fim do
  carregamento com o canal 11,8 e 12,3 ms de média (mediana 10 e 11), sem o canal 10,3 e 10,2 (mediana 10 e 10); o
  script fica pronto ~5 ms (mediana) depois do commit.

## C2. Classificador de formulários

`Vault/Scripts/cofre.js` (o `Classifier`, funções puras sobre descritores, e a leitura do DOM, só leitura),
`Browser/DevToolsCalls.cs` (correção encontrada aqui, abaixo).

- **Campo a campo** (`classifyField`): o `autocomplete=` manda primeiro (secções e modificadores como `shipping`,
  `billing`, `home` não contam; `off` e símbolos desconhecidos passam às heurísticas; nas palavras-passe o `off` é
  ignorado, como no Chrome e no Firefox). Depois, heurísticas por `name`, `id`, rótulo (`<label>`, `aria-labelledby` ou
  o texto logo antes), `placeholder` e `aria-label`, normalizados (sem acentos, camelCase separado, «Nº» → número), em
  português de Portugal e do Brasil, inglês e espanhol: CVC (só para nunca o preencher nem guardar), código de uma
  utilização, cartão (número, titular, validade, mês, ano), IBAN, **NIF/CPF** (e CNPJ, NIPC), documentos (passaporte,
  carta de condução/CNH, Cartão de Cidadão/RG), matrícula/placa, código postal/CEP, telefone, e-mail, bairro,
  complemento, utilizador, nome próprio, apelido/sobrenome, empresa, cidade/localidade, distrito/estado, país, número da
  porta, morada/endereço, nome. Ignora pesquisas, cupões, estado civil e datas de nascimento. `type=tel` sem texto de
  telefone só conta se o texto disser CEP, CPF, cartão… (os sites usam-no para teclados numéricos).
- **Formulário inteiro** (`classifyForm`): `login`, `signup`, `change`, `address`, `payment` ou `other`. Só contam os
  campos visíveis. O utilizador é o campo de texto visível mais próximo antes da 1.ª palavra-passe (de preferência um
  utilizador ou e-mail), e só há um. Uma palavra-passe: início de sessão (ou criação de conta com `new-password`); duas:
  atual + nova → mudança, palavra-passe + confirmação → criação de conta (ou mudança sem utilizador); três → mudança.
  Os campos de cartão só contam com um número de cartão no mesmo formulário. Campos soltos (sem `<form>`) formam um
  formulário por documento ou raiz sombra.
- **Visibilidade** (`isVisible`): pelo menos 5×5 px, `display`, `visibility`, opacidade com a dos antepassados ≥ 0,1,
  dentro do ecrã e não tapado (`elementFromPoint` no centro devolve o campo, um descendente ou o rótulo dele). Os
  campos escondidos de rastreadores não têm tipo: nada se oferece neles.
- **Mensagens** (só depois de um foco do utilizador ou da página num campo com tipo, medido no fotograma seguinte, já
  com o campo à vista): `focus` (campo, formulário, tipos, retângulo na página de topo — os iframes da mesma origem
  somam a posição deles — e, num campo de utilizador ou e-mail, o texto já escrito), `input`, `blur` (não quando o foco
  só saiu da janela; ao voltar, o mesmo campo não pede outra vez), `scroll`; um clique num campo que já tem o foco volta
  a pedir. A classificação refaz-se a cada foco; um `MutationObserver` (só depois do 1.º foco, debounce de 250 ms, só
  mudanças que mexem em campos) invalida a usada ao escrever. As raízes sombra com campos ganham os mesmos ouvintes
  (no mundo isolado): o foco de um campo para outro da mesma raiz sombra não chega à janela.
- **Correção no motor (`Browser/DevToolsCalls.cs`):** os pedidos DevTools de um browser partilham a numeração do
  CefSharp e o CEF só aceita números crescentes. Com o canal do cofre a pedir no commit de cada página, ao mesmo tempo
  que os scripts precoces e a miniatura, um pedido numerado numa thread e executado depois de outro mais novo falhava
  («Generated MessageId 14 doesn't match returned Message Id 12», medido): a preparação do cofre perdia-se de vez em
  quando (e, mais raramente, já antes, um registo de scripts ou uma avaliação). Agora todos os pedidos DevTools do Éteris
  passam por `DevToolsCalls.Run`: numerar e pôr na fila da thread do CEF acontecem juntos e por ordem, nunca diretamente
  na thread do CEF. Guarda: `DevToolsCallsTests` (nenhuma chamada fora do portão).

Testes: `Vault/CofreClassifierTests` (no Node: **37 formulários** — GitHub, Google, Microsoft, Mercado Livre, gov.br,
Portal das Finanças, bancos PT e BR, Amazon, Facebook, Reddit, login com telefone, com campos de rastreio escondidos,
sem `<form>`, criação de conta, mudança de palavra-passe, códigos de uma utilização, moradas de Portugal e do Brasil,
checkout com CPF e com NIF, cartões com e sem autocomplete, IBAN, matrícula, passaporte; os descritores seguem os
atributos públicos destes formulários, sem os visitar; o autocomplete primeiro; a visibilidade; o debounce e as mudanças
que contam; a normalização; os tipos do script estão no esquema do navegador), `DevToolsCallsTests`,
`VaultChannelGuardTests` (o script continua sem escrever no DOM).

Prova ao vivo (`pp\vivo\c2.ps1`, `pp\vivo\c2\`; perfil de teste novo, página local `127.0.0.1:8898/casos.html` com 12
casos, foco a percorrer os campos com Tab em teclas reais, registo **temporário** das mensagens com o `name` do campo,
que não foi para o código): todos os campos classificados como nos testes (`classificado-completo.txt`): início de
sessão (topo, campos soltos, raiz sombra, iframe da mesma origem e um formulário que chega 1,5 s depois), criação de
conta com confirmação, mudança com três campos, código de uma utilização, morada de Portugal, endereço do Brasil com
CPF, cartão com CVC; os dois campos de rastreio escondidos (opacidade 0 e fora do ecrã) não mandam nada. Capturas
`c2-casos.png` e `c2-fim.png`.

## C3. Sugestões, teclado e preencher palavras-passe

`Vault/SuggestionList.xaml(.cs)`, `Vault/SuggestionGeometry.cs`, `Vault/VaultKeys.cs`, `Vault/VaultFillPolicy.cs`,
`Vault/Scripts/cofre.js` (`fillPlan`, `fillField` e `fill`), `MainWindow.Vault.cs`, `MainWindow.xaml.cs`
(`ShortcutFromPage`).

- **Quando aparece a lista:** num `focus` do script (clique ou Tab num campo visível; um clique num campo que já tem o
  foco também), na aba à vista (ou numa das metades do ecrã dividido), num campo de **utilizador** ou de
  **palavra-passe atual** de um início de sessão ou de uma mudança, se houver palavras-passe deste site. **Nunca ao
  carregar a página.** Com «Mostrar sugestões ao clicar num campo» desligado, só ↓ a abre. O cofre abre aqui pela
  primeira vez (o arranque continua sem o abrir).
- **O que sugere** (`VaultFillPolicy.Suggestions`): a mesma origem primeiro, depois o mesmo domínio registável marcado
  «de *conta.exemplo.com*», confirmado outra vez contra a origem do documento (a do processo do navegador); uma
  credencial de `https` nunca aparece em `http`, uma de `http` numa página `http` (fora do próprio computador) leva
  «Ligação não segura»; domínios parecidos não têm nada. No campo do utilizador, filtra pelo que já foi escrito. Cada
  linha: a inicial, o utilizador (ou «(sem utilizador)») e de onde é; nunca a palavra-passe. Rodapé «Gerir
  palavras-passe…» (abre `eteris://passwords/`).
- **A lista é do Éteris:** `UserControl` WPF na camada por cima da página (`AddToOverlay`), com a moldura e a sombra do
  Éteris, sempre a sair do campo. Posição (`SuggestionGeometry.Place`, pura): retângulo CSS × zoom da página + origem
  da página da aba na camada; por baixo do campo, por cima sem lugar, nunca fora da página da aba (no ecrã dividido,
  da metade dela); a escala do ecrã não entra (em DIP, CSS × zoom). Fecha quando a janela perde o foco, se mexe ou
  muda de tamanho, a aba muda, o documento muda, o campo perde o foco ou o campo sai da página ao rolar.
- **Teclado** (`VaultKeys`, puro, e `VaultKeyFromPage` na thread do CEF, antes de a página ver a tecla): com a lista
  aberta, ↑ ↓ escolhem (com volta), Enter com uma linha escolhida preenche, Esc fecha — **a página não vê nenhuma
  delas**; Tab, ou Enter sem escolha, fecham a lista e seguem para a página (enviar o formulário continua a funcionar);
  as letras seguem para a página (filtram a lista); Ctrl e Alt nunca são da lista. Com a lista fechada num campo com
  sugestões, ↓ abre-a com a primeira escolhida.
- **Rato:** um clique numa linha preenche; um clique nos primeiros **300 ms** da lista não conta (cliques roubados). O
  clique não ativa a camada (`WM_MOUSEACTIVATE` → `MA_NOACTIVATE`): o foco do teclado fica no campo da página, que não
  recebe um `blur`. Só um rato que se mexe escolhe uma linha: a lista a aparecer por baixo de um cursor parado não tira
  a escolha feita com as setas (encontrado na prova ao vivo).
- **Preencher** (`FillVaultLoginAsync`): (1) a aba à vista na janela ativa, o mesmo documento (`VaultChannel.IsCurrent`:
  o contexto ainda existe e a origem do navegador é a mesma) e uma credencial deste site (`CanFill`); (2) o Windows
  Hello só com «Pedir o Windows Hello ao preencher palavras-passe» ligado (D-5, desligado por omissão), com a janela de
  graça e cancelado se a aba sair da frente; depois volta a confirmar (1); (3) a palavra-passe decifra-se agora; (4)
  `__cofre.fill({field, form, values})` no contexto desse documento; (5) o dicionário dos valores esvazia-se. No
  script: só o formulário do campo escolhido, só os campos com tipo (visíveis) — o utilizador e a palavra-passe
  **atual**, nunca a nova, o CVC nem campos escondidos (`fillPlan`); cada campo com o foco, o valor pelo **setter nativo
  do protótipo** (por cima do setter que um framework como o React põe na instância) e os eventos `input` e `change` a
  borbulhar (`fillField`, a única zona do script que escreve na página); o foco volta ao campo onde o utilizador
  estava, e os focos causados pelo próprio preenchimento não voltam a abrir a lista. Sem nada preenchido, o aviso
  «Não foi possível preencher este formulário.».

Testes: `Vault/SuggestionTests` (`SuggestionGeometryTests`: por baixo, largura mínima, zoom 150%, escala do ecrã de
100% a 200% sem mexer, ecrã dividido e a borda da metade, por cima sem lugar, campo fora da página, página estreita;
`VaultKeysTests`: nada com a lista fechada, ↓ abre só num campo com sugestões, setas, Enter com e sem escolha, Esc, Tab,
letras e atalhos para a página; `VaultFillPolicyTests`: exatas primeiro e o mesmo site marcado, `http`/`https` e o
aviso, `localhost` seguro, domínios parecidos sem nada, filtro pelo prefixo, onde se sugere, aba escondida, janela
inativa, documento que saiu, outro site, a pergunta com os dois nomes, os 300 ms, o Windows Hello pelo interruptor),
`Vault/CofreFillTests` (no Node: setter nativo por cima do da instância, foco, `input` e `change` a borbulhar; recusa
o que não tem valor nativo; o plano só com os campos do início de sessão visíveis; sem utilizador só a palavra-passe),
`VaultChannelGuardTests` (o script só escreve na zona «preencher»).

Prova ao vivo (`pp\vivo\c3.ps1`, `pp\vivo\c3\registo.txt` e capturas; perfil de teste com 3 palavras-passe inventadas
criadas pelo `VaultService` real — duas de `http://127.0.0.1:8899` e uma de `https://127.0.0.1:8899`, que nunca
aparece; páginas locais em HTML simples e em **React 19** com campos controlados, a partir de uma cópia que já estava
no computador; teclas e cliques reais; a página regista as teclas que lhe chegam):

1. Tab para o e-mail → a lista com as duas de `http` (a de `https` não aparece) por baixo do campo (`1-lista.png`);
   ↓ Enter → preenchidos o utilizador e a palavra-passe (17 carateres), a página não viu nenhuma tecla, a lista fechou
   e não voltou (`3-preenchido.png`).
2. Esc fecha (a página não o vê); ↓ volta a abrir com a primeira escolhida (`4-reaberta.png`).
3. Um clique (rato real) na segunda linha preenche a outra palavra-passe.
4. Escrever «b» filtra a lista para a única que começa por b; a letra chegou à página (`5-filtro.png`).
5. No campo da palavra-passe: ↓ ↓ Enter preenche o utilizador e a palavra-passe da segunda.
6. Zoom a 150%: a lista sai do campo, com a largura dele × 1,5 (`6-zoom150.png`).
7. Ecrã dividido: a lista sai do campo da metade ativa (React, à esquerda) e, depois de um clique no e-mail da metade
   direita, desse campo, dentro da metade direita; preencher funciona nas duas (`7-ecra-dividido.png`,
   `8-react-preenchido.png`, `9-ecra-dividido-direita.png`).
8. React sozinho: ↓ Enter muda o estado do React (o título da página vem do estado).
9. Com «Pedir o Windows Hello ao preencher palavras-passe» ligado e o Hello falso (`ETERIS_TEST_REAUTH`): a recusar,
   nada se preenche; a aceitar, preenche.

Por fazer neste bloco: ícones da cache de favicons na lista (por agora só a inicial), a «última utilização» (`used`) ao
preencher chegou no C7. A confirmação com os dois nomes quando se escolhe à mão noutro site chegou com o menu de
contexto (C6).

## C4. Guardar, atualizar e «Nunca neste site»

`Vault/SaveFlow.cs` (`SavePolicy`, decisões puras), `Vault/SaveBubble.xaml(.cs)`, `Vault/Scripts/cofre.js` (envio,
`formGone`, `probe`), `Vault/VaultChannel.cs`, `Vault/VaultScripts.cs`, `MainWindow.Vault.cs`, `MainWindow.xaml` (o
ícone de chave).

- **O candidato** (`submit`): o script apanha, em captura e antes dos ouvintes da página, o `submit` do formulário, o
  Enter num campo e o clique num botão, e manda o utilizador e a palavra-passe (num início de sessão), ou a nova (numa
  criação de conta ou mudança, igual na confirmação; numa mudança também a atual). Só inícios de sessão, criações de
  conta e mudanças: um código de uma utilização nunca é palavra-passe. O navegador guarda o candidato **só na memória**,
  preso à aba, com a origem do documento no processo do navegador; larga-o ao fim de 5 minutos, ao fechar ou adormecer
  a aba, ou ao ser julgado. Sem candidato: «Oferecer para guardar palavras-passe» desligado (o script nem manda o envio:
  a preferência entra no script quando o canal prepara o documento), janela privada (D-3, idem), páginas que não são
  `http(s)`, palavra-passe vazia ou com mais de 512 carateres.
- **Sucesso provável**, o critério do Chrome: (1) o frame principal navega para outro documento nos 20 s seguintes e
  a página nova **não** tem outra vez um início de sessão à vista na mesma origem (o navegador pergunta ao script dela
  com `probe`; noutro site, num SSO ou numa página que não é `http(s)` conta como sucesso); (2) numa SPA, o formulário
  enviado desaparece (retirado ou escondido) nos 20 s seguintes e, 0,8 s depois, não há um início de sessão à vista
  (`formGone`). Um erro de login — a página volta com o formulário, ou o formulário fica com a mensagem de erro — não
  oferece nada. Uma navegação muito depois do envio também não (o utilizador desistiu).
- **O que se oferece** (`SavePolicy.Decide`, com as guardadas da mesma origem decifradas no momento): nada num site da
  lista «nunca» ou quando a palavra-passe é igual à guardada; **«Atualizar»** quando o mesmo utilizador tem outra
  (numa mudança sem utilizador no formulário, a guardada com a palavra-passe atual, ou a única do site); **«Guardar»**
  nos outros casos. Uma palavra-passe gerada à espera de confirmação (C5) confirma-se sem perguntar.
- **A bolha** sai da barra de endereço, com o bico no cadeado, e passa a linha entre as barras e a página: nada da
  página chega lá, e a pergunta usa a origem do processo do navegador («Guardar a palavra-passe de *site*?» ou
  «Atualizar a palavra-passe de *utilizador* em *site*?»). Utilizador editável; palavra-passe escondida, com um olho que
  a mostra sem pedir o Windows (o utilizador acabou de a escrever) e com a janela fora das capturas enquanto está à
  vista. «Guardar»/«Atualizar» (a anterior vai para o histórico local de 1 versão), «Agora não», ⋯ › «Nunca neste
  site». Abre sem tirar o teclado à página. «Agora não», × ou Esc dentro da bolha fecham-na e deixam o **ícone de
  chave** na barra de endereço até à próxima navegação (as navegações dos 3 s seguintes — redirecionamentos — não
  contam); um clique na chave reabre a bolha. Ao mudar de aba a bolha fecha e a chave fica com a aba dela.

Testes: `Vault/SaveFlowTests` (candidato só com a origem do navegador e sem segredos no `ToString`; sem candidato com
«oferecer» desligado, na janela privada, fora de login/criação/mudança, sem palavra-passe, acima de 512, fora da web;
prazo de 5 minutos; o candidato não aparece no código do cofre; navegação com e sem o início de sessão de volta, noutro
site, fora da web, tarde demais; `formGone` só do formulário e documento do candidato; guardar, igual, nunca, atualizar,
outro utilizador, criação de conta, mudança pela palavra-passe atual e com uma só guardada, confirmar uma gerada; a
chave e os redirecionamentos), `Vault/CofreSaveTests` (no Node: o que vai no envio de um início de sessão, criação de
conta e mudança; nada noutros formulários, sem palavra-passe, com a confirmação diferente ou num código de uma
utilização; as restrições para o gerador; a preferência «oferecer» entra no script; o `probe` é uma chamada
permitida), `VaultChannelGuardTests` (o script continua sem escrever na página fora do preenchimento).

Prova ao vivo (`pp\vivo\c4.ps1`, `pp\vivo\c4\registo.txt` e capturas; perfil de teste novo; páginas locais em
`127.0.0.1:8900` — início de sessão que navega, SPA que troca o formulário, mudança de palavra-passe; palavras-passe
inventadas escritas com teclas reais; bolha, botões e chave lidos e clicados por UI Automation; o cofre lido por um
comando de teste que diz a que texto esperado cada palavra-passe é igual, sem a mostrar):

1. Palavra-passe errada (a página volta com o erro e o formulário): nenhuma bolha.
2. Certa (navega): «Guardar a palavra-passe de 127.0.0.1?» com o utilizador e a palavra-passe escondida
   (`1-guardar.png`); o olho mostra-a; «Guardar» → o cofre tem a palavra-passe certa.
3. A mesma outra vez: nada.
4. Outra palavra-passe: «Atualizar a palavra-passe de ana.c4@exemplo.test em 127.0.0.1?» (`2-atualizar.png`) →
   o cofre tem a nova e a anterior no histórico.
5. Mudar a palavra-passe num formulário sem utilizador: «Atualizar…» da conta certa (achada pela atual).
6. SPA: errada (o formulário fica com o erro) não oferece; certa (o formulário sai) oferece; «Agora não» deixa a
   chave (`3-chave.png`); a chave reabre a bolha; Esc dentro dela fecha; a navegação seguinte tira a chave.
7. ⋯ › «Nunca neste site» (`4-nunca.png`): o login seguinte no site não oferece nada; o site está na lista «nunca».

## C5. Gerador

`Vault/PasswordGenerator.cs` (`PasswordGenerator`, `GeneratorPolicy`), `Vault/VaultService.cs` (`SaveGenerated`,
`ConfirmGenerated`), `Vault/Scripts/cofre.js` (`generate`, `context`, preencher por tipo), `Browser/PageMenu.cs`,
`MainWindow.PageMenu.cs`, `MainWindow.Vault.cs`.

- **A palavra-passe:** `RandomNumberGenerator`, **20 carateres** por omissão, com minúsculas, maiúsculas, algarismos e
  símbolos (`-_.!@#$%*+=?`), sem os que se confundem (`l I O 0 o 1`): 68 carateres, ~121 bits. Respeita `minlength` e
  `maxlength` (até 64) e um subconjunto de `pattern`: uma sequência de classes (`[…]`, `\d`, `\w`, `.`, letras) com
  quantificadores gera-se peça a peça; outro padrão (lookaheads, grupos) aceita a palavra-passe normal se ela o
  cumprir. A palavra-passe inteira tem de cumprir o padrão (como o HTML: `^(?:padrão)$`); um padrão impossível, sem
  nada aleatório (só texto fixo) ou inválido não dá nada, e o gerador não se oferece.
- **Onde aparece:** no foco de um campo de **palavra-passe nova** (criação de conta, mudança), com «Oferecer para
  guardar palavras-passe» ligado e **nunca na janela privada** (D-3; o script nem manda as restrições). O script manda
  as restrições do campo (mensagem `generate`) e a lista do Éteris mostra «Usar palavra-passe forte» com uma
  **pré-visualização parcial** (os 4 primeiros carateres e «…»); a palavra-passe inteira fica só na memória do
  navegador até ser aceite ou o foco sair. Escrever no campo fecha a lista; ↓ volta a abri-la. Também no menu de
  contexto de qualquer campo de palavra-passe: **«Gerar palavra-passe forte»** (o clique direito manda a mensagem
  `context` com o campo, o formulário, o tipo e as restrições; o menu espera por ela até ~250 ms).
- **Ao aceitar** (Enter, clique, ou o menu): (1) a aba à vista na janela ativa e o mesmo documento; (2) a palavra-passe
  **guarda-se logo** no cofre, marcada «gerada, à espera de confirmação», para não se perder se a página falhar (como o
  Chrome); uma gerada por confirmar do mesmo site e utilizador é substituída (a anterior fica no histórico), uma
  confirmada nunca; (3) preenche a nova e a confirmação desse formulário (pelo menu num campo que não é de palavra-passe
  nova, só esse campo), nunca a atual. Na lista do gestor fica com a etiqueta **«Não confirmada»**.
- **Depois do envio com sucesso** (o critério do C4): a gerada é confirmada **sem perguntar**, com o utilizador do
  formulário (numa mudança sem utilizador, o da conta que tinha a palavra-passe atual); se esse utilizador já tinha
  uma palavra-passe guardada no site, é essa que muda (a antiga vai para o histórico) e a gerada sai. Aviso «Palavra-
  passe gerada guardada para *site*». Sem envio, fica «Não confirmada» até o utilizador a apagar.

Testes: `Vault/PasswordGeneratorTests` (20 carateres com as quatro classes e nada ambíguo; mais de 100 bits; 2000 sem
repetir; distribuição uniforme dentro de cada classe em 3000 palavras-passe; `minlength`/`maxlength` e limites
impossíveis; `pattern` simples, de várias peças e com lookaheads, cumprido 30 vezes cada; padrões impossíveis ou
inválidos sem nada; onde se oferece, nunca na janela privada; a pré-visualização parcial; «Gerar palavra-passe forte»
no menu de contexto só em campos de palavra-passe e fora da janela privada; guardada antes do envio como «Não
confirmada», substituída por outra gerada, confirmada pelo envio; numa conta que já existe só a substitui ao
confirmar), `Vault/SaveFlowTests` (a gerada numa mudança sem utilizador é da conta com a palavra-passe atual),
`Vault/CofreFillTests` (no Node: a gerada preenche a nova e a confirmação, nunca a atual; a mensagem do menu de
contexto com o campo e as restrições).

Prova ao vivo (`pp\vivo\c5.ps1`, `pp\vivo\c5\registo.txt` e capturas; perfil de teste novo; página local de
criação de conta em `127.0.0.1:8901` com `minlength=12`, `maxlength=16` e `pattern=[A-Za-z0-9]+`; teclas reais):

1. Tab para a palavra-passe nova: a lista «Usar palavra-passe forte» com 4 carateres e «…» (`1-gerador.png`).
2. ↓ Enter: a nova e a confirmação com 16 carateres, iguais e a cumprir o padrão (`2-preenchido.png`); no cofre, uma
   palavra-passe de 16 carateres sem utilizador, `gerada=True`.
3. `eteris://passwords/` noutra aba: a linha com a etiqueta «Não confirmada» (`3-nao-confirmada.png`).
4. Enviar (navega): no cofre, a mesma palavra-passe com o e-mail do formulário e `gerada=False`.
5. Shift+F10 na palavra-passe nova: «Gerar palavra-passe forte» no menu (`4-menu.png`); escolhida, preenche os dois
   campos e fica por confirmar (sem envio).

## C6. Preencher contactos, pagamentos, documentos e viagens

`Vault/AutofillFill.cs` (`AutofillPolicy`, `AutofillValues`, `AutofillRows`, decisões puras), `Vault/Scripts/cofre.js`
(`fillPlan` com os formatos de cada campo, `fit`, `focusAfter`), `Infrastructure/DarkMenu.cs` (submenus),
`Theme/Theme.xaml` (`DarkSubmenuItem`), `Browser/PageMenu.cs`, `MainWindow.PageMenu.cs`, `MainWindow.Vault.cs`,
`Vault/SuggestionList.xaml(.cs)` (título e rodapé por tipo).

- **Quando aparece a lista** (`AutofillPolicy.ModeFor`): num campo de contacto (nome, apelido/sobrenome, empresa, rua,
  número, complemento, bairro, cidade, distrito/estado, código postal/CEP, país, telefone, e-mail) de um formulário de
  **morada** (ou de pagamento com morada) → «Informações de contacto»; num campo de cartão (número, titular, validade,
  mês, ano) de um formulário de **pagamento** → cartões; num campo **IBAN** → IBAN; num campo de **NIF/CPF** (D-2) →
  os números fiscais, em qualquer formulário. **Nunca no CVC.** Os outros documentos (passaporte, carta de
  condução/CNH, Cartão de Cidadão/RG) e as viagens (matrícula/placa, fidelidade) **não se detetam sozinhos**: só pelo
  menu de contexto. Cada secção de `eteris://autofill/` desligada («Preencher automaticamente…») não mostra a lista
  dela. Um e-mail ou telefone solto (newsletter) não é uma morada. As linhas vêm dos metadados do cofre — os números
  **não se decifram** para a lista: «Ana Teste · Avenida Inventada 1000, São Paulo», «Visa •••• 1111 · 08/29 ·
  alcunha», «PT50 •••• 0154», «CPF •••• 4725 · Ana Teste»; rodapé «Gerir informações de contacto…», «Gerir
  pagamentos…», «Gerir documentos…» (abre a secção certa de `eteris://autofill/`).
- **Cartões, IBAN e documentos nunca em `http`** (o próprio computador conta como seguro): nem lista nem menu (no menu,
  «Só em ligações seguras (https)»), e o preenchimento confirma outra vez. **Sempre com a reautenticação** (D-5: «Pedir
  o Windows Hello ao preencher pagamentos», ligado por omissão; cartões e IBAN pedem `FillPayment`, documentos
  `FillDocument`), com a janela de graça e cancelada se a aba sair da frente; recusada, nada se preenche.
- **Preencher** (`FillItemAsync`): a aba à vista na janela ativa e o mesmo documento (confirmados antes e depois da
  reautenticação); o item decifra-se agora; os valores por tipo de campo (`AutofillValues`: nunca o código de
  segurança) vão para `__cofre.fill` no contexto do documento e o dicionário esvazia-se. No script, **só o formulário
  do campo escolhido**, cada campo no formato dele: nome completo num só campo; a rua com o número quando o formulário
  não tem campo para ele; validade MM/AA, MM/AAAA (pelo `placeholder` ou comprimento) ou MMAA; mês e ano em listas
  (`08` = `8`, `2029` = `29`); país e estado em listas pelo nome ou pelo código; num campo curto demais para o formato,
  só os algarismos (CPF de 11, CEP de 8, NIF de 9). Os campos que o utilizador já escreveu ficam como estão (menos o
  campo onde escolheu). Depois de um cartão, **o CVC fica vazio e com o foco**; de resto, o foco volta ao campo.
- **Menu de contexto num campo** (qualquer campo de uma página onde o cofre está, também na janela privada):
  **«Preencher com… ›»** com «Palavra-passe ›», «Contacto ›», «Cartão ›» (cartões e IBAN), «Documento ›» e «Viagem ›»
  (submenus novos do menu escuro: → ou Enter abrem, ← ou Esc voltam; cada um só abre o cofre quando é aberto). Num
  campo do tipo da categoria preenche o formulário inteiro como a lista; noutro campo (o passaporte num campo
  «Passaporte», a matrícula num campo «Matrícula»), **só esse campo** com o valor principal do item (número do
  documento, matrícula/placa ou VIN, número de fidelidade). Em «Palavra-passe ›», as deste site primeiro e, por baixo,
  até 15 de **outros sites**: escolher uma pede a confirmação com os dois nomes («Esta palavra-passe é de *a*. Usar em
  *b*?»), a defesa contra domínios parecidos que faltava do C3.

Testes: `Vault/AutofillFillTests` (quando aparece cada lista, por tipo de campo e de formulário: nunca no CVC, NIF/CPF
sozinho, documentos e viagens só pelo menu; os interruptores das secções; cartões, IBAN e documentos nunca em `http`,
com o próprio computador seguro; a reautenticação de cada categoria, ligada por omissão; os valores de um contacto do
Brasil, de um cartão (nunca o código de segurança) e do NIF/CPF formatado; o valor principal de cada item para um só
campo; as linhas mascaradas; «Preencher com…» no menu de contexto, também na janela privada; no Node: a morada com o
nome completo e a rua com ou sem o número, o cartão no formato de cada campo e nunca o CVC, o foco no CVC, as listas de
país e estado, os algarismos num campo curto, os campos já escritos ficam), `PageMenuTests`, `VaultChannelGuardTests` (o
script continua sem escrever na página fora do preenchimento).

Prova ao vivo (`pp\vivo\c6.ps1`, `pp\vivo\c6\registo.txt` e capturas; perfil de teste com 2 contactos, um cartão de
teste 4111 1111 1111 1111, um IBAN, CPF, NIF, passaporte, um veículo e um programa de fidelidade, todos inventados e
criados pelo `VaultService` real; páginas locais em `127.0.0.1:8902`; Windows Hello falso por `ETERIS_TEST_REAUTH`;
teclas reais; listas e menus lidos e clicados por UI Automation; a página mostra o valor de cada campo e o foco):

1. Morada do Brasil: o foco no nome mostra os dois contactos (`1-contactos.png`); «Ana Teste» preenche nome,
   sobrenome, CEP, endereço, número, complemento, bairro, cidade, estado (lista: SP) e telefone; o CPF fica vazio
   (`2-morada-br.png`).
2. CPF num campo de 11 algarismos: a lista dos números fiscais aparece sozinha; o CPF, com o Windows a aceitar, entra
   só com os algarismos.
3. Morada de Portugal: nome completo num campo, «Rua Inventada, 12» (sem campo para o número), código postal,
   localidade, país (lista: PT); Tab até ao NIF → «NIF •••• 6789» → 123456789 (`3-morada-pt.png`).
4. Cartão: a lista «Visa •••• 1111 · 08/29 · Cartão de teste» (`4-cartoes.png`); ↓ Enter com o Windows a aceitar →
   número, titular e validade 08/29; o CVV vazio e com o foco (`5-cartao.png`).
5. Check-in: o foco no passaporte não mostra lista nenhuma; Shift+F10 › «Preencher com…» › «Documento» ›
   «Passaporte •••• 4567 · Ana Teste» (`6-menu-documento.png`) → P1234567 só nesse campo; na matrícula, › «Viagem» ›
   o veículo (`7-menu-viagem.png`) → AA-00-AA.
6. Com o Windows a recusar, escolher o cartão não preenche nada.

Não provado ao vivo: a recusa em `http` fora do próprio computador (as páginas de teste só podem estar em `127.0.0.1`,
que conta como seguro); fica nos testes.

## C7. Janelas privadas

`Vault/VaultWindowPolicy.cs` (a tabela, pura), `Vault/VaultService.cs` (`MarkUsed`), `Vault/VaultStore.cs` (abrir sem
escrever), `Vault/SaveFlow.cs`, `Vault/PasswordGenerator.cs`, `Vault/VaultPages.cs`, `MainWindow.Vault.cs`
(`RecordVaultUse`).

| | Janela normal | Janela privada |
|---|---|---|
| Preencher a pedido (lista ao clicar, menu de contexto) | sim | **sim**: lê o cofre do perfil, como o Chrome e o Firefox |
| «Última utilização» (`used`) ao preencher | sim | **não** (não fica rasto) |
| Candidatos, bolha de guardar, ícone de chave | sim | **não** (D-3; o script nem manda o envio) |
| Gerador (lista e «Gerar palavra-passe forte») | sim | **não** (D-3; uma palavra-passe criada na privada guarda-se à mão depois) |
| `eteris://passwords` e `eteris://autofill` | sim | recusadas («Abra numa janela normal»; o menu abre-as numa janela normal) |

- **Uma só tabela** (`VaultWindowPolicy.Allows`) decide tudo isto, e as decisões das outras tarefas passam por ela: o
  candidato (`SavePolicy`), o gerador (`GeneratorPolicy`), as páginas (`VaultPages`), a preferência «oferecer» que
  entra no script quando o canal prepara o documento, e a «última utilização» (`RecordVaultUse`, o único sítio da
  janela que a grava — guarda nos testes).
- **«Última utilização»** (que faltava do C3): depois de um preenchimento que preencheu alguma coisa numa janela
  normal, o `used` do item (palavra-passe, contacto, cartão, IBAN, documento, viagem) passa a agora; só o metadado muda
  (nem a palavra-passe nem o histórico).
- **Correção encontrada na prova ao vivo:** abrir o cofre escrevia a versão do esquema (`PRAGMA user_version = 1`) a
  cada abertura, e isso mudava o contador do cabeçalho do `cofre.db` mesmo sem mudar nada — o ficheiro mudava só por
  uma janela privada ler o cofre. Agora a versão só se escreve num cofre novo: ler não muda o ficheiro.

Testes: `Vault/PrivateVaultTests` (a tabela; sem candidato, sem gerador e as páginas noutra janela; no menu de contexto
«Preencher com…» sim e «Gerar palavra-passe forte» não; as páginas de gestão recusadas; ler o cofre — abrir, sugerir,
decifrar — deixa o `cofre.db` igual, byte a byte; `MarkUsed` só muda o `used`; guarda: a janela só grava o `used` por
`RecordVaultUse`, que pergunta à tabela, e o script recebe «oferecer» pela tabela).

Prova ao vivo (`pp\vivo\c7.ps1`, `pp\vivo\c7\registo.txt` e capturas; perfil de teste com uma palavra-passe,
contactos, cartão e documentos inventados; páginas locais em `127.0.0.1:8903`; Windows Hello falso a aceitar; a
«última utilização» de cada item lida por um comando de teste):

1. Ctrl+Shift+N; na janela privada, o foco no e-mail mostra a palavra-passe guardada (`1-privada-lista.png`); ↓ Enter
   preenche.
2. Enviar com sucesso (navega): nenhuma bolha nem ícone de chave.
3. Criação de conta: a palavra-passe nova não mostra o gerador; Shift+F10 tem «Preencher com…» e não tem «Gerar
   palavra-passe forte» (`2-privada-menu.png`).
4. O cartão preenche a pedido (com o Windows a aceitar), com o CVV vazio e o foco nele.
5. `eteris://passwords/` na janela privada: «Abra numa janela normal» (`3-privada-gestor.png`).
6. Fechada a janela privada: o `cofre.db` tem **o mesmo SHA-256 de antes** e todos os itens continuam «usada=nunca».
   (Antes da correção acima, o hash mudava sem nenhum item mudar.)
7. Na janela normal, o mesmo preenchimento grava a «última utilização» da palavra-passe (o hash muda).

## D1. Selo e coleções secretas (`Eteris.Sync`)

As sete coleções do cofre entram na biblioteca da sincronização como **secretas** (`Model/SyncRecord.cs`,
`Collections.Secret` e `IsSecret`): `logins`, `loginsNever`, `cards`, `ibans`, `contacts`, `idDocs`, `travel`.

- **Esquemas** (`Secrets/SecretSchemas.cs`, partilhados com o Android): os campos de cada coleção (desenho, secção
  4.1), com o tipo (texto, inteiro, verdadeiro/falso). `Canonical` dá a forma canónica (só os campos conhecidos, por
  ordem de nome, sem nulos) e `IsValid` confere os tipos. O código de segurança do cartão (`cvc`, `cvv`, `csc`,
  `cid`, `cvn`, `securityCode`…) é recusado em qualquer nível; o cofre do navegador usa a mesma regra
  (`VaultFields.IsForbidden` chama `SecretSchemas.IsForbidden`).
- **Chaves** (`Crypto/KeyRing.cs`): `K_seg = HKDF(DEK, "secrets:v1")` e `K_skey = HKDF(DEK, "secret-keys:v1")`. Derivam
  da cadeia, por isso qualquer aparelho emparelhado as tem, e mudam com a geração e com a «nova cadeia».
- **Selo** (`Secrets/SecretSeal.cs`): o `Data` de um registo secreto é `{"v":1,"n":<nonce de 96 bits>,"c":<AES-256-GCM>}`
  com `{"k": chave natural, "d": campos}` lá dentro e os dados associados
  `eteris-secret|v1|<cadeia>|<geração>|<coleção>|<Id>`. A `Key` no motor é opaca: `"s:" + HMAC(K_skey, coleção ‖ chave
  natural)`, 128 bits, igual em todos os aparelhos da conta. Recusa selar mais de 64 KiB.
- **Motor** (`Engine/SyncEngine.cs`): `PutSecret`, `DeleteSecret` (a lápide leva só a chave natural, selada) e
  `OpenSecret` (null quando o selo não abre: o registo fica de fora, sem parar a ronda). Os campos de um Éteris mais
  recente dentro do selo seguem tal e qual quando este aparelho muda o registo. `Put`/`Delete` recusam as coleções
  secretas (só passam pelo selo).

Testes: `Sync/SyncSecretsTests` — as coleções e os esquemas do desenho; nenhum campo proibido nos esquemas e a mesma
regra no cofre; forma canónica e tipos; chaves derivadas da DEK e iguais entre aparelhos, diferentes noutra conta; ida
e volta; nenhum texto em claro no selo; **cada byte** do texto cifrado e do nonce verificado; o selo preso ao `Id`, à
coleção, à cadeia e à geração (e a outra conta); campos proibidos em qualquer nível e o limite de tamanho; no motor, o
registo secreto viaja selado entre dois aparelhos e nenhum dos textos inventados (site, utilizador, palavra-passe,
número do cartão, nota) aparece no transporte nem no estado do motor; selo movido para outro `Id` ou outra `Key` não
abre; campos desconhecidos preservados; lápide com a chave natural.

Prova ao vivo: nenhuma (biblioteca pura).

## D2. `ProfileSync` com segredos e escolha por aparelho

- **Perfil com cofre** (`Engine/ISecretSource.cs`): o perfil local dá, de cada item secreto, só a **chave local** (opaca,
  HMAC com uma chave deste computador) e a **impressão** `{"h": HMAC(conteúdo canónico)}`. A base do `ProfileSync`
  guarda essa impressão, nunca o conteúdo nem o site. Quando a impressão muda, o `ProfileSync` pede o conteúdo
  (`ReadSecret`: **só esse item se decifra**), sela-o e faz `PutSecret`. Contar as alterações (o agendador, depois de cada
  alteração local) não decifra nada.
- **No sentido contrário**, os selos das coleções **ligadas** abrem-se e o conteúdo vai ao perfil em
  `LocalChange.Secret` (com a impressão em `Data`), para o cofre o voltar a cifrar com a chave local. Um selo que não
  abre ou com campos de tipo errado fica de fora, conta em `SyncReport.SecretErrors`, e nessa ronda **nada se apaga**
  dessa coleção aqui (o que falta pode ser só um registo que este Éteris não abre). Um item que não se consegue selar
  (campo proibido, grande demais) fica de fora e volta a ser tentado (a base dele não muda).
- **Conflitos:** LWW por registo (HLC). Quem perde aplica a versão nova e o cofre guarda a palavra-passe anterior no
  **histórico local** (1 versão), com um aviso no estado (`ILocalNotices`): «… mudaram noutro computador; as anteriores
  ficaram no histórico». Na **primeira junção**, o que só existe aqui sobe e, com a mesma chave natural, vale a versão
  da conta (a deste computador vai para o histórico).
- **Escolha por aparelho** (`SavedSync.Collections`, `SyncAgent.EnabledCollections`/`SetCollections`, linhas e
  omissões em `Model/SyncGroups.cs`): as coleções desligadas **não se leem, não se enviam, não se aplicam e os selos delas
  nunca se abrem**; a base delas fica como estava. Desligar não apaga nada (nem aqui nem na conta). Ligar outra vez faz
  a **primeira junção só dessa coleção** (`SavedSync.Joined`, `ProfileSyncOptions.Fresh`). Omissões (D-1): tudo ligado
  menos Pagamentos (`cards`, `ibans`) e Documentos de identificação (`idDocs`). Um estado de antes destas linhas conta
  como já juntado nas coleções que existiam (favoritos, Espaços, definições, Escudo, notas); as secretas juntam-se na
  primeira ronda. `SyncAgent.HasSecrets` diz se aprovar um aparelho novo lhe entrega segredos (D4).

Testes: `Sync/SyncSecretProfileTests` (um perfil em memória com cofre e chave local própria por aparelho) — omissões da
decisão D-1 e escolha guardada; **nenhum segredo nem site em claro** no transporte, no estado do motor, na base nem nos
instantâneos (oito textos inventados, também em Base64); só o item alterado é lido do cofre (40 palavras-passe, 1
leitura); conflito → a mais recente ganha e a outra fica no histórico com aviso; primeira junção; coleção desligada
nunca abre selos nem envia, e ligá-la junta só essa (e desligar outra vez não apaga nada); convergência em 3 aparelhos
e lápides que não ressuscitam; selo estragado fica de fora sem apagar nada aqui; cofre ilegível não envia remoções.

Prova ao vivo: com o D3 (o cofre real do navegador entre dois perfis de teste, pela pasta local).

## D3. O cofre na sincronização do navegador

- **Chave local e impressão gravadas com cada item** (`VaultStore`: colunas `sync_key` e `sync_print`, com
  `K_sync = HKDF(chave do cofre, "Eteris.Cofre:sync")`): `sync_key = HMAC(K_sync, coleção ‖ chave natural)` e
  `sync_print = HMAC(K_sync, forma canónica do item)`. São escritas sempre que o item muda (guardar, editar, «última
  utilização»), por isso a sincronização compara o cofre **sem decifrar nada**. Um cofre de antes (sem as colunas)
  ganha-as ao abrir; os itens dele ganham a chave e a impressão na primeira leitura. Nenhuma das duas diz o site, o
  utilizador ou o conteúdo, e são de cada computador (outra chave do cofre, outras chaves locais).
- **Formato da sincronização** (`Vault/VaultSyncMap.cs`): cada item vai com os nomes de `SecretSchemas`; a chave
  natural é `origem\nutilizador` nas palavras-passe (`gerada\n<ID>` numa gerada por confirmar, que pode ter o mesmo
  site e utilizador que uma confirmada), a origem em «nunca neste site» e o ID do item nas outras coleções. Os
  derivados (marca e últimos 4 algarismos do cartão, máscaras) não viajam: refazem-se ao gravar.
- **Aplicar** (`VaultService.ApplySynced`): grava os campos **tal como vieram**, cifrados com a chave deste cofre (a
  impressão sai igual à do conteúdo recebido: nada volta em eco). Recusa o que não é aceitável — tipos errados, código
  de segurança, origem que não está normalizada (ou que não é `http(s)`/`android://`), chave natural que não bate com os
  campos, cartão com Luhn errado, IBAN inválido, tipo de documento ou de viagem desconhecido, ID estranho. Uma
  palavra-passe substituída fica no **histórico local** (1 versão) e o estado da sincronização avisa «A palavra-passe de
  *site* mudou noutro computador; a anterior ficou no histórico…». **A página `eteris://passwords/` mostra agora a
  anterior**: «Mostrar» (com a confirmação do Windows) mostra também «Palavra-passe anterior», que nunca vai na lista.
- **Perfil do navegador** (`Vault/VaultSyncProfile.cs`, `ProfileWithVault`; `Sync/SyncService.cs`): o resto do perfil
  (`BrowserProfile`) mais o cofre. As coleções do cofre só se leem quando estão ligadas neste aparelho; **sem nenhuma
  ligada, ou sem cofre neste perfil, a leitura não o abre nem o cria**. Um cofre que não abre (chave em falta, DPAPI de
  outra conta, base ocupada) conta como «não se leu», nunca como vazio. Mudar o cofre neste computador marca uma ronda
  para 10 s depois (`VaultService.Changed` → `SyncService.Touch`), como os favoritos.
- **Nunca sobe:** o histórico local, os candidatos por guardar, a chave do cofre, a janela de graça, os «Pedir o Windows
  Hello…» e a «última utilização» das janelas privadas (nem chega a ser gravada: C7; ler o cofre não muda a impressão).

Testes: `Vault/VaultSyncProfileTests` — só chaves e impressões opacas, estáveis ao reabrir; as sete coleções de ida e
volta para outro computador **sem eco**, com chaves locais diferentes, servindo depois as sugestões, o cartão e «nunca
neste site», e nenhum dos onze textos inventados em claro no `cofre.db` do outro; a anterior no histórico, com aviso e
à vista no «Mostrar» (nunca na lista); dez itens inaceitáveis recusados; ler não muda a impressão e a «última
utilização» sim (um só sítio da janela a grava); sem coleções ligadas ou sem cofre não abre nem cria nada; cofre sem
chave = ilegível; cofre de antes ganha as colunas; e **dois computadores pela pasta local com o cofre real** (guardar
num aparece no outro, apagar some, mudar nos dois deixa a anterior no histórico com aviso, Pagamentos só depois de
ligado).

## D4. O tópico «O que passa entre PCs», aprovação e `.eteris`

- **Tópico** (Definições › Sincronização, `Sync/SyncChoices.cs`, `settings.html`): o texto do topo do desenho e uma
  linha por grupo de `SyncGroups` — Favoritos, Espaços, Definições, Decisões do Escudo, Notas, **Palavras-passe**
  («Palavras-passe, notas e sites onde nunca guardar»), **Pagamentos** («Cartões (sem o código de segurança) e IBAN»),
  **Informações de contacto** («Nomes, moradas, e-mails e telefones»), **Documentos de identificação** («Passaporte, carta
  de condução/CNH, Cartão de Cidadão/RG, NIF/CPF») e **Viagens** («Veículos e programas de fidelidade») —, cada uma com o
  interruptor deste computador (um checkbox focável, com o nome da linha para o leitor de ecrã), e duas linhas só de
  informação: «Abas abertas» e «Chaves de acesso» («Não passam: ficam no Windows Hello ou na chave de segurança»).
  Omissões da decisão D-1. A rota `sync-choose` passa por `SyncChoices.ChooseAsync`: ligar Pagamentos ou Documentos
  pede o Windows Hello (`ReauthPurpose.SyncSecrets`, sempre, sem a janela de graça) e, recusado, a linha fica desligada
  com «Não foi confirmado com o Windows: … continua desligado». Ligar uma linha marca uma ronda já.
- **Aprovar um aparelho novo** (`sync-approve`, `SyncChoices.MayApproveAsync`): com segredos em jogo (coleções do cofre
  ligadas aqui ou registos secretos na conta) o pedido diz «Este computador vai receber também as suas palavras-passe,
  informações de contacto e viagens (e os pagamentos e documentos, se estiverem ligados): aprovar pede o Windows Hello»
  e «Aprovar» pede-o (`ApproveDevice`, sempre); recusado, o pedido continua à espera.
- **`.eteris`** (`ProfileTransfer`, `ProfileArchive`): sem o cofre por omissão. A caixa «Incluir palavras-passe,
  pagamentos e documentos» (desmarcada) pede o Windows Hello (`ExportProfileSecrets`, sempre) **antes** de decifrar o que
  quer que seja; leva o cofre inteiro, cada item selado com chaves derivadas da chave do ficheiro (`ArchiveSeals`), por
  isso nem dentro do ficheiro aberto há um segredo fora dos selos. Ao importar, os itens entram no cofre (com a mesma
  chave natural vale o do ficheiro; a anterior fica no histórico) e o resumo diz quantos.
- **Código de recuperação:** o texto diz «Com este código abre-se também o cofre de palavras-passe (e os pagamentos e
  documentos, se os sincronizar)» e aconselha guardá-lo também fora do próprio cofre.

Testes: `Sync/SyncChoicesTests` — as doze linhas pela ordem, os textos (os dois nomes de cada documento) e as omissões;
Pagamentos e Documentos pedem o Windows (`SyncSecrets`, sempre) e, recusado, ficam desligados; desligar não pede; as
outras linhas nunca pedem; aprovar com segredos pede `ApproveDevice` e sem segredos não; exportar sem segredos não pede
nem decifra nada, recusado não decifra nada, aceite leva-os; o ficheiro com segredos abre só com o código, recusa
coleções que não são do cofre e o código de segurança; do cofre A para o cofre B pelo ficheiro, com a anterior no
histórico e o resumo; a página tem o tópico, a caixa desmarcada e o texto do código; as rotas passam pelas decisões
testadas.

## D5. Nova cadeia e documentação da sincronização

- **Nova cadeia** (remover um aparelho ou gerar um código novo; `SyncEngine.StartRotationAsync`): cada registo secreto é
  aberto com as chaves da cadeia antiga e **selado de novo** com as da nova (outra `Key` opaca, outro selo, dados
  associados com a cadeia nova); as lápides também (levam a chave natural selada). Um aparelho que fica e recebe a conta
  nova volta a selar o que ele próprio tinha escrito (`RestageOwn`), com as chaves dos selos da cadeia antiga que o motor
  guarda no seu estado (protegido por DPAPI; um estado de antes destas chaves deixa-os para o perfil voltar a enviar).
  O aparelho removido não abre nada da cadeia nova.
- **Aviso** ao remover um aparelho com segredos na conta: «O aparelho removido já tinha as suas palavras-passe: mude as
  dos sites importantes.» (gerar só um código novo não o diz).
- **Documentação** (`docs/sincronizacao.md`): secção nova «Coleções secretas» (coleções, chaves naturais e campos, a
  cifra interna, o perfil com segredos, o tópico, emparelhamento, nova cadeia e `.eteris`), a linha das palavras-passe
  e a das chaves de acesso em «O que sincroniza», a ameaça «quem tem o código de recuperação ou um aparelho emparelhado
  tem também as palavras-passe…» com o remédio, e em «O que a Google vê» que o número de palavras-passe também pesa no
  tamanho do instantâneo (a ordem de grandeza).

Testes: `Sync/SyncSecretRotationTests` — remover um de três aparelhos: todos os selos mudam (nenhuma `Key` antiga),
abrem só com a `K_seg` nova (nem com a antiga, nem com a antiga com a cadeia nova), os que ficam continuam iguais e a
sincronizar, o removido para e com o código e as chaves antigas não abre nada, e nenhum texto inventado aparece na
cadeia nova; um código novo também volta a selar, sem o aviso do aparelho removido; `RestageOwn` sela de novo o que o
aparelho escreveu (lápide incluída) e um estado antigo sem as chaves não passa segredos; a documentação lista as sete
coleções, todos os campos, as derivações, os dados associados, o tópico, a ameaça e o que a Google vê.

### Prova ao vivo do bloco D (D2–D5)

`pp\vivo\d\*.ps1`, `pp\vivo\d\registo.txt` e capturas, fora do repositório. Compilação do ramo `sync`; até cinco
perfis de teste (`profile-pp-da`…`de`) na mesma pasta local como «conta» (`ETERIS_TEST_SYNC_FOLDER`: nenhum pedido de
rede); Windows Hello falso (`ETERIS_TEST_REAUTH=aceitar|recusar`); as Definições lidas e carregadas por DevTools em
`127.0.0.1` (`ETERIS_TEST_DEVTOOLS_PORT`); cofres semeados e lidos por um comando de teste que usa o `VaultService`
real; só dados inventados.

1. **A** com um item de cada coleção liga a sincronização; o tópico «O que passa entre PCs» mostra as doze linhas
   com as omissões D-1 (`1-a-topico.png`). **B**, sem cofre, entra com o código: recebe as duas palavras-passe
   (iguais às de A), «nunca neste site», o contacto e o veículo; **nenhum cartão, IBAN nem documento** (linhas
   desligadas).
2. Em B com o Windows a **recusar**, ligar Pagamentos deixa a linha desligada com «Não foi confirmado com o Windows:
   «Pagamentos» continua desligado.» (`2-b-pagamentos-recusado.png`). Com o Windows a aceitar liga; o cartão e o IBAN
   só chegam depois de A também ligar Pagamentos (`3-b-pagamentos-chegaram.png`).
3. A mesma palavra-passe mudada em A e em B antes das rondas: fica a de B nos dois; em A a de A vai para o histórico
   e o estado diz «A palavra-passe de contas.exemplo.test mudou noutro computador; a anterior ficou no histórico…»
   (`4-a-aviso-historico.png`); «Mostrar» no gestor de A (Windows a aceitar) mostra a atual e a «Palavra-passe
   anterior» — e a captura feita nesse momento mostra a janela de **B**, não a de A: com um segredo à vista a janela
   sai das capturas (A5). Apagar uma palavra-passe em B some em A.
4. Um terceiro perfil (C) pede para entrar: o pedido diz que ele vai receber as palavras-passe e que aprovar pede o
   Windows Hello (`6-b-pedido-com-segredos.png`); aprovado (Windows a aceitar), C recebe o cofre das linhas ligadas
   por omissão. Com o Windows a **recusar**, «Aprovar» deixa o pedido de um quarto perfil à espera, com «Não foi
   confirmado com o Windows: o pedido continua à espera.» (`7-a-aprovar-recusado.png`).
5. `.eteris`: com o Windows a recusar, «Incluir palavras-passe…» não exporta nada (nenhum ficheiro novo); sem a caixa,
   34 registos e importá-lo num perfil novo (E) **não cria cofre**; com a caixa e o Windows a aceitar, 41 registos
   (7 do cofre) e importá-lo dá a E um item de cada coleção. Os dois ficheiros sem nenhum texto inventado em claro.
   (Os ficheiros vão para Transferências do Windows; foram logo movidos para `pp\vivo\d\eteris`.)
6. **Nova cadeia:** A remove C; o aviso acaba em «O aparelho removido já tinha as suas palavras-passe: mude as dos
   sites importantes.» (`10-a-removido-aviso.png`); o estado de A (lido pelo comando de teste, dentro da DPAPI) passa
   para a cadeia nova com os selos da cadeia nova e todas as `Key` opacas, lápide incluída. B, fechado durante a troca,
   recebe a conta nova ao abrir e continua; uma palavra-passe mudada depois em B chega a A (a anterior no histórico).
   C para («removido noutro aparelho») e fica com o que tinha (`11-c-removido-parado.png`).
7. **Nada em claro:** em todos os ficheiros da pasta da «conta» (o que a Google veria; antes e depois da troca de chave)
   e no `cofre.db` de cada perfil, 0 dos textos inventados em Latin-1, UTF-8 e UTF-16; no estado da sincronização de
   A, B e C (o JSON inteiro decifrado da DPAPI: motor, base, caixa de saída), 0.

**Correções encontradas na prova:** a página `eteris://autofill/` ainda dizia «a sincronização das palavras-passe e do
preenchimento automático ainda não existe»; agora cada secção (Pagamentos, Informações de contacto, Documentos,
Viagens) e as Definições das Palavras-passe têm «Sincronizar entre computadores» com o que passa e a ligação para o
tópico (`12-b-preenchimento-textos.png`). O texto da conta em Definições › Sincronização passou a incluir as
palavras-passe e o preenchimento automático (`13-b-definicoes-conta.png`). A promessa «As suas palavras-passe não vão
para ninguém» em `eteris://privacy/` dizia «o cofre … ainda não sincroniza»; agora diz que, com a sincronização ligada,
só as linhas escolhidas em «O que passa entre PCs» passam para os outros computadores do utilizador, pela pasta dele
no Drive, com uma segunda cifra (e, desligada, que não saem deste computador; teste
`VaultClearDataTests.Promise_PasswordsGoToNoOne_SaysWhatTheSyncDoes`).

## E2. Chaves de acesso pelo Windows (prova)

Sem código: só a prova de que o WebAuthn das páginas segue para a janela do Windows (risco R7 do desenho) e a
documentação. **Prova ao vivo** (`pp\vivo\e2\e2.ps1`, `pp\vivo\e2\registo.txt` e capturas; perfil de teste; uma
página local servida em `127.0.0.1` e aberta como `http://localhost:8911/`, com um utilizador inventado; nenhum site
de fora):

1. `PublicKeyCredential` existe e `isUserVerifyingPlatformAuthenticatorAvailable()` dá `false` (este PC não tem Windows
   Hello configurado; o mesmo que a A3 mediu: `DeviceNotPresent`).
2. «Criar chave de acesso» (Enter real no botão) → abre-se a janela **«Segurança do Windows» › «Salvar sua chave de
   acesso»**, com «teste@exemplo.test» e «Chave de acesso do localhost»; sem Hello, pede «Insira a chave de segurança
   na porta USB» (`e2-1-criar-janela-windows.png`). A janela é do Windows (processo `CredentialUIBroker` no primeiro
   plano) e fica presa à janela do Éteris (aparece na árvore de UI Automation dela).
3. Não se tocou na janela: nada foi criado. O prazo do pedido (30 s) **não** a fechou (45 s depois continuava aberta,
   e a promessa da página continuava por resolver); fechar a janela do Éteris fecha-a. No Chrome o prazo cancela a
   operação: diferença anotada em `docs/paridade.md` (secção 9), para ver com o motor.

O tópico «O que passa entre PCs» diz «Chaves de acesso — Não passam: ficam no Windows Hello ou na chave de segurança»,
e `docs/sincronizacao.md` tem a mesma linha em «O que sincroniza». **Por provar à mão (dono):** num PC com Windows
Hello, a mesma página pede o PIN, o rosto ou a impressão digital, e «Outro dispositivo» (telemóvel por QR) aparece na
mesma janela.
