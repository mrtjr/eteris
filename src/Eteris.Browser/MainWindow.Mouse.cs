using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CefSharp;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser;

public partial class MainWindow
{
    private static readonly Brush DropHighlight = Frozen("#338B5CF6");

    private void WatchMouseAndDrops()
    {
        // Botões laterais do rato: voltar / avançar, em qualquer ponto da janela.
        PreviewMouseDown += OnSideMouseButton;

        // Largar na faixa de abas, na barra de ferramentas ou na barra lateral abre numa nova aba.
        // Largar na própria página continua a ser da página (ex.: enviar ficheiros).
        foreach (var zone in new Panel[] { TitleBar, (Panel)Toolbar.Child })
            WatchDropZone(zone);
        WatchDropZone((Panel)SideBar.Child);
    }

    private void OnSideMouseButton(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { GoForward(); e.Handled = true; }
    }

    private void WatchDropZone(Panel zone)
    {
        zone.AllowDrop = true;
        var original = zone.Background;
        zone.DragEnter += (_, e) => { if (DropUrls(e.Data).Any()) zone.Background = DropHighlight; };
        zone.DragLeave += (_, _) => zone.Background = original;
        zone.DragOver += (_, e) =>
        {
            e.Effects = DropUrls(e.Data).Any() ? DragDropEffects.Link | DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        zone.Drop += (_, e) =>
        {
            zone.Background = original;
            var urls = DropUrls(e.Data).Take(10).ToList();
            foreach (var url in urls) Tabs.NewTab(url, activate: ReferenceEquals(url, urls[^1]));
            e.Handled = true;
        };
    }

    private static IEnumerable<string> DropUrls(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            return files.Select(DropTarget.FromFile);
        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text
            && DropTarget.FromText(text, Engine) is { } url)
            return [url];
        return [];
    }
}
