using System.Windows;
using System.Windows.Input;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    private const double DragThreshold = 6;
    private BrowserTab? _dragTab;
    private Point _dragStart;
    private bool _dragging;

    private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragTab = (sender as FrameworkElement)?.DataContext as BrowserTab;
        _dragStart = e.GetPosition(TabList);
        _dragging = false;
    }

    private void TabList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is null || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(TabList);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _dragStart.X) < DragThreshold) return;
            _dragging = true;
            Tabs.Active = _dragTab;
            TabList.CaptureMouse();
        }
        var pinned = Tabs.PinnedCount;
        var index = _dragTab.IsPinned
            ? TabLayout.IndexAt(position.X, PinnedTabWidth, pinned)
            : pinned + TabLayout.IndexAt(position.X - pinned * PinnedTabWidth, TabWidth, Tabs.Tabs.Count - pinned);
        Tabs.Move(_dragTab, index);
    }

    private void TabList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            TabList.ReleaseMouseCapture();
            e.Handled = true;
        }
        _dragTab = null;
        _dragging = false;
    }
}
