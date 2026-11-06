using System.IO;
using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

public partial class MainWindow
{
    private readonly SessionStore _session = new(Path.Combine(AppPaths.Root, "session.json"));
    private DispatcherTimer? _sessionTimer;

    // Restaura as abas da última sessão; um endereço passado ao abrir junta-se no fim e fica ativo.
    private void StartTabs(string? initialUrl)
    {
        if (_session.Load() is { } previous)
        {
            foreach (var url in previous.Urls) Tabs.NewTab(url, activate: false);
            Tabs.Active = Tabs.Tabs[previous.ActiveIndex];
        }

        if (initialUrl is not null) Tabs.NewTab(initialUrl);
        else if (Tabs.Tabs.Count == 0)
        {
            Tabs.NewTab(NewTabPage.Url);
            Loaded += (_, _) => FocusOmnibox();
        }

        Tabs.Tabs.CollectionChanged += (_, _) => ScheduleSessionSave();
        Tabs.PropertyChanged += (_, _) => ScheduleSessionSave();
        Closing += (_, _) => SaveSession();
    }

    // Chamado também quando o endereço de uma aba muda (OnTabPropertyChanged).
    private void ScheduleSessionSave()
    {
        _sessionTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _sessionTimer!.Stop();
            SaveSession();
        }, Dispatcher);
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void SaveSession()
    {
        try
        {
            var active = Tabs.Active is { } tab ? Tabs.Tabs.IndexOf(tab) : 0;
            _session.Save(Tabs.Tabs.Select(t => t.Address), active);
        }
        catch (IOException) { }
    }
}
