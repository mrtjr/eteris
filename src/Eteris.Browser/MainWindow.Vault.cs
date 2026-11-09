using Eteris.Browser.Vault;

namespace Eteris.Browser;

// O cofre na janela (docs/palavras-passe.md): abrir as páginas do gestor de palavras-passe e do preenchimento
// automático. As páginas recusam-se na janela privada, por isso dali abrem numa janela normal.
public partial class MainWindow
{
    internal void OpenVaultPage(string url)
    {
        if (!VaultPages.OpensInNormalWindow(IsPrivate))
        {
            OpenVaultPageHere(url);
            return;
        }
        if (NormalWindows().FirstOrDefault(w => !w._forgetOnCloseDone) is { } normal)
        {
            normal.OpenVaultPageHere(url);
            normal.Activate();
        }
        else OpenWindow(isPrivate: false, url);
    }

    // Reaproveita a aba da mesma página (eteris://autofill/ com outra secção: muda só a secção).
    private void OpenVaultPageHere(string url)
    {
        var page = url.Split('#')[0];
        if (Tabs.Tabs.FirstOrDefault(t => t.Address.StartsWith(page, StringComparison.OrdinalIgnoreCase)) is not { } open)
        {
            Tabs.NewTab(url);
            return;
        }
        Tabs.Active = open;
        if (!string.Equals(open.Address, url, StringComparison.OrdinalIgnoreCase)) LoadUrl(url);
    }
}
