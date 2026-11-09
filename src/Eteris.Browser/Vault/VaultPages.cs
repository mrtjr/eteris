namespace Eteris.Browser.Vault;

// Uma entrada do submenu «Palavras-passe e preenchimento automático» (menu do Éteris, paleta, Definições).
public sealed record VaultPageEntry(string Title, string PaletteTitle, string Subtitle, string Url, string Glyph);

// As páginas do cofre (docs/palavras-passe.md, B1): o gestor de palavras-passe e as quatro secções do preenchimento
// automático, pela ordem do submenu do Chrome.
public static class VaultPages
{
    public const string PasswordsUrl = "eteris://passwords/";
    public const string AutofillUrl = "eteris://autofill/";
    public const string MenuTitle = "Palavras-passe e preenchimento automático";

    public static IReadOnlyList<VaultPageEntry> Entries { get; } =
    [
        new("Gestor de palavras-passe", "Palavras-passe", "Ver, copiar, importar e exportar as palavras-passe guardadas", PasswordsUrl, ""),
        new("Pagamentos", "Pagamentos", "Cartões (sem o código de segurança) e IBAN", AutofillUrl + "#pagamentos", ""),
        new("Informações de contacto", "Informações de contacto", "Nomes, moradas, e-mails e telefones", AutofillUrl + "#contacto", ""),
        new("Documentos de identificação", "Documentos de identificação", "Passaporte, carta de condução/CNH, Cartão de Cidadão/RG, NIF/CPF", AutofillUrl + "#documentos", ""),
        new("Viagens", "Viagens", "Veículos e programas de fidelidade", AutofillUrl + "#viagens", ""),
    ];

    public static bool IsVaultPage(string? url)
        => url is not null && (url.StartsWith(PasswordsUrl, StringComparison.OrdinalIgnoreCase) || url.StartsWith(AutofillUrl, StringComparison.OrdinalIgnoreCase));

    // As páginas recusam-se na janela privada (como a secção Sincronização): o menu abre-as numa janela normal.
    public static bool OpensInNormalWindow(bool isPrivate) => isPrivate;
}
