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
    private Workspace? _dropWorkspace;
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
        // A posição é relativa à lista (já conta com a rolagem da faixa); junto às pontas a faixa rola sozinha. Só
        // com o rato sobre a faixa: a caminho da margem da página a aba não muda de lugar nem a faixa rola.
        var inStrip = e.GetPosition(TabScroll);
        if (TabLayout.ReordersAt(inStrip.Y, TabScroll.ActualHeight))
        {
            DragScrollTabs(inStrip);
            MoveDraggedTabTo(position);
        }
        else StopDragScroll();

        // Sobre a margem direita ou inferior da página (até à orla da janela): a faixa acende; largar aí divide.
        var inPage = e.GetPosition(BrowserHost);
        var (slackRight, slackBottom) = SplitDropSlack();
        var target = SplitDropZone.CanSplit(_dragPreviousActive, _dragTab)
            ? SplitDropZone.TargetAt(inPage.X, inPage.Y, BrowserHost.ActualWidth, BrowserHost.ActualHeight, slackRight, slackBottom)
            : SplitDropTarget.None;
        if (target != _dropTarget) ShowSplitDropHint(target);
        _dropTarget = target;

        // Sobre o chip de outro Espaço: o chip acende; largar aí move a aba para lá.
        var workspace = WorkspaceUnder(e);
        if (!ReferenceEquals(workspace, _dropWorkspace)) ShowWorkspaceDropHint(workspace);
        _dropWorkspace = workspace;
    }

    // Entre a página e a orla da janela (moldura de 6 px e, maximizada, a margem que fica fora do ecrã), no máximo a
    // largura da faixa de largada; com a barra lateral aberta à direita, só a moldura (o resto é a barra).
    private (double Right, double Bottom) SplitDropSlack()
    {
        if (!BrowserHost.IsVisible || !BrowserHost.IsDescendantOf(this)) return (0, 0);
        var corner = BrowserHost.TranslatePoint(new Point(BrowserHost.ActualWidth, BrowserHost.ActualHeight), this);
        var right = Math.Clamp(ActualWidth - corner.X, 0, SplitDropZone.Width);
        var bottom = Math.Clamp(ActualHeight - corner.Y, 0, SplitDropZone.Width);
        if (_sidebarOpen && SidebarSideNow == Eteris.Browser.Sidebar.SidebarSide.Right) right = Math.Min(right, PageFrame.Padding.Right);
        return (right, bottom);
    }

    // Põe a aba arrastada no índice que fica por baixo da posição (relativa à lista de abas).
    private void MoveDraggedTabTo(Point position)
    {
        if (_dragTab is null) return;
        var pinned = Tabs.PinnedCount;
        var index = _dragTab.IsPinned
            ? TabLayout.IndexAt(position.X, PinnedTabWidth, pinned)
            : pinned + TabLayout.IndexAt(position.X - pinned * PinnedTabWidth, TabWidth, Tabs.Tabs.Count - pinned);
        Tabs.Move(_dragTab, index);
    }

    // A captura perdeu-se a meio do arrasto (Alt+Tab, uma janela que rouba a ativação, um menu): o arrasto acaba ali,
    // sem largada — senão a faixa íris e o chip ficavam acesos e o arrasto «preso» até ao arrasto seguinte.
    private void TabList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragging || !ReferenceEquals(e.OriginalSource, TabList)) return;
        StopDragScroll();
        ResetTabDrag();
    }

    private void ResetTabDrag()
    {
        if (_dropTarget != SplitDropTarget.None) ShowSplitDropHint(SplitDropTarget.None);
        if (_dropWorkspace is not null) ShowWorkspaceDropHint(null);
        _dragTab = null;
        _dragPreviousActive = null;
        _dropTarget = SplitDropTarget.None;
        _dropWorkspace = null;
        _dragging = false;
    }

    private void TabList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopDragScroll();
        var (dragged, previous, target, workspace) = (_dragTab, _dragPreviousActive, _dropTarget, _dropWorkspace);
        var dropped = _dragging;
        // O estado limpa-se antes de largar a captura: largá-la dispara LostMouseCapture, que já não tem nada a cancelar.
        ResetTabDrag();
        // Sem arrasto, um clique no ícone da fixada ativa fora de casa volta a casa (MainWindow.PinnedHome.cs).
        if (!dropped) TryGoHomeFromIcon(e);
        else _homeClick = null;
        if (dropped)
        {
            TabList.ReleaseMouseCapture();
            e.Handled = true;
        }
        if (dropped && workspace is not null && dragged is not null)
        {
            // A aba que estava ativa antes do arrasto volta a ser a ativa (a arrastada vai embora).
            if (previous is not null && !ReferenceEquals(previous, dragged) && Tabs.Tabs.Contains(previous)) Tabs.Active = previous;
            MoveToWorkspace([dragged], workspace);
            return;
        }
        if (dropped && target != SplitDropTarget.None && SplitDropZone.CanSplit(previous, dragged) && Tabs.Tabs.Contains(previous!))
        {
            Tabs.Active = previous;
            OpenSideBySide(dragged!, SplitDropZone.OrientationOf(target));
        }
    }
}
