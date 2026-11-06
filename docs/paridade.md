# Paridade com Chrome, Firefox e Brave

Objetivo: diminuir a distância — ou empatar — com as três referências do Éteris (**Chrome, Firefox e Brave**;
o Edge não é referência) antes de oferecer o navegador a outras pessoas. Cada lacuna tem uma forma de medir e um
critério de «feito»; nada passa a «resolvido» sem número ou teste.

Estado em 2026-11-06, versão 0.2.2.

## Critérios para começar a distribuir

O Éteris pode ser oferecido a outras pessoas quando os pontos marcados **[bloqueia]** estiverem resolvidos.

| # | Lacuna | Chrome | Firefox | Brave | Éteris hoje | Prioridade |
|---|---|---|---|---|---|---|
| 1 | Custo de desenhar as páginas (processador, bateria) | 6,6–6,7% de 1 núcleo | 21,7–28,3% | 4,8–8,0% | **8–11%** no `main` (era 28–36% na 0.2.2) | ✅ resolvido (por publicar) |
| 1b | Abas escondidas continuam a ser desenhadas | param | param | param | **param** no `main` (era ~30% por aba animada na 0.2.2) | ✅ resolvido (por publicar) |
| 2 | Isolamento das páginas (sandbox) | sim | sim | sim | **desligado** (`--no-sandbox` nos 6 processos); viável em 1–2 semanas, ver `docs/sandbox.md` | **[bloqueia]** |
| 3 | Atualizações de segurança do motor | automáticas, dias | automáticas, dias | automáticas, dias | Chromium 156 (igual ao Chrome hoje); Dependabot diário + atualização automática dos utilizadores | ✅ processo montado (falta a 1.ª atualização real) |
| 4 | Interface por cima das páginas (avisos, paleta, pesquisa) | — | — | — | camada numa janela própria, por cima de qualquer motor | ✅ resolvido (por publicar) |
| 5 | Abas do motor de vídeo completas (Ctrl+F, zoom, imprimir, leitura) | — | — | — | em falta | alta |
| 6 | Antifingerprinting | médio | forte (FPP/RFP) | forte («farbling») | por medir | alta |
| 7 | Conteúdo com DRM (Netflix, Spotify) | sim | opcional | opcional | não (Widevine removido por privacidade) | média |
| 8 | Extensões | sim | sim | sim | não | média |
| 9 | Tradução de páginas | sim (servidor) | sim (local) | sim | não (planeada: local) | média |
| 10 | Sincronização entre aparelhos | sim | sim | sim | não | baixa |

## 1. Desenho das páginas — medido

Medição (2026-11-06): páginas locais `fps.html` (animação) e `scroll.html` (rolagem contínua), uma só aba,
janela 1360×860, 10 s, soma do processador de todos os processos do navegador em percentagem de 1 núcleo; todas
a 60 fps (confirmado pelo título da página). Éteris em build Release; Chrome e Brave com perfis temporários,
apagados no fim. Scripts: `perf-render.ps1` (Éteris) e `perf-browser.ps1` (referências).

| Página | Éteris 0.2.2 (fora da tela) | Protótipo em janela própria | Éteris 0.2.3 | Chrome 156 | Brave 157 | Firefox 159 |
|---|---|---|---|---|---|---|
| Animação | 28,1% | 8,4% | **11,1%** | 6,7% | 8,0% | 28,3% |
| Rolagem contínua | 35,9% | 8,8% | **8,4%** | 6,6% | 4,8% | 21,7% |
| Animação + 1 aba escondida animada | 60,9% | 18,0% | **10,5%** | — | — | — |
| Rolagem + 1 aba escondida animada | 60,0% | 15,5% | **8,0%** | — | — | — |

Firefox 159.0.1 (versão da Microsoft Store) medido com `perf-firefox.ps1`: perfil temporário próprio
(`-no-remote -new-instance`, sem tocar no Firefox aberto), sem ecrãs de primeira utilização, janela 1360×860,
20 s de espera antes de medir. Num perfil novo o Firefox pode ainda ter tarefas de fundo (ex.: listas de
navegação segura), por isso o valor pode estar um pouco acima do uso normal.

O protótipo (ramo `prototipo/hwndhost`, `CefSharp.Wpf.HwndHost`) troca só o controlo do motor e fica ao nível do
Chrome e do Brave. Custo conhecido: tudo o que o Éteris desenha por cima da página (avisos, paleta, barra de
pesquisa, menus internos) fica escondido pela janela da página — o mesmo que já acontece nas abas do motor de
vídeo (#4). O efeito Mica não pesa (medido com e sem: igual).

**Abas escondidas (#1b).** Em modo janela o CEF não deixa dizer ao Chromium que uma aba deixou de se ver: não
resultaram `WasHidden`, esconder a janela do CEF ou a do Chromium, o rastreio de oclusão nem o estilo Chrome
(medido com `vis.html`, que mostra no título `document.visibilityState` e os quadros por segundo). Congelar a
página (`Page.setWebLifecycleState`) parava o custo, mas a página voltava «hidden» e sem desenhar. O que resulta:
**minimizar a janela do Chromium** da aba escondida (`MainWindow.PageVisibility.cs`) — a página passa a «hidden»,
deixa de desenhar e os temporizadores abrandam mas continuam (como nas abas de fundo do Chrome); ao restaurar,
volta «visible» e é redimensionada para a área da aba.

Feito (no `main`, por publicar):
1. Avisos, paleta, pesquisa na página, notas, permissões, código QR, minibarra, bolha do link, folha de atalhos e
   barra de carregamento numa janela transparente presa à principal (`MainWindow.Overlay.cs`), visível só enquanto
   algum deles está à vista.
2. Motor principal em janela própria; atalhos do Éteris com o foco na página (`ShortcutKeyboardHandler`) e metade
   ativa do ecrã dividido pelo foco (`PageFocusHandler`).
3. Abas escondidas sem custo.

Pendentes desta migração:
- Ctrl+roda do rato: o zoom é feito pelo próprio Chromium (lembrado por site, como no Chrome), mas o selo de zoom
  do Éteris na barra de endereço não o acompanha.
- Por cima da paleta e da folha de atalhos a página fica escurecida, mas já não desfocada.
- O contorno da metade ativa no ecrã dividido e os cantos arredondados da página ficam por baixo da página.

## 2. Sandbox — investigado: viável, 1–2 semanas

Os 6 processos do motor correm com `--no-sandbox`. Desde o CEF 138 o sandbox no Windows exige o «bootstrap» (a
aplicação arranca como DLL nativa que recebe a informação do sandbox); o CefSharp não deixa passar essa informação
e os processos das páginas não podem carregar o .NET dentro do sandbox. Plano em 5 passos (A–E) e custos em
`docs/sandbox.md`. Critério: `chrome://sandbox` mostra os processos das páginas isolados.

## 3. Atualizações do motor

Critério: cada versão nova do CefSharp (que segue o Chrome estável) chega aos utilizadores em ≤ 14 dias, com os
testes a passar.

Processo (montado em 2026-11-06):
1. **Aviso automático.** `.github/dependabot.yml` procura todos os dias versões novas do CefSharp (os pacotes do
   motor sobem juntos) e abre um pedido de alteração; o fluxo `build` corre os testes nesse pedido. Também vigia os
   restantes pacotes NuGet, os crates do escudo e as ações do GitHub.
2. **Licenças.** `licenses/INDEX.md` regista a versão do CefSharp e do CEF; o teste
   `ChromiumCredits_WereGeneratedForTheCurrentEngine` falha enquanto as licenças não forem geradas para o motor novo
   (`python build/licenses/collect.py`, que extrai os créditos do Chromium da versão nova).
3. **Verificar ao vivo** com o perfil de teste (abrir páginas, medir com `perf-render.ps1`).
4. **Publicar.** Subir a versão (csproj, `build/publish.ps1`, CHANGELOG), tag `v*` → o fluxo `release.yml` publica a
   Release e o Velopack atualiza os utilizadores sozinho (pacote delta).

O que falta para o ✅ definitivo: fazer uma atualização real do motor de ponta a ponta e medir o tempo total.

## Restantes

Ordem depois dos três bloqueadores: #5, #6 (medir primeiro com testes públicos de fingerprinting), #9, #7, #8, #10.
