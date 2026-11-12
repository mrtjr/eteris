# Atualizações assinadas

Desde a 0.2.16 o Éteris só instala uma atualização que o autor assinou com uma chave que não está no GitHub (achados
INF-02 e CAD-01 da auditoria de 2026-11-10). Antes, quem conseguisse publicar uma release no repositório — um token
roubado, a conta comprometida, um fluxo do CI — executava código em todas as instalações: o pacote só era conferido com
o SHA do `releases.win.json`, que vinha da mesma release.

## O que é assinado

Os bytes do `releases.win.json` da release, tal como o `vpk pack` o deixou em `Releases\` na máquina de quem publica.
Esse índice lista todas as versões (pacotes completos e deltas), cada uma com o seu SHA256. A assinatura vai ao lado,
no ficheiro `releases.win.json.sig`, também como ficheiro da release.

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
3. `build\release.ps1 -Version X.Y.Z -Key <chave.pem>`: recusa enquanto o CI do commit da etiqueta não estiver verde,
   assina o `Releases\releases.win.json` desta máquina, sobe o `.sig`, confere que o índice do rascunho é o mesmo que se
   assinou e só então publica.

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
