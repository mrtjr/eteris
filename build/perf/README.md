# Medições de desempenho (Éteris contra Chrome, Firefox e Brave)

Ferramentas do plano de desempenho da 0.2.14 (preparação P-0 e fase 0, diagnóstico D0–D11): a bateria padrão, as
linhas de base, o A/A, as passagens com a ordem dos navegadores rodada e a análise por pares com intervalos de
confiança. Vieram das pastas de trabalho das medições da 0.2.3 à 0.2.13, limpas: sem caminhos de nenhuma máquina,
tudo relativo a esta pasta e a uma pasta de trabalho fora do repositório.

> **Corridas pela primeira vez na medição da 0.2.14** (2026-11-09; `docs/paridade.md`, «Medição da 0.2.14»), numa
> cópia de trabalho com correções que ainda não voltaram a esta pasta: a árvore de processos sem PID reutilizados, o
> teste do servidor local, o perfil novo do Firefox e os passos das 20 abas, do bfcache e da pressão de memória. Os
> scripts desta pasta, tal como estão, não são exatamente os que deram esses números.

## Regras (valem para todas as corridas)

- **Nunca a instância nem o perfil do dono.** O Éteris corre num perfil de teste (`--user-data-dir` dentro da pasta de
  trabalho), com `ETERIS_TEST_MUTE=1` (sem som). Chrome e Brave correm numa instância própria com perfil temporário
  (`--user-data-dir`, `--no-first-run --no-default-browser-check --mute-audio`), o Firefox com
  `-no-remote -new-instance -profile <temporário>`; os perfis temporários das referências apagam-se no fim
  (`ApagarPerfil`). Os scripts só tocam em processos com a pasta do perfil de teste na linha de comandos.
- **Mede-se a publicação, não o Debug**: a pasta de saída do `build/publish.ps1`, como cópia portátil. Os interruptores
  `ETERIS_TEST_*` de diagnóstico são **ignorados numa instalação** (há um `Update.exe` do Velopack na pasta de cima),
  para uma variável esquecida nunca mudar o Éteris instalado de ninguém.
- **Nada pede administrador e nada se descarrega.** O `wpr.exe` (rastro do núcleo) e o `powercfg /energy` ficam de fora
  até à decisão D-15; sem eles: o rastro do Chromium (`rastro.ps1`), os contadores do Windows em bruto (CIM) e os
  contadores do Éteris.
- **Máquina sossegada.** Nenhuma compilação (CEF, CefSharp, `build-all`) durante as janelas de medição; sem outras
  janelas de teste abertas; tela a 60 Hz (registada, não se muda). Uma ronda em que o antivírus ou o Windows Update
  passem de 2% de um núcleo fica marcada (`fundo_alto=1`) e a análise deixa-a de fora.
- **`PAUSA-UI`**: um ficheiro com este nome na pasta de trabalho suspende tudo (o dono está a usar o PC). Os scripts
  nunca mudam de área de trabalho nem bloqueiam a sessão: isso é sempre o dono (estado `manual` do `ocl.ps1`).
- Restaurar a área de transferência no fim, se algum passo manual a usar.
- `git grep -n -i "j[uú]nior" -- build/perf` tem de dar vazio.

## Requisitos

Windows PowerShell 5.1 (o do Windows), Python 3 no `PATH` (o servidor das páginas e as análises; o `abas/analisar.py`
precisa também de `numpy` e `Pillow`). Os navegadores de referência nos sítios de origem (Chrome e Brave em
`Program Files`, Firefox em `Program Files` ou o da Microsoft Store); `-Exe` escolhe outro.

## O que há aqui

| Ficheiro | Para quê | Substitui (pastas de trabalho antigas) |
|---|---|---|
| `comum.ps1` | funções comuns: servidor, árvore de processos, lançar navegadores, CPU/PMF/GPU por tipo de processo (CIM em bruto), contadores, CSV | `perf-tools\measure.ps1` (partes), `arranque-tools\common.ps1`, `run-test.ps1`, `mainwin.ps1` |
| `Janela.cs` | janelas e entrada (roda do rato, teclas, cliques) só com a janela de teste à frente | `perf-tools\win.cs`, `keys.ps1`, `click.ps1`, `close.ps1` |
| `servidor.py` | servidor local das páginas (127.0.0.1) e registo dos sinais `/p/…` e `/b?…` | `python -m http.server`, `abas2\srv.py` |
| `paginas\` | páginas de teste (ver abaixo) | `perf-pages\`, `abas\pages\` |
| `medir.ps1` | uma passagem da bateria num braço (B-ARR, B-MEM, B-CPU, B-ENTRADA, B-CARGA) | `perf-tools\measure.ps1`, `breakdown.ps1`, `arranque-tools\final-mem.ps1`, `memab.ps1` |
| `ronda.ps1` + `bracos.exemplo.psd1`, `bracos.aa.exemplo.psd1`, `bracos.fase2.exemplo.psd1` | várias rondas em vários braços, ordem rodada (quadrado latino); o da fase 2 tem um braço por funcionalidade do motor | — (era à mão) |
| `estatistica.py` | pares por ronda: medianas, razão, mediana das diferenças com IC 95% por bootstrap, σd e pares precisos | — |
| `arranque-ab.ps1` + `fases.py` | A/B do arranque intercalado lançamento a lançamento, com as fases do rastro | `arranque-tools\ab.ps1`, `final-startup.ps1`, `phases.py` |
| `filhos.ps1` | hora de criação de cada processo filho, vista de fora (para as referências) | `arranque-tools\children.ps1` |
| `ocl.ps1` | B-OCL (D4) e restauro da janela (D11) | — |
| `ecoqos.ps1` | D7: EcoQoS e prioridades dos processos | — |
| `rastro.ps1` | rastro do Chromium pelo DevTools para o Perfetto UI (D5) e *memory-infra* (D9) | `cdp*.ps1` |
| `contadores.py` | resumo do ficheiro de contadores do Éteris | — |
| `ambiente.ps1` | regista o ambiente da máquina (só leitura) | — |
| `abas\bateria.ps1`, `abas\sessao.ps1`, `abas\Captura.cs`, `abas\analisar.py` | B-ABAS: troca de abas a 60 fps no Éteris, no Chrome e no Brave com os mesmos casos e o mesmo critério | `abas\abasrun.ps1`, `abas\battery.ps1`, `abas\swap.py`, `flash\flashcap.ps1`, e do Brave `brun2.ps1`, `bbattery2.ps1`, `bcap.ps1`, `BraveCap.cs`, `bswap.py`, `blaunch.ps1`, `bclose.ps1` |
| `abas\cpu-escondida.ps1` | CPU de uma aba animada escondida e à vista (E8, P1) | `abas\cpu.ps1` |
| `e3-cache-gpu.ps1` | E3: a cache dos shaders da GPU liga e desliga com o ajuste; a janela privada não escreve na partição do perfil e as caches apagam-se ao sair depois dela | — |

Ficou de fora: o amostrador da thread da interface (`smp.py`), porque o lado do Éteris (`ETERIS_TRACE_SAMPLE`) era de
um build de diagnóstico e não está em nenhum ramo; o A/B do sandbox (`sbx\ab-startup.ps1`), que o plano do sandbox
(P0) traz para o repositório; e os vídeos do B-VID (não há ficheiros de vídeo no repositório e nada se descarrega: usar
vídeos já existentes na máquina, servidos a partir de uma pasta própria).

### Páginas (`paginas\`)

| Página | Cenário |
|---|---|
| `start.html` | B-ARR: sinal `painted` depois de 2 `requestAnimationFrame` |
| `mem.html` | B-MEM: página estática |
| `fps.html`, `scroll.html` | B-CPU: animação de 40 caixas; rolagem automática. `?sinal=0` tira o `fetch` por segundo dos fps (passa pelo tratador de pedidos e pelo serviço de rede); os fps leem-se então do título |
| `dano-zero.html` | B-CPU: `requestAnimationFrame` que não muda nada (E14, `ThrottleRepeatedNoDamageFrames`) |
| `links.html` | B-CPU: ~2000 links; com a roda do rato e o cursor parado, um link novo debaixo do cursor a cada passo (E5) |
| `iframes.html` + `quadro.html` | B-CPU: 6 iframes animados de outros sites (`f1…f6.eteris-teste.example`) |
| `pedidos.html` + `px.svg` | B-CARGA (D10): ~300 subrecursos em 20 nomes (`h01…h20.eteris-teste.example`), 6 ciclos |
| `entrada.html` | B-ENTRADA: latência da entrada até ao fotograma (`quadro`, todas as entradas; `evento`, Event Timing ≥ 16 ms) |
| `ocl.html` | B-OCL: animação, fps e `document.visibilityState` por sinal |
| `fecho.html` | link para o Espreitar (Alt+clique) |
| `webgl.html` | E3: compila um programa WebGL e desenha (sinal `painted`, `v=1` com WebGL) |
| `abas\*.html` | B-ABAS: páginas claras e escuras, animada, vídeo de canvas, painel |

Os nomes `*.eteris-teste.example` vão para 127.0.0.1: no Éteris por `ETERIS_TEST_HOST_RULES`, no Chrome e no Brave por
`--host-resolver-rules`; o Firefox usa `?local=1` (a mesma origem). O Chromium nunca partilha processos entre abas de
`localhost` ou de um IP: para ver a partilha (B-MEM C4L, D6) as páginas têm de vir por estes nomes.

## Interruptores do Éteris para as medições

| Variável | Efeito | Onde |
|---|---|---|
| `ETERIS_TEST_MUTE=1` | sem som nos dois motores (posto sempre pelos scripts; só com `--user-data-dir`) | `Infrastructure/TestSwitches.cs` |
| `ETERIS_TEST_DEVTOOLS_PORT=<porta>` | DevTools do motor em 127.0.0.1 (`rastro.ps1`, `chrome://process-internals`); só com `--user-data-dir` | idem |
| `ETERIS_TEST_PURO=1` ou `=devtools,midia,camada,mica,animacoes` | braço «puro» (D5): sem as camadas do Éteris por cima do motor — sessões DevTools por aba e miniaturas, sondagem da central de multimédia, janela em camadas, Mica, animação eterna do ponto «a tocar» | `Infrastructure/PerfDiagnostics.cs` |
| `ETERIS_TEST_CONTADORES=1` ou `=<caminho>` (`{pid}` trocado) | contadores dos mecanismos, uma linha por segundo: `wpf.lotes` (lotes do WPF ≈ fotogramas do processo principal; prova do E1), `camada.mostrar`/`esconder` e `bolha.mostrar`/`esconder` (E5: com o ajuste e5 a bolha do link deixa de ligar a camada), `janela.escondeu`/`voltou` (E8: páginas escondidas com a janela que não se vê; o e8 está ligado por omissão, e o braço sem ele é o `ocl.ps1` com `-Ambiente ETERIS_TEST_AJUSTES=-e8`), `midia.sondagem` (E4), `sono.pressao` (E13: abas adormecidas por pouca memória), `pedidos`, `pedidos.us`, `pedidos.cancelados` (D10) | `Infrastructure/PerfCounters.cs` |
| `ETERIS_TEST_HOST_RULES=MAP *.eteris-teste.example 127.0.0.1` | `--host-resolver-rules`, só nomes `.example`, `.test`, `.invalid`, `.localhost` para 127.0.0.1 | `Infrastructure/PerfDiagnostics.cs` |
| `ETERIS_TRACE_STARTUP=1` ou `=<caminho>` | (numa instalação, só com `--user-data-dir`) rastro das fases do arranque e da memória; com as linhas `filho` (hora de criação de cada processo filho; o 1.º renderer = `renderer.processo`, D8) | `Infrastructure/StartupTrace.cs`, `StartupTrace.Children.cs` |
| `ETERIS_TRACE_GC_AT=20,60` | GC completo antes das fotografias da memória desses segundos (só conta com o rastro ligado, logo numa instalação só com `--user-data-dir`) | idem |
| `ETERIS_TEST_PRESSAO=1` | pouca memória simulada para o sono por pressão (E13; o e13 está ligado por omissão, e o braço sem ele leva `ETERIS_TEST_AJUSTES=-e13`): uma aba adormece a cada 15 s, a usada há mais tempo entre as deixadas há pelo menos 2 min; a pressão real (~25 GB reservados) só com a decisão D-16 | `Infrastructure/PerfDiagnostics.cs`, `Tabs/MemoryPressure.cs` |
| `ETERIS_TEST_AJUSTES=e3,-e5` (ou `tudo`, `nada`, `motor`, `e14`) | liga e desliga os ajustes de desempenho das fases 1 e 2 (braços A/B com o mesmo binário); os ligados por omissão (e1, e4, e5, e8, e13) desligam-se um a um com o sinal menos (`-e8`); `motor` são os do motor (E7, E14, E15, E16) e `e14` as entradas e14.1…e14.6; o argumento `--eteris-ajustes=…` faz o mesmo e ganha à variável, `--eteris-sem-ajustes-motor` desliga só os do motor e `--eteris-sem-ajustes` desliga todos (os dois valem também numa instalação: são o recuo) | `Infrastructure/PerfTuning.cs` |
| `ETERIS_TEST_ENERGIA=0`, `=1` ou `=3` | com o ajuste e16, o estado da poupança de energia do Chromium escrito no Local State (passo A do E16: 3 contra 0, na tomada); sem a variável, o e16 escreve 1 («com bateria baixa») | `Infrastructure/PerfDiagnostics.cs`, `Infrastructure/EnergySaver.cs` |

`ETERIS_TEST_PURO`, `ETERIS_TEST_CONTADORES`, `ETERIS_TEST_HOST_RULES`, `ETERIS_TEST_PRESSAO`, `ETERIS_TEST_ENERGIA` e
`ETERIS_TEST_AJUSTES` são inertes numa instalação. O contador
`wpf.lotes` ouve o `MediaContext.CommittingBatch` interno do WPF, nunca o `CompositionTarget.Rendering` (um ouvinte
nesse evento obriga o WPF a desenhar todos os fotogramas e estragaria a prova). Se uma versão do WPF não tiver o evento,
o ficheiro diz `wpf.lotes=indisponivel`.

### Ajustes ligados por omissão

| Ajuste | O que faz | Porquê ligado | Braço sem ele |
|---|---|---|---|
| `e1` | o ponto «a tocar» da central de multimédia só anima enquanto se vê | desde a fase 1 | `ETERIS_TEST_AJUSTES=-e1` |
| `e4` | a central de multimédia sonda de 2 em 2 s só as abas à vista, quentes ou com som | desde a fase 1 | `ETERIS_TEST_AJUSTES=-e4` |
| `e5` | a bolha do link numa janela do tamanho do texto, fora da camada do tamanho da janela | desde a fase 1 | `ETERIS_TEST_AJUSTES=-e5` |
| `e8` | a página à vista deixa de desenhar com a janela **minimizada**, noutra área de trabalho ou com a sessão bloqueada; nunca com som, multimédia, câmara/microfone/ecrã ou Picture-in-Picture | D4 medido (página animada, CPU total): minimizada 6,47% → 0,59% de um núcleo; com 20 abas 9,31% → 1,25% | `ETERIS_TEST_AJUSTES=-e8` |
| `e13` | com pouca memória, adormece uma aba de cada vez (a usada há mais tempo, com as isenções) e a rolagem volta ao acordar; a opção «Adormecer também quando falta memória» (Definições › Abas) aparece ligada e, desligada, deixa só o sono por tempo | decisão D-6 (modo equilibrado); com o sinal simulado e 8 abas: 10 → 4 renderers e 404 → 253 MB | `ETERIS_TEST_AJUSTES=-e13` |

O e8 **não resolve a janela tapada por outra**: aí a página continua a desenhar a 60 fotogramas por segundo, com ou sem
ele (8,96% contra 9,39%), enquanto o Chrome, o Firefox e o Brave param. Fica em aberto (o cálculo de oclusão com
histerese do plano). O e13 mediu-se só com o sinal simulado: prova que o mecanismo adormece e liberta memória, não
quando o Windows dá o sinal a sério (D-16). Todos os outros ajustes continuam desligados por omissão.

**O restauro do e8 não foi medido.** O plano só o aceita com «0 fotogramas errados ao restaurar» (B-ABAS ao restaurar)
e pede o diagnóstico D11 (restaurar com 1 e com 20 abas); o B-OCL inclui ainda a outra área de trabalho e a sessão
bloqueada. A medição que o ligou tem só a CPU com a janela à vista, minimizada e tapada: não há nenhuma medição do
restauro, da área de trabalho nem do bloqueio, e o D11 não correu. A revisão do fecho da 0.2.14 corrigiu, por leitura
do código e com testes da regra e do texto (`UnseenWindowTests`), quatro caminhos que **nunca correram na aplicação**:
a janela ativada a partir de outra área de trabalho, a página segurada ou quente ao restaurar, uma disposição com a
janela minimizada e a página que ainda não apresentou. A conferência da versão corrigiu um quinto, da mesma forma (só
leitura e testes): a página cuja espera de apresentação desiste (um 1.º documento que leva mais de 9 s a acabar de
carregar) ficava a desenhar com a janela minimizada até a aba sair e voltar; agora esconde-se quando a espera acaba.
Antes do pacote, com o PC livre e num perfil de teste (cerca de 15 min):

1. minimizar e restaurar com o cursor numa caixa de texto da página e continuar a escrever sem clicar — o foco do
   teclado: quando o WPF devolve o foco, a janela da página ainda está minimizada (o restauro dela vem a seguir, em
   fila, e não repõe o foco);
2. gravar o restauro e contar os fotogramas errados, com 1 e com 20 abas (`ocl.ps1 -Estados minimizada,restauro` e
   `abas\sessao.ps1`);
3. outra área de trabalho, pelo dono (`ocl.ps1 -Estados manual`): voltar com Ctrl+Win+seta (a janela volta sem ser
   ativada) e voltar pelo ícone do Éteris ou por Alt+Tab (a janela é ativada ainda tapada) — a página tem de aparecer
   em menos de meio segundo, sem trocar de aba;
4. bloquear e desbloquear a sessão (Win+L), também pelo dono;
5. um link aberto por outra aplicação com o Éteris minimizado: vê-se a página antiga até a nova aparecer, nunca uma
   área vazia;
6. repetir a CPU da janela minimizada na publicação final (`ocl.ps1 -Estados visivel,minimizada -JanelaS 60`, 2 rondas):
   o e8 foi reescrito depois da medição (os 0,59% são do commit `8046e32`, com o ajuste ligado só no seu braço) e as
   outras cinco provas são funcionais, nenhuma volta a medir a CPU. Minimizada, tem de ficar perto de 0,6%.

Se algum falhar e não houver tempo para o corrigir, o recuo é desligar o e8: `--eteris-ajustes=-e8` na linha de
comandos (vale numa instalação), ou tirá-lo dos ligados por omissão (`OnByDefault` em `Infrastructure/PerfTuning.cs`).
Tirá-lo dos ligados por omissão obriga a tirar, no mesmo commit, o destaque «Janela minimizada não gasta» das Novidades
(`Browser/WhatsNewPage.cs`; o teste `WhatsNewTests.Highlights_AnnounceTheTweaksOnlyWhileTheyAreOnByDefault` falha
enquanto lá estiver) e a linha «Janela minimizada deixa de desenhar» da entrada 0.2.14 do `CHANGELOG.md`; o mesmo vale
para o e13 e o destaque «Abas que adormecem quando falta memória».

## Linhas de base

- **L0** = a 0.2.13 publicada (sem sandbox): `build/publish.ps1` na tag `v0.2.13`. Serve para a comparação com as
  referências e para a discordância dos 19 contra 28 MB por aba.
- **L1** = o ramo da 0.2.14 com o sandbox já fundido (publicação com o sandbox). **É a linha de base de todas as
  experiências.** Na medição da 0.2.14 foi a publicação do commit `8046e32` (o e8 e o e13 ainda desligados por omissão).
- **L1c** = L1 com `--eteris-no-sandbox` (braço com `Extra = @('--eteris-no-sandbox')`): filhos nativos sem sandbox, para
  separar o custo do sandbox e para as verificações que têm de abrir os processos filhos de fora (D7).

Cada braço é uma entrada do `.psd1` do `ronda.ps1` (copiar `bracos.exemplo.psd1` para fora do repositório e pôr os
caminhos das publicações).

## Como medir

### Antes de tudo

1. Publicar L0 (e depois L1) com o `build/publish.ps1` numa pasta própria; nunca instalar para medir.
2. `.\ambiente.ps1 -Eteris <exe L0>,<exe L1>` (o `ronda.ps1` também o corre): adaptadores e frequência da tela, HAGS,
   plano de energia, tomada ou bateria, versões. No 1.º arranque de cada navegador, ver também `chrome://gpu`
   (Éteris, Chrome, Brave) e `about:support` (Firefox) para confirmar o adaptador ativo — a máquina tem três (D2).
3. Um arranque de cada perfil do Éteris antes da 1.ª ronda (cria o perfil; o 1.º lançamento de cada ronda é aquecimento).
4. Nada de outras janelas de teste; nenhuma compilação a correr.

### D0 — ruído da bancada (A/A)

```powershell
Copy-Item build\perf\bracos.aa.exemplo.psd1 $env:TEMP\aa.psd1   # pôr o caminho da L0
.\build\perf\ronda.ps1 -Config $env:TEMP\aa.psd1 -Rondas 10 -Cenarios arranque,memoria,fps,scroll -JanelaS 60
python -I build\perf\estatistica.py $env:TEMP\eteris-perf\resultados\medicoes.csv A1 A2 --delta 0.5,1,2
```

O mesmo binário nos dois braços: o `σd` de cada métrica é o ruído; a coluna «pares» diz quantas rondas são precisas
para ver um efeito δ com 80% de potência (n ≈ 8·(σd/δ)²). Com σd ≈ 1,5 p.p., um efeito de 0,5 p.p. pede ~70 pares,
por isso: janela de 60 s, CPU do **processo afetado** (as métricas `cpu.<exe>:<tipo>`), e o **contador do mecanismo**
como prova principal; a CPU só confirma efeitos ≥ 2·σd. Os objetivos do plano só valem se o D1 der um intervalo que
não inclua 1,0×; senão o objetivo passa a «não piorar».

### D1 — bateria padrão em L0 e nas três referências, na mesma noite

```powershell
.\build\perf\ronda.ps1 -Config <bracos.psd1 com L0, chrome, brave, firefox> -Rondas 5 `
    -Cenarios arranque,memoria,fps,scroll,fps0,scroll0,dano-zero,links -JanelaS 60 -Memoria5min
python -I build\perf\estatistica.py <csv> L0 chrome
```

- **Passagens e rotação.** Cada ronda corre todos os braços; a ordem roda de ronda para ronda (na ronda r começa o
  braço r). A deriva da noite (na 0.2.9 a animação do Éteris subiu 15,3 → 18,5 → 21,7) deixa de cair sempre no mesmo
  navegador. **B-CPU deriva**: um braço com `-Cenarios fps` e `-Rondas 10` seguidas.
- **Chrome e Brave, 1.º e 2.º arranque.** Com `PerfilNovo`, o 1.º lançamento de cada ronda é o 1.º arranque de um perfil
  novo, ainda sem a semente das variações (`pintura_ms.primeiro`); os seguintes já a têm (`pintura_ms`, e os cenários
  de CPU e memória, que vêm depois do arranque). Para a CPU no 1.º arranque (D3), um braço só com `-Cenarios fps`.
- **Memória privada no critério do Chrome.** O número de memória é a **PMF** (*private memory footprint*: o commit
  privado, `PrivateBytes` do contador do Windows — o que o Gestor de tarefas do Chrome mostra), por tipo de processo:
  `c1.15s.pmf.<exe>:<tipo>` (1 aba aos 15 s), `c2.45s.…` (5 abas aos 45 s) e `c2.300s.…` (aos 5 min, depois da purga de
  fundo do Blink). O **processo da GPU fica sempre à parte** (`…pmf.total_sem_gpu`; a GPU é bimodal: 88 contra
  176–222 MB). O conjunto de trabalho privado (`wsp`, a métrica das tabelas antigas) vem ao lado, só para continuidade:
  os 19 MB por aba da 0.2.9 eram `wsp`, não PMF. Memória por aba a mais = (c2 − c1) / 4, na mesma métrica.
- **Repartição por processo** nos quatro navegadores: `cpu.<exe>:<tipo>` (`--type=` do Chromium, `-contentproc` do
  Firefox), mais `cpu.dwm`, `gpu3d.<tipo>` (motores 3D por processo) e `cpu.fundo`.

### D2 e D3 — o que o motor liga (leitura, à mão)

- **D2**: `chrome://gpu` e `chrome://version` no Éteris (L0 e L1), no Chrome e no Brave, lado a lado: «Graphics Feature
  Status», a GPU *ACTIVE*, «Operating system». Os `/prefetch:N` dos filhos aparecem na linha de comandos
  (`Get-CimInstance Win32_Process` filtrado pelo perfil de teste).
- **D3**: no Chrome e no Brave em perfil temporário, 1.º e 2.º arranque: `chrome://version/?show-variations-cmd`.
  Guardar **só os nomes das funcionalidades** (`--enable-features`/`--disable-features`); nada do perfil do dono.

### D4 e D11 — janela que não aparece e restauro

```powershell
.\build\perf\ocl.ps1 -Braco L1 -Navegador eteris -Exe <exe L1> -Estados visivel,minimizada,coberta,restauro
.\build\perf\ocl.ps1 -Braco L1-20 -Navegador eteris -Exe <exe L1> -Abas 20 -Estados minimizada,restauro
.\build\perf\ocl.ps1 -Braco L1 -Navegador eteris -Exe <exe L1> -Estados manual   # outra área de trabalho / Win+L, pelo dono
```

Por estado: CPU por tipo, `dwm`, fps e visibilidade vistos pela página e `contador.wpf.lotes`. Os fotogramas errados ao
restaurar medem-se com `abas\sessao.ps1` (ação `key:` de restaurar).

### D5 — onde está a CPU a mais

- **Braço «puro»**: o mesmo exe com `Ambiente = @('ETERIS_TEST_PURO=1')` contra o normal. Se o «puro» ficar ao nível do
  Chrome, a diferença é das camadas do Éteris; senão é do motor ou do alojamento. Para atribuir, um braço por camada
  (`ETERIS_TEST_PURO=midia`, `=camada`…).
- **Contadores**: o `medir.ps1` liga-os sozinho nos cenários de CPU, entrada e pedidos do Éteris (`-SemContadores`
  tira-os; custam um temporizador de 1 s e um Stopwatch por pedido, e a 0.2.13 não os tem) e junta `contador.<nome>`
  (média por segundo na janela) ao CSV. Em repouso, `contador.wpf.lotes` tem de dar 0
  (hoje espera-se ~60, por causa da animação eterna do E1).
- **Rastro do Chromium**: braço com `Ambiente = @('ETERIS_TEST_DEVTOOLS_PORT=9333')` (Chrome/Brave:
  `Extra = @('--remote-debugging-port=9333')`), e durante a janela de medição
  `.\build\perf\rastro.ps1 -PortaDevTools 9333 -Segundos 10 -Saida <ficheiro>.json`; abrir no Perfetto UI. O `wpr.exe`
  só com a decisão D-15.

### D6 — partilha de processos e o Espreitar

C4/C4L: 4 abas do mesmo site; `chrome://process-internals` (Éteris com `ETERIS_TEST_DEVTOOLS_PORT`, aberto numa aba)
mostra os renderers. Para o C4L, páginas servidas em `a.eteris-teste.example` com `ETERIS_TEST_HOST_RULES` (o «HTTPS
primeiro» do Éteris não sobe endereços com porta, por isso `http://…:18473/` serve). C6: o mesmo com o Espreitar
(`fecho.html`, Alt+clique) aberto e fechado, `medir.ps1`/`Measure-Memoria` antes, durante e 60 s depois.

### D7 — EcoQoS e prioridades

`.\build\perf\ecoqos.ps1 -Braco L1c -Navegador eteris -Exe <exe L1> -Extra --eteris-no-sandbox` (e o mesmo no Chrome):
prioridade, EcoQoS e prioridade de memória de cada processo com a aba animada à vista, escondida e com a janela
minimizada. Em L1c porque o sandbox pode recusar abrir os renderers de fora.

### D8 — caminho crítico do arranque

`.\build\perf\arranque-ab.ps1 -Variantes "L1=<exe L1>" -Rondas 20` e `python -I build\perf\fases.py
"<trabalho>\rastos\arranque-ab-L1-*.tsv"`: a tabela das marcas e, em baixo, a hora de criação de cada processo filho
(`renderer.processo` = o 1.º renderer) contra `cef.navegar` e `aba.1.commit`. Os métodos do JIT: uma variante com
`-Ambiente "L1:DOTNET_JitStdOutFile=<ficheiro>","L1:DOTNET_JitDisasmSummary=1"`. Efeitos de poucos ms (E10, E12)
julgam-se pelas marcas com ≥ 20 rondas, nunca pelo total.

### D9 — memory-infra

Com a porta de depuração (como no D5) e 5 abas de sites diferentes (C3/C3L) abertas:
`.\build\perf\rastro.ps1 -PortaDevTools 9333 -Memoria -Saida <ficheiro>.json` no Éteris, no Chrome e no Brave; abrir no
Perfetto UI (V8, Blink GC, PartitionAlloc, cc, GPU por renderer).

### D10 — custo de cada pedido (B-CARGA)

Cenário `pedidos` do `medir.ps1` (6 ciclos de ~300 subrecursos em 20 nomes): `load_ms.mediana`, CPU do processo
principal (`cpu.Eteris.Browser:principal`) e, no Éteris, `contador.pedidos.us / contador.pedidos` (µs por pedido no
tratador). Escudo desligado: num braço com perfil próprio, desligar uma vez o Escudo para `www.eteris-teste.example`
no popup do Escudo (a decisão é pelo site de topo). Brave com os Escudos ligados e desligados, Chrome tal como vem.

### B-ABAS — troca de abas

```powershell
.\build\perf\abas\bateria.ps1 -Navegador eteris -Exe <exe L1> -Prefixo L1
.\build\perf\abas\bateria.ps1 -Navegador brave -Prefixo brave -Sessoes reais   # WWt, WWn, WG (D-13)
```

Resultados em `resultados\abas\<etiqueta>.total.txt` (linha `TOTAL`: capturas, fotogramas errados por tipo, latência da
tecla à página à vista). Capturas em que a janela perdeu o primeiro plano são descartadas.

## Fase 2 — funcionalidades do motor (E14, E15) e poupança de energia (E16)

A nossa CEF compila com `is_official_build`, que força `disable_fieldtrial_testing_config`: tudo o que o Chrome estável
liga por variações (Finch) fica desligado no Éteris. A fase 2 liga-as **uma de cada vez**, sem recompilar, pela lista
`Shields/EnginePerformance.cs` (o mesmo `Merge` das promessas de `EngineHardening.cs`; o código tem, para cada uma, o
ficheiro e a linha do Chromium 156 e o estudo da `fieldtrial_testing_config.json` de onde vêm os parâmetros). **Todas
estão desligadas por omissão** até ao seu A/B: mudam a configuração do motor antes da 1.ª página (R-2), e as de GPU podem
fazer cair o processo da GPU antes dela (D-18: desde a fusão com o sandbox, a 1.ª falha de um arranque com ajustes antes
da 1.ª página desliga-os nessa versão, `PerfTuning.Suspend` pelo `StartupGuard`, e só a 2.ª desliga o sandbox;
`docs/sandbox.md`, «Rede de segurança do arranque e proxy»). Nada que mude a impressão digital liga por
omissão: o Graphite (e14.6) espera pela D-3 e, ligado, fica ✘ em `eteris://privacy/` («As afinações de desempenho não
mudam o que os sites medem»).

Os nomes verificam-se no `libcef.dll` do NuGet e, quando existe, no da compilação própria
(`build/cef-codecs/out/runtime`) (`EngineFeatureNamesTests`: cada nome é uma `base::Feature`, com o estado por
omissão que o plano conta; as `V8Flag_x` são flags do V8 e procura-se a flag). Cada uma mede-se com um braço próprio
contra L1, por exemplo `@{ Nome = 'L1-e14.1'; Navegador = 'eteris'; Exe = '...'; Ambiente = @('ETERIS_TEST_AJUSTES=e14.1') }`,
e confirma-se em `chrome://gpu` / `chrome://version` (com `ETERIS_TEST_DEVTOOLS_PORT`) que o motor a recebeu.

| Ajuste | Funcionalidade | O que faz | Risco | Como medir (prova principal primeiro) |
|---|---|---|---|---|
| `e14.1` | `ThrottleRepeatedNoDamageFrames` | depois de 360 fotogramas seguidos sem dano, o cc espaça os BeginMainFrame (×2, e ×2 outra vez 60 depois) | a cadência do rAF dessa página desce (como no Chrome com o estudo) | `dano-zero` (deve cair muito no renderer); `fps`, `scroll` sem mudança |
| `e14.2` | `ExternalBeginFrameSourceWinUsesRunOrPostTask` | o vsync corre o BeginFrame logo, sem `PostTask` | baixo: ordem das tarefas do Viz | **latência** (`entrada`, «quadro»), não a CPU (~0, revisão R1) |
| `e14.3` | `SingleVideoFrameRateThrottling` | um vídeo de 30 fps sozinho → composição a 30 Hz | vídeos de cadência irregular; volta a 60 Hz com outro dano | B-VID 30 fps: CPU da GPU e do renderer; `fps` sem mudança |
| `e14.4` | `GpuPersistentCache` + `GpuPersistentCacheMetadata` | cache persistente dos shaders (pasta `GPUPersistentCache`) | dado local, como o E3; **só liga com o e3** (a fábrica dos ficheiros não olha para o `disable-gpu-shader-disk-cache`) | arranque e 1.º segundo de `webgl`/`fps` com a cache quente; `e3-cache-gpu.ps1` para as pastas |
| `e14.5` | `DelegatedCompositing` (`limit_to_ui`, o modo por omissão no Windows) | as superfícies vão como camadas DirectComposition | espera-se 0 ou pior (a raiz de cada aba só tem a página); miniaturas e T1/T6 | B-CPU, `gpu3d` do processo da GPU, `cpu.dwm`; opcional, depois das outras |
| `e14.6` | `SkiaGraphite` (`dawn_skip_validation=true`, `dawn_backend_validation=false`) | o Graphite (Dawn/D3D11) desenha em vez do Ganesh; nas placas Intel decide o `SkiaGraphiteWinIntel`, que não se liga | **muda o canvas** (D-3, workflow do farbling); quedas da GPU por controlador. Sem o e3 o Éteris desliga o `SkiaGraphiteUsePersistentCache` (nada de pipelines no disco). O `dawn_use_persistent_cache` do estudo já não é lido na 156 | por último: B-CPU completo, MotionMark, `chrome://gpu` («Graphite»), WebGL, impressão |
| `e15.1` | `WebGLDiscardBackBuffer` + `OffscreenCanvasPropagateVisibility` | o WebGL escondido larga o back buffer | 1 fotograma em branco ao voltar à aba (T1) | `webgl.html` escondida: `\GPU Process Memory(*)` (Dedicated, Shared) e PMF da GPU; −≥ 4 MB |
| `e15.2` | `KillSpareRenderOnMemoryPressure` | com pressão de memória o renderer de reserva fecha | a navegação seguinte arranca um renderer | só com pressão real (D-16); sem ela, ausência de regressão (D9, 2.ª aba) |
| `e15.3` | `SpareRPHUseCriticalMemoryPressure` | o renderer de reserva só cede à pressão crítica | o mesmo, ao contrário | idem |
| `e15.4` | `PartitionAllocSortActiveSlotSpans` | o PartitionAlloc ordena os blocos ativos ao purgar (o `PartitionAllocMemoryReclaimer` do plano já está ligado na 156; os braços do estudo só diferem nisto) | CPU a mais em cada purga | D9 (PartitionAlloc por renderer) em C3/C5; só se o D9 mostrar peso |
| `e15.5` | `V8Flag_sqrt_allocation_limits` | os limites do heap do V8 crescem pela raiz quadrada (os fatores do estudo são os do código) | mais recolhas de lixo nas páginas grandes | D9 (V8 por renderer), Speedometer; só se o D9 mostrar peso |
| `e16` | `performance_tuning.battery_saver_mode.state` no Local State | a poupança de energia do Chromium: 30 fps, despertares alinhados, sem pré-carregamento | nunca provado na CEF; 30 fps vê-se na rolagem | **passo A**: `ETERIS_TEST_AJUSTES=e16` com `ETERIS_TEST_ENERGIA=3` contra `=0`, na tomada: `fps.html` a 30 contra 60 fps no título e −30% de CPU de desenho |

Critérios (plano): E14 entra se baixar a soma ≥ 0,5 p.p. sem regressão de fps, 0 fotogramas errados (T1/T6, miniaturas,
ecrã dividido) e 0 falhas da GPU num dia de uso; E15 com −≥ 1 MB por renderer ou −≥ 4 MB de GPU por aba WebGL escondida.
Cada uma recua tirando o ajuste (`--eteris-ajustes=-e14.1`), e todas com `--eteris-sem-ajustes-motor`.

O E16 só tem o passo A: se a CEF não cair para 30 fps com o estado 3, a ideia cai e fica o P5 (patch, fase 3). Se cair,
a definição «Poupar energia na bateria» e o padrão vêm com a D-8. Mudar o estado em tempo real (tomada ↔ bateria) pedia o
gestor de preferências globais da CEF, que o CefSharp 156 não expõe (só o `IRequestContext.SetPreference`, do perfil):
fica o valor do arranque. Sem o ajuste, uma chave escrita antes sai do Local State na mesma escrita do DNS cifrado.

## Resultados

Tudo vai para a pasta de trabalho (por omissão `%TEMP%\eteris-perf`; `-Trabalho` muda-a): `resultados\medicoes.csv`
(formato longo: `Ronda, Braco, Cenario, Metrica, Valor, Hora`; `-Resultados` muda-o — o plano guarda-os na pasta de
notas das medições), `resultados\ambiente-*.json`, `rastos\` (rastros e contadores do Éteris), `perfis\`. No fim, as
medianas com o intervalo entram na `docs/paridade.md`, no commit da versão ou antes dele.

**Decide-se pelo intervalo de confiança** (`estatistica.py`) da mediana das diferenças por ronda, nunca por uma mediana
sozinha; cada experiência tem o seu contador do mecanismo como prova principal.
