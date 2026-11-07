using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Faixa de abas do topo que nunca perde abas: a partir da largura mínima legível as abas deixam de encolher e a
// faixa rola (roda do rato, setas nas pontas, arrastar junto à orla); a aba ativa é sempre trazida à vista.
public partial class MainWindow
{
    // Deslocamento animado da faixa: o ScrollViewer não anima o seu próprio offset.
    public static readonly DependencyProperty TabScrollOffsetProperty = DependencyProperty.Register(
        nameof(TabScrollOffset), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, _) => ((MainWindow)d).TabScroll.ScrollToHorizontalOffset(((MainWindow)d).TabScrollOffset)));

    public double TabScrollOffset
    {
        get => (double)GetValue(TabScrollOffsetProperty);
        set => SetValue(TabScrollOffsetProperty, value);
    }

    private void ScrollTabsTo(double offset, bool animate = true)
    {
        var current = TabScroll.HorizontalOffset;
        if (Math.Abs(offset - current) < 0.5) return;
        if (!animate)
        {
            BeginAnimation(TabScrollOffsetProperty, null);
            TabScrollOffset = offset;
            return;
        }
        BeginAnimation(TabScrollOffsetProperty, new DoubleAnimation(current, offset, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void TabScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabScroll.ScrollableWidth <= 0) return;
        // Roda para baixo avança (como na faixa do Firefox); não é preciso Shift.
        var direction = e.Delta < 0 ? 1 : -1;
        ScrollTabsTo(TabLayout.Step(TabScroll.HorizontalOffset, direction, TabScroll.ViewportWidth, TabScroll.ExtentWidth));
        e.Handled = true;
    }

    private void TabScrollLeft_Click(object sender, RoutedEventArgs e)
        => ScrollTabsTo(TabLayout.Step(TabScroll.HorizontalOffset, -1, TabScroll.ViewportWidth, TabScroll.ExtentWidth));

    private void TabScrollRight_Click(object sender, RoutedEventArgs e)
        => ScrollTabsTo(TabLayout.Step(TabScroll.HorizontalOffset, 1, TabScroll.ViewportWidth, TabScroll.ExtentWidth));

    private void TabScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateTabArrows();

    // Setas só quando há abas escondidas; cada uma ativa só para o lado onde há mais, com a sombra a indicá-lo.
    private void UpdateTabArrows()
    {
        var (left, right) = TabLayout.Arrows(TabScroll.HorizontalOffset, TabScroll.ViewportWidth, TabScroll.ExtentWidth);
        var overflow = TabScroll.ScrollableWidth > 0.5;
        TabScrollLeft.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        TabScrollRight.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        TabScrollLeft.IsEnabled = left;
        TabScrollRight.IsEnabled = right;
        TabFadeLeft.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        TabFadeRight.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
    }

    // A aba ativa fica inteira à vista (ao mudar de aba, ao abrir uma nova, ao fechar a última visível).
    private void RevealActiveTab()
    {
        if (Tabs.Active is not { } active) return;
        // Os contentores das abas só existem depois do layout.
        Dispatcher.InvokeAsync(() =>
        {
            if (TabScroll.ScrollableWidth <= 0 || !ReferenceEquals(Tabs.Active, active)
                || TabList.ItemContainerGenerator.ContainerFromItem(active) is not FrameworkElement container
                || !container.IsDescendantOf(TabList)) return;
            var left = container.TransformToAncestor(TabList).Transform(new Point(0, 0)).X;
            var target = TabLayout.RevealOffset(TabScroll.HorizontalOffset, left, container.ActualWidth, TabScroll.ViewportWidth, TabScroll.ExtentWidth);
            ScrollTabsTo(target);
        }, DispatcherPriority.Loaded);
    }

    // A arrastar uma aba até à orla da faixa, a faixa rola para se chegar às abas escondidas.
    private void DragScrollTabs(Point positionInViewport)
    {
        if (TabScroll.ScrollableWidth <= 0) return;
        var direction = TabLayout.DragScrollDirection(positionInViewport.X, TabScroll.ViewportWidth);
        if (direction == 0) return;
        var target = Math.Clamp(TabScroll.HorizontalOffset + direction * 12, 0, TabScroll.ScrollableWidth);
        ScrollTabsTo(target, animate: false);
    }
}
