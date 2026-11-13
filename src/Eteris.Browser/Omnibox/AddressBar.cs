using Eteris.Browser.Browser;

namespace Eteris.Browser.Omnibox;

// O que a barra de endereço mostra e se leva cadeado. O endereço «confirmado» é o que o motor já entregou à aba
// (BrowserTab.Address); o «pendente» é o que a pessoa pediu e o motor ainda não confirmou (BrowserTab.PendingAddress).
// Só o confirmado pode dar um ícone de ligação: um endereço escrito e ainda por carregar, texto por enviar ou a
// página de erro do Éteris nunca mostram «Ligação segura».
public static class AddressBar
{
    // O texto: o pedido enquanto não chega, senão o da página.
    public static string Text(string? committed, string? pending) => pending ?? committed ?? string.Empty;

    // null: sem ícone (a escrever, ou nada na barra). Unknown: o glifo neutro.
    public static ConnectionKind? Icon(string? committed, string? pending, bool showsError, bool edited, bool focused, bool empty)
    {
        if (focused || empty) return null;
        if (pending is not null || edited || showsError) return ConnectionKind.Unknown;
        return SiteInfo.Describe(committed ?? string.Empty).Kind;
    }

    // O endereço pedido desiste quando a carga que ele começou acaba sem ter havido commit (204, transferência, Esc, erro
    // de rede). O fim de carga da página anterior (loadStarted ainda falso) não conta.
    public static bool ClearsPending(bool isLoading, bool loadStarted) => !isLoading && loadStarted;
}
