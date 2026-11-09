using Eteris.Browser.Data;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Browser;

// E12 do plano de desempenho da 0.2.14 (ajuste e12, desligado por omissão: regra R-2). A nova aba era gerada no momento
// do pedido, na thread de E/S do motor: 6 consultas SQL, o JSON e o modelo, dentro da cadeia cef.navegar → aba.1.commit.
// Com o ajuste, a 1.ª nova aba do perfil começa a ser gerada no arranque, em fundo (App.OnStartup), e o 1.º pedido usa-a se
// ainda vale; senão, gera-se como sempre. Nunca na janela privada (o contexto privado nem chega a perguntar) e só uma vez.
public static class NewTabPrecompute
{
    // Quanto tempo a cópia pré-gerada vale: o suficiente para a 1.ª janela pedir a nova aba, pouco para os dados mudarem.
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    // Quanto o pedido espera pela cópia que ainda se está a fazer (o mesmo trabalho que faria sozinho).
    public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(250);

    public sealed record Ready(string Html, UserSettings Prefs, string Engine, int Blocked, DateTime At);

    private static Task<Ready?>? _pending;

    public static void Start(Func<DateTime> clock)
    {
        if (_pending is not null) return;
        _pending = Task.Run(() =>
        {
            try
            {
                var prefs = Settings.Current;
                var engine = SearchEngine.ById(prefs.SearchEngine);
                var now = clock();
                var blocked = Shields.ShieldStats.SessionBlocked;
                return new Ready(Render(engine, now, prefs, blocked), prefs, engine.Name, blocked, now);
            }
            catch (Exception) { return null; }
        });
    }

    // A nova aba do perfil (não privada), como sempre foi gerada.
    public static string Render(SearchEngine engine, DateTime now, UserSettings prefs, int blocked)
    {
        var sites = AppData.TryRead(h => h.TopSites(8));
        var bookmarks = AppData.Bookmarks(b => b.All(10));
        return NewTabPage.Render(sites, engine, now,
            blocked: blocked, bookmarks: bookmarks,
            week: AppData.ShieldWeek(), prefs: prefs,
            reading: AppData.ReadingList(), sessions: AppData.SavedSessions(),
            icons: AppData.SiteIcons(sites.Select(s => s.Url).Concat(bookmarks.Select(b => b.Url))));
    }

    // A cópia pré-gerada, uma só vez (a 1.ª nova aba), se ainda vale para estas definições, este motor de pesquisa, este
    // dia, esta contagem do Escudo e dentro do MaxAge. null: gerar como sempre.
    public static string? Take(UserSettings prefs, string engine, DateTime now, int blocked)
    {
        var pending = Interlocked.Exchange(ref _pending, null);
        if (pending is null) return null;
        try
        {
            if (!pending.Wait(MaxWait)) return null;
        }
        catch (Exception) { return null; }
        return pending.Result is { } ready && Valid(ready, prefs, engine, now, blocked) ? ready.Html : null;
    }

    // As definições são um record imutável: outra instância é outra escolha (Settings.Set troca-a).
    public static bool Valid(Ready ready, UserSettings prefs, string engine, DateTime now, int blocked)
        => ReferenceEquals(ready.Prefs, prefs) && ready.Engine == engine && ready.Blocked == blocked
           && ready.At.Date == now.Date && now >= ready.At && now - ready.At <= MaxAge;
}
