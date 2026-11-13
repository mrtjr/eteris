using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Eteris.Browser.Tests;

// Proposta 1 «Espreitar a sério» (atrito 3): as linhas da camada por cima da página seguem as alturas das barras.
// Antes ligavam-se a RowDefinition.ActualHeight, que não é propriedade de dependência nem avisa quando muda: a ligação
// só era lida ao criar a camada. Quem passava das abas no topo às verticais ficava com o véu do Espreitar 42 px abaixo
// do topo da página (por cobrir e clicável), e a barra de procura, a de permissões, os avisos e a bolha também.
[Collection(WpfCollection.Name)]
public class OverlayRowsTests
{
    private sealed class Bars : DependencyObject
    {
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register("Title", typeof(double), typeof(Bars), new PropertyMetadata(42.0));
        public static readonly DependencyProperty ToolbarProperty = DependencyProperty.Register("Toolbar", typeof(double), typeof(Bars), new PropertyMetadata(48.0));
    }

    [Fact]
    public void BindChromeRows_FollowsTheBarHeights() => StaRunner.Run(() =>
    {
        var bars = new Bars();
        var layer = new Grid();
        MainWindow.BindChromeRows(layer, bars, Bars.TitleProperty, Bars.ToolbarProperty);
        Assert.Equal(3, layer.RowDefinitions.Count);
        Assert.Equal(new GridLength(42), layer.RowDefinitions[0].Height);
        Assert.Equal(new GridLength(48), layer.RowDefinitions[1].Height);
        Assert.Equal(new GridLength(1, GridUnitType.Star), layer.RowDefinitions[2].Height);

        // Abas no topo → verticais (a barra de título vai a 0), compacto, ecrã inteiro e de volta.
        foreach (var (title, toolbar) in new[] { (0.0, 48.0), (0.0, 40.0), (0.0, 0.0), (21.5, 24.0), (42.0, 48.0) })
        {
            bars.SetValue(Bars.TitleProperty, title);
            bars.SetValue(Bars.ToolbarProperty, toolbar);
            Assert.Equal(new GridLength(title), layer.RowDefinitions[0].Height);
            Assert.Equal(new GridLength(toolbar), layer.RowDefinitions[1].Height);
        }
    }, "BindChromeRows");

    // O erro antigo, provado: uma grelha ligada a RowDefinition.ActualHeight de outra fica com a 1.ª medida.
    [Fact]
    public void BindingToActualHeight_DoesNotFollow() => StaRunner.Run(() =>
    {
        var source = new Grid();
        source.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        source.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        source.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Layout(source);
        Assert.Equal(42, source.RowDefinitions[0].ActualHeight);

        var old = new Grid();
        foreach (var row in source.RowDefinitions.Take(2))
        {
            var bound = new RowDefinition();
            BindingOperations.SetBinding(bound, RowDefinition.HeightProperty,
                new Binding(nameof(RowDefinition.ActualHeight)) { Source = row, Converter = new ToGridLength() });
            old.RowDefinitions.Add(bound);
        }
        old.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Layout(old);
        Assert.Equal(new GridLength(42), old.RowDefinitions[0].Height);

        // Abas verticais: a barra de título passa a 0. A fonte muda; a grelha ligada não.
        source.RowDefinitions[0].Height = new GridLength(0);
        Layout(source);
        Layout(old);
        Assert.Equal(0, source.RowDefinitions[0].ActualHeight);
        Assert.Equal(new GridLength(42), old.RowDefinitions[0].Height);
    }, "ActualHeight (erro antigo)");

    // A janela liga a camada às propriedades animadas das barras (as que o SlideBar anima), nunca a ActualHeight.
    [Fact]
    public void OverlayLayer_BindsToTheBarProperties()
    {
        var code = File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "MainWindow.Overlay.cs"));
        Assert.Contains("BindChromeRows(root, this, TitleRowHeightProperty, ToolbarRowHeightProperty);", code);
        Assert.DoesNotContain("ActualHeight)", code);
        Assert.DoesNotContain("new[] { TitleRow, ToolbarRow }", code);
    }

    private static void Layout(Grid grid)
    {
        grid.Measure(new Size(800, 600));
        grid.Arrange(new Rect(0, 0, 800, 600));
    }

    private sealed class ToGridLength : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => new GridLength(value is double pixels ? pixels : 0);

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
