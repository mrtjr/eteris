using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Eteris.Browser.Browser;

// eteris://passwords/ — o gestor de palavras-passe (docs/palavras-passe.md, B2). O HTML inicial não leva registos
// (VAU-09: só o token, os interruptores e «loading»); a lista — só metadados (site, utilizador, datas) — chega por /list,
// que só responde à própria página; a palavra-passe e a nota chegam só por /reveal, depois da reautenticação. As rotas
// estão em Vault/VaultRoutes.cs.
public static class PasswordsPage
{
    public const string Url = Vault.VaultPages.PasswordsUrl;

    private static readonly Lazy<string> s_template = new(() =>
    {
        using var stream = typeof(PasswordsPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.passwords.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Template => s_template.Value;

    // Acentos e IDN como são; <, >, & e aspas continuam escapados para o <script>.
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static string Render(object data) => Template.Replace("{{DATA}}", JsonSerializer.Serialize(data, Json));
}
