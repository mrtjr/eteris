using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Tabs/VideoEngine.cs: o ficheiro de média que nenhum dos motores reconhece pelo tipo (um .aac servido como
// audio/vnd.dlna.adts) — o WebView2 gravava-o em Transferências e a aba ficava vazia, em #t=2 herdado da página anterior.
public class VideoEnginePlayerPageTests
{
    private const string Aac = "http://127.0.0.1:8791/media/bear.aac";

    [Theory]
    [InlineData(Aac, Aac, "audio/vnd.dlna.adts", null, true)]
    [InlineData(Aac, Aac + "#t=2", "audio/vnd.dlna.adts", null, true)]           // o fragmento não conta
    [InlineData(Aac, "http://127.0.0.1:8791/pagina.html", "audio/vnd.dlna.adts", null, false)] // link a guardar dentro da página
    [InlineData(Aac, null, "audio/vnd.dlna.adts", null, false)]
    [InlineData(Aac, Aac, "audio/vnd.dlna.adts", "attachment; filename=bear.aac", false)] // o site pediu para guardar
    [InlineData("https://exemplo.pt/relatorio.pdf", "https://exemplo.pt/relatorio.pdf", "application/pdf", null, false)]
    public void PlaysInsteadOfDownload(string download, string? navigation, string? mime, string? disposition, bool plays)
        => Assert.Equal(plays, VideoEngine.PlaysInsteadOfDownload(download, navigation, mime, disposition));

    [Fact]
    public void PlayerPage_UsesTheMediaElementWithTheFileAsSource()
    {
        var page = VideoEngine.PlayerPage(Aac, "audio/vnd.dlna.adts");
        Assert.Contains("<audio controls autoplay src=\"http://127.0.0.1:8791/media/bear.aac\">", page);
        Assert.Contains("<title>bear.aac</title>", page);
        Assert.Contains("<video controls autoplay", VideoEngine.PlayerPage("https://exemplo.pt/v/filme.mp4", "application/octet-stream"));
        // Pela extensão, quando o tipo não diz nada.
        Assert.Contains("<audio", VideoEngine.PlayerPage("https://exemplo.pt/a/musica.m4a", null));
    }

    [Fact]
    public void PlayerPage_EscapesTheAddress()
    {
        var page = VideoEngine.PlayerPage("https://exemplo.pt/a.aac?x=\"><script>alert(1)</script>", "audio/aac");
        Assert.DoesNotContain("<script>", page);
        Assert.Contains("&quot;&gt;&lt;script&gt;", page);
    }

    [Theory]
    [InlineData("https://exemplo.pt/v.mp4", "https://exemplo.pt/v.mp4", true)]
    [InlineData("https://exemplo.pt/v.mp4#t=5", "https://exemplo.pt/v.mp4", true)]
    [InlineData("https://exemplo.pt/pagina-anterior.html", "https://exemplo.pt/v.mp4", false)] // transferência desviada
    [InlineData(null, "https://exemplo.pt/v.mp4", false)]
    public void ResumesOnlyFromTheSamePage(string? shown, string target, bool resumes)
        => Assert.Equal(resumes, VideoEngine.ResumesFrom(shown, target));

    [Theory]
    [InlineData("https://exemplo.pt/v.mp4#t=5", "https://exemplo.pt/v.mp4")]
    [InlineData("https://exemplo.pt/v.mp4", "https://exemplo.pt/v.mp4")]
    public void WithoutFragment(string url, string expected) => Assert.Equal(expected, VideoEngine.WithoutFragment(url));
}
