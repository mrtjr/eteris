using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// «Terminar sessão privada»: a confirmação diz o que fecha; as escolhas de http:// feitas lá acabam com ela.
public class PrivateSessionTests
{
    [Theory]
    [InlineData(1, 0, "Terminar a sessão privada? Fecha a janela privada e apaga os cookies, a cache, o armazenamento e as permissões dela.")]
    [InlineData(1, 1, "Terminar a sessão privada? Fecha a janela privada (1 aba) e apaga os cookies, a cache, o armazenamento e as permissões dela.")]
    [InlineData(2, 5, "Terminar a sessão privada? Fecha as 2 janelas privadas (5 abas) e apaga os cookies, a cache, o armazenamento e as permissões delas.")]
    public void Confirmation_SaysWhatCloses(int windows, int tabs, string expected)
        => Assert.Equal(expected, PrivateSession.Confirmation(windows, tabs));

    [Fact]
    public void Done_CountsTheWindows()
    {
        Assert.Contains("a janela privada foi fechada", PrivateSession.Done(1));
        Assert.Contains("3 janelas privadas fechadas", PrivateSession.Done(3));
    }

    [Fact]
    public void ForgetPrivate_DropsOnlyThePrivateHttpChoices()
    {
        var https = new HttpsFirst();
        https.Allow("velho.example", isPrivate: true);
        https.Allow("normal.example", isPrivate: false);
        https.ForgetPrivate();
        Assert.NotNull(https.Upgrade("http://velho.example/", isPrivate: true));
        Assert.Null(https.Upgrade("http://normal.example/", isPrivate: false));
    }

    // O HTTPS primeiro está nas «Novidades» da 0.2.9 (junto da rede local); o Anular e o Esquecer e triturar ficam no
    // CHANGELOG.md (as Novidades levam só os 6–8 destaques da versão).
    [Fact]
    public void WhatsNew_AnnouncesHttpsFirst()
    {
        // Entre os destaques da 0.2.9 (os 8 a seguir aos 4 da 0.2.15, aos 4 da 0.2.14, aos 3 da 0.2.13, aos 2 da 0.2.12,
        // aos 2 da 0.2.11 e aos 5 da 0.2.10). Conta-se a partir do destaque do sandbox (o 1.º da 0.2.14), para uma versão
        // nova não obrigar a mudar este índice.
        var sandbox = WhatsNewPage.Highlights.ToList().FindIndex(h => h.Title == WhatsNewPage.SandboxTitle);
        Assert.Contains(WhatsNewPage.Highlights.Skip(sandbox + 16).Take(8), h => h.Detail.Contains("HTTPS", StringComparison.Ordinal));
    }

    // ARQ-06: o fim da sessão privada esquecia sete coisas em dois sítios da janela (a última janela privada a fechar e
    // «Terminar sessão privada»); uma loja nova de estado privado tinha de se lembrar nos dois. Agora é um só.
    [Fact]
    public void End_IsTheOnlyPlaceThatForgetsThePrivateSession()
    {
        string[] forgets =
        [
            "Shields.ShieldEngine.Https.ForgetPrivate();", "Shields.LocalNetworkGrants.ForgetPrivate();", "Permissions.PermissionGrants.ForgetPrivate();",
            "Shields.SiteRules.ForgetPrivate();", "Data.AppData.ForgetPrivateShield();", "Shields.Cname.PrivateCache.Clear();",
            "Shields.PartitionedCookieJar.Private.Clear();", "Shields.ShieldTightening.ForgetPrivate();",
        ];
        var session = Source("Tabs/PrivateSession.cs");
        var window = Source("MainWindow.Private.cs");
        Assert.All(forgets, line =>
        {
            Assert.Equal(1, session.Split(line).Length - 1);
            Assert.DoesNotContain(line, window);
        });
        Assert.Equal(2, window.Split("PrivateSession.End();").Length - 1);
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", relative));
}
