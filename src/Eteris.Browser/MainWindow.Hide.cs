using System.Windows;
using System.Windows.Input;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;
using Eteris.Browser.Browser;

namespace Eteris.Browser;

// «Esconder elemento»: o utilizador escolhe um elemento; fica escondido neste site daqui em diante.
public partial class MainWindow
{
    private bool _picking;

    private async void PickElementToHide()
    {
        if (_picking || ActiveBrowser is not { } browser || WebHost(Tabs.Active?.Address) is not { } host) return;
        _picking = true;
        try
        {
            Keyboard.Focus(browser);
            var response = await PageScript.EvaluateAsync(browser, PickerScript.Pick, TimeSpan.FromMinutes(3), userGesture: true, asyncBody: true);
            if (!response.Success || response.Result is not string selector) return;
            if (!HiddenElements.IsSafeSelector(selector))
            {
                ShowToast("Não foi possível esconder este elemento");
                return;
            }
            AppData.HideElement(host, selector);
            ShowToast("Elemento escondido — fica assim neste site");
        }
        catch (Exception) { }
        finally
        {
            _picking = false;
        }
    }

    private static void ApplyHiddenElements(ChromiumWebBrowser browser, string url)
    {
        if (WebHost(url) is not { } host) return;
        var css = HiddenElements.Css(AppData.HiddenElements(host));
        if (css.Length > 0) PageScript.Run(browser, CosmeticScripts.Inject(css));
    }

    private void ResetHiddenElements()
    {
        if (WebHost(Tabs.Active?.Address) is not { } host) return;
        AppData.ClearHiddenElements(host);
        ReloadActive();
        ShowToast("Elementos escondidos neste site repostos");
    }
}
