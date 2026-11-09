# Patch SÓ de teste, aplicado a uma CÓPIA da árvore (git worktree ou git archive fora do repositório), nunca ao próprio
# repositório: a versão de teste atualiza-se por um feed local (ETERIS_SBXTEST_FEED) em vez do GitHub, e o reinício do
# Update.exe recebe ETERIS_SBXTEST_RESTART_ARGS (separados por «|», por exemplo o --user-data-dir do perfil de teste,
# para a versão nova nunca abrir o perfil do utilizador). O resto do caminho é o do Éteris: CheckAndDownloadAsync no
# arranque, botão «Atualizar para …» (RestartRequested) e ApplyPendingOnExit ao sair.
# Uso: python patch-velopack-teste.py <cópia>\src\Eteris.Browser\Updates\UpdateService.cs
# Escrito para a 0.2.9–0.2.12 (provas do Velopack 2 e 3). Se uma versão mudar estas linhas, o script recusa e é preciso
# adaptá-lo antes da prova (e registar a diferença).
import os
import sys

target = os.path.abspath(sys.argv[1])
repo = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
if os.path.commonpath([target, repo]) == repo:
    sys.exit("recusado: o patch de teste só se aplica a uma cópia fora deste repositório")

s = open(target, encoding="utf-8-sig").read()
old1 = 'try { manager = new UpdateManager(new GithubSource(RepoUrl, null, false)); }'
new1 = ('try { manager = Environment.GetEnvironmentVariable("ETERIS_SBXTEST_FEED") is { Length: > 0 } sbxFeed'
        ' ? new UpdateManager(new SimpleFileSource(new System.IO.DirectoryInfo(sbxFeed)))'
        ' : new UpdateManager(new GithubSource(RepoUrl, null, false)); }')
old2 = '_manager.WaitExitThenApplyUpdates(Pending.TargetFullRelease, silent: true, restart: RestartRequested);'
new2 = ('_manager.WaitExitThenApplyUpdates(Pending.TargetFullRelease, silent: true, restart: RestartRequested,'
        ' Environment.GetEnvironmentVariable("ETERIS_SBXTEST_RESTART_ARGS")?.Split(\'|\'));')
if s.count(old1) != 1 or s.count(old2) != 1:
    sys.exit("recusado: o UpdateService.cs desta versão não tem as linhas esperadas; adaptar o patch")
s = s.replace(old1, new1).replace(old2, new2)
open(target, "w", encoding="utf-8").write(s)
print("ok")
