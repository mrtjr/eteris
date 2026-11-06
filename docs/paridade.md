# Paridade com Chrome, Firefox e Brave

Objetivo: diminuir a distância — ou empatar — com as três referências do Éteris (**Chrome, Firefox e Brave**;
o Edge não é referência) antes de oferecer o navegador a outras pessoas. Cada lacuna tem uma forma de medir e um
critério de «feito»; nada passa a «resolvido» sem número ou teste.

Estado em 2026-11-06, versão 0.2.2.

## Critérios para começar a distribuir

O Éteris pode ser oferecido a outras pessoas quando os pontos marcados **[bloqueia]** estiverem resolvidos.

| # | Lacuna | Chrome | Firefox | Brave | Éteris hoje | Prioridade |
|---|---|---|---|---|---|---|
| 1 | Custo de desenhar as páginas (processador, bateria) | 6,6–6,7% de 1 núcleo | (não instalado) | 4,8–8,0% | 28–36% (cópia por quadro); protótipo em janela própria: 8,4–8,8% | **[bloqueia]** |
| 1b | Abas escondidas continuam a ser desenhadas | param | param | param | ~30% de 1 núcleo por aba animada escondida (protótipo: ~9%) | **[bloqueia]** |
| 2 | Isolamento das páginas (sandbox) | sim | sim | sim | **desligado** (CefSharp não o suporta num executável .NET) | **[bloqueia]** |
| 3 | Atualizações de segurança do motor | automáticas, dias | automáticas, dias | automáticas, dias | Chromium 156 (igual ao Chrome hoje), mas sem processo garantido | **[bloqueia]** |
| 4 | Interface por cima das páginas (avisos, paleta, pesquisa) nas abas do motor de vídeo | — | — | — | escondida pela página | com o #1 |
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

| Página | Éteris 0.2.2 (fora da tela) | Protótipo em janela própria | Chrome 156 | Brave 157 |
|---|---|---|---|---|
| Animação | 28,1% | **8,4%** | 6,7% | 8,0% |
| Rolagem contínua | 35,9% | **8,8%** | 6,6% | 4,8% |

O protótipo (ramo `prototipo/hwndhost`, `CefSharp.Wpf.HwndHost`) troca só o controlo do motor e fica ao nível do
Chrome e do Brave. Custo conhecido: tudo o que o Éteris desenha por cima da página (avisos, paleta, barra de
pesquisa, menus internos) fica escondido pela janela da página — o mesmo que já acontece nas abas do motor de
vídeo (#4). O efeito Mica não pesa (medido com e sem: igual).

**Abas escondidas (#1b).** Com uma segunda aba escondida que tenha animações (a página de boas-vindas), o total
sobe para ~61% na 0.2.2 e ~18% no protótipo: o Éteris continua a desenhar abas que ninguém vê, ao contrário das
três referências. Avisar o motor com `WasHidden` não chega em modo janela (só se aplica fora da tela); a
investigar: ocultação real da janela da aba, ou congelar as abas escondidas sem som (ciclo de vida da página).

Plano:
1. Avisos, paleta, pesquisa na página e restantes painéis em janelas próprias (flutuam por cima de qualquer motor).
2. Migrar o motor principal para janela própria; repetir a medição e confirmar o ganho.
3. Abas escondidas sem custo (#1b): meta ≤ 0,5% de 1 núcleo por aba escondida, como nas referências.

## 2. Sandbox — por investigar

O Chromium isola cada página num processo com permissões mínimas; no Éteris esse isolamento está desligado
(o sandbox do CEF exige que o executável principal seja ligado estaticamente a `cef_sandbox.lib`, impossível num
executável .NET). Caminho a investigar: um lançador nativo (C++) ligado ao sandbox que arranca o .NET por dentro
(`hostfxr`). Critério: `chrome://sandbox` (ou equivalente) mostra os processos das páginas isolados.

## 3. Atualizações do motor

Critério: cada versão nova do CefSharp (que segue o Chrome estável) chega aos utilizadores em ≤ 14 dias, com os
testes a passar. Passos: aviso automático de nova versão do CefSharp no GitHub, publicação pelo fluxo `release.yml`
(corrigido na 0.2.2) e atualização automática do Velopack.

## Restantes

Ordem depois dos três bloqueadores: #5, #6 (medir primeiro com testes públicos de fingerprinting), #9, #7, #8, #10.
