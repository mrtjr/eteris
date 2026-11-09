using System.IO;
using System.Text.Json;

namespace Eteris.Browser.Browser;

// eteris://passwords/ — o gestor de palavras-passe (docs/palavras-passe.md, B2). O HTML inicial leva só metadados (site,
// utilizador, datas): a palavra-passe e a nota chegam só por /reveal, depois da reautenticação. As rotas estão em
// Vault/VaultRoutes.cs.
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

    public static string Render(object data) => Template.Replace("{{DATA}}", JsonSerializer.Serialize(data));
}
