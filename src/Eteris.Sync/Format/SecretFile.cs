namespace Eteris.Sync.Format;

// Apagar do disco um ficheiro com segredos (o estado da sincronização, com a chave da conta; os tokens da Google) de
// maneira que o que a pessoa mandou apagar nunca volte a ser lido — mesmo quando o Windows não deixa apagar o ficheiro
// agora (preso por outro processo, sem permissão).
//
// Quando o apagar falha, fica uma marca ao lado do ficheiro («<ficheiro>.apagar», vazia) e tenta-se esvaziá-lo no sítio.
// Com a marca, quem lê acaba primeiro o apagar e não lê nada (MayRead), e quem grava conteúdo novo tira antes o antigo
// (BeforeWrite). Sem isto, um «Sair» com «apagar a chave» que encontrasse o ficheiro preso deixava a chave no disco e a
// abertura seguinte retomava a sincronização com ela, sem aviso.
public static class SecretFile
{
    public static string PendingDeletePath(string path) => path + ".apagar";

    // Pode ler-se o ficheiro? Com um apagar por acabar, acaba-o agora; enquanto não se conseguir, não há conteúdo.
    public static bool MayRead(string path)
    {
        var mark = PendingDeletePath(path);
        if (!File.Exists(mark)) return true;
        if (!TryDelete(path)) return false;
        TryDelete(mark);
        return true;
    }

    // Antes de gravar conteúdo novo: um apagar por acabar faz-se primeiro (o conteúdo antigo não fica por baixo).
    public static void BeforeWrite(string path)
    {
        var mark = PendingDeletePath(path);
        if (File.Exists(mark) && TryDelete(path)) TryDelete(mark);
    }

    // Depois de o conteúdo novo estar no sítio: a marca de um apagar antigo que só agora deixou de valer sai.
    public static void AfterWrite(string path)
    {
        var mark = PendingDeletePath(path);
        if (File.Exists(mark)) TryDelete(mark);
    }

    // Apaga o ficheiro e o temporário de uma gravação interrompida. Se o ficheiro não sair: deixa a marca e esvazia-o no
    // sítio (o que o outro processo deixar). Só lança (IOException) quando nem a marca nem esvaziar foram possíveis —
    // aí o conteúdo antigo pode voltar a ser lido, e quem chamou tem de o dizer.
    public static void Delete(string path)
    {
        TryDelete(path + ".tmp");
        if (TryDelete(path))
        {
            TryDelete(PendingDeletePath(path));
            return;
        }
        var marked = TryWriteEmpty(PendingDeletePath(path), FileMode.Create);
        var emptied = TryWriteEmpty(path, FileMode.Truncate);
        if (!marked && !emptied) throw new IOException("O ficheiro não se apagou e não foi possível marcá-lo para apagar.");
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool TryWriteEmpty(string path, FileMode mode)
    {
        try
        {
            using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
