using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Revisão final do plano 6: a varredura do arranque («Esquecer ao fechar» que ficou por fazer) só abre o perfil do motor
// de vídeo para os sites que lá deixaram alguma coisa por apagar, e o aviso dela diz o que falhou.
public class ForgetLeftoversTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"eteris-pendentes-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void PendingSites_SurviveARestart_AndLeaveWhenCleared()
    {
        var path = Path.Combine(_dir, "video-por-esquecer.txt");
        var set = new SiteSetFile(path);
        Assert.False(set.Contains("exemplo.pt"));
        set.Add("exemplo.pt");
        set.Add("twitch.tv");
        set.Add("exemplo.pt");
        var again = new SiteSetFile(path);
        Assert.True(again.Contains("exemplo.pt"));
        Assert.True(again.Contains("twitch.tv"));
        again.Remove("exemplo.pt");
        Assert.False(new SiteSetFile(path).Contains("exemplo.pt"));
        again.Clear();
        Assert.False(new SiteSetFile(path).Contains("twitch.tv"));
        Assert.False(File.Exists(path)); // sem nada pendente, nem o ficheiro fica
    }

    [Fact]
    public void PendingSites_AnUnreadableFileCountsAsEmpty()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "video-por-esquecer.txt");
        File.WriteAllText(path, "\n  \nexemplo.pt\n");
        var set = new SiteSetFile(path);
        Assert.True(set.Contains("exemplo.pt"));
        Assert.False(set.Contains(""));
    }

    [Theory]
    // Uma aba viva do motor de vídeo: limpa-se sempre (é barato e apanha os cookies de terceiros).
    [InlineData(true, false, false, false, true)]
    [InlineData(true, true, false, false, true)]
    // Triturar, temporizador e saída: a regra de sempre (o site já abriu no motor de vídeo).
    [InlineData(false, false, false, true, true)]
    [InlineData(false, false, false, false, false)]
    // Varredura do arranque: só o que ficou pendente, mesmo que o site já tenha aberto no motor de vídeo.
    [InlineData(false, true, false, true, false)]
    [InlineData(false, true, true, false, true)]
    // Um site pendente abre-o sempre (o Triturar também tira o nome do ficheiro), mesmo fora da regra MayHoldData.
    [InlineData(false, false, true, false, true)]
    public void VideoProfile_IsOpenedOnlyWhenSomethingMayBeThere(bool liveCore, bool startupSweep, bool pending, bool mayHoldData, bool expected)
        => Assert.Equal(expected, VideoEngine.ClearsSite(liveCore, startupSweep, pending, mayHoldData));

    [Fact]
    public void LeftoversToast_SaysWhatWasForgottenAndWhatFailed()
    {
        Assert.Null(SiteScope.LeftoversDone([new("a.pt", 0, 0, true)]));
        // Nada a apagar e só o armazenamento por fazer (sem página viva do motor principal neste arranque): sem aviso,
        // senão aparecia em cada arranque sem nada que se saiba ter ficado.
        Assert.Null(SiteScope.LeftoversDone([new("a.pt", 0, 0, false)]));
        Assert.Equal("a.pt foi esquecido (tinha ficado por apagar ao sair)", SiteScope.LeftoversDone([new("a.pt", 2, 0, true)]));
        Assert.Equal("2 sites esquecidos (tinham ficado por apagar ao sair): a.pt, b.pt",
            SiteScope.LeftoversDone([new("a.pt", 2, 0, true), new("b.pt", 0, 3, true), new("c.pt", 0, 0, true)]));
        // Nada apagado, mas o motor de vídeo não abriu: di-lo na mesma.
        Assert.Equal("Não foi possível esquecer tudo de a.pt (tinha ficado por apagar ao sair) — 0 cookies e 0 entradas do histórico apagados — não foi possível apagar os dados do motor de vídeo",
            SiteScope.LeftoversDone([new("a.pt", 0, 0, true, VideoEngineFailed: true)]));
        Assert.Equal("Não foi possível esquecer tudo de a.pt (tinha ficado por apagar ao sair) — 1 cookie e 0 entradas do histórico apagados — não foi possível apagar todos os cookies",
            SiteScope.LeftoversDone([new("a.pt", 1, 0, true, CookiesFailed: true)]));
        Assert.Equal("Não foi possível esquecer tudo de a.pt e b.pt (tinham ficado por apagar ao sair); c.pt foi esquecido",
            SiteScope.LeftoversDone([new("a.pt", 1, 0, false), new("b.pt", 4, 0, true, VideoEngineFailed: true), new("c.pt", 1, 0, true)]));
    }

    [Fact]
    public void StartupSweep_UsesThePendingSet_AndTheToastCarriesTheFailures()
    {
        var forget = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Forget.cs"));
        var sweep = forget[forget.IndexOf("private async Task ForgetLeftoversAsync()", StringComparison.Ordinal)..];
        sweep = sweep[..sweep.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("ShredSiteAsync(site, startupSweep: true)", sweep);
        Assert.Contains("SiteScope.LeftoversDone(", sweep);
        Assert.Contains("VideoEngine.ClearsSite(LiveVideoCore() is not null, startupSweep, AppData.VideoForgetPending.Contains(site)", forget);
        // Marcado antes de tentar, desmarcado só quando o motor de vídeo apagou tudo.
        var mark = forget.IndexOf("AppData.VideoForgetPending.Add(site);", StringComparison.Ordinal);
        var clear = forget.IndexOf("var video = await ClearVideoSiteAsync(site, origins);", StringComparison.Ordinal);
        var done = forget.IndexOf("if (video.Complete) AppData.VideoForgetPending.Remove(site);", StringComparison.Ordinal);
        Assert.True(mark > 0 && mark < clear && clear < done);
        var video = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.VideoEngine.cs"));
        Assert.Contains("NoteVideoForgetPending(e.Uri);", video);
        // Desligar o «esquecer» (no cadeado ou com «Repor» nas Definições por site) tira o nome do ficheiro.
        var data = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Data", "AppData.cs"));
        var set = data[data.IndexOf("public static void SetForgetOnClose(", StringComparison.Ordinal)..];
        set = set[..set.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("VideoForgetPending.Remove(SiteScope.OfHost(host))", set);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
