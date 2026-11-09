# Ferramentas das provas ao vivo do sandbox

Os ajudantes das provas ao vivo do sandbox (`docs/sandbox.md`), que antes viviam numa pasta temporária e se perdiam
entre sessões. Servem as provas da 0.2.14: regressão ao vivo, medição A/B/C e 4.ª prova do Velopack. Nenhum destes
scripts entra na publicação nem corre no CI.

Regras de todas as provas:
- **Perfil de teste**, sempre: `--user-data-dir=<Work>\profile-*`. Nunca o perfil, a instalação ou a versão de teste do
  utilizador, nem os navegadores dele.
- **Pasta de trabalho** (`<Work>`): `-Work <pasta>`, ou a variável `ETERIS_PROVAS`, ou `%TEMP%\eteris-provas`. Guarda
  os perfis, as capturas e o `PAUSA-UI`. Nenhum caminho com o nome do utilizador fica nos scripts.
- **`<Work>\PAUSA-UI`**: o PC está em uso. Os scripts que abrem janelas ou enviam teclas saem com o código 3 e a
  verificação fica pendente.
- Silêncio (`ETERIS_TEST_MUTE=1`), fecho por `WM_CLOSE` ao PID de teste, e teclas só com o Éteris de teste em primeiro
  plano. O PIN do Windows Hello e as palavras-passe são do utilizador: nenhum script os escreve.

| Script | Para quê |
|---|---|
| `comum.ps1` | Pasta de trabalho, `PAUSA-UI` e a fila de instâncias de teste (lido pelos outros) |
| `run-test.ps1 -Exe <exe> [-Url …] [-Out x.png] [-ExtraArgs …]` | Abre o Éteris num perfil de teste, espera, captura e fecha; espera pela vez se outra prova estiver aberta |
| `mainwin.ps1 -ProcessId <pid>` | A janela principal do processo (ignora a camada transparente) |
| `close.ps1 -ProcessId <pid>` | Fecho normal (`WM_CLOSE`) e espera até 10 s |
| `keys.ps1 -ProcessId <pid> -Keys …` | Teclas com guarda de primeiro plano (pára se outra aplicação estiver à frente) |
| `shot.ps1 -ProcessId <pid> -Out x.png [-Screen]` | Captura da janela (`PrintWindow`) ou da área do ecrã |
| `ab-startup.ps1 -A <exe> -B <exe> [-C <exe>] [-CArg …] [-Csv …]` | Medição intercalada do arranque (frio e 2.º arranque), por braços: A = versão anterior, B = candidata com sandbox, C = a mesma com `--eteris-no-sandbox` |
| `vpk-snapshot.ps1 -Out <ficheiro> [-PackId …]` | Fotografia do que uma instalação de teste do Velopack pode tocar: atalhos (também os afixados na barra de tarefas), desinstalação, registo do navegador, `UserChoice`, perfis `cr.sb.*` de AppContainer e a pasta do pacote |
| `patch-velopack-teste.py <cópia>\src\Eteris.Browser\Updates\UpdateService.cs` | Patch só de teste, numa cópia fora do repositório (recusa o próprio repositório): feed local `ETERIS_SBXTEST_FEED` e argumentos do reinício `ETERIS_SBXTEST_RESTART_ARGS` |

Ver também, em `build/sandbox`: `sandbox-check.ps1` (o sandbox de cada filho), `network-sandbox-acl.ps1` (a ACL do
serviço de rede numa cópia portátil, e `-Check`) e `appcontainer-profiles.ps1` (`-Remove` no fim de uma prova).
