using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;
using Eteris.Browser.Vault;

namespace Eteris.Browser;

// O cofre na janela (docs/palavras-passe.md): abrir as páginas do gestor de palavras-passe e do preenchimento
// automático, e o que essas páginas pedem ao processo do navegador (WindowsVaultHost). As páginas recusam-se na janela
// privada, por isso dali abrem numa janela normal.
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

    // Pedidos de reautenticação à espera: a aba que os fez saiu da frente (outra aba, outra janela fechou-a) → desistem.
    private static readonly List<CancellationTokenSource> s_vaultFront = [];

    internal static CancellationToken VaultFrontToken()
    {
        var source = new CancellationTokenSource();
        lock (s_vaultFront)
        {
            s_vaultFront.RemoveAll(s => s.IsCancellationRequested);
            s_vaultFront.Add(source);
        }
        return source.Token;
    }

    private static void CancelVaultRequests()
    {
        List<CancellationTokenSource> all;
        lock (s_vaultFront)
        {
            all = [.. s_vaultFront];
            s_vaultFront.Clear();
        }
        foreach (var source in all) source.Cancel();
    }

    // A janela fechou: os pedidos dela desistem e a janela de graça dela acaba.
    private void ForgetVaultWindow()
    {
        CancelVaultRequests();
        ReauthGate.ForgetWindowIfCreated(new WindowInteropHelper(this).Handle);
    }

    internal void OpenTabFromVault(string url) => Tabs.NewTab(url);

    // ---- O canal com o script do cofre em cada aba (Vault/VaultChannel.cs) ----

    // Lido e escrito na thread do CEF (commit, fim do carregamento) e na da janela (fechar, adormecer).
    private readonly Dictionary<BrowserTab, VaultChannel> _vaultChannels = [];
    private readonly Lock _vaultChannelsGate = new();

    // Thread do CEF: commit ou fim do carregamento do frame de topo. Só páginas http(s); o canal nasce na 1.ª e o cofre
    // não abre (só abre quando um campo pede sugestões).
    private void VaultPageCommitted(BrowserTab tab, ChromiumWebBrowser browser)
    {
        if (browser.IsDisposed || browser.GetBrowser() is not { } cef) return;
        var url = SafeFrame.MainUrl(cef);
        if (VaultChannel.EligibleOrigin(url, url) is null) return;
        VaultChannel channel;
        lock (_vaultChannelsGate)
        {
            if (!_vaultChannels.TryGetValue(tab, out var known) || known.IsDisposed)
                _vaultChannels[tab] = known = new VaultChannel(cef,
                    (context, message) => Dispatcher.InvokeAsync(() => OnVaultMessage(tab, context, message)),
                    () => Dispatcher.InvokeAsync(() => OnVaultNavigated(tab)));
            channel = known;
        }
        _ = Task.Run(channel.Refresh);
    }

    // A aba fechou, adormeceu ou passou para o motor de vídeo.
    private void ReleaseVaultChannel(BrowserTab tab)
    {
        VaultChannel? channel;
        lock (_vaultChannelsGate)
        {
            if (!_vaultChannels.Remove(tab, out channel)) return;
        }
        channel.Dispose();
    }

    private VaultChannel? VaultChannelOf(BrowserTab tab)
    {
        lock (_vaultChannelsGate) return _vaultChannels.GetValueOrDefault(tab);
    }

    // Uma mensagem validada do script (thread da janela).
    private void OnVaultMessage(BrowserTab tab, VaultContext context, VaultMessage message)
    {
    }

    // O frame de topo mudou de documento (thread da janela).
    private void OnVaultNavigated(BrowserTab tab)
    {
    }
}

// O que as páginas do cofre pedem ao processo do navegador, feito na thread da interface: a janela dona, os diálogos
// de ficheiro nativos (o CSV nunca entra na página), a confirmação de uma pasta na nuvem, a Reciclagem, a área de
// transferência segura e o esconder das capturas.
internal sealed class WindowsVaultHost : IVaultHost
{
    private static System.Windows.Threading.Dispatcher Ui => Application.Current.Dispatcher;

    private static MainWindow? Owner(int browserId)
        => MainWindow.WindowOf(browserId) ?? Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive);

    public Task<nint> WindowAsync(int browserId)
        => Ui.InvokeAsync(() => Owner(browserId) is { } window ? new WindowInteropHelper(window).Handle : 0).Task;

    public CancellationToken WhileInFront(int browserId) => MainWindow.VaultFrontToken();

    public Task<string?> PickImportFileAsync(int browserId) => Ui.InvokeAsync(() =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importar palavras-passe",
            Filter = "Ficheiros CSV (*.csv)|*.csv|Todos os ficheiros (*.*)|*.*",
            CheckFileExists = true,
        };
        return (Owner(browserId) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }).Task;

    public Task<string?> PickExportFileAsync(int browserId, string suggestedName) => Ui.InvokeAsync(() =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar palavras-passe",
            Filter = "Ficheiros CSV (*.csv)|*.csv",
            FileName = suggestedName,
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return (Owner(browserId) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }).Task;

    public Task<bool> ConfirmAsync(int browserId, string title, string message) => Ui.InvokeAsync(() =>
    {
        var answer = Owner(browserId) is { } owner
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }).Task;

    public bool Recycle(string path) => RecycleBin.Send(path);

    public bool CopySecret(string text) => SecureClipboard.Shared.Copy(text);

    public IDisposable ShowSecret() => SecretOnScreen.Show();

    public void OpenTab(int browserId, string url) => Ui.InvokeAsync(() => Owner(browserId)?.OpenTabFromVault(url));

    public IReadOnlyDictionary<string, string> Icons(IEnumerable<string> urls) => AppData.SiteIcons(urls);

    public UserSettings Settings => Data.Settings.Current;

    public bool SetSetting(string key, string value) => Data.Settings.Set(key, value);

    public IDisposable Schedule(TimeSpan delay, Action run) => Timers.Schedule(delay, run);
}

// «Enviar o ficheiro CSV para a Reciclagem» depois de importar (o ficheiro tem todas as palavras-passe em claro).
internal static class RecycleBin
{
    private const uint Delete = 3;
    private const ushort AllowUndo = 0x40, NoConfirmation = 0x10, Silent = 0x4, NoErrorUi = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public nint Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        public int Aborted;
        public nint NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref FileOperation operation);

    public static bool Send(string path)
    {
        // A lista de ficheiros termina com dois nulos (o marshaller junta um).
        var operation = new FileOperation { Function = Delete, From = path + "\0", Flags = (ushort)(AllowUndo | NoConfirmation | Silent | NoErrorUi) };
        return SHFileOperation(ref operation) == 0 && operation.Aborted == 0 && !System.IO.File.Exists(path);
    }
}
