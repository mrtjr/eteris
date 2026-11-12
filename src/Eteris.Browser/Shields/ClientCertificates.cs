using System.Globalization;
using System.Security.Cryptography.X509Certificates;

namespace Eteris.Browser.Shields;

// Uma linha do diálogo de certificados de cliente (ClientCertificateDialog): quem é o titular, quem o emitiu e até
// quando vale. «Thumbprint» identifica o certificado na coleção do motor.
public sealed record ClientCertificateRow(string Subject, string Issuer, string ValidUntil, string Thumbprint);

// MOT-01: um site que pede certificado de cliente só recebe um se o utilizador o escolher (PrivacyRequestHandler,
// OnSelectClientCertificate). Nunca há certificado «por omissão», nem o primeiro da lista, nem o último usado.
public static class ClientCertificates
{
    // As linhas por ordem da coleção; o texto vem do certificado (dado do sistema, não da página).
    public static IReadOnlyList<ClientCertificateRow> Rows(X509Certificate2Collection certificates)
    {
        var rows = new List<ClientCertificateRow>(certificates.Count);
        foreach (var certificate in certificates)
            rows.Add(new ClientCertificateRow(Name(certificate, forIssuer: false), Name(certificate, forIssuer: true),
                certificate.NotAfter.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), certificate.Thumbprint));
        return rows;
    }

    // O certificado que o utilizador escolheu (pela impressão digital, sem distinguir maiúsculas), ou null: sem escolha,
    // com uma impressão que não está na lista ou com a lista vazia, nada é enviado.
    public static X509Certificate2? Choose(X509Certificate2Collection certificates, string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return null;
        foreach (var certificate in certificates)
            if (string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)) return certificate;
        return null;
    }

    private static string Name(X509Certificate2 certificate, bool forIssuer)
    {
        var simple = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer);
        if (!string.IsNullOrWhiteSpace(simple)) return simple;
        var full = forIssuer ? certificate.Issuer : certificate.Subject;
        return string.IsNullOrWhiteSpace(full) ? "(sem nome)" : full;
    }
}
