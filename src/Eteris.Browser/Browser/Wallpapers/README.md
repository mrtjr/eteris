# Fundos da nova aba

Ficha de origem e termos dos ficheiros desta pasta, que o Éteris embute no executável
(`Eteris.Browser.csproj`) e serve em `eteris://newtab/wallpaper/`. Copiada para `licenses/Wallpapers-NOTICE.txt`
por `build/licenses/collect.py`, para acompanhar a instalação ao lado das outras licenças.

| Ficheiro | Dimensões | Conteúdo |
|---|---|---|
| `coast.jpg` | 1672 × 941 (JPEG) | «Costa Atlântica»: ondas e nuvens sobre uma costa rochosa |
| `mountains.jpg` | 1672 × 941 (JPEG) | «Serra ao amanhecer»: cordilheira ao nascer do sol |
| `coast-live.webm` | 1440 × 810 (WebM/VP9, 10 s, sem som, em ciclo) | versão animada da «Costa Atlântica» (modo «Ao vivo») |

## Origem

Imagens geradas por IA com o ChatGPT (OpenAI) pelo autor do Éteris, em 2026-11-06. Não são fotografias nem vêm de
bancos de imagens. O vídeo foi produzido a partir da imagem gerada e codificado com o FFmpeg (`Lavf58.76.100`),
sem faixa de áudio.

## Termos

- Nos termos de uso da OpenAI, quem gera o resultado é o seu titular, na medida permitida pela lei aplicável
  (https://openai.com/policies/terms-of-use/).
- Imagens geradas por IA podem não ter proteção de direitos de autor em várias jurisdições (por exemplo, o
  Copyright Office dos EUA não regista obras produzidas sem autoria humana). Nesses casos, a licença abaixo
  aplica-se até onde houver direitos a licenciar; onde não os houver, os ficheiros são de uso livre.
- Redistribuem-se com o Éteris sob a **GPL-3.0-or-later** (ficheiro `LICENSE` na raiz do repositório), como o resto
  do projeto. Podem ser copiados, alterados e redistribuídos nos mesmos termos, mantendo esta nota.

Qualquer ficheiro novo nesta pasta tem de ser acrescentado à tabela acima: o teste
`LicenseComplianceTests.EmbeddedMedia_IsListedInTheNotices` expande os recursos embutidos do `.csproj` e falha se
encontrar um ficheiro de média sem registo aqui.
