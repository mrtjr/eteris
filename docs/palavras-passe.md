# Gestor de palavras-passe e preenchimento automático do Éteris

Estado: **bloco A (base local: A0–A5) e, do bloco B (interfaces de gestão), B1 a B3 feitos no ramo `sync`, por
publicar** (os blocos C a E do desenho — preenchimento na página, sincronização e fecho — estão por fazer). O gestor é próprio, em
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
  registos nem diagnósticos.

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
