using System.IO;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace Eteris.Browser.Tests;

// O ecrã calmo da Pausa (MainWindow.Pause.cs) para o leitor de ecrã: o que o nome de automação diz tem de chegar à árvore.
[Collection(WpfCollection.Name)]
public class PauseAccessibilityTests
{
    // Um WrapPanel não tem par de automação: o nome «Temporizador da Pausa» nunca chegava ao leitor de ecrã, que só ouvia
    // «1 min, botão». O painel dos chips é um grupo com nome.
    [Fact]
    public void TheTimerChips_AreANamedGroup()
    {
        var (type, name) = StaRunner.Run(() =>
        {
            var chips = new MainWindow.PauseChips();
            AutomationProperties.SetName(chips, "Temporizador da Pausa");
            var peer = UIElementAutomationPeer.CreatePeerForElement(chips);
            return (peer?.GetAutomationControlType(), peer?.GetName());
        }, nameof(PauseAccessibilityTests) + "." + nameof(TheTimerChips_AreANamedGroup));
        Assert.Equal(AutomationControlType.Group, type);
        Assert.Equal("Temporizador da Pausa", name);
    }

    // O botão de maximizar do ecrã calmo diz «Restaurar» com a janela maximizada, como a dica: o nome vem do estilo
    // CaptionButton (ligado à dica, que segue a do MaximizeButton), e não de um valor local fixo «Maximizar».
    [Fact]
    public void TheMaximizeButton_SaysWhatItDoes()
    {
        var pause = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "MainWindow.Pause.cs"));
        Assert.Contains("new Binding(nameof(FrameworkElement.ToolTip)) { Source = MaximizeButton }", pause);
        Assert.Contains("maximize.ClearValue(AutomationProperties.NameProperty);", pause);
        Assert.Contains("var chips = new PauseChips", pause);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Eteris.Browser"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
