# Patch SÓ de teste, aplicado a uma CÓPIA da árvore (git worktree ou git archive fora do repositório), nunca ao próprio
# repositório: a versão de teste atualiza-se por um feed local (ETERIS_SBXTEST_FEED) em vez do GitHub, e o reinício do
# Update.exe recebe ETERIS_SBXTEST_RESTART_ARGS (separados por «|», por exemplo o --user-data-dir do perfil de teste,
# para a versão nova nunca abrir o perfil do utilizador). O resto do caminho é o do Éteris: CheckAndDownloadAsync no
# arranque, botão «Atualizar para …» (RestartRequested) e ApplyPendingOnExit ao sair. Desde a 0.2.16 o feed local passa
# também pela verificação da assinatura (SignedUpdateSource): o releases.win.json e o releases.win.json.sig vêm da pasta
# do feed, e a chave pública de teste tem de estar no update-keys.txt da cópia (docs/atualizacoes.md, prova manual).
# Uso: python patch-velopack-teste.py <cópia>\src\Eteris.Browser\Updates\UpdateService.cs
# Escrito para a 0.2.9–0.2.12 (provas do Velopack 2 e 3). Se uma versão mudar estas linhas, o script recusa e é preciso
# adaptá-lo antes da prova (e registar a diferença).
import os
import sys

# realpath dá o caminho final (nomes longos em vez dos curtos 8.3, e as maiúsculas do disco) e normcase compara sem
# maiúsculas: «c:\users\…» ou o nome curto do repositório também são o repositório. Noutro disco o commonpath dá
# ValueError, e aí é de certeza uma cópia.
target = os.path.normcase(os.path.realpath(sys.argv[1]))
repo = os.path.normcase(os.path.realpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..")))
try:
    inside = os.path.commonpath([target, repo]) == repo
except ValueError:
    inside = False
if inside:
    sys.exit("recusado: o patch de teste só se aplica a uma cópia fora deste repositório")

s = open(target, encoding="utf-8-sig").read()
old1 = 'try { return new UpdateManager(CreateSource()); }'
new1 = ('try { return Environment.GetEnvironmentVariable("ETERIS_SBXTEST_FEED") is { Length: > 0 } sbxFeed'
        ' ? new UpdateManager(new SignedUpdateSource(new SimpleFileSource(new System.IO.DirectoryInfo(sbxFeed)),'
        ' (name, _) => System.IO.File.ReadAllBytesAsync(System.IO.Path.Combine(sbxFeed, name)), UpdateSignature.EmbeddedKeys))'
        ' : new UpdateManager(CreateSource()); }')
old2 = '_manager.WaitExitThenApplyUpdates(Pending.TargetFullRelease, silent: true, restart: RestartRequested);'
new2 = ('_manager.WaitExitThenApplyUpdates(Pending.TargetFullRelease, silent: true, restart: RestartRequested,'
        ' Environment.GetEnvironmentVariable("ETERIS_SBXTEST_RESTART_ARGS")?.Split(\'|\'));')
if s.count(old1) != 1 or s.count(old2) != 1:
    sys.exit("recusado: o UpdateService.cs desta versão não tem as linhas esperadas; adaptar o patch")
s = s.replace(old1, new1).replace(old2, new2)
open(target, "w", encoding="utf-8").write(s)
print("ok")
