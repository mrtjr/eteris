using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Vault;

// O script do cofre (Vault/Scripts/cofre.js, recurso embutido). Só o VaultChannel o leva à página, num mundo isolado
// (guarda: VaultChannelGuardTests). O nome da ligação entra como cadeia JSON no lugar da marca.
public static class VaultScripts
{
    public const string BindingPlaceholder = "__ETERIS_LIGACAO__";

    private static readonly Regex s_binding = new("^[A-Za-z][A-Za-z0-9]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Lazy<string> s_source = new(() => Load("Eteris.Browser.Vault.cofre.js"));

    // O texto do recurso, com a marca (os testes do classificador correm-no no Node com null no lugar da ligação).
    public static string Source => s_source.Value;

    public static string Cofre(string binding)
    {
        if (string.IsNullOrEmpty(binding) || !s_binding.IsMatch(binding)) throw new ArgumentException("Nome de ligação inválido.", nameof(binding));
        return Source.Replace(BindingPlaceholder, JsonSerializer.Serialize(binding), StringComparison.Ordinal);
    }

    private static string Load(string name)
    {
        using var stream = typeof(VaultScripts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Recurso em falta: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
