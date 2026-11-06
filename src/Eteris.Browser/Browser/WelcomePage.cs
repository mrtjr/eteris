using System.IO;
using System.Text.Json;
using Eteris.Browser.Data;

namespace Eteris.Browser.Browser;

// eteris://welcome — acolhimento no primeiro arranque (tema, motor, importar favoritos, privacidade extra).
public static class WelcomePage
{
    public const string Url = "eteris://welcome/";

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(WelcomePage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.welcome.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Render(UserSettings settings, IReadOnlyList<ImportSource> imports)
    {
        var data = new
        {
            token = InternalPages.Token,
            theme = settings.Theme,
            engine = settings.SearchEngine,
            dns = settings.SecureDns,
            clearOnExit = settings.ClearOnExit,
            imports = imports.Select(i => new { id = i.Id, name = i.Name, count = i.Count }),
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
