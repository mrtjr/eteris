using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// E5 do plano de desempenho da 0.2.14 (ajuste e5): a bolha do link e o contorno do ecrã dividido saem da camada do
// tamanho da janela (MainWindow.Overlay.cs). A bolha ganha uma janela do tamanho do texto que nunca rouba o foco; o
// contorno fica na janela principal, 2 px para fora da metade ativa.
public class LinkBubbleTests
{
    [Fact]
    public void Bubble_KeepsItsPlaceAtTheBottomLeftOfThePageArea()
    {
        var place = MainWindow.LinkBubblePlace(new Rect(100, 50, 1360, 860), new Thickness(12, 0, 0, 12), new Size(300, 26));
        Assert.Equal(112, place.X);
        Assert.Equal(50 + 860 - 12 - 26, place.Y);
    }

    [Fact]
    public void Bubble_FollowsTheSidebarMargin()
        => Assert.Equal(100 + 12 + 320, MainWindow.LinkBubblePlace(new Rect(100, 50, 1360, 860), new Thickness(12 + 320, 0, 0, 12), new Size(300, 26)).X);

    [Fact]
    public void Ring_GoesOutsideThePaneWithTheTweak()
    {
        var pane = new Size(600, 400);
        Assert.Equal(new Rect(10, 20, 600, 400), MainWindow.SplitRingBounds(new Point(10, 20), pane, outside: false));
        var o = MainWindow.SplitRingOutset;
        Assert.Equal(new Rect(10 - o, 20 - o, 600 + 2 * o, 400 + 2 * o), MainWindow.SplitRingBounds(new Point(10, 20), pane, outside: true));
    }

    // A espessura do contorno no XAML é a mesma que sai para fora da metade (senão a página tapava parte dele, ou ficava
    // uma faixa vazia).
    [Fact]
    public void Ring_OutsetMatchesItsThickness()
    {
        var xaml = Source("MainWindow.xaml");
        var ring = Regex.Match(xaml, @"<Border x:Name=""SplitFocusRing""[^>]*>");
        Assert.True(ring.Success);
        Assert.Contains($"BorderThickness=\"{MainWindow.SplitRingOutset}\"", ring.Value);
    }

    [Theory]
    [InlineData("LinkStatusBubble", true, true)]
    [InlineData("SplitFocusRing", true, true)]
    [InlineData("Toast", true, false)]
    [InlineData("LinkStatusBubble", false, false)]
    [InlineData("SplitFocusRing", false, false)]
    public void OnlyTheBubbleAndTheRing_StayOutOfTheOverlay(string name, bool tweak, bool stays)
        => Assert.Equal(stays, MainWindow.StaysOutOfOverlay(name, tweak));

    [Fact]
    public void Tweak_IsOnByDefault()
        => Assert.True(Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.OwnLinkBubble).OnByDefault);

    // A janela da bolha nunca recebe a ativação nem os cliques, não aparece na barra de tarefas e é da janela principal
    // (fica por cima das janelas das páginas e esconde-se com ela).
    [Fact]
    public void BubbleWindow_NeverStealsFocusOrClicks()
    {
        var code = Source("MainWindow.LinkBubble.cs");
        foreach (var part in new[] { "Owner = this", "ShowActivated = false", "ShowInTaskbar = false", "Focusable = false", "IsHitTestVisible = false" })
            Assert.Contains(part, code);
        Assert.Matches(@"style \| WsExNoActivate \| WsExTransparent \| WsExToolWindow", code);
        Assert.Contains("WsExNoActivate = 0x08000000", code);
        Assert.Contains("WsExTransparent = 0x00000020", code);
        Assert.DoesNotMatch(@"_linkBubble\.Activate\(|bubble\.Activate\(", code);
    }

    // Ligações: a camada salta os dois elementos, a bolha segue a barra lateral, o contorno usa o retângulo de fora.
    [Fact]
    public void Window_IsWired()
    {
        Assert.Contains("if (StaysOutOfOverlay(name, ownBubble)) continue;", Source("MainWindow.Overlay.cs"));
        Assert.Contains("SetLinkBubbleOffset(new Thickness(12 + margins.Left, 0, 0, 12));", Source("MainWindow.Sidebar.cs"));
        Assert.DoesNotContain("LinkStatusBubble.Margin =", Source("MainWindow.Sidebar.cs"));
        Assert.Contains("SplitRingBounds(origin, new Size(pane.ActualWidth, pane.ActualHeight), RingOutsideOverlay)", Source("MainWindow.Split.cs"));
        var toast = Source("MainWindow.Toast.cs");
        Assert.Contains("SyncLinkBubble(show: true);", toast);
        Assert.Contains("SyncLinkBubble(show: false);", toast);
    }

    // Regra R-2 do sandbox: a camada cria-se no ContentRendered, antes da 1.ª página; a janela da bolha não nasce aí, mas no
    // 1.º link a mostrar (e só com a camada: no braço «puro» sem ela, a bolha fica na janela principal).
    [Fact]
    public void BubbleWindow_IsCreatedOnTheFirstLinkNotAtStartup()
    {
        Assert.DoesNotContain("SetUpLinkBubble(", Source("MainWindow.Overlay.cs"));
        var code = Source("MainWindow.LinkBubble.cs");
        Assert.Matches(@"private void SyncLinkBubble\(bool show\)\s*\{\s*if \(_linkBubble is null && show && _overlay is not null\) SetUpLinkBubble\(", code);
        Assert.Single(Regex.Matches(code, @"SetUpLinkBubble\(\(Grid\)"));
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
