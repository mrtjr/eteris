namespace Eteris.Browser.Tabs;

// «Terminar sessão privada»: o texto da confirmação e do fim (a ação fecha as janelas privadas, e com cada uma vai o
// contexto em memória — cookies, cache, armazenamento —, as permissões dadas lá, que também só vivem em memória, e as
// autorizações de http:// dadas lá).
public static class PrivateSession
{
    public static string Confirmation(int windows, int tabs)
    {
        var what = windows == 1 ? "a janela privada" : $"as {windows} janelas privadas";
        var count = tabs switch
        {
            0 => string.Empty,
            1 => " (1 aba)",
            _ => $" ({tabs} abas)",
        };
        return $"Terminar a sessão privada? Fecha {what}{count} e apaga os cookies, a cache, o armazenamento e as permissões {(windows == 1 ? "dela" : "delas")}.";
    }

    public static string Done(int windows)
        => windows == 1 ? "Sessão privada terminada — a janela privada foi fechada e nada ficou"
                        : $"Sessão privada terminada — {windows} janelas privadas fechadas e nada ficou";
}
