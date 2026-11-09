using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Eteris.Browser.Tests;

// Regra do E1 (plano de desempenho da 0.2.14): uma animação eterna do WPF (RepeatBehavior Forever) só pode correr
// enquanto o elemento se vê. A do ponto «a tocar» começava no Loaded e nunca parava: o WPF enviava um lote ao compositor
// a cada fotograma, ~60 por segundo, o tempo todo, com o ponto escondido.
public class ForeverAnimationRulesTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    // No XAML: dentro do EnterActions de um Trigger de propriedade (IsVisible…), com um StopStoryboard no ExitActions que
    // pára esse BeginStoryboard. Nunca num EventTrigger (Loaded…), que não tem como parar.
    [Fact]
    public void Xaml_ForeverAnimationsStopWithTheirTrigger()
    {
        var found = 0;
        foreach (var file in Files("*.xaml"))
        {
            var doc = XDocument.Load(file);
            foreach (var element in doc.Descendants().Where(e => (string?)e.Attribute("RepeatBehavior") == "Forever"))
            {
                found++;
                var where = $"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber}";
                Assert.DoesNotContain(element.Ancestors(), a => a.Name == Wpf + "EventTrigger");
                var begin = element.Ancestors(Wpf + "BeginStoryboard").FirstOrDefault();
                Assert.True(begin is not null, $"{where}: animação eterna fora de um BeginStoryboard");
                Assert.True(begin!.Parent?.Name == Wpf + "Trigger.EnterActions", $"{where}: só no EnterActions de um Trigger");
                var name = (string?)begin.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name");
                Assert.False(string.IsNullOrEmpty(name), $"{where}: o BeginStoryboard precisa de nome para o StopStoryboard");
                var trigger = begin.Parent!.Parent!;
                Assert.True(trigger.Elements(Wpf + "Trigger.ExitActions").Elements(Wpf + "StopStoryboard")
                    .Any(s => (string?)s.Attribute("BeginStoryboardName") == name), $"{where}: falta o StopStoryboard «{name}» no ExitActions");
            }
        }
        Assert.True(found > 0, "o teste já não encontra nenhuma animação eterna no XAML (o spinner do tema tinha uma)");
    }

    // O ponto «a tocar» deixou de ter animação no XAML: começa e pára no código, com a visibilidade.
    [Fact]
    public void LiveDot_HasNoTriggerInXaml()
    {
        var xaml = File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "MainWindow.xaml"));
        var dot = Regex.Match(xaml, @"<Ellipse x:Name=""MediaLiveDot""[^>]*/>");
        Assert.True(dot.Success, "o MediaLiveDot passou a ter conteúdo (Triggers?) no XAML");
    }

    // No código: cada ficheiro que cria uma animação eterna também a tira (BeginAnimation(…, null)).
    [Fact]
    public void Code_ForeverAnimationsAreAlsoRemoved()
    {
        foreach (var file in Files("*.cs"))
        {
            var code = File.ReadAllText(file);
            if (!code.Contains("RepeatBehavior.Forever", StringComparison.Ordinal)) continue;
            Assert.True(Regex.IsMatch(code, @"BeginAnimation\([^;]*\bnull\)") || Regex.IsMatch(code, @"BeginAnimation\([^;]*\?[^;]*:\s*null\)"),
                $"{Path.GetFileName(file)}: animação eterna sem BeginAnimation(…, null) que a pare");
        }
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static IEnumerable<string> Files(string pattern)
        => Directory.EnumerateFiles(Path.Combine(Root, "src", "Eteris.Browser"), pattern, SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(Root, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "obj" or "bin" or "publish"));
}
