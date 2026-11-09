using System.Windows.Threading;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    // Pedido de aba nova feito por uma página (chega já cancelado no Chromium; a aba, se abrir, é nossa).
    // Os scripts de anúncios abrem logo ao carregar no botão do rato, e o aviso de onde o utilizador clicou pode
    // chegar uns milissegundos depois do pedido: sem gesto decide-se já; com gesto espera-se até ActivationWait.
    private void DecidePopup(BrowserTab tab, PopupRequest request, Func<UserActivation?> activation)
    {
        if (!request.UserGesture || IsOriginOf(activation(), request))
        {
            ApplyPopupDecision(tab, request, activation());
            return;
        }

        var waited = TimeSpan.Zero;
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            waited += timer.Interval;
            if (!IsOriginOf(activation(), request) && waited < PopupPolicy.ActivationWait) return;
            timer.Stop();
            ApplyPopupDecision(tab, request, activation());
        };
        timer.Start();
    }

    // O clique que originou o pedido: registado pouco antes dele (o aviso chegou primeiro) ou depois.
    private static bool IsOriginOf(UserActivation? activation, PopupRequest request)
        => activation is not null && activation.AtUtc >= request.AtUtc - TimeSpan.FromMilliseconds(600);

    private void ApplyPopupDecision(BrowserTab tab, PopupRequest request, UserActivation? activation)
    {
        if (PopupPolicy.Allow(request, ShieldEngine.Current, ShieldOn, activation))
        {
            Tabs.NewTab(request.TargetUrl, request.Foreground);
            return;
        }

        var host = Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var target) && target.Host.Length > 0 ? target.Host : "pop-up";
        ShieldStats.CountBlocked(persist: !IsPrivate);
        tab.RecordBlocked(host);
        // Com clique, o utilizador viu que algo devia acontecer: diz-se o que foi travado.
        if (request.UserGesture && ReferenceEquals(tab, Tabs.Active)) ShowToast($"Anúncio em nova aba bloqueado ({host})");
    }
}
