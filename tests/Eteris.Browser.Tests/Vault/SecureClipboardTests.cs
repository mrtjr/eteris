using System.IO;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tests.Sync;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// A5 (docs/palavras-passe.md): copiar um segredo sem o deixar no histórico da área de transferência nem na nuvem do
// Windows, limpo ao fim de 60 s se ainda lá estiver; e a janela fora das capturas enquanto um segredo está à vista.
// Os testes nunca tocam na área de transferência real (backend falso): a prova com a verdadeira é ao vivo.
public class SecureClipboardTests
{
    private sealed class FakeClipboard : IClipboardBackend
    {
        public uint Sequence { get; set; } = 10;
        public string? Text;
        public List<(string Format, byte[] Data)> Extra = [];
        public int Clears;
        public bool Busy;

        public uint SequenceNumber => Sequence;

        public bool SetText(string text, IReadOnlyList<(string Format, byte[] Data)> extra)
        {
            if (Busy) return false;
            Text = text;
            Extra = extra.ToList();
            Sequence++;
            return true;
        }

        public bool Clear()
        {
            Clears++;
            Text = null;
            Extra = [];
            Sequence++;
            return true;
        }
    }

    private sealed class FakeScheduler
    {
        public readonly List<(TimeSpan Delay, Action Run)> Scheduled = [];

        public IDisposable Schedule(TimeSpan delay, Action run)
        {
            Scheduled.Add((delay, run));
            return new Nothing();
        }

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    // Os três formatos que o Windows respeita: fora do histórico (Win+V), fora da nuvem e fora de quem vigia a área de
    // transferência.
    [Fact]
    public void ExclusionFormats_ArePresent()
    {
        var formats = SecureClipboard.ExclusionFormats.ToDictionary(f => f.Format, f => f.Data);
        Assert.Equal(new byte[4], formats["CanIncludeInClipboardHistory"]);
        Assert.Equal(new byte[4], formats["CanUploadToCloudClipboard"]);
        Assert.True(formats.ContainsKey("ExcludeClipboardContentFromMonitorProcessing"));

        var clip = new FakeClipboard();
        var copier = new SecureClipboard(clip, new FakeScheduler().Schedule);
        Assert.True(copier.Copy("Inventada-Copia-1!"));
        Assert.Equal("Inventada-Copia-1!", clip.Text);
        Assert.Equal(SecureClipboard.ExclusionFormats.Select(f => f.Format), clip.Extra.Select(f => f.Format));
    }

    [Fact]
    public void Clears_After60s_OnlyIfStillTheSame()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), SecureClipboard.ClearAfter);
        var clip = new FakeClipboard();
        var scheduler = new FakeScheduler();
        var copier = new SecureClipboard(clip, scheduler.Schedule);
        copier.Copy("Inventada-Copia-1!");
        var (delay, run) = Assert.Single(scheduler.Scheduled);
        Assert.Equal(SecureClipboard.ClearAfter, delay);
        run();
        Assert.Equal(1, clip.Clears);
        Assert.Null(clip.Text);

        // O utilizador copiou outra coisa entretanto: fica.
        copier.Copy("Inventada-Copia-2!");
        clip.Sequence += 3;
        clip.Text = "outra coisa do utilizador";
        scheduler.Scheduled[^1].Run();
        Assert.Equal(1, clip.Clears);
        Assert.Equal("outra coisa do utilizador", clip.Text);
    }

    // Copiar duas vezes: o primeiro prazo não apaga a segunda cópia; o segundo sim.
    [Fact]
    public void SecondCopy_IsClearedByItsOwnDeadline()
    {
        var clip = new FakeClipboard();
        var scheduler = new FakeScheduler();
        var copier = new SecureClipboard(clip, scheduler.Schedule);
        copier.Copy("Inventada-A!");
        copier.Copy("Inventada-B!");
        scheduler.Scheduled[0].Run();
        Assert.Equal("Inventada-B!", clip.Text);
        scheduler.Scheduled[1].Run();
        Assert.Null(clip.Text);
    }

    [Fact]
    public void BusyClipboard_CopiesNothing_AndSchedulesNothing()
    {
        var clip = new FakeClipboard { Busy = true };
        var scheduler = new FakeScheduler();
        Assert.False(new SecureClipboard(clip, scheduler.Schedule).Copy("Inventada-C!"));
        Assert.Empty(scheduler.Scheduled);
    }

    // A cópia faz-se no processo do navegador, pela API do Windows, com os formatos de exclusão: nada de
    // Clipboard.SetText (sem exclusão) no cofre.
    [Fact]
    public void Win32Backend_UsesTheExclusionFormats()
    {
        var folder = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Vault");
        var source = File.ReadAllText(Path.Combine(folder, "SecureClipboard.cs"));
        Assert.Contains("RegisterClipboardFormatW", source);
        Assert.Contains("GetClipboardSequenceNumber", source);
        foreach (var file in Directory.GetFiles(folder, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Clipboard.SetText", text);
            Assert.DoesNotContain("Clipboard.SetDataObject", text);
        }
    }

    // Um segredo à vista tira todas as janelas do Éteris das capturas, mesmo com a definição desligada.
    [Theory]
    [InlineData(false, 26100, false, CaptureShield.WdaNone)]
    [InlineData(false, 26100, true, CaptureShield.WdaExcludeFromCapture)]
    [InlineData(true, 26100, false, CaptureShield.WdaExcludeFromCapture)]
    [InlineData(false, 18363, true, CaptureShield.WdaMonitor)]
    public void SecretOnScreen_ExcludesFromCapture(bool hide, int build, bool secret, uint expected)
        => Assert.Equal(expected, CaptureShield.AffinityFor(hide, build, secret));

    [Fact]
    public void SecretHolds_CountAndNotifyOnce()
    {
        var changes = new List<bool>();
        var holds = new SecretHolds(changes.Add);
        var a = holds.Hold();
        var b = holds.Hold();
        Assert.True(holds.Visible);
        a.Dispose();
        a.Dispose();
        Assert.True(holds.Visible);
        b.Dispose();
        Assert.False(holds.Visible);
        Assert.Equal([true, false], changes);
    }

    // Até a esconder ou, no máximo, 30 s.
    [Fact]
    public void SecretOnScreen_ReleasesAfter30s()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), SecretOnScreen.Limit);
        var holds = new SecretHolds(_ => { });
        var scheduler = new FakeScheduler();
        var expired = 0;
        var shown = new SecretOnScreen(holds.Hold(), scheduler.Schedule, () => expired++);
        Assert.True(holds.Visible);
        var (delay, run) = Assert.Single(scheduler.Scheduled);
        Assert.Equal(SecretOnScreen.Limit, delay);
        run();
        Assert.False(holds.Visible);
        Assert.Equal(1, expired);
        shown.Dispose();
        Assert.Equal(1, expired);

        var hidden = new SecretOnScreen(holds.Hold(), scheduler.Schedule, () => expired++);
        hidden.Dispose();
        Assert.False(holds.Visible);
        scheduler.Scheduled[^1].Run();
        Assert.Equal(1, expired);
    }
}
