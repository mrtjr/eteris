namespace Eteris.Browser.Omnibox;

// Texto da bolha «para onde vai este link» (canto inferior esquerdo), como nos outros navegadores.
public static class LinkStatus
{
    public const int MaxLength = 160;

    public static string? Format(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var text = status.Trim();
        // Endereços com %20, %C3%A9… ficam legíveis (sem alterar o destino real).
        try { text = Uri.UnescapeDataString(text); } catch (UriFormatException) { }
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}
