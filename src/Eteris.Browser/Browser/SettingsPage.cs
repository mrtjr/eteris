using System.IO;
using System.Reflection;
using System.Text.Json;
using Eteris.Browser.Data;

namespace Eteris.Browser.Browser;

// eteris://settings — cada alteração é enviada para eteris://settings/set?k=…&v=…&t=token e aplicada de imediato.
public static class SettingsPage
{
    public const string Url = "eteris://settings/";

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(SettingsPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.settings.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Version
        => typeof(SettingsPage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
           ?? "0.0.0";

    public static string Render(UserSettings settings)
    {
        var data = new
        {
            token = InternalPages.Token,
            version = Version,
            engine = settings.SearchEngine,
            startup = settings.Startup,
            sleep = settings.SleepMinutes,
            httpsFirst = settings.HttpsFirst,
            cookies = settings.BlockThirdPartyCookies,
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
