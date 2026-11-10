using System.Windows;
using System.Windows.Automation.Peers;

namespace Eteris.Browser.Sync;

// O anúncio das barras ao leitor de ecrã (0.2.15, revisão final). Em WPF, AutomationProperties.LiveSetting sozinho não
// anuncia nada: é preciso levantar LiveRegionChanged no par de automação do texto. FromElement devolve null enquanto
// ninguém tiver criado o par, por isso cria-se aqui; e um elemento ainda escondido (a barra fica visível logo a seguir
// ao Show) só é anunciado quando aparecer.
internal static class LiveRegion
{
    public static void Announce(UIElement element)
    {
        if (!element.IsVisible)
        {
            // Idempotente: vários pedidos com o elemento escondido dão um só anúncio quando aparecer.
            element.IsVisibleChanged -= OnBecameVisible;
            element.IsVisibleChanged += OnBecameVisible;
            return;
        }
        Raise(element);
    }

    private static void OnBecameVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element || e.NewValue is not true) return;
        element.IsVisibleChanged -= OnBecameVisible;
        Raise(element);
    }

    private static void Raise(UIElement element)
    {
        try { UIElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged); }
        catch (InvalidOperationException) { }
    }
}
