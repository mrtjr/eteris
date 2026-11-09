# Gestor de palavras-passe e preenchimento automático do Éteris

Estado: **bloco A (base local) em curso no ramo `sync`, por publicar; ainda sem interface.** O gestor é próprio, em
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
