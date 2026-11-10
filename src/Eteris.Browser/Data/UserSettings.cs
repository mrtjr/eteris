using System.IO;
using System.Text.Json;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Data;

// Preferências do utilizador (eteris://settings). Valores validados: a página só consegue escolher opções conhecidas.
// As propriedades têm set e não init por causa do código gerado (ajuste e12, Data/StartupJson.cs): com init, o gerador
// do System.Text.Json cria o record com todas no inicializador e as que faltam no ficheiro ficavam a default(T) (um
// settings.json antigo perdia o SleepOnLowMemory = true, o SearchEngine e o resto). Com set, só as que estão no ficheiro
// são escritas, como na leitura por reflexão. Ninguém as muda depois: as alterações passam pelo With (cópias com with).
public sealed record UserSettings
{
    public static readonly string[] Engines = ["duckduckgo", "brave", "startpage"];
    public static readonly int[] SleepChoices = [0, 5, 20, 60];
    public static readonly string[] NewTabThemes = ["aurora", "ocean", "sunset", "forest", "minimal"];

    public string SearchEngine { get; set; } = "duckduckgo";
    public string Startup { get; set; } = "restore";
    public int SleepMinutes { get; set; } = 20;
    // Adormecer também com pouca memória (ajuste e13, Tabs/MemoryPressure.cs; ligado por omissão, decisão D-6);
    // desligado = só o tempo conta.
    public bool SleepOnLowMemory { get; set; } = true;
    // Arquivo automático: abas sem visita há N dias vão para o Arquivo (0 = nunca).
    public int ArchiveDays { get; set; } = Tabs.ArchivePolicy.DefaultDays;
    // O utilizador escolheu o prazo em Definições › Abas. Até lá as abas esquecidas só desvanecem: o Éteris não
    // fecha abas sozinho sem o utilizador o ter pedido.
    public bool ArchiveConfirmed { get; set; }
    public bool HttpsFirst { get; set; } = true;
    public bool BlockThirdPartyCookies { get; set; } = true;
    public bool Mica { get; set; } = true;
    public bool VerticalTabs { get; set; }
    // «normal» (abas numa barra, endereço noutra) ou «compact» (abas e endereço numa só linha).
    public string Density { get; set; } = "normal";
    // Cor do Éteris: uma chave da paleta (Theme/Accent.cs), «wallpaper» (da paisagem) ou «windows» (do sistema).
    public string Accent { get; set; } = global::Eteris.Browser.Theme.Accent.Default;
    public bool BlockAutoplay { get; set; } = true;
    public bool SearchSuggestions { get; set; }
    public string SecureDns { get; set; } = "off";
    public bool ClearOnExit { get; set; }
    public string Theme { get; set; } = "dark";
    public bool CleanUrls { get; set; } = true;
    // Links que passam por um rastreador com o destino no endereço vão direto ao destino (Shields/Debounce.cs).
    public bool SkipTrackingRedirects { get; set; } = true;
    // Sites que só serviram de salto (sem visita a sério há 45 dias) perdem cookies e armazenamento ao fim de 1 hora.
    public bool ForgetBounceTrackers { get; set; } = true;
    // Rastreadores escondidos atrás de um subdomínio do próprio site (CNAME) são bloqueados (Shields/CnameUncloak.cs).
    public bool UncloakCname { get; set; } = true;
    public bool BlockNotificationPrompts { get; set; } = true;
    // Esconder as janelas das capturas de ecrã e do Recall (Infrastructure/CaptureShield.cs). Desligado por omissão,
    // também na janela privada: ligado, as capturas, gravações e partilhas de ecrã mostram um vazio.
    public bool HideFromCapture { get; set; }
    // Língua enviada aos sites (Accept-Language e navigator.languages): «pt-PT», «system» ou «en-US», sempre uma só.
    // Por omissão a do Windows (medido, docs/paridade.md #6): pt-PT fixo num Windows pt-BR com fuso de São Paulo era
    // uma combinação rara (13,49 bits no Cover Your Tracks, contra 7,92 do Chrome).
    public string SiteLanguage { get; set; } = "system";
    // A língua foi escolhida nas Definições. Até à 0.2.9 o pt-PT era o valor por omissão e ficava gravado como se fosse
    // escolhido: sem esta marca, um pt-PT gravado passa à predefinida (SettingsStore.Load).
    public bool SiteLanguageChosen { get; set; }
    public string LastSeenVersion { get; set; } = string.Empty;
    public string NewTabTheme { get; set; } = "aurora";
    public string NewTabWallpaper { get; set; } = "coast";
    public string NewTabWallpaperMotion { get; set; } = "photo";
    public bool NewTabClock { get; set; } = true;
    public bool NewTabTiles { get; set; } = true;
    public bool NewTabWeek { get; set; } = true;
    public bool NewTabTips { get; set; } = true;
    public bool SidebarCollapsed { get; set; }
    // Barra lateral unificada (Ctrl+Shift+B): aberta ao arrancar, último painel, largura e lado (Sidebar/SidebarPanels.cs).
    public bool Sidebar { get; set; }
    public string SidebarPanel { get; set; } = global::Eteris.Browser.Sidebar.SidebarPanels.Notes;
    public int SidebarWidth { get; set; } = (int)global::Eteris.Browser.Sidebar.SidebarPanels.DefaultWidth;
    public string SidebarSide { get; set; } = "right";
    public string ReaderTheme { get; set; } = "dark";
    public int ReaderSize { get; set; } = 20;
    public bool ReaderSerif { get; set; } = true;
    // Palavras-passe e preenchimento automático (docs/palavras-passe.md, B1). Oferecer para guardar e as sugestões ao
    // clicar num campo viajam entre computadores; «Pedir o Windows Hello…» é deste computador (D-5: desligado para as
    // palavras-passe, como o Chrome e o Firefox; ligado para pagamentos e documentos).
    public bool PasswordOffer { get; set; } = true;
    public bool PasswordSuggestions { get; set; } = true;
    public bool PasswordAskHello { get; set; } = Vault.ReauthPolicy.DefaultAskWhenFillingPasswords;
    public bool PaymentsAskHello { get; set; } = Vault.ReauthPolicy.DefaultAskWhenFillingPayments;
    // «Preencher automaticamente…» de cada secção de eteris://autofill: só controla as sugestões (nada se preenche sem
    // o utilizador escolher).
    public bool AutofillPayments { get; set; } = true;
    public bool AutofillContacts { get; set; } = true;
    public bool AutofillDocuments { get; set; } = true;
    public bool AutofillTravel { get; set; } = true;

    // Desligar «Pedir o Windows Hello ao preencher pagamentos» ou «… palavras-passe» também pede o Windows Hello
    // (ReauthPurpose.ChangePaymentSetting / ChangePasswordSetting): só se mudam nas páginas do cofre.
    public static bool NeedsReauth(string key, string value) => key is "payAskHello" or "pwAskHello" && value == "false";
    // (A opção experimental «SyncPreview» da 0.2.10–0.2.12 saiu: a secção Sincronização e a conta aparecem sempre que
    // há o identificador da aplicação Google. Um settings.json antigo com ela continua a abrir; o valor é ignorado e a
    // sessão Google e o resto ficam como estavam.)

    public UserSettings? With(string key, string value) => key switch
    {
        "engine" when Engines.Contains(value) => this with { SearchEngine = value },
        "startup" when value is "restore" or "newtab" => this with { Startup = value },
        "sleep" when int.TryParse(value, out var minutes) && minutes >= 0 && minutes <= 24 * 60 => this with { SleepMinutes = minutes },
        "sleepPressure" when bool.TryParse(value, out var on) => this with { SleepOnLowMemory = on },
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
        "bounceForget" when bool.TryParse(value, out var on) => this with { ForgetBounceTrackers = on },
        "cname" when bool.TryParse(value, out var on) => this with { UncloakCname = on },
        "hideFromCapture" when bool.TryParse(value, out var on) => this with { HideFromCapture = on },
        "siteLanguage" when Shields.RequestIdentity.LanguageChoices.Contains(value) => this with { SiteLanguage = value, SiteLanguageChosen = true },
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
        "pwOffer" when bool.TryParse(value, out var on) => this with { PasswordOffer = on },
        "pwSuggest" when bool.TryParse(value, out var on) => this with { PasswordSuggestions = on },
        "pwAskHello" when bool.TryParse(value, out var on) => this with { PasswordAskHello = on },
        "payAskHello" when bool.TryParse(value, out var on) => this with { PaymentsAskHello = on },
        "afPayments" when bool.TryParse(value, out var on) => this with { AutofillPayments = on },
        "afContacts" when bool.TryParse(value, out var on) => this with { AutofillContacts = on },
        "afDocuments" when bool.TryParse(value, out var on) => this with { AutofillDocuments = on },
        "afTravel" when bool.TryParse(value, out var on) => this with { AutofillTravel = on },
        _ => null,
    };
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public UserSettings Load()
    {
        try { return File.Exists(path) ? Migrate(Read(File.ReadAllText(path)) ?? new()) : new(); }
        catch (Exception) { return new(); }
    }

    // Ajuste e12: lida com o código gerado (Data/StartupJson.cs); sem ele, por reflexão, como sempre. O mesmo resultado.
    public static UserSettings? Read(string json)
        => Infrastructure.PerfTuning.IsOn(Infrastructure.PerfTuning.ReadyNewTab)
            ? JsonSerializer.Deserialize(json, StartupJsonContext.Default.UserSettings)
            : JsonSerializer.Deserialize<UserSettings>(json);

    // O pt-PT que a 0.2.9 e anteriores gravavam por omissão passa à língua do Windows; o escolhido nas Definições fica.
    public static UserSettings Migrate(UserSettings settings)
        => !settings.SiteLanguageChosen && settings.SiteLanguage == "pt-PT" ? settings with { SiteLanguage = "system" } : settings;

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
