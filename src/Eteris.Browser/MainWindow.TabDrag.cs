using System.Windows;
using System.Windows.Input;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    private const double DragThreshold = 6;
    private BrowserTab? _dragTab;
    private BrowserTab? _dragPreviousActive;
    private SplitDropTarget _dropTarget;
    private Point _dragStart;
    private bool _dragging;

    private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragTab = (sender as FrameworkElement)?.DataContext as BrowserTab;
        // A aba ativa antes de arrastar: fica de um lado, a arrastada do outro, se a largada for na margem.
        _dragPreviousActive = Tabs.Active;
        _dropTarget = SplitDropTarget.None;
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
        // A posição é relativa à lista (já conta com a rolagem da faixa); junto às pontas a faixa rola sozinha.
        DragScrollTabs(e.GetPosition(TabScroll));
        var pinned = Tabs.PinnedCount;
        var index = _dragTab.IsPinned
            ? TabLayout.IndexAt(position.X, PinnedTabWidth, pinned)
            : pinned + TabLayout.IndexAt(position.X - pinned * PinnedTabWidth, TabWidth, Tabs.Tabs.Count - pinned);
        Tabs.Move(_dragTab, index);

        // Sobre a margem direita ou inferior da página: a faixa acende; largar aí cria o ecrã dividido.
        var inPage = e.GetPosition(BrowserHost);
        var target = SplitDropZone.CanSplit(_dragPreviousActive, _dragTab)
            ? SplitDropZone.TargetAt(inPage.X, inPage.Y, BrowserHost.ActualWidth, BrowserHost.ActualHeight)
            : SplitDropTarget.None;
        if (target != _dropTarget) ShowSplitDropHint(target);
        _dropTarget = target;
    }

    private void TabList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var (dragged, previous, target) = (_dragTab, _dragPreviousActive, _dropTarget);
        if (_dragging)
        {
            TabList.ReleaseMouseCapture();
            e.Handled = true;
            ShowSplitDropHint(SplitDropTarget.None);
        }
        _dragTab = null;
        _dragPreviousActive = null;
        _dropTarget = SplitDropTarget.None;
        var dropped = _dragging;
        _dragging = false;
        if (dropped && target != SplitDropTarget.None && SplitDropZone.CanSplit(previous, dragged) && Tabs.Tabs.Contains(previous!))
        {
            Tabs.Active = previous;
            OpenSideBySide(dragged!, SplitDropZone.OrientationOf(target));
        }
    }
}
