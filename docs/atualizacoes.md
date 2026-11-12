# Atualizações assinadas

Desde a 0.2.16 o Éteris só instala uma atualização que o autor assinou com uma chave que não está no GitHub (achados
INF-02 e CAD-01 da auditoria de 2026-11-10). Antes, quem conseguisse publicar uma release no repositório — um token
roubado, a conta comprometida, um fluxo do CI — executava código em todas as instalações: o pacote só era conferido com
o SHA do `releases.win.json`, que vinha da mesma release.

## O que é assinado

Os bytes do `releases.win.json` da release, tal como o `vpk upload github` o pôs no rascunho. Não é o
`Releases\releases.win.json` que o `vpk pack` deixa na máquina de quem publica (esse lista todas as versões da pasta): o
`vpk upload github` gera outro, só com os pacotes desta versão (o completo e o delta, os `Full` e `Delta` de
`Releases\assets.win.json`), cada um com o seu SHA256, e noutra ordem. Antes de o assinar, o `build/release.ps1` confere
cada entrada com os pacotes desta máquina (ver «Ordem de publicação»). A assinatura vai ao lado, no ficheiro
`releases.win.json.sig`, também como ficheiro da release.

O cliente (`src/Eteris.Browser/Updates/SignedUpdateSource.cs`):

1. Descarrega `releases.win.json` e `releases.win.json.sig` da **última release publicada**
   (`https://github.com/mrtjr/eteris/releases/latest/download/…`, no máximo 1 MB cada).
2. Confere a assinatura com as chaves públicas embutidas. Sem assinatura, com uma assinatura inválida ou sem nenhuma chave
   embutida, a procura falha («Não foi possível procurar atualizações») e nada é descarregado. Sem chaves, nem chega a
   pedir nada à rede.
3. Do feed que a biblioteca (Velopack, `GithubSource`) monta, só passam os pacotes cujo nome consta do índice assinado com
   o mesmo `PackageId`, `Version`, `Type` e `SHA256`; os outros campos (`SHA1`, `Size`, notas) passam a ser os do índice
   assinado. Uma entrada só com SHA1 não serve.
4. Depois de cada descarregamento (completo ou delta), volta a calcular o SHA256 do ficheiro; se não for o do índice
   assinado, apaga-o e a atualização falha.

Repor um índice assinado antigo numa release nova só congela as atualizações (os clientes não veem versões novas); não
instala nada que o autor não tenha assinado.

## Algoritmo e formatos

- ECDSA P-256 com SHA-256.
- Assinatura: `r||s` de 64 bytes (`DSASignatureFormat.IeeeP1363FixedFieldConcatenation`), em Base64, no `.sig`.
- Chave pública: `SubjectPublicKeyInfo` (DER) em Base64, uma por linha, em `src/Eteris.Browser/Updates/update-keys.txt`
  (recurso embutido; as linhas vazias e as que começam por `#` não contam). A 1.ª linha de chave é a de uso, as seguintes
  as de reserva. O cliente aceita uma assinatura de qualquer uma delas.
- Chave privada: PKCS#8 cifrado em PEM (AES-256-CBC, PBKDF2-SHA256 com 600 000 iterações), protegida por uma frase-passe.

O código de verificação é um só (`UpdateSignature.cs`): o Éteris usa-o e a ferramenta `build/sign` compila-o também.

## A ferramenta `build/sign`

```
dotnet run --project build/sign -c Release -- new-key <ficheiro.pem>
dotnet run --project build/sign -c Release -- public <ficheiro.pem>
dotnet run --project build/sign -c Release -- sign <ficheiro.pem> <ficheiro>
dotnet run --project build/sign -c Release -- verify <update-keys.txt> <ficheiro> <ficheiro.sig>
```

- `new-key` recusa um ficheiro que já existe, pede a frase-passe duas vezes (pelo menos 12 carateres) e escreve na saída
  só a linha pública, para colar em `update-keys.txt`.
- `sign` escreve `<ficheiro>.sig`. `verify` usa as chaves do ficheiro que recebe.
- A frase-passe lê-se da consola sem eco, ou de uma linha da entrada quando esta vem redirecionada; nunca de argumentos nem
  de variáveis de ambiente (ficariam no histórico da consola ou num registo).
- Saída: 0 feito ou válido, 1 recusado (frase-passe errada, assinatura inválida, ficheiro que já existe), 2 uso errado.

## Onde vive cada chave

- **Públicas:** no repositório, em `update-keys.txt`. O `build/pack.ps1` recusa empacotar sem nenhuma linha de chave: um
  Éteris publicado assim recusaria todas as atualizações para sempre.
- **Privadas:** nunca no repositório, no GitHub, no CI, numa variável de ambiente persistente nem num registo. A de uso
  num suporte guardado à parte (decisão D1, em baixo); a de reserva noutro sítio físico. Sem a de reserva, perder a de uso
  deixa todas as instalações sem atualizações.

## Ordem de publicação

1. `build\publish.ps1` e `build\pack.ps1` como sempre (`docs/motor.md`, «Como publicar»).
2. `vpk upload github …` **sem** `--publish`: a release fica em rascunho.
3. `build\release.ps1 -Version X.Y.Z -Key <chave.pem>`: recusa enquanto o CI do commit da etiqueta não estiver verde;
   descarrega o `releases.win.json` do rascunho e confere cada entrada com os ficheiros de `Releases\` desta máquina em
   todos os campos em que o Éteris confia — `FileName`, `PackageId`, `Version` (a do `-Version`), `Type`, `SHA256` e
   `Size` — e que as entradas são exatamente os `Full` e `Delta` de `Releases\assets.win.json`, nem a mais nem a menos
   (um token roubado podia ter trocado o índice do rascunho); só então assina os bytes desse índice tal como vieram
   (nunca reescritos em PowerShell), sobe o `.sig`, volta a descarregar o índice e o `.sig`, verifica-os e publica.

A primeira versão com a verificação (a 0.2.16) tem de sair já assinada: é o índice dela que os clientes novos leem na
procura seguinte. Se a última release publicada não tiver um `.sig` válido, todos os clientes desde a 0.2.16 ficam sem
atualizações até sair uma release assinada.

## Rotação

1. Gerar a chave nova e juntar a linha pública dela a `update-keys.txt` numa versão (assinada ainda com a antiga).
2. A partir da versão seguinte, assinar com a nova.
3. Tirar a pública antiga duas versões depois (quem ainda não atualizou continua a aceitar a nova, que já conhece).

Se a chave de uso se perder, assina-se com a de reserva (já embutida) e roda-se a seguir.

## O que não cobre

- O `Setup.exe` que um utilizador novo descarrega do GitHub: não é assinado (decisão D3, Authenticode).
- Uma chave guardada na máquina de publicação não protege se essa máquina estiver comprometida no momento de assinar.
- Na emergência pelo CI (`release.ps1 -IndiceDaRelease`) a assinatura só prova que foi o dono a publicar, não o que o CI
  compilou (decisão D2).

## Prova manual

Feita com uma chave de teste numa pasta temporária, apagada no fim:

```
dotnet run --project build/sign -c Release -- new-key %TEMP%\teste.pem      (a linha pública vai para chaves.txt)
dotnet run --project build/sign -c Release -- sign %TEMP%\teste.pem <cópia do releases.win.json>
dotnet run --project build/sign -c Release -- verify chaves.txt <cópia> <cópia>.sig        → 0
(um byte trocado na cópia)                                                                  → 1
```

A prova do lado do cliente usa uma cópia de teste com `build/sandbox/provas/patch-velopack-teste.py` (feed local em
`ETERIS_SBXTEST_FEED`, que também passa pela verificação) e a chave pública de teste no `update-keys.txt` da cópia: com o
feed assinado aparece «Atualizar para …»; com um byte trocado no `releases.win.json` do feed, ou sem o `.sig`, as
Definições › Sobre dizem «Não foi possível procurar atualizações» e a pasta `packages` da instalação de teste não ganha
nenhum pacote.

## Estado até o dono pôr as chaves

O `update-keys.txt` sai do repositório só com comentários: a geração das chaves é um passo manual do dono (em baixo).
Até lá:

- o Éteris compilado deste repositório arranca normalmente, mas **recusa todas as atualizações** (cada procura falha
  fechada, sem pedir nada à rede, e Definições › Sobre diz «Não foi possível procurar atualizações»);
- o `build/pack.ps1` recusa empacotar (também na emergência pelo CI);
- o `build/release.ps1` recusa publicar (a verificação não tem chaves).

As instalações que já existem (0.2.15 e anteriores) não verificam assinaturas e atualizam para a primeira versão com a
verificação como sempre; é dessa versão em diante que a assinatura passa a ser obrigatória.

## Passos manuais do dono

Nada disto entra no repositório além das linhas públicas.

1. Gerar duas chaves **fora da pasta do repositório**, com frases-passe diferentes:
   `dotnet run --project build/sign -c Release -- new-key <suporte>\eteris-atualizacoes-1.pem` e `…-2.pem`. A 1.ª é a de
   uso; a 2.ª é a de reserva e vai logo para um sítio físico diferente (sem ela, perder a 1.ª deixa todas as instalações
   sem atualizações para sempre).
2. Colar as duas linhas públicas em `src/Eteris.Browser/Updates/update-keys.txt` (a de uso primeiro), correr
   `dotnet test --filter "FullyQualifiedName~UpdateSignatureTests.EmbeddedKeys_AreP256PublicKeys"` e fazer commit
   («Chaves públicas das atualizações (uso e reserva)»).
3. Publicar a primeira versão com a verificação já assinada (`build\release.ps1 -Key …`): é o índice dela que os
   clientes novos leem na procura seguinte.
4. Na conta do GitHub: segundo fator com chave física; regra do ramo `main` (sem force-push nem apagar; verificação
   `build` obrigatória nos pedidos, Dependabot incluído); regra das etiquetas `v*` (sem mover nem apagar); releases
   imutáveis; para o `vpk upload` e o `release.ps1`, um token de acesso fino só com «Contents: read and write» neste
   repositório, em vez do `gh auth token`.

## Decisões por tomar (dono)

Cada uma tem uma recomendação; nenhuma está tomada, e o resto do código não depende delas.

- **D1 — Onde vive a chave privada de uso.** (a) Ficheiro PEM cifrado num suporte amovível, ligado só para assinar:
  custo zero, protege de um token roubado e da conta comprometida; não protege se a máquina estiver comprometida no
  momento de assinar. (b) Dispositivo físico (cartão ou chave PIV): a chave nunca sai do dispositivo; custa o
  dispositivo e exige uma variante do `Signer` sobre a CNG, ainda por fazer. (c) Ficheiro na máquina de publicação: é o
  caso que a revisão do CAD-01 diz que não protege. **Recomendação: (a).** Decisão do dono: *por tomar*.
- **D2 — Emergência pelo CI.** O CI nunca tem a chave: o trabalho `publish-sem-codecs` deixa um rascunho e o dono assina
  e publica com `build\release.ps1 -Key … -IndiceDaRelease` a partir de qualquer máquina com a chave (assina o índice
  que o CI pôs no rascunho: nesse caminho a assinatura só prova que foi o dono a publicar, não o que o CI compilou).
  Alternativa: a chave num segredo do GitHub, que volta a pôr a raiz de confiança na conta. **Recomendação: o CI sem a
  chave, como está implementado.** Decisão do dono: *por tomar*.
- **D3 — Authenticode (custa dinheiro; valores indicativos, a confirmar).** (a) Não assinar: é o estado atual; o
  SmartScreen avisa e o `Setup.exe` que os novos utilizadores descarregam não tem prova de autoria (as atualizações
  ficam cobertas pela assinatura do índice). (b) Programa de assinatura para código aberto (SignPath Foundation): sem
  custo, sujeito a aprovação e às regras do programa. (c) Azure Trusted Signing: mensalidade baixa, exige identidade
  validada e disponibilidade no país do dono. (d) Certificado OV ou EV em dispositivo físico: centenas de euros por ano.
  **Recomendação:** pedir (b); se não for aceite, (c); até lá fica (a). Em qualquer delas, com o sandbox os três binários
  do arranque (`Eteris.Browser.exe`, `chrome_elf.dll`, `Eteris.Browser.dll`) têm de levar o mesmo certificado,
  assinados depois do `brand-bootstrap.ps1` e antes do `vpk pack` (docs/sandbox.md, «Assinatura»). Parte já pronta: o
  `build\pack.ps1 -SignParams "<parâmetros do signtool>"`, que passa `--signParams` ao `vpk pack` só quando é dado.
  Decisão do dono: *por tomar*.
- **D4 — Publicação faseada (ARQ-01).** Publicar primeiro como pré-lançamento (os clientes ignoram-no: a fonte usa
  `GithubSource(RepoUrl, null, false)` e o índice de `releases/latest`) e promover ao fim de 24 h quando a versão mexe
  no cofre, na sincronização ou no sandbox. Implica um `-PreLancamento` no `release.ps1` e um segundo comando para
  promover. **Recomendação: sim, para essas versões.** Decisão do dono: *por tomar* (nada implementado).
- **D5 — D-7 do plano de desempenho (ajuste e10).** Fechada pelo código desta versão: o Velopack nunca aplica no
  arranque; o Éteris aplica ao sair, ou no arranque depois da instância única quando não há outro Éteris da instalação
  a correr nem link nos argumentos. O ajuste e10 saiu. Só falta o dono confirmar.
- **D6 — Sandbox desligado «até à versão seguinte» (INF-01).** As quedas depois de haver conteúdo web já não contam, e a
  sessão volta com as abas a dormir depois de um arranque falhado. Fica por fazer o botão «Voltar a ligar» (plano 2,
  Task 14). Uma nova tentativa automática ao fim de 24 h repetiria dois arranques falhados por dia num computador onde
  o sandbox não funciona mesmo. **Recomendação: ficar pelo botão.** Decisão do dono: *por tomar*.
- **D7 — Frase pública (PRO-09).** docs/site/index.html diz «cada promessa pode ser conferida» e, em inglês, «checks
  every promise». Trocar por «as promessas listadas em eteris://privacy/» é uma mudança de texto público.
  **Recomendação: trocar.** Decisão do dono: *por tomar*.
