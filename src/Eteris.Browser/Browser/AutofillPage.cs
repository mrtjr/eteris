using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Eteris.Browser.Browser;

// Um campo do formulário de morada.
public sealed record AddressField(string Key, string Label, string Placeholder = "", bool Wide = false);

// eteris://autofill/ — Pagamentos, Informações de contacto, Documentos de identificação e Viagens (docs/palavras-passe.md,
// B3). O HTML inicial não leva registos (VAU-09); a lista chega por /list, que só responde à própria página, com os
// números só mascarados («Visa •••• 1111», «PT50 •••• 0154»); o número completo chega por /reveal depois da
// reautenticação. As rotas estão em Vault/VaultRoutes.Autofill.cs.
public static class AutofillPage
{
    public const string Url = Vault.VaultPages.AutofillUrl;

    private static readonly Lazy<string> s_template = new(() =>
    {
        using var stream = typeof(AutofillPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.autofill.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Template => s_template.Value;

    // Letras e símbolos como são (o «•» das máscaras, os acentos); <, >, & e aspas continuam escapados para o <script>.
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static string Render(object data) => Template.Replace("{{DATA}}", JsonSerializer.Serialize(data, Json));

    // A morada com os campos e os nomes de cada país: Portugal (código postal 0000-000, distrito, andar), Brasil (CEP,
    // bairro, estado, complemento) e os outros.
    public static IReadOnlyList<AddressField> AddressForm(string? country) => country?.ToUpperInvariant() switch
    {
        "PT" =>
        [
            new("givenName", "Nome"), new("familyName", "Apelido"), new("organization", "Empresa", Wide: true),
            new("street", "Morada (rua)", "Rua Augusta", Wide: true), new("number", "Número"), new("complement", "Andar, porta", "2.º Esq."),
            new("postalCode", "Código postal", "1000-001"), new("city", "Localidade"), new("region", "Distrito"),
            new("email", "E-mail"), new("phone", "Telefone", "+351 …"),
        ],
        "BR" =>
        [
            new("givenName", "Nome"), new("familyName", "Sobrenome"), new("organization", "Empresa", Wide: true),
            new("street", "Logradouro", "Avenida Paulista", Wide: true), new("number", "Número"), new("complement", "Complemento", "Apto. 12"),
            new("neighborhood", "Bairro"), new("postalCode", "CEP", "00000-000"), new("city", "Cidade"), new("region", "Estado", "SP"),
            new("email", "E-mail"), new("phone", "Telefone", "+55 …"),
        ],
        _ =>
        [
            new("givenName", "Nome"), new("familyName", "Apelido"), new("organization", "Empresa", Wide: true),
            new("street", "Morada", Wide: true), new("number", "Número"), new("complement", "Complemento"),
            new("postalCode", "Código postal"), new("city", "Cidade"), new("region", "Região"),
            new("email", "E-mail"), new("phone", "Telefone"),
        ],
    };
}
