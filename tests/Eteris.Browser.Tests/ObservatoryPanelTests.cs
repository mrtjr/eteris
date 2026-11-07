using System.IO;
using CefSharp;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B9 — o desenho do Observatório (pura geometria) e os textos do painel.
public class ObservatoryPanelTests
{
    private static IReadOnlyList<CompanyGroup> Groups(int domains, int companies, int requests = 1)
    {
        var o = new SiteObservatory();
        o.Reset("https://www.site.pt/");
        string[] owners = ["doubleclick.net", "facebook.net", "criteo.com", "adnxs.com", "demdex.net", "bluekai.com", "taboola.com"];
        for (var i = 0; i < domains; i++)
        {
            var host = i % (companies + 1) == companies ? $"x{i}.desconhecido.example" : $"h{i}.{owners[i % (companies + 1) % owners.Length]}";
            for (var r = 0; r < requests; r++) o.Record($"https://{host}/p", ResourceType.Image, ObservedVerdict.Blocked);
        }
        return SiteObservatory.ByCompany(o.Snapshot().ThirdParties);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(9, 3)]
    [InlineData(48, 6)]
    [InlineData(70, 6)]
    public void Nodes_StayInsideTheSquare_AndNeverTouch(int domains, int companies)
    {
        var drawing = ObservatoryLayout.Place(Groups(domains, companies, requests: 200), 440);
        Assert.Equal(Math.Min(domains, ObservatoryLayout.MaxNodes), drawing.Nodes.Count);
        Assert.Equal(domains - drawing.Nodes.Count, drawing.Hidden);
        foreach (var n in drawing.Nodes)
        {
            Assert.InRange(n.X - n.Radius, 0, 440);
            Assert.InRange(n.X + n.Radius, 0, 440);
            Assert.InRange(n.Y - n.Radius, 0, 440);
            Assert.InRange(n.Y + n.Radius, 0, 440);
            // Longe do círculo do site (raio 29) no centro.
            Assert.True(Dist(n.X - 220, n.Y - 220) - n.Radius > 40);
        }
        for (var i = 0; i < drawing.Nodes.Count; i++)
            for (var j = i + 1; j < drawing.Nodes.Count; j++)
            {
                var a = drawing.Nodes[i];
                var b = drawing.Nodes[j];
                Assert.True(Dist(a.X - b.X, a.Y - b.Y) > a.Radius + b.Radius, $"{a.Domain.Host} toca em {b.Domain.Host}");
            }
    }

    [Fact]
    public void EachCompany_HoldsAContiguousSector_AndTheSameInputGivesTheSameDrawing()
    {
        var groups = Groups(20, 4);
        var first = ObservatoryLayout.Place(groups, 440);
        var again = ObservatoryLayout.Place(groups, 440);
        Assert.Equal(first.Nodes.Select(n => (n.X, n.Y)), again.Nodes.Select(n => (n.X, n.Y)));
        var order = first.Nodes.Select(n => n.Group).ToList();
        for (var i = 1; i < order.Count; i++) Assert.True(order[i] >= order[i - 1]);
        Assert.Equal(groups.Count, first.Companies.Count);
        Assert.Equal(-Math.PI / 2, first.Nodes[0].Angle, 6);
    }

    [Fact]
    public void NodeSize_GrowsWithRequests_WithinBounds()
    {
        Assert.Equal(ObservatoryLayout.MinRadius, ObservatoryLayout.RadiusFor(0));
        Assert.True(ObservatoryLayout.RadiusFor(10) > ObservatoryLayout.RadiusFor(1));
        Assert.Equal(ObservatoryLayout.MaxRadius, ObservatoryLayout.RadiusFor(100_000));
    }

    [Fact]
    public void Empty_DrawsOnlyTheSite()
    {
        var drawing = ObservatoryLayout.Place([], 440);
        Assert.Empty(drawing.Nodes);
        Assert.Empty(drawing.Companies);
        Assert.Equal(220, drawing.Center);
    }

    [Fact]
    public void PanelTexts()
    {
        var o = new SiteObservatory();
        o.Reset("https://www.jornal.pt/");
        Assert.Equal("Nenhum pedido registado ainda nesta página.", ObservatoryText.Stats(o.Snapshot(), []));
        o.Record("https://cdn.jornal.pt/a.js", ResourceType.Script, ObservedVerdict.Allowed);
        Assert.Equal("Só pedidos ao próprio site — nenhum terceiro contactado.", ObservatoryText.Stats(o.Snapshot(), []));
        o.Record("https://www.google-analytics.com/c", ResourceType.Ping, ObservedVerdict.Blocked);
        o.Record("https://fonts.gstatic.com/f.woff2", ResourceType.FontResource, ObservedVerdict.Allowed);
        o.Record("https://cdn.desconhecido.example/i.png", ResourceType.Image, ObservedVerdict.Allowed);
        var snap = o.Snapshot();
        var groups = SiteObservatory.ByCompany(snap.ThirdParties);
        Assert.Equal("3 domínios de terceiros · 1 empresa conhecida · 1 bloqueado", ObservatoryText.Stats(snap, groups));
        Assert.Equal("GOOGLE · 2 domínios · 1 de 2 pedidos bloqueados", ObservatoryText.GroupHeader(groups[0]));
        Assert.Equal("OUTROS · 1 domínio · 1 pedido", ObservatoryText.GroupHeader(groups[1]));
        Assert.Equal("Google · 1 pedido — 1 beacon", ObservatoryText.DetailLine(snap.ThirdParties[0], "Google"));
        Assert.Equal("Empresa desconhecida · 1 pedido — 1 imagem", ObservatoryText.DetailLine(snap.ThirdParties[2], null));
        Assert.Equal("1 de 1 pedido bloqueado", SiteObservatory.BlockedOf(1, 1));
    }

    // O painel é desenhado na camada de sobreposição (a página é uma janela nativa e taparia o WPF), abre pelo Escudo e
    // pela paleta e fecha com Esc venha a tecla da moldura ou da página.
    [Fact]
    public void Panel_LivesInTheOverlay_AndIsReachable()
    {
        var root = FindRoot();
        var panel = File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", "MainWindow.Observatory.cs"));
        Assert.Contains("AddToOverlay(overlay)", panel);
        var main = File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", "MainWindow.xaml.cs"));
        Assert.Contains("key == Key.Escape && ObservatoryOpen) CloseObservatory()", main);
        Assert.Contains("else if (ObservatoryOpen) CloseObservatory();", main);
        Assert.Contains("Click=\"ShieldObservatory_Click\"", File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", "MainWindow.xaml")));
        Assert.Contains("\"Observatório do site\"", File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", "MainWindow.Palette.cs")));
    }

    private static double Dist(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
