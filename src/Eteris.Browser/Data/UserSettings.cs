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
    public bool HttpsFirst { get; init; } = true;
    public bool BlockThirdPartyCookies { get; init; } = true;
    public bool Mica { get; init; } = true;
    public bool VerticalTabs { get; init; }
    public bool BlockAutoplay { get; init; } = true;
    public bool SearchSuggestions { get; init; }
    public string NewTabTheme { get; init; } = "aurora";
    public bool NewTabClock { get; init; } = true;
    public bool NewTabTiles { get; init; } = true;
    public bool NewTabWeek { get; init; } = true;
    public bool SidebarCollapsed { get; init; }

    public UserSettings? With(string key, string value) => key switch
    {
        "engine" when Engines.Contains(value) => this with { SearchEngine = value },
        "startup" when value is "restore" or "newtab" => this with { Startup = value },
        "sleep" when int.TryParse(value, out var minutes) && minutes >= 0 && minutes <= 24 * 60 => this with { SleepMinutes = minutes },
        "httpsFirst" when bool.TryParse(value, out var on) => this with { HttpsFirst = on },
        "cookies" when bool.TryParse(value, out var on) => this with { BlockThirdPartyCookies = on },
        "mica" when bool.TryParse(value, out var on) => this with { Mica = on },
        "ntTheme" when NewTabThemes.Contains(value) => this with { NewTabTheme = value },
        "ntClock" when bool.TryParse(value, out var on) => this with { NewTabClock = on },
        "ntTiles" when bool.TryParse(value, out var on) => this with { NewTabTiles = on },
        "ntWeek" when bool.TryParse(value, out var on) => this with { NewTabWeek = on },
        "suggest" when bool.TryParse(value, out var on) => this with { SearchSuggestions = on },
        "autoplay" when bool.TryParse(value, out var on) => this with { BlockAutoplay = on },
        "verticalTabs" when bool.TryParse(value, out var on) => this with { VerticalTabs = on },
        "sidebarCollapsed" when bool.TryParse(value, out var on) => this with { SidebarCollapsed = on },
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
