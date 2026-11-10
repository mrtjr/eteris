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

    // offerSignIn: mostrar o passo «Já usa o Éteris noutro computador?» (quem chama decide, pela regra do início de
    // sessão; aqui só se põe o valor nos dados da página).
    public static string Render(UserSettings settings, IReadOnlyList<ImportSource> imports, bool offerSignIn = false)
    {
        var data = new
        {
            token = InternalPages.Token,
            theme = settings.Theme,
            engine = settings.SearchEngine,
            dns = settings.SecureDns,
            clearOnExit = settings.ClearOnExit,
            imports = imports.Select(i => new { id = i.Id, name = i.Name, count = i.Count }),
            signIn = offerSignIn,
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
