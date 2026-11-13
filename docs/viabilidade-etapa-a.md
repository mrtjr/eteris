# Viabilidade da Etapa A (2026-11-12)

Etapa A do roteiro aprovado pelo dono a 2026-11-10: **(1) aviso de preço + recorte ao vivo** e **(2) tradução local**.
Estudo antes de planear: o que o motor permite, o que há para usar, onde vive no código, o que muda na promessa de
privacidade, esforço, riscos e o que fica fora. Nada foi implementado; o único protótipo (tradução) foi descartável,
fora do repositório.

Marcas de prova: **[medido]** nesta máquina; **[código]** lido no código do Éteris; **[fonte]** lido na fonte indicada
(2026-11-12); **[não verificado]** dedução ou conhecimento geral, por confirmar.

## Resumo

| Item | Viável? | Recomendação | Esforço |
|---|---|---|---|
| 1. Aviso de preço + recorte ao vivo | Sim, só com o Éteris aberto. Provado com a janela minimizada; o limite são as **defesas anti-robô** das lojas (2 de 5 bloquearam sempre) | Começar pela **vigia** (preço e «avise-me quando mudar»), corrida num motor escondido numa janela própria fora do ecrã; o **recorte ao vivo** depois, como janela pequena com um motor próprio | 7 tarefas |
| 2. Tradução local | Sim, medido dentro do Éteris, também com sandbox. Custa **~540 MB** de memória por motor carregado | Motor **Bergamot** (o da tradução do Firefox) em WebAssembly, numa página interna escondida e sem rede, com os **modelos da Mozilla** (MPL-2.0), largado quando não se usa | 5 tarefas |

As provas (tarefas 0) foram feitas a 2026-11-12 com protótipos descartáveis (código temporário numa worktree, não
guardado), perfis de teste e, para o sandbox, o Éteris 0.2.16 instalado com um perfil de teste. Resultados nas secções
«Prova» de cada item.

## 1. Aviso de preço + recorte ao vivo

### O que o motor permite

- **Cada página é uma janela nativa** (CefSharp com `HwndHost`, `Browser/PageBrowser.cs`) [código]. Os comentários em
  `MainWindow.Mini.cs` e `MainWindow.Screenshot.cs` ainda falam em renderização fora do ecrã (OSR); parecem
  desatualizados [não verificado]. Consequência: nada do WPF se desenha por cima de uma página (por isso o Observatório vive numa
  camada própria) [código]; os controlos do recorte têm de ficar fora da área da página.
- **Uma janela pequena com uma página viva já existe duas vezes:** os sites fixados na barra lateral
  (`MainWindow.WebPanels.cs`: um `ChromiumWebBrowser` por site, no contexto da janela, com o Escudo) e a «janela
  flutuante» (`MainWindow.Mini.cs`: a janela inteira encolhe para um canto, sempre por cima) [código]. Um recorte é
  um painel destes numa janela própria, sempre por cima, que mostra só um elemento.
- **Escolher um elemento já existe:** o «Esconder elemento» (`Shields/PickerScript.cs`) realça o elemento debaixo do
  rato e devolve um seletor estável [código]. Uma variante devolve o seletor, o texto e o retângulo sem esconder nada.
- **Uma página escondida que carrega um endereço já existe:** o «Espreitar» (`MainWindow.Glance.cs`) cria um browser
  escondido num contexto só em memória [código]. Mas corre **sem JavaScript** (`Javascript = CefState.Disabled`)
  [código], e muitas lojas só põem o preço com JavaScript [não verificado]: a vigia usa o padrão, não as definições.
- **Abas a dormir e janela minimizada não impedem a vigia** [medido, secção «Prova»]. O sono por tempo e o sono com
  pouca memória visam abas (`BrowserTab`) e os sites fixados têm um sono próprio (ajuste e6) (`MainWindow.Sleep.cs`,
  `MainWindow.Pressure.cs`, `MainWindow.WebPanels.cs`) [código, lido em parte]: o motor da vigia não deve entrar
  nessas listas. Um motor escondido dentro da janela principal (como o do «Espreitar») continua a correr JavaScript
  com a janela minimizada e a página diz-se `visible`, mas passa a ter **14×14 píxeis**; numa janela própria fora do
  ecrã mantém o tamanho normal.
- **Só corre com o Éteris aberto:** o WPF fecha a aplicação ao fechar a última janela (não há `ShutdownMode` próprio
  em `App.xaml(.cs)`) [código]. Não há ícone na bandeja.
- **Avisos do Windows:** não há código nenhum. O alvo é `net10.0-windows`, sem versão do SDK do Windows
  (`Eteris.Browser.csproj`), e não há `AppUserModelID` em `src` nem em `build` [código]. Há duas vias [não verificado]:
  (a) o balão de um ícone temporário na bandeja (`Shell_NotifyIcon`, só P/Invoke), que o Windows 10/11 mostra como
  notificação; (b) notificações «a sério» (Windows App SDK ou CommunityToolkit), que pedem outro alvo de compilação e
  um `AppUserModelID` no atalho que o Velopack cria. Dentro do Éteris já há o aviso curto (`MainWindow.Toast.cs`) e o
  piscar na barra de tarefas (`Infrastructure/WindowAttention.cs`) [código].

### Prova (tarefa 0) [medido]

Cópia da worktree (sem sandbox), perfil de teste, um motor escondido com o Escudo e um contexto só em memória (sem
cookies), JavaScript ligado e imagens desligadas. Lia o preço dos dados estruturados, do texto e de um seletor 6 s
depois de cada carga. Três corridas, 22 rondas, das 19:15 às 19:37: janela à vista, depois **minimizada durante 10
min**, sempre com o sinal simulado de pouca memória ligado (`ETERIS_TEST_PRESSAO=1`). Não confirmei se alguma aba
chegou a adormecer.

| Página | À vista | Minimizada (motor dentro da janela) | Minimizada (janela própria fora do ecrã) |
|---|---|---|---|
| Loja local, preço posto por temporizadores encadeados | ✔ | ✔ 6/6 rondas | ✔ 3/3 |
| Loja local, preço posto num `requestAnimationFrame` | ✔ | ✔ 6/6 | ✔ 3/3 |
| Kabum (preço já no HTML, nos dados estruturados) | ✔ | ✔ 7/7 | ✔ 3/3 |
| Pichau (sem JavaScript o `curl` só recebe um desafio de 6 KB) | ✔ | ✔ 7/7 | ✔ 3/3 |
| Mercado Livre | ✔ na 1.ª visita | ✘: redireciona para a aplicação (`meli://`), por o motor ter 14×14 px | ✘ 4/4: página «Mercado Libre» sem preço |
| PcComponentes (pt), Chip7 (pt) | ✘ «Um momento…», HTTP 403 | ✘ | não testado |
| Terabyte | os dois produtos foram para outra página (indisponíveis): teste inválido | | |

- **Minimizar não estraga nada:** temporizadores e `requestAnimationFrame` correm, o preço posto por JavaScript
  aparece ao fim de ~2,1 s e ~0,5 s, sempre igual. A página diz-se `visible` mesmo minimizada, por isso também não é
  abrandada.
- **O limite são as defesas anti-robô**, à vista ou minimizada. 2 lojas pararam sempre no desafio da Cloudflare e o
  Mercado Livre deixou de dar o preço depois da 1.ª visita. O limiar combinado («3 lojas reais nas três situações»)
  **cumpriu-se só com 2 lojas reais** (Kabum e Pichau), mais as duas lojas locais.
- **O motor dentro da janela principal fica com 14×14 px** quando ela é minimizada, e há sites que o tratam como
  telemóvel. A janela própria fora do ecrã (1266×886) resolve isso.
- Cada verificação levou 6–9 s, 6 dos quais de espera fixa do protótipo.
- O protótipo falhou sempre a 1.ª carga de cada corrida (motor ainda por criar): é defeito do protótipo, não do motor.
- Numa das sessões de teste da tradução, a cópia da worktree fechou-se sozinha uma vez, sem registo no Windows nem no
  perfil. Não voltou a acontecer em 4 sessões. Fica a vigiar.

### Desenho mínimo

Um núcleo comum, a **vigia**, e três gestos:

1. **«Vigiar o preço»** — no menu da página quando ela declara um preço (dados estruturados: JSON-LD `Product`/
   `offers.price`, `itemprop="price"`, `product:price:amount`) [não verificado em lojas reais]; sem dados
   estruturados, o utilizador aponta o preço com o seletor. Avisa quando baixa (ou baixa abaixo de um valor).
2. **«Avise-me quando mudar»** — aponta-se um pedaço; avisa quando o texto dele muda.
3. **«Recorte ao vivo»** — aponta-se um pedaço; abre uma janela pequena, sempre por cima, redimensionável, com uma
   barra própria («abrir a página», recarregar, fechar). Dentro, um motor próprio carrega a página **no contexto da
   janela de origem** (vê a sessão iniciada, como os sites fixados) e um estilo injetado isola o elemento (página
   rolada até ele, elementos fixos escondidos, sem barras). Recarrega de N em N minutos, se se pedir.

A **vigia corre sozinha**: de tempos a tempos (mínimo proposto 30 min, com variação aleatória), um motor escondido,
um de cada vez, **numa janela própria fora do ecrã com o tamanho de uma janela normal** (provado: dentro da janela
principal minimizada fica com 14×14 px), carrega o endereço com JavaScript, sem imagens nem som, com o Escudo, espera
que acalme, lê o pedaço (ou o preço) e fecha-se. Quando o site barra a visita automática, a vigia diz
«este site não deixa vigiar de fora» e passa a ler o preço **só quando o utilizador abre a página** numa aba (sem
visita extra). **Por omissão num contexto só em memória, sem os cookies do utilizador** (a loja vê uma
visita anónima); por vigia, opção «com a minha sessão». Ao abrir o Éteris, as vigias em atraso correm depois de o
arranque assentar. Mostra sempre o estado: «verificado há 20 min», «falhou desde ontem (a página pediu um captcha)»,
«o pedaço desapareceu» (as falhas silenciosas são a queixa principal do Distill e do Visualping, segundo o mapa das
extensões).

Avisos (decisão 3): só dentro do Éteris, com o aviso interno e o piscar na barra de tarefas. Clicar abre a página.
As notificações do Windows ficam para depois.

### Onde vive no código

- `src/Eteris.Browser/Watch/` (novo): lógica pura e testável — ler preços (`R$ 1.299,90`, `1 299,90 €`,
  `$1,299.90`), normalizar o texto, decidir «mudou/baixou», agenda e recuo em caso de falha.
- `Data/WatchStore.cs` (novo, SQLite no perfil, como `SnoozedTabsStore`); nunca na janela privada; apagado pelo
  «Limpar dados».
- `MainWindow.Watch.cs` (corredor das vigias, avisos) e `ClipWindow.xaml(.cs)` (o recorte), com `PageBrowser`.
- Página interna `eteris://vigias` (lista, pausar, apagar, último valor, preços dos últimos 90 dias), servida como as
  outras (`Browser/InternalPages.cs`, `EterisSchemeHandlerFactory.cs`).
- Entradas: menu da página (`Browser/PageMenu.cs`), menu de contexto e paleta. **A entrada no menu do navegador
  depende de `MainWindow.BrowserMenu.cs`, que outra sessão está a mudar: fica para depois de essa fundir.**

### Privacidade

A promessa «Nada sai do computador sem um pedido seu» (`Browser/PrivacyPage.cs`) mantém-se, mas a vigia é **tráfego de
fundo repetido**, pedido uma vez pelo utilizador e feito sem ele estar a ver. Tem de ficar escrito:

- `eteris://privacy`: linha nova «Vigias que criou: N, a sites X e Y, de H em H horas; o site vê uma visita a cada
  verificação (sem os seus cookies, salvo onde escolheu o contrário); o Escudo bloqueia os rastreadores dessas visitas».
- `docs/site/privacidade.html`, secção 4 («o Éteris faz apenas estes pedidos»): acrescentar as vigias.
- Nada vai para o projeto nem para terceiros além do próprio site; as visitas das vigias não entram no histórico.

### Plano em tarefas

0. ~~Prova da vigia~~ — feita (secção «Prova»): minimizar não estraga nada; as defesas anti-robô sim.
1. **Núcleo puro + testes**: leitura de preços (formatos pt-BR, pt-PT, en; dados estruturados), deteção de mudança,
   agenda com recuo.
2. **Guardar e listar**: `WatchStore`, `eteris://vigias`, «Limpar dados», nunca em privado.
3. **Apontar**: variante do seletor (seletor + texto + retângulo), deteção de preço estruturado, entradas no menu da
   página, menu de contexto e paleta.
4. **Corredor**: motor escondido numa janela própria fora do ecrã, um de cada vez, contexto em memória ou da janela,
   tempo limite, estados de falha (desafio anti-robô, redirecionamento para uma aplicação, pedaço desaparecido) e a
   leitura passiva quando o utilizador abre a página.
5. **Avisos**: aviso interno + piscar na barra de tarefas (`MainWindow.Toast.cs`, `Infrastructure/WindowAttention.cs`);
   clique abre a página.
6. **Recorte ao vivo**: `ClipWindow` sempre por cima, estilo de isolamento, recarregar opcional, posição guardada,
   máximo de 3 abertos.
7. **Privacidade e prova ao vivo**: linha em `eteris://privacy`, site, CHANGELOG/Novidades; prova com o perfil de
   teste (vigia que dispara, janela minimizada, abas a dormir, pouca memória simulada).

### Riscos

- **Sites que barram robôs** (captchas, Cloudflare): **medido**, 2 de 5 lojas barraram sempre e uma terceira deixou
  de responder depois da 1.ª visita. Lojas que mudam o desenho: a vigia perde o pedaço. Mitigação: estados visíveis,
  nunca falhar em silêncio, intervalos longos, leitura passiva quando o utilizador abre a página. Não testei se a
  opção «com a minha sessão» passa os desafios.
- **Preço diferente sem sessão** (região, cliente registado): a opção «com a minha sessão» resolve, à custa de a loja
  saber que é o utilizador a verificar.
- **O recorte recarrega o endereço** num segundo motor: perde-se o estado que não está no URL (filtros escolhidos,
  carrinho, aplicações de página única), e um seletor não chega a pedaços dentro de iframes de outra origem.
- **A vigia corre JavaScript de terceiros sem ninguém a ver:** o motor dela tem de recusar transferências, janelas
  novas, pedidos de permissão, diálogos e som.
- **Custo:** cada verificação carrega uma página inteira (segundos de CPU, alguns MB) [não verificado]; cada recorte
  aberto é um processo de página vivo (ordem de uma aba) [não verificado].
- **Termos dos sites:** visitas automáticas espaçadas, para uso próprio; risco baixo, mas é uma decisão do dono
  [não verificado].

### Fica fora

Vigias com o Éteris fechado (serviço ou bandeja), avisos por e-mail ou telemóvel (pediriam um servidor), histórico de
preços de terceiros à Keepa (dados e servidor), cupões, sincronizar vigias entre PCs (possível depois, pela
sincronização cifrada).

## 2. Tradução local

### O Chromium já traduz?

Não de forma que sirva. A tradução de páginas do Chrome usa os servidores da Google, e a tradução no aparelho
depende de componentes que a Google descarrega pelo «component updater» [não verificado no motor do Éteris]. O
Éteris desliga esse atualizador e o descarregamento de modelos de propósito (`disable-component-update`,
`component-updater=url-source=…` local, `OptimizationGuideModelDownloading` e `OptimizationGuideOnDeviceModel`
desligados em `Infrastructure/CefConfigurator.cs`) [código]. A frase do mapa «o motor Chromium já tem os dados» vale
para a deteção da língua, não para a tradução em si.

### Opções

| Opção | Licença | Tamanho | Como corre | Veredicto |
|---|---|---|---|---|
| **Mozilla: motor Bergamot + modelos da tradução do Firefox** | MPL-2.0, motor e modelos [fonte: github.com/mozilla/translations, github.com/browsermt/bergamot-translator] | 26 MB a descarregar e 37 MB no disco por sentido [medido] | WebAssembly dentro do Chromium [medido] | **Escolhida** |
| OPUS-MT `tc-big-en-pt` (Universidade de Helsínquia) | CC-BY-4.0 [fonte: ficha no Hugging Face] | 0,2 mil milhões de parâmetros [fonte] | Precisa de um motor nativo (CTranslate2 ou ONNX Runtime) [não verificado] | Reserva, se a qualidade da Mozilla não chegar |
| NLLB-200 600M (Meta) | CC-BY-NC-4.0, não comercial [fonte: ficha no Hugging Face] | 600 milhões de parâmetros | — | Fora: a restrição não comercial não cabe na GPL-3.0 do Éteris |
| Argos Translate | Código MIT; modelos de várias licenças [não verificado] | — | Python + CTranslate2 | Fora: obrigaria a embutir Python |

A licença do Éteris é a GPL-3.0 (`LICENSE`); a MPL-2.0 é compatível com ela (MPL-2.0 §3.3, salvo aviso
«Incompatible With Secondary Licenses») [não verificado ficheiro a ficheiro]. As dependências do Bergamot (Marian,
intgemm, sentencepiece…) têm de entrar em `licenses/` e `THIRD-PARTY-NOTICES.md`.

### Línguas e modelos [fonte: registo de modelos da Mozilla, `models.json` gerado a 2026-11-12]

- 118 pares no registo, **108 publicados**: 56 do inglês para outra língua e 52 de outra língua para o inglês.
- **pt→en, en→pt, es→en, en→es**: publicados, arquitetura `base-memory`, 31 milhões de parâmetros, qualidade
  COMET22 0,887 / 0,889 / 0,857 / 0,854.
- **Não há pt↔es direto**: passa pelo inglês (dois modelos, o dobro do tempo, erros somados).
- O português é um só («pt»), sem escolha entre pt-PT e pt-BR.
- Ficheiros por sentido [medido]: modelo 31,6 MB, léxico 4,0–4,6 MB, vocabulário 0,8 MB; comprimidos 25,9 MB (en→pt) e
  25,6 MB (pt→en). Os SHA-256 dos modelos descarregados batem com os do registo.

### Protótipo [medido]

Pacote `@browsermt/bergamot-translator` 0.4.9 (npm, MPL-2.0, de 2022) com os modelos atuais (2026) servidos por um
servidor local, no Chrome 157 sem interface, num AMD Ryzen 7 7700X (8 núcleos, 31 GB). Texto original de ~360 palavras
em pt e ~270 em en, repetido, cache desligada, 1 thread:

| Sentido | Carregar o modelo | Débito | Um parágrafo |
|---|---|---|---|
| pt → en | 0,40 s | **591 palavras/s** | 149 ms |
| en → pt | 0,41 s | **548 palavras/s** | 169 ms |

- Com 4 threads o débito ficou igual (574 e 560 palavras/s): o protótipo mandava os parágrafos num só lote; dividir
  o trabalho é uma tarefa, não um limite.
- Este WebAssembly usa a multiplicação de matrizes de recurso (o Firefox tem uma nativa; o Chromium não)
  [fonte: README do pacote]. Mesmo assim, um artigo de 1500 palavras traduz-se em ~3 s neste PC, e a parte à vista
  em menos de 1 s.
- Qualidade vista à mão: boa, com erros típicos («A câmara anunciou» → «The camera announced»; «aposentados»,
  «on-line», ao jeito do pt-BR).

### Prova dentro do Éteris (tarefa 0) [medido]

Mesmo motor, mesmos modelos e mesmo texto, no motor do Éteris (Chromium 156):

| Onde | pt → en | en → pt | Carregar o modelo | Um parágrafo |
|---|---|---|---|---|
| Cópia da worktree, sem sandbox, página e modelos servidos por `eteris://` (1.ª corrida, a frio) | 464 palavras/s | 424 | 0,83 / 0,43 s | 147 / 175 ms |
| Idem, 2.ª corrida | **578** | **553** | 0,40 s | 137 / 171 ms |
| **Éteris 0.2.16 instalado, com sandbox** (perfil de teste; página por `http://127.0.0.1`; nenhum processo filho com `--no-sandbox`) | **496** | **497** | 0,47–0,52 s | 184 / 197 ms |
| Cópia da worktree com o processo da página limitado a ~1/3 de núcleo (teto de CPU de um «job» do Windows; aproximação grosseira de um portátil fraco) | 177 | 150 | 1,6 / 2,3 s | 0,6 / 1,0 s |

- **O esquema `eteris://` tal como está não chega:** o `fetch()` dos ficheiros falha («Failed to fetch») porque o
  esquema não está registado com `IsFetchEnabled` (`Infrastructure/CefConfigurator.cs`). Com essa opção ligada, tudo
  funcionou (módulos, worker, WebAssembly, 31 MB de modelo). Na implementação: ligá-la no `eteris://` (vale para todas
  as páginas internas; a rever) ou servir o motor por um esquema próprio só dele.
- **Memória: ~540 MB por motor com um modelo carregado** (o processo da página passou de 77 para 618–653 MB privados e
  ficou lá enquanto o motor existiu). Com os dois sentidos carregados um a seguir ao outro, o pico foi 634 MB. O
  desenho tem de carregar o motor só quando se pede, ter um só de cada vez e largá-lo ao fim de uns minutos sem uso
  (como o «Espreitar», ajuste e6).
- **PC lento:** um ecrã de texto (~300 palavras) leva ~1,7–2,0 s com o modelo já carregado, e ~4 s na 1.ª tradução.
  O limiar combinado (menos de 2 s) cumpre-se à justa, e só a partir da 2.ª. A limitação de CPU das DevTools não
  serve para isto: só abranda a thread principal, não o worker do motor (deu 561 / 529 palavras/s com «4× mais
  lento»).

### Desenho mínimo

- **Onde corre o motor: numa página interna escondida** (`eteris://tradutor`), com motor próprio no contexto só em
  memória, como o «Espreitar», e sem acesso à rede (só o esquema `eteris://`). Fica no sandbox do renderer.
  - Não na própria página: a CSP de muitos sites bloqueia WebAssembly e workers injetados, e o JavaScript da página
    veria o motor [não verificado].
  - Não nativo no processo principal: seria mais rápido, mas punha código C++ a ler texto da internet fora do
    sandbox, e acrescentava uma compilação nativa nova.
- **Fluxo:** um script num mundo isolado (CDP, `Browser/DevToolsCalls.cs`) recolhe os blocos de texto da página,
  primeiro os que estão à vista; o .NET passa-os ao motor e devolve as traduções, que substituem os nós de texto (o
  modo HTML do Bergamot mantém negritos e links) [fonte: README do pacote]. Um `MutationObserver` traduz o que vai
  aparecendo. «Mostrar original» repõe tudo. Campos de formulário não se traduzem.
- **Quando aparece:** uma barra discreta «Esta página está em inglês. Traduzir para português?» só quando a língua da
  página (`<html lang>` + deteção) não é a do utilizador e há modelo. Nunca traduz sozinha sem um primeiro «sim» por
  língua («Traduzir sempre inglês»). Na janela privada traduz, mas não guarda escolhas.
- **Modelos (decisão 4):** descarregados a pedido de uma release do Éteris no GitHub. Ficam em `%LocalAppData%`, fora
  do perfil, partilhados pelos perfis. Cada um é conferido antes de cada uso, pelo SHA-256 fixado no código ou por uma
  lista assinada como a das atualizações; um modelo que não confere nunca se carrega. Apagáveis nas Definições.

### Onde vive no código

`src/Eteris.Browser/Translation/` (novo: catálogo e descarregamento dos modelos, motor escondido, script da página),
`MainWindow.Translate.cs` (barra e comandos), `Browser/translate.html` + WebAssembly em `Browser/Vendor/`, Definições
(`Browser/SettingsPage.cs`, `settings.html`), menu da página e paleta. A entrada no menu do navegador fica, também
aqui, para depois de `MainWindow.BrowserMenu.cs` fundir.

### Privacidade

- `eteris://privacy`: linha nova «A tradução corre neste computador: o texto das páginas não sai». Pode ser provada ao
  vivo, como as outras linhas (o motor não tem rede).
- `docs/site/privacidade.html`, secção 4: acrescentar o descarregamento dos modelos (só quando se pede uma língua; quem
  serve o ficheiro vê o endereço IP e a língua pedida).
- Com a decisão 4, os modelos vêm do GitHub, o mesmo sítio das atualizações: nenhum terceiro novo. O balde da Mozilla
  (`storage.googleapis.com`, da Google) só é usado por quem publica, ao preparar a release.

### Plano em tarefas

0. ~~Prova no Éteris~~ — feita (secção «Prova dentro do Éteris»): passa à justa no PC lento simulado; custa ~540 MB
   por motor carregado.
1. **Motor e modelos**: página escondida, catálogo, descarregar e conferir antes de cada uso, apagar; publicar os
   modelos (pt↔en, es↔en) numa release do Éteris, com a nota da licença MPL-2.0; medir a memória de es↔pt pelo inglês
   (decisão 5); `fetch` no esquema; carregar só a pedido e largar o motor sem uso; escolher a origem do WebAssembly (compilá-lo de forma reprodutível com
   Emscripten, ou usar uma compilação publicada pela Mozilla).
2. **Script da página**: recolher, traduzir o que está à vista primeiro, substituir, repor, conteúdo novo.
3. **Barra, deteção da língua, Definições**, menu da página e paleta.
4. **Licenças e privacidade**: `licenses/`, `THIRD-PARTY-NOTICES.md`, `eteris://privacy`, site, CHANGELOG/Novidades.
5. **Testes e prova ao vivo**: unitários; ao vivo com o registo de rede a mostrar zero pedidos durante a tradução.

### Riscos

- **Qualidade abaixo da do Google**, com erros de sentido (exemplo acima) e mistura de pt-PT com pt-BR.
- **Manutenção do motor:** o pacote npm é de 2022; a versão viva do Bergamot está na árvore do Firefox. Os modelos de
  2026 funcionaram com ele [medido], mas uma arquitetura nova de modelo pode exigir atualizar o motor.
- **Sites dinâmicos:** páginas que se redesenham podem desfazer ou baralhar a tradução; mexer no DOM pode partir
  alguns sites.
- **Memória:** ~540 MB por motor carregado [medido]. Num PC com 8 GB isto pesa. Mitigação: um só motor, só a pedido,
  largado sem uso.
- **PCs fracos:** num PC lento simulado, um ecrã leva ~2 s, e ~4 s na 1.ª vez [medido, aproximação]. Num portátil
  fraco real **não foi medido**. Mitigação: o que está à vista primeiro.

### Fica fora

Traduzir o que se escreve num campo, PDF, texto em imagens, voz, escolha pt-PT/pt-BR, línguas fora do catálogo da
Mozilla e **qualquer recurso à nuvem**.

## 3. As lacunas da 0.2.14 que vinham com a Etapa A

As notas do dono juntam à Etapa A duas lacunas medidas na 0.2.14 (`docs/paridade.md`): a página **continua a desenhar
com a janela tapada** por outra, e a **troca para uma aba parada há mais de 8 s** ficou por medir. Não fazem parte
deste estudo: são trabalho de desempenho, com o seu próprio método (`build/perf/README.md`). Uma interação: um recorte
sempre por cima tapa parte da janela principal; enquanto a primeira lacuna estiver aberta, a página por baixo continua
a desenhar, como hoje com qualquer outra janela por cima.

## 4. Decisões

**Estado (2026-11-12):** as decisões 1 a 7 são a **recomendação aceite pela sessão principal a pedido do dono, a
confirmar pelo dono**, com dois acertos à recomendação inicial (3 e 4). A 8 surgiu das provas e foi decidida da mesma
forma (por delegação do dono na sessão principal, a confirmar por ele).

1. **Cookies nas vigias:** **sem** os cookies do utilizador por omissão (visita anónima), com a opção «com a minha
   sessão» por vigia.
2. **Intervalo:** mínimo 30 min, omissão 2 h. **Nada corre com o Éteris fechado** nesta etapa.
3. **Avisos (acerto):** na 1.ª versão, **só dentro do Éteris**: o aviso interno e o piscar na barra de tarefas.
   Nada de ícone na bandeja nem de notificações do Windows.
4. **Origem dos modelos (acerto):** **(b) a pedido, de uma release do Éteris no GitHub.** Cada modelo é **conferido
   antes de ser usado**: com o SHA-256 fixado no código, ou com uma lista assinada como a das atualizações
   (`docs/atualizacoes.md`). **Nunca se carrega um modelo sem conferir.** Ficam de fora as opções (a), dentro do
   instalador, e (c), do balde da Mozilla.
5. **Línguas:** **pt↔en e es↔en** na 1.ª versão, com es↔pt a passar pelo inglês, **se o custo for só mais modelos**;
   senão, só pt↔en. Dados para decidir:
   - es↔en são mais 2 modelos (~52 MB a descarregar, só para quem os pedir).
   - es↔pt carrega dois modelos no mesmo motor e demora o dobro. A memória disso não foi medida (um modelo: ~540 MB).
     Mede-se na tarefa 1 da tradução; se passar muito disso, es↔pt fica fora e es↔en fica.
6. **Português:** o «pt» único (mistura de pt-PT e pt-BR) é aceitável na 1.ª versão, **dito na interface**.
7. **Ordem:** a do roteiro (vigia e recorte primeiro, tradução depois). As duas tarefas 0 já estão feitas (secções
   «Prova»).
8. **Sites que barram visitas automáticas:** nesses sites, a vigia **só lê o preço quando o utilizador abre a página**
   e mostra que esse site não deixa vigiar sozinho. O Éteris **nunca tenta contornar a proteção do site** (CAPTCHA,
   desafios anti-robô, impressões digitais falsas).
