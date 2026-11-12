using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// MOT-14, UIX-04: DependencyPropertyDescriptor.AddValueChanged guarda o alvo e o tratador numa tabela estática do WPF e
// nada os solta. Cada página e cada janela que o usavam ficavam na memória até o Éteris sair (as privadas incluídas).
[Collection(WpfCollection.Name)]
public class PropertyWatchTests
{
    [Fact]
    public void Fires_UntilDisposed() => StaRunner.Run(() =>
    {
        var border = new Border();
        var count = 0;
        var watch = PropertyWatch.Start(border, UIElement.OpacityProperty, () => count++);
        border.Opacity = 0.5;
        Assert.Equal(1, count);
        watch.Dispose();
        watch.Dispose();
        border.Opacity = 0.2;
        Assert.Equal(1, count);
    }, "PropertyWatch");

    [Fact]
    public void Disposed_LetsTheTargetBeCollected() => StaRunner.Run(() =>
    {
        var target = WatchAndDrop();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(target.IsAlive);
    }, "PropertyWatch (recolha)");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WatchAndDrop()
    {
        var border = new Border();
        PropertyWatch.Start(border, UIElement.OpacityProperty, () => { }).Dispose();
        return new WeakReference(border);
    }

    // A regra: ninguém mais chama AddValueChanged (o que se vigia passa por PropertyWatch e liberta-se).
    [Fact]
    public void NoAddValueChanged_OutsidePropertyWatch()
    {
        var source = Path.Combine(Root, "src", "Eteris.Browser");
        var offenders = Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine(source, "obj")) && !f.Contains(Path.Combine(source, "bin")) && !f.EndsWith("PropertyWatch.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("AddValueChanged(", StringComparison.Ordinal))
            .Select(Path.GetFileName);
        Assert.Empty(offenders);
    }

    // As vigilâncias da camada libertam-se ao fechar a janela e ao tirar o elemento, e não se repetem por elemento.
    [Fact]
    public void Overlay_ReleasesItsWatches()
    {
        var overlay = File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "MainWindow.Overlay.cs"));
        Assert.Contains("_overlayWatches", overlay);
        Assert.Contains("PropertyWatch.Start(", overlay);
        Assert.Contains("ReleaseOverlayWatches(element)", overlay);
        Assert.Contains("ReleaseOverlayWatches();", overlay);
        Assert.Contains("browser.AddressChanged +=", File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "MainWindow.xaml.cs")));
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
