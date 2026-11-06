using System.IO;
using System.Text.Json;

namespace Eteris.Browser.Browser;

public sealed record Highlight(string Icon, string Title, string Detail, string? Keys = null);

// eteris://whatsnew — destaques da versão, mostrados uma vez depois de cada atualização.
public static class WhatsNewPage
{
    public const string Url = "eteris://whatsnew/";

    public static readonly IReadOnlyList<Highlight> Highlights =
    [
        new("🛡", "Privacidade a sério", "DNS cifrado, links sem parâmetros de rastreio, modo rigoroso por site, notificações bloqueadas e o histórico interno do Chromium desligado."),
        new("🎨", "Tema claro e Mica", "Escuro, claro ou o do Windows — e as barras deixam transparecer o ambiente de trabalho."),
        new("🗂", "Grupos de abas", "Junte abas por tema, dê-lhes nome e cor e encolha-as numa só etiqueta."),
        new("⫴", "Abas verticais e ecrã dividido", "Uma lista lateral para dezenas de abas, e duas páginas lado a lado."),
        new("📖", "Modo de leitura e «Ler mais tarde»", "Só o artigo, em escuro, sépia ou claro; e uma lista para ler depois.", "F9"),
        new("⌨", "Paleta e atalhos", "Tudo à distância do teclado — inclusive seguir links pelas letras.", "Ctrl+K · F1"),
        new("🧮", "Barra de endereço esperta", "Contas, conversões, bangs (!w, !yt…) e «Mudar para este separador»."),
        new("📸", "Capturas e PDF", "A página inteira numa imagem, ou guardada como PDF.", "Ctrl+Shift+S"),
        new("✂", "Esconder elementos", "Clique num anúncio ou caixa e desaparece para sempre nesse site."),
        new("📊", "Painel da semana", "Na nova aba: rastreadores bloqueados por dia e o que foi poupado."),
    ];

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(WhatsNewPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.whatsnew.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    // Abre-se uma vez por versão (nunca no primeiro arranque, que tem as boas-vindas).
    public static bool ShouldShow(string lastSeenVersion, string currentVersion, bool firstRun)
        => !firstRun && !string.Equals(lastSeenVersion, currentVersion, StringComparison.Ordinal);

    public static string Render(string version)
    {
        var data = new { version, items = Highlights.Select(h => new { icon = h.Icon, title = h.Title, detail = h.Detail, keys = h.Keys }) };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
