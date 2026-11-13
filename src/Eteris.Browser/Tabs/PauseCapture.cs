namespace Eteris.Browser.Tabs;

// A Pausa (MainWindow.Pause.cs) só cala a saída e esconde as páginas: a câmara, o microfone e o ecrã partilhado de uma
// chamada continuam a ir para o outro lado. O ecrã calmo tapa o indicador da barra de abas, por isso diz quais sites
// podem continuar a captar — «podem»: o motor não avisa quando a captura acaba (CaptureSessions conta até o documento
// mudar). Não corta a captura (a chamada é do utilizador); só não a esconde.
public static class PauseCapture
{
    private const string What = "continuar a usar a câmara, o microfone ou o ecrã";

    // O que o ecrã calmo de outra janela diz de uma janela privada com captura (o site só se nomeia na própria).
    public const string PrivateWindow = "uma janela privada";

    // null: nenhum site a captar. Os sites pela ordem dada, sem repetir (sem distinguir maiúsculas).
    public static string? Notice(IEnumerable<string> sites)
    {
        var names = sites.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count switch
        {
            0 => null,
            1 => $"{names[0]} pode {What}",
            2 => $"{names[0]} e {names[1]} podem {What}",
            _ => $"{names[0]} e mais {names.Count - 1} sites podem {What}",
        };
    }

    // O nome de automação do ecrã calmo: o leitor de ecrã ouve o aviso ao entrar.
    public static string ViewName(string? notice) => notice is null ? "Pausa" : $"Pausa — {notice}";
}
