using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Eteris.Browser.Shields;
using Eteris.Browser.Tests.Sync;

namespace Eteris.Browser.Tests;

// MOT-01: um site que pede certificado de cliente só recebe um se o utilizador o escolher no diálogo do Éteris. O motor
// 156.0.15 cortava a ligação sem diálogo (prova ao vivo de 2026-11-12: resultado C); o Éteris trata o pedido sempre.
public class ClientCertificateTests
{
    private static X509Certificate2 Make(string name)
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine([GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", .. parts]));

    [Fact]
    public void Choose_NeverSelectsWithoutTheUser()
    {
        using var a = Make("Ana Teste");
        using var b = Make("Bruno Teste");
        var all = new X509Certificate2Collection { a, b };
        Assert.Null(ClientCertificates.Choose(all, null));
        Assert.Null(ClientCertificates.Choose(all, ""));
        Assert.Null(ClientCertificates.Choose(all, "00"));
        Assert.Same(b, ClientCertificates.Choose(all, b.Thumbprint));
        Assert.Null(ClientCertificates.Choose([], a.Thumbprint));
    }

    [Fact]
    public void Choose_IgnoresCaseOfTheThumbprint()
    {
        using var a = Make("Ana Teste");
        var all = new X509Certificate2Collection { a };
        Assert.Same(a, ClientCertificates.Choose(all, a.Thumbprint.ToLowerInvariant()));
    }

    [Fact]
    public void Rows_ShowHolderIssuerAndExpiry()
    {
        using var a = Make("Ana Teste");
        var row = ClientCertificates.Rows([a]).Single();
        Assert.Equal("Ana Teste", row.Subject);
        Assert.Equal("Ana Teste", row.Issuer);
        Assert.Equal(a.NotAfter.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), row.ValidUntil);
        Assert.Equal(a.Thumbprint, row.Thumbprint);
    }

    [Fact]
    public void Handler_AlwaysTakesTheDecision()
    {
        var source = Source("Shields", "PrivacyRequestHandler.cs");
        Assert.Contains("override bool OnSelectClientCertificate(", source);
        Assert.Contains("ClientCertificates.Choose(", source);
    }

    [Fact]
    public void EveryRequestHandler_GetsTheEterisDialog()
    {
        // Abas, sites fixados na barra lateral e Espreitar: nenhum fica com o comportamento do motor.
        foreach (var file in new[] { "MainWindow.xaml.cs", "MainWindow.WebPanels.cs", "MainWindow.Glance.cs" })
            Assert.Contains("chooseCertificate: ", Source(file));
    }

    [Fact]
    public void Dialog_DefaultsToNotSending_AndRemembersNothing()
    {
        var code = Source("ClientCertificateDialog.xaml.cs");
        var xaml = Source("ClientCertificateDialog.xaml");
        Assert.Contains("Não enviar", xaml);
        Assert.Contains("Enviar certificado", xaml);
        Assert.Contains("IsDefault=\"True\"", xaml);
        Assert.Contains("IsCancel=\"True\"", xaml);
        Assert.Contains("O site fica a saber quem é.", code + xaml);
        Assert.DoesNotContain("Settings", code);
    }
}
