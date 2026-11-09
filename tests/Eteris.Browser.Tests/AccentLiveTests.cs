using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

// A «Cor do Éteris» muda ao vivo: o WPF congela os pincéis que servem de valor a Setters de estilos selados, por
// isso o IrisBrush é substituído por um pincel animado (impossível de congelar) e lido sempre por DynamicResource.
[Collection(WpfCollection.Name)]
public class AccentLiveTests
{
    private static void OnSta(Action action, [System.Runtime.CompilerServices.CallerMemberName] string test = "")
        => StaRunner.Run(action, nameof(AccentLiveTests) + "." + test);

    [Fact]
    public void RecoloredBrush_SurvivesASealedStyle_AndStillTakesNewColours()
    {
        OnSta(() =>
        {
            var target = Accent.Gradient(Accent.Palette.First(c => c.Key == "coral").Color, light: false);
            var brush = AccentManager.Recolored(null, Accent.IrisStops);
            var style = new Style(typeof(Ellipse));
            style.Setters.Add(new Setter(Shape.StrokeProperty, brush));
            style.Seal();
            Assert.False(brush.IsFrozen);

            var next = AccentManager.Recolored(brush, target);
            Assert.Equal(brush.GradientStops.Select(s => s.Offset), next.GradientStops.Select(s => s.Offset));
            style = new Style(typeof(Ellipse));
            style.Setters.Add(new Setter(Shape.StrokeProperty, next));
            style.Seal();
            Assert.False(next.IsFrozen);
            Assert.Equal(target.Count(), next.GradientStops.Count);
        });
    }

    [Fact]
    public void PlainIrisBrush_IsFrozenBySealedStyle_WhichIsWhyItMustBeReplaced()
    {
        OnSta(() =>
        {
            var plain = new LinearGradientBrush(Colors.Violet, Colors.Cyan, 0);
            var style = new Style(typeof(Ellipse));
            style.Setters.Add(new Setter(Shape.StrokeProperty, plain));
            style.Seal();
            Assert.True(plain.IsFrozen);
        });
    }

    [Fact]
    public void Xaml_ReadsAccentResources_Dynamically()
    {
        var root = FindRoot();
        var files = Directory.EnumerateFiles(System.IO.Path.Combine(root, "src", "Eteris.Browser"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}"));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("StaticResource IrisBrush", text);
            Assert.DoesNotContain("StaticResource AccentInkBrush", text);
        }
    }

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
