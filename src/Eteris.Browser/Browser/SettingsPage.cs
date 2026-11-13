using System.IO;
using System.Reflection;
using System.Text.Json;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Browser;

// eteris://settings — cada alteração é enviada para eteris://settings/set?k=…&v=…&t=token e aplicada de imediato.
public static class SettingsPage
{
    public const string Url = "eteris://settings/";

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(SettingsPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.settings.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Version
        => typeof(SettingsPage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
           ?? "0.0.0";

    public sealed record About(string Chromium, string CefSharp, bool FullEngine, IReadOnlyList<Shields.ListSummary> Lists,
        string UpdateStatus, bool CanCheck, int Crashes = 0);

    public static string ListsLine(IReadOnlyList<Shields.ListSummary> lists, DateTime nowUtc)
    {
        var rules = lists.Sum(l => l.Rules);
        var newest = lists.Where(l => !l.Bundled).Select(l => l.UpdatedUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        var when = newest == DateTime.MinValue ? "cópia incluída no Éteris"
            : (nowUtc - newest).TotalDays < 1 ? "atualizadas hoje"
            : (int)(nowUtc - newest).TotalDays == 1 ? "atualizadas ontem"
            : $"atualizadas há {(int)(nowUtc - newest).TotalDays} dias";
        return $"{lists.Count} listas · {rules.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("pt-PT"))} regras · {when}";
    }

    // O estado do Google para a página. Numa janela privada só o estado, sem a conta: o texto «Ligado como Nome
    // (e-mail)…» passa a «Ligado ao Google Drive…» e não vai endereço nenhum.
    internal static object SyncGoogleData(Sync.GoogleSync.Status g, bool isPrivate) => isPrivate
        ? new { state = g.State, text = g.State == "signedIn" ? Sync.GoogleSync.SignedInText(null) : g.Text, url = (string?)null, account = false, pending = false }
        : new { state = g.State, text = g.Text, url = g.Url, account = g.Account is not null, pending = g.AccountPending };

    // A conta e a sincronização para a página (ao abrir e em cada resposta das rotas sync-*). Só disco: nenhum pedido.
    // Na pasta de teste (ETERIS_TEST_SYNC_FOLDER) a «conta» é a pasta: «Ligar» e «Sair» sem Google.
    internal static object SyncData(bool isPrivate)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        object google;
        if (Sync.SyncService.FolderMode)
        {
            var on = Sync.SyncService.Connected;
            google = new
            {
                state = on ? "signedIn" : "signedOut",
                text = on ? $"Ligado à pasta de teste «{System.IO.Path.GetFileName(Sync.SyncService.TestFolder)}» (ETERIS_TEST_SYNC_FOLDER; nenhum pedido de rede)."
                    : "Pasta de teste (ETERIS_TEST_SYNC_FOLDER): carregue em «Ligar» para sincronizar por ela.",
                url = (string?)null, account = false, pending = false, folder = true,
            };
        }
        else google = SyncGoogleData(Sync.GoogleSync.Current(), isPrivate);
        return new
        {
            ok = true,
            google,
            available = Sync.SyncService.Available,
            // Protegido: chamado na thread de E/S do CEF, de onde nenhuma exceção pode sair (sem estado: «desligado»).
            sync = isPrivate ? new { phase = "off" } : Sync.SyncService.StatusData(Sync.SyncService.Guarded(() => Sync.SyncService.Status, null), now),
            connected = !isPrivate && Sync.SyncService.Available && Sync.SyncService.Connected,
            enabled = !isPrivate && Sync.SyncService.Enabled,
            hasKey = !isPrivate && Sync.SyncService.Agent()?.HasKey == true,
            // «O que passa entre PCs»: as linhas e o interruptor de cada uma neste computador.
            // A linha da escolha da conta diz se está ligada, onde e quando (T10).
            choices = isPrivate ? null : Sync.SyncChoices.Data(Sync.SyncService.Agent()?.EnabledCollections ?? Eteris.Sync.Model.SyncGroups.DefaultCollections,
                Sync.SyncChoices.AccountChoice.Of(Sync.SyncService.Agent())),
            message = isPrivate ? null : Sync.SyncService.Message,
            // Uma saída da conta a correr («revoke», «lost», «startover»): a página desativa os botões e pergunta amiúde.
            busy = isPrivate ? null : Sync.SyncService.ExitBusy,
        };
    }

    // A página das Definições muda as definições sem reautenticação: desligar «Pedir o Windows Hello ao preencher
    // pagamentos» só se faz em eteris://autofill, que pede o Windows Hello (UserSettings.NeedsReauth).
    public static bool CanSetFromPage(string key, string value) => !UserSettings.NeedsReauth(key, value);

    // isPrivate: as Definições abertas numa janela privada não mostram nada da conta Google (nem nome nem e-mail) e a
    // secção Sincronização fica indisponível (as rotas sync-* também recusam nesse contexto).
    public static string Render(UserSettings settings, About? about = null, bool focusAbout = false,
        IReadOnlyList<ImportSource>? imports = null, IReadOnlyList<SiteSummary>? sites = null, bool isPrivate = false)
    {
        var data = new
        {
            focus = focusAbout ? "sobre" : null,
            chromium = about?.Chromium,
            cefsharp = about?.CefSharp,
            shield = about is null ? null : (about.FullEngine ? "uBlock Origin (adblock-rust) · " : "lista mínima · ") + ListsLine(about.Lists, DateTime.UtcNow),
            updateStatus = about?.UpdateStatus,
            // O registo de falhas (Infrastructure/CrashLog.cs): só aparece com falhas registadas.
            crashLog = about is { Crashes: > 0 }
                ? $"{about.Crashes} {(about.Crashes == 1 ? "falha registada" : "falhas registadas")}, só com o tipo e a pilha: {Path.Combine(AppPaths.Root, CrashLog.FileName)}"
                : null,
            canCheck = about?.CanCheck ?? false,
            token = InternalPages.Token,
            version = Version,
            engine = settings.SearchEngine,
            startup = settings.Startup,
            sleep = settings.SleepMinutes,
            // O ajuste e13 está ligado por omissão: a opção aparece, ligada. Com o ajuste desligado pelo interruptor
            // (ou pela rede de segurança do arranque) a opção não faria nada: null esconde-a.
            sleepPressure = PerfTuning.IsOn(PerfTuning.SleepUnderPressure) ? settings.SleepOnLowMemory : (bool?)null,
            archive = settings.ArchiveDays,
            archiveConfirmed = settings.ArchiveConfirmed,
            httpsFirst = settings.HttpsFirst,
            cookies = settings.BlockThirdPartyCookies,
            mica = settings.Mica,
            autoplay = settings.BlockAutoplay,
            suggest = settings.SearchSuggestions,
            dns = settings.SecureDns,
            clearOnExit = settings.ClearOnExit,
            hideFromCapture = settings.HideFromCapture,
            siteLanguage = settings.SiteLanguage,
            systemLanguage = Shields.RequestIdentity.Language("system"),
            theme = settings.Theme,
            cleanUrls = settings.CleanUrls,
            debounce = settings.SkipTrackingRedirects,
            bounceForget = settings.ForgetBounceTrackers,
            cname = settings.UncloakCname,
            notifications = settings.BlockNotificationPrompts,
            pwOffer = settings.PasswordOffer,
            pwSuggest = settings.PasswordSuggestions,
            pwAskHello = settings.PasswordAskHello,
            imports = (imports ?? []).Select(i => new { id = i.Id, name = i.Name, count = i.Count }),
            sites = (sites ?? []).Select(s => new
            {
                site = s.Site,
                details = new[]
                    {
                        s.ZoomPercent is { } z ? $"zoom {z} %" : null,
                        s.ShieldOff ? "escudo desligado" : null,
                        s.HiddenElements > 0 ? $"{s.HiddenElements} elemento(s) escondido(s)" : null,
                    }.Where(d => d is not null).Concat(s.Flags).Concat(s.Permissions),
            }),
            syncPrivate = isPrivate,
            syncWhat = Sync.SyncService.WhatSyncs,
            syncStays = Sync.SyncService.WhatStays,
            syncInitial = SyncData(isPrivate),
            tabs = settings.VerticalTabs ? "side" : "top",
            density = settings.Density,
            accent = settings.Accent,
            accents = Theme.Accent.Palette.Select(c => new { key = c.Key, name = c.Name, color = Theme.Accent.Hex(c.Color) }),
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
