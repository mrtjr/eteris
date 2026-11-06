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
        new("🛡", "Privacidade a sério", "DNS cifrado, links sem rastreio, modo rigoroso por site, notificações bloqueadas e o histórico interno do Chromium desligado."),
        new("🗂", "Sessões guardadas", "Guarde as abas de uma janela com nome e reabra-as quando quiser — também a partir da nova aba.", "Ctrl+K"),
        new("♫", "Centro de multimédia", "Um botão mostra o que toca em todas as abas: tocar, pausar, silenciar, ir para a aba."),
        new("🔊", "Ouvir artigos", "No modo de leitura, «Ouvir» lê o artigo em voz alta com as vozes do Windows — nada sai do computador.", "F9"),
        new("⧉", "Janela flutuante", "A janela encolhe para um canto e fica por cima — para ver um vídeo enquanto trabalha."),
        new("🌙", "Escurecer este site", "Modo escuro só para os sites que escolher, lembrado da próxima vez."),
        new("📝", "Notas", "Um bloco de notas ao lado da página, com um clique para guardar o endereço."),
        new("▦", "Código QR da página", "Abra a página no telemóvel sem contas nem sincronização."),
        new("🎨", "Tema claro e Mica", "Escuro, claro ou o do Windows — e as barras deixam transparecer o ambiente de trabalho."),
        new("📚", "Grupos, abas verticais e ecrã dividido", "Grupos com nome e cor, uma lista lateral para dezenas de abas e duas páginas lado a lado."),
        new("⌨", "Paleta e atalhos", "Tudo à distância do teclado — abas de todas as janelas, comandos e «Colar e ir».", "Ctrl+K · F1"),
        new("🔗", "Links para um destaque", "Selecione texto e copie um link que abre a página já nesse parágrafo."),
        new("📸", "Capturas, PDF e favoritos", "A página inteira numa imagem ou em PDF; favoritos importados e exportados.", "Ctrl+Shift+S"),
        new("✂", "Esconder elementos", "Clique num anúncio ou caixa e desaparece para sempre nesse site."),
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
