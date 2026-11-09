# Medições de desempenho (Éteris contra Chrome, Firefox e Brave)

Ferramentas do plano de desempenho da 0.2.14 (preparação P-0 e fase 0, diagnóstico D0–D11): a bateria padrão, as
linhas de base, o A/A, as passagens com a ordem dos navegadores rodada e a análise por pares com intervalos de
confiança. Vieram das pastas de trabalho das medições da 0.2.3 à 0.2.13, limpas: sem caminhos de nenhuma máquina,
tudo relativo a esta pasta e a uma pasta de trabalho fora do repositório.

> **Ainda não foram corridas nesta forma.** Os scripts foram reescritos a partir dos que deram os números da
> `docs/paridade.md` e do `docs/sandbox.md`, mas a primeira corrida (e os acertos que ela pedir) só acontece depois da
> 0.2.13 sair. Até lá nada aqui é prova de nada.

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
| `ronda.ps1` + `bracos.exemplo.psd1`, `bracos.aa.exemplo.psd1` | várias rondas em vários braços, ordem rodada (quadrado latino) | — (era à mão) |
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
| `ETERIS_TEST_MUTE=1` | sem som nos dois motores (posto sempre pelos scripts) | `Infrastructure/TestSwitches.cs` |
| `ETERIS_TEST_DEVTOOLS_PORT=<porta>` | DevTools do motor em 127.0.0.1 (`rastro.ps1`, `chrome://process-internals`) | idem |
| `ETERIS_TEST_PURO=1` ou `=devtools,midia,camada,mica,animacoes` | braço «puro» (D5): sem as camadas do Éteris por cima do motor — sessões DevTools por aba e miniaturas, sondagem da central de multimédia, janela em camadas, Mica, animação eterna do ponto «a tocar» | `Infrastructure/PerfDiagnostics.cs` |
| `ETERIS_TEST_CONTADORES=1` ou `=<caminho>` (`{pid}` trocado) | contadores dos mecanismos, uma linha por segundo: `wpf.lotes` (lotes do WPF ≈ fotogramas do processo principal; prova do E1), `camada.mostrar`/`esconder` e `bolha.mostrar`/`esconder` (E5: com o ajuste e5 a bolha do link deixa de ligar a camada), `janela.escondeu`/`voltou` (E8: páginas escondidas com a janela que não se vê; no `ocl.ps1` com `-Ambiente ETERIS_TEST_AJUSTES=e8`), `midia.sondagem` (E4), `sono.pressao` (E13: abas adormecidas por pouca memória), `pedidos`, `pedidos.us`, `pedidos.cancelados` (D10) | `Infrastructure/PerfCounters.cs` |
| `ETERIS_TEST_HOST_RULES=MAP *.eteris-teste.example 127.0.0.1` | `--host-resolver-rules`, só nomes `.example`, `.test`, `.invalid`, `.localhost` para 127.0.0.1 | `Infrastructure/PerfDiagnostics.cs` |
| `ETERIS_TRACE_STARTUP=1` ou `=<caminho>` | rastro das fases do arranque e da memória; com as linhas `filho` (hora de criação de cada processo filho; o 1.º renderer = `renderer.processo`, D8) | `Infrastructure/StartupTrace.cs`, `StartupTrace.Children.cs` |
| `ETERIS_TRACE_GC_AT=20,60` | GC completo antes das fotografias da memória desses segundos | idem |
| `ETERIS_TEST_PRESSAO=1` | pouca memória simulada para o sono por pressão (E13, com `ETERIS_TEST_AJUSTES=e13`): uma aba adormece a cada 15 s, a usada há mais tempo; a pressão real (~25 GB reservados) só com a decisão D-16 | `Infrastructure/PerfDiagnostics.cs`, `Tabs/MemoryPressure.cs` |
| `ETERIS_TEST_AJUSTES=e3,-e5` (ou `tudo`, `nada`) | liga e desliga os ajustes de desempenho da fase 1 (braços A/B com o mesmo binário); o argumento `--eteris-ajustes=…` faz o mesmo e ganha à variável, e `--eteris-sem-ajustes` desliga todos (também numa instalação: é o recuo) | `Infrastructure/PerfTuning.cs` |

`ETERIS_TEST_PURO`, `ETERIS_TEST_CONTADORES`, `ETERIS_TEST_HOST_RULES`, `ETERIS_TEST_PRESSAO` e `ETERIS_TEST_AJUSTES` são inertes numa
instalação. O contador
`wpf.lotes` ouve o `MediaContext.CommittingBatch` interno do WPF, nunca o `CompositionTarget.Rendering` (um ouvinte
nesse evento obriga o WPF a desenhar todos os fotogramas e estragaria a prova). Se uma versão do WPF não tiver o evento,
o ficheiro diz `wpf.lotes=indisponivel`.

## Linhas de base

- **L0** = a 0.2.13 publicada (sem sandbox): `build/publish.ps1` na tag `v0.2.13`. Serve para a comparação com as
  referências e para a discordância dos 19 contra 28 MB por aba.
- **L1** = o ramo da 0.2.14 com o sandbox já fundido (publicação com o sandbox). **É a linha de base de todas as
  experiências.** Este ramo ainda não tem o sandbox fundido: a L1 mede-se depois dessa fusão.
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

## Resultados

Tudo vai para a pasta de trabalho (por omissão `%TEMP%\eteris-perf`; `-Trabalho` muda-a): `resultados\medicoes.csv`
(formato longo: `Ronda, Braco, Cenario, Metrica, Valor, Hora`; `-Resultados` muda-o — o plano guarda-os na pasta de
notas das medições), `resultados\ambiente-*.json`, `rastos\` (rastros e contadores do Éteris), `perfis\`. No fim, as
medianas com o intervalo entram na `docs/paridade.md`, no commit da versão ou antes dele.

**Decide-se pelo intervalo de confiança** (`estatistica.py`) da mediana das diferenças por ronda, nunca por uma mediana
sozinha; cada experiência tem o seu contador do mecanismo como prova principal.
