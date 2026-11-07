using System.Windows;
using System.Windows.Media;
using Eteris.Browser.Data;

namespace Eteris.Browser;

public partial class MainWindow
{
    private void UpdateStar()
    {
        var address = Tabs.Active?.Address;
        var web = WebHost(address) is not null;
        StarButton.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
        if (!web) return;

        var marked = AppData.IsBookmarked(address!);
        StarGlyph.Text = marked ? "" : "";
        StarGlyph.Foreground = marked ? (Brush)FindResource("IrisBrush") : (Brush)FindResource("TextMutedBrush");
        StarButton.ToolTip = marked ? "Remover dos favoritos (Ctrl+D)" : "Adicionar aos favoritos (Ctrl+D)";
    }

    private void ToggleBookmark()
    {
        if (Tabs.Active is not { } tab || WebHost(tab.Address) is null) return;
        AppData.ToggleBookmark(tab.Address, tab.Title);
        UpdateStar();
        if (SidebarShows(global::Eteris.Browser.Sidebar.SidebarPanels.Bookmarks)) RefreshLinksPanel();
    }

    private void Star_Click(object sender, RoutedEventArgs e) => ToggleBookmark();
}
