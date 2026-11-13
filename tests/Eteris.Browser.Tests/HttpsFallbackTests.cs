using System.IO;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// Revisão aos 50: «HTTPS primeiro» com recuo rápido. Desligado o HttpsUpgrades do Chromium (ciclo 31), um site só http://
// com a porta 443 a descartar pacotes deixava a aba ~21 s em branco antes do aviso; agora, ao fim de ~3 s sem resposta
// do https://, o motor pergunta ao http:// e, se ele responder, o aviso aparece logo.
public class HttpsFallbackTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsPending_UntilTheUpgradedHttpsAnswersFailsOrIsHandled()
    {
        var now = T0;
        var https = new HttpsFirst(clock: () => now);
        Assert.False(https.IsPending("https://antigo.example/"));
        var secure = https.Upgrade("http://antigo.example/")!;
        Assert.True(https.IsPending(secure));
        // Outro contexto (janela privada) não tem esta subida.
        Assert.False(https.IsPending(secure, isPrivate: true));
        now += HttpsFirst.FallbackAfter;
        Assert.True(https.IsPending(secure));
        https.Settled(secure);
        Assert.False(https.IsPending(secure));
        https.Upgrade("http://antigo.example/");
        Assert.Equal("http://antigo.example/", https.FailedUpgrade(secure));
        Assert.False(https.IsPending(secure));
        // Uma subida esquecida há muito não conta.
        https.Upgrade("http://antigo.example/");
        now += HttpsFirst.UpgradeWindow + TimeSpan.FromSeconds(1);
        Assert.False(https.IsPending(secure));
    }

    [Theory]
    // O http:// responde: o site não fala HTTPS (ou não a tempo) — o aviso aparece já.
    [InlineData(200, null, true)]
    [InlineData(404, null, true)]
    [InlineData(500, null, true)]
    [InlineData(301, "http://antigo.example/novo", true)]
    [InlineData(302, "https://outro.example/", true)]
    // O http:// manda para o https:// do mesmo site: o site fala HTTPS e só está lento — continua-se à espera.
    [InlineData(301, "https://antigo.example/", false)]
    [InlineData(308, "https://www.antigo.example/pagina", false)]
    [InlineData(302, "/", true)]
    // Sem resposta: o aviso, se for caso disso, chega pela falha do https://.
    [InlineData(0, null, false)]
    public void Probe_DecidesBetweenTheWarningAndWaiting(int status, string? location, bool insecure)
        => Assert.Equal(insecure, HttpsFirst.ProbeSaysInsecure(status, location, "http://antigo.example/"));

    [Fact]
    public void FallbackIsAboutThreeSeconds() => Assert.InRange(HttpsFirst.FallbackAfter, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));

    // O pedido da página nunca espera: o recuo é um temporizador à parte, e a sondagem vai pelo motor (sem HttpClient,
    // sem cookies) e só para a navegação se ela ainda for a do separador.
    [Fact]
    public void Handler_WatchesTheUpgradeWithoutHoldingTheRequest()
    {
        var handler = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Shields", "PrivacyRequestHandler.cs"));
        Assert.Contains("WatchUpgrade(browser, secure, request.Url);", handler);
        Assert.Contains("Task.Delay(HttpsFirst.FallbackAfter).ContinueWith(", handler);
        Assert.Contains("probe.Method = \"HEAD\";", handler);
        Assert.Contains("frame.CreateUrlRequest(probe, client)", handler);
        Assert.DoesNotContain("AllowStoredCredentials", handler);
        Assert.DoesNotContain("HttpClient", handler);
        Assert.Contains("main?.LoadUrl(ErrorPage.InsecureUrl(-7, http));", handler);
        // O comentário do motor já não atribui o -7 ao Chromium.
        var configurator = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Infrastructure", "CefConfigurator.cs"));
        Assert.DoesNotContain("sem aviso nem recuo", configurator);
    }

    [Theory]
    [InlineData("http://u:p@loja.example/conta/repor?token=SEGREDO#x", "http://loja.example/")]
    [InlineData("http://antigo.example:8080/a/b?c=d", "http://antigo.example:8080/")]
    [InlineData("http://ANTIGO.example", "http://antigo.example/")]
    public void ProbeUrl_DropsPathQueryAndCredentials(string original, string probe)
        => Assert.Equal(probe, HttpsFirst.ProbeUrl(original));

    [Fact]
    public void Handler_ProbesOnlyTheOrigin()
    {
        var handler = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Shields", "PrivacyRequestHandler.cs"));
        Assert.Contains("probe.Url = HttpsFirst.ProbeUrl(original);", handler);
        Assert.DoesNotContain("probe.Url = original;", handler);
        Assert.Contains("HttpsFirst.ProbeSaysInsecure(status, location, original)", handler);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
