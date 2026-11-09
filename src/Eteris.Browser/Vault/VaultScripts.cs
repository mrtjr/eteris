using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Vault;

// O script do cofre (Vault/Scripts/cofre.js, recurso embutido). Só o VaultChannel o leva à página, num mundo isolado
// (guarda: VaultChannelGuardTests). O nome da ligação entra como cadeia JSON no lugar da marca.
public static class VaultScripts
{
    public const string BindingPlaceholder = "__ETERIS_LIGACAO__";

    // «Oferecer para guardar palavras-passe» (e o gerador), decidido quando o canal prepara o documento: desligado nas
    // Definições ou na janela privada (D-3), o script nem manda o envio de um formulário nem as restrições do gerador.
    public const string OfferPlaceholder = "__ETERIS_OFERECER__";

    private static readonly Regex s_binding = new("^[A-Za-z][A-Za-z0-9]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Lazy<string> s_source = new(() => Load("Eteris.Browser.Vault.cofre.js"));

    // O texto do recurso, com a marca (os testes do classificador correm-no no Node com null no lugar da ligação).
    public static string Source => s_source.Value;

    public static string Cofre(string binding, bool offer = true)
    {
        if (string.IsNullOrEmpty(binding) || !s_binding.IsMatch(binding)) throw new ArgumentException("Nome de ligação inválido.", nameof(binding));
        return Source.Replace(BindingPlaceholder, JsonSerializer.Serialize(binding), StringComparison.Ordinal)
            .Replace(OfferPlaceholder, offer ? "true" : "false", StringComparison.Ordinal);
    }

    private static string Load(string name)
    {
        using var stream = typeof(VaultScripts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Recurso em falta: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
