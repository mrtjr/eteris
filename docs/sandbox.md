# Sandbox no Éteris — investigação (2026-11-06)

Pergunta: dá para isolar os processos das páginas como no Chrome, Firefox e Brave? **Resposta: dá, mas é trabalho
de 1–2 semanas e obriga a alterar o CefSharp.** Ver o plano em baixo.

## Estado atual (medido)

Os 6 processos do motor do Éteris (páginas, placa gráfica, utilitários) correm com `--no-sandbox`. Uma falha
explorável no código de uma página fica com os mesmos acessos que o utilizador. Nas três referências esses
processos correm fechados (sem acesso a ficheiros nem ao sistema).

## Como o sandbox funciona hoje no CEF (Windows)

- Desde o CEF 138, a única forma suportada é o **bootstrap**: um executável do CEF (`bootstrap.exe`, renomeado
  para o nome da aplicação) arranca a aplicação como uma **DLL nativa** que exporta `RunWinMain` e entrega-lhe a
  informação do sandbox. A biblioteca `cef_sandbox.lib` (o modelo antigo) deixou de ser distribuída.
  Interface: [`cef_sandbox_win.h`](https://github.com/chromiumembedded/cef/blob/master/include/cef_sandbox_win.h).
- Essa informação tem de chegar ao `CefInitialize` no processo principal (é ele que cria os processos fechados) e ao
  `CefExecuteProcess` em cada processo filho, lançados também através do bootstrap.
- O bootstrap e a `libcef.dll` têm de ser da mesma versão; o bootstrap vem na distribuição binária completa do CEF
  (não no pacote NuGet). A assinatura de código fica a cargo da aplicação.
- Outros projetos fizeram esta migração recentemente: [Tauri](https://github.com/tauri-apps/tauri/pull/16431),
  [waterui](https://github.com/water-rs/waterui/issues/897).

## Porque é difícil no Éteris

1. **O CefSharp não deixa passar a informação do sandbox** ao `CefInitialize` (não há nenhuma API para isso; o
   código C++/CLI chama-o com `nullptr`). É preciso alterar o CefSharp: versão própria compilada a partir do código
   (BSD) ou contribuição aceite no projeto oficial.
2. **Os processos das páginas não podem carregar o .NET dentro do sandbox.** O processo filho do CefSharp
   (`CefSharp.BrowserSubprocess`) é .NET; com sandbox, os filhos têm de ser nativos (só CEF). O que o Éteris usa
   desse processo filho deixa de funcionar:
   - 19 chamadas de JavaScript (`EvaluateScriptAsync`, `ExecuteScriptAsync`, `EvaluateScriptAsPromiseAsync`,
     `ExecuteJavaScriptAsync`) em 10 ficheiros;
   - `CosmeticFilterHandler.OnContextCreated` (filtros cosméticos).
   Tudo isto pode ser feito pelo DevTools do lado do navegador (`Runtime.evaluate`,
   `Page.addScriptToEvaluateOnNewDocument`), como os scriptlets já fazem.

## Plano proposto (por ordem, cada passo verificável)

| Passo | O quê | Custo | Verificação |
|---|---|---|---|
| A | Trocar as 19 chamadas e o `OnContextCreated` por DevTools (útil já: menos dependência do processo filho do CefSharp) | 1–2 dias | testes + funcionalidades ao vivo |
| B | Prova: Éteris a correr com um processo filho nativo mínimo (só CEF, ainda sem sandbox) | 1 dia | tudo funciona sem o `CefSharp.BrowserSubprocess` |
| C | CefSharp com a informação do sandbox no `Cef.Initialize` (versão própria; propor ao projeto oficial) | 2–3 dias | compila e arranca |
| D | DLL nativa `RunWinMain` + bootstrap: no processo principal arranca o .NET (`hostfxr`) e passa a informação; nos filhos chama `CefExecuteProcess` | 2 dias | `chrome://sandbox` mostra os processos isolados |
| E | Empacotamento (Velopack, licenças, assinatura) | 1 dia | instalador funciona |

Custo contínuo: enquanto o CefSharp oficial não aceitar a alteração do passo C, cada atualização do motor (ver
`docs/paridade.md`, #3) obriga a recompilar a versão própria do CefSharp.

## Alternativas consideradas

- **Ficar sem sandbox:** inaceitável para distribuir (é a maior diferença de segurança face às referências).
- **Carregar o .NET dentro dos processos fechados:** o sandbox bloqueia o acesso a ficheiros de que o .NET precisa.
- **Trocar o CefSharp por outra camada sobre o CEF:** reescrita muito maior do que os passos A–E.
