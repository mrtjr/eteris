namespace Eteris.Browser.Tabs;

// Regras da sessão de várias janelas (MainWindow.Session.cs) que não precisam de janela.
public static class SessionExit
{
    // «Atualizar para…» e o encerramento do Windows congelam a sessão completa antes de fechar as janelas; se o
    // Éteris ainda estiver vivo passado este tempo, o encerramento foi cancelado e a gravação tem de voltar.
    public static readonly TimeSpan UnfreezeAfter = TimeSpan.FromSeconds(10);

    // A janela dona (a 1.ª, cuja posição fica em window.json) vai à frente; as outras pela ordem recebida.
    public static IReadOnlyList<T> Order<T>(IReadOnlyList<T> windows, Func<T, bool> isOwner)
    {
        var owner = -1;
        for (var i = 0; i < windows.Count && owner < 0; i++)
            if (isOwner(windows[i])) owner = i;
        return owner < 0 ? windows : [windows[owner], .. windows.Where((_, i) => i != owner)];
    }
}
