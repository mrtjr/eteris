using System.IO;
using System.Text.Json;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Data;

// Preferências do utilizador (eteris://settings). Valores validados: a página só consegue escolher opções conhecidas.
public sealed record UserSettings
{
    public static readonly string[] Engines = ["duckduckgo", "brave", "startpage"];
    public static readonly int[] SleepChoices = [0, 5, 20, 60];
    public static readonly string[] NewTabThemes = ["aurora", "ocean", "sunset", "forest", "minimal"];

    public string SearchEngine { get; init; } = "duckduckgo";
    public string Startup { get; init; } = "restore";
    public int SleepMinutes { get; init; } = 20;
    // Arquivo automático: abas sem visita há N dias vão para o Arquivo (0 = nunca).
    public int ArchiveDays { get; init; } = Tabs.ArchivePolicy.DefaultDays;
    // O utilizador escolheu o prazo em Definições › Abas. Até lá as abas esquecidas só desvanecem: o Éteris não
    // fecha abas sozinho sem o utilizador o ter pedido.
    public bool ArchiveConfirmed { get; init; }
    public bool HttpsFirst { get; init; } = true;
    public bool BlockThirdPartyCookies { get; init; } = true;
    public bool Mica { get; init; } = true;
    public bool VerticalTabs { get; init; }
    // «normal» (abas numa barra, endereço noutra) ou «compact» (abas e endereço numa só linha).
    public string Density { get; init; } = "normal";
    // Cor do Éteris: uma chave da paleta (Theme/Accent.cs), «wallpaper» (da paisagem) ou «windows» (do sistema).
    public string Accent { get; init; } = global::Eteris.Browser.Theme.Accent.Default;
    public bool BlockAutoplay { get; init; } = true;
    public bool SearchSuggestions { get; init; }
    public string SecureDns { get; init; } = "off";
    public bool ClearOnExit { get; init; }
    public string Theme { get; init; } = "dark";
    public bool CleanUrls { get; init; } = true;
    // Links que passam por um rastreador com o destino no endereço vão direto ao destino (Shields/Debounce.cs).
    public bool SkipTrackingRedirects { get; init; } = true;
    public bool BlockNotificationPrompts { get; init; } = true;
    // Esconder as janelas das capturas de ecrã e do Recall (Infrastructure/CaptureShield.cs). Desligado por omissão,
    // também na janela privada: ligado, as capturas, gravações e partilhas de ecrã mostram um vazio.
    public bool HideFromCapture { get; init; }
    // Língua enviada aos sites (Accept-Language e navigator.languages): «pt-PT», «system» ou «en-US», sempre uma só.
    public string SiteLanguage { get; init; } = "pt-PT";
    public string LastSeenVersion { get; init; } = string.Empty;
    public string NewTabTheme { get; init; } = "aurora";
    public string NewTabWallpaper { get; init; } = "coast";
    public string NewTabWallpaperMotion { get; init; } = "photo";
    public bool NewTabClock { get; init; } = true;
    public bool NewTabTiles { get; init; } = true;
    public bool NewTabWeek { get; init; } = true;
    public bool NewTabTips { get; init; } = true;
    public bool SidebarCollapsed { get; init; }
    // Barra lateral unificada (Ctrl+Shift+B): aberta ao arrancar, último painel, largura e lado (Sidebar/SidebarPanels.cs).
    public bool Sidebar { get; init; }
    public string SidebarPanel { get; init; } = global::Eteris.Browser.Sidebar.SidebarPanels.Notes;
    public int SidebarWidth { get; init; } = (int)global::Eteris.Browser.Sidebar.SidebarPanels.DefaultWidth;
    public string SidebarSide { get; init; } = "right";
    public string ReaderTheme { get; init; } = "dark";
    public int ReaderSize { get; init; } = 20;
    public bool ReaderSerif { get; init; } = true;

    public UserSettings? With(string key, string value) => key switch
    {
        "engine" when Engines.Contains(value) => this with { SearchEngine = value },
        "startup" when value is "restore" or "newtab" => this with { Startup = value },
        "sleep" when int.TryParse(value, out var minutes) && minutes >= 0 && minutes <= 24 * 60 => this with { SleepMinutes = minutes },
        "archive" when int.TryParse(value, out var days) && days >= 0 && days <= 365 => this with { ArchiveDays = days, ArchiveConfirmed = true },

        "httpsFirst" when bool.TryParse(value, out var on) => this with { HttpsFirst = on },
        "cookies" when bool.TryParse(value, out var on) => this with { BlockThirdPartyCookies = on },
        "mica" when bool.TryParse(value, out var on) => this with { Mica = on },
        "ntTheme" when NewTabThemes.Contains(value) => this with { NewTabTheme = value },
        "ntWallpaper" when value is "coast" or "mountains" or "none" => this with { NewTabWallpaper = value },
        "ntWallpaperMotion" when value is "photo" or "live" or "live-always" => this with { NewTabWallpaperMotion = value },
        "ntClock" when bool.TryParse(value, out var on) => this with { NewTabClock = on },
        "ntTiles" when bool.TryParse(value, out var on) => this with { NewTabTiles = on },
        "ntWeek" when bool.TryParse(value, out var on) => this with { NewTabWeek = on },
        "ntTips" when bool.TryParse(value, out var on) => this with { NewTabTips = on },
        "notifications" when bool.TryParse(value, out var on) => this with { BlockNotificationPrompts = on },
        "cleanUrls" when bool.TryParse(value, out var on) => this with { CleanUrls = on },
        "debounce" when bool.TryParse(value, out var on) => this with { SkipTrackingRedirects = on },
        "hideFromCapture" when bool.TryParse(value, out var on) => this with { HideFromCapture = on },
        "siteLanguage" when Shields.RequestIdentity.LanguageChoices.Contains(value) => this with { SiteLanguage = value },
        "theme" when value is "dark" or "light" or "system" => this with { Theme = value },
        "clearOnExit" when bool.TryParse(value, out var on) => this with { ClearOnExit = on },
        "dns" when value == "off" || Shields.SecureDns.Providers.ContainsKey(value) => this with { SecureDns = value },
        "suggest" when bool.TryParse(value, out var on) => this with { SearchSuggestions = on },
        "autoplay" when bool.TryParse(value, out var on) => this with { BlockAutoplay = on },
        "verticalTabs" when bool.TryParse(value, out var on) => this with { VerticalTabs = on },
        "density" when value is "normal" or "compact" => this with { Density = value },
        "accent" when global::Eteris.Browser.Theme.Accent.IsValidChoice(value) => this with { Accent = value },
        "sidebarCollapsed" when bool.TryParse(value, out var on) => this with { SidebarCollapsed = on },
        "sidebar" when bool.TryParse(value, out var on) => this with { Sidebar = on },
        "sidebarPanel" when global::Eteris.Browser.Sidebar.SidebarPanels.IsKnownKey(value) => this with { SidebarPanel = value },
        "sidebarWidth" when int.TryParse(value, out var px) && px >= global::Eteris.Browser.Sidebar.SidebarPanels.MinWidth
                             && px <= global::Eteris.Browser.Sidebar.SidebarPanels.MaxWidth => this with { SidebarWidth = px },
        "sidebarSide" when value is "left" or "right" => this with { SidebarSide = value },
        "readerTheme" when value is "dark" or "sepia" or "light" => this with { ReaderTheme = value },
        "readerSize" when int.TryParse(value, out var px) && px >= 15 && px <= 28 => this with { ReaderSize = px },
        "readerSerif" when bool.TryParse(value, out var on) => this with { ReaderSerif = on },
        _ => null,
    };
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public UserSettings Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    public void Save(UserSettings settings)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
    }
}

// Definições em vigor, partilhadas por todas as janelas do processo.
public static class Settings
{
    private static readonly Lock Gate = new();
    private static SettingsStore? _store;
    private static UserSettings? _current;

    public static event Action<UserSettings>? Changed;

    private static SettingsStore Store => _store ??= new SettingsStore(Path.Combine(AppPaths.Root, "settings.json"));

    // Sem ficheiro de definições: é a primeira vez que o Éteris abre (neste perfil).
    public static bool IsFirstRun => !File.Exists(Path.Combine(AppPaths.Root, "settings.json"));

    // Grava as definições atuais (cria o ficheiro): o acolhimento não volta a aparecer.
    public static void MarkWelcomed()
    {
        lock (Gate)
        {
            try { Store.Save(Current); } catch (Exception) { }
        }
    }

    // A versão cujas novidades já foram mostradas (fora do With: não é uma escolha da página).
    public static void MarkVersionSeen(string version)
    {
        UserSettings updated;
        lock (Gate)
        {
            _current = updated = Current with { LastSeenVersion = version };
            try { Store.Save(updated); } catch (Exception) { }
        }
    }

    public static UserSettings Current
    {
        get { lock (Gate) return _current ??= Store.Load(); }
    }

    public static bool Set(string key, string value)
    {
        UserSettings updated;
        lock (Gate)
        {
            if (Current.With(key, value) is not { } next) return false;
            _current = updated = next;
            try { Store.Save(next); } catch (Exception) { }
        }
        Changed?.Invoke(updated);
        return true;
    }
}
