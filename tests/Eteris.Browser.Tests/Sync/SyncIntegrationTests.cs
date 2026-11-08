using System.IO;
using System.Security.Cryptography;
using System.Text;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Sync;
using Eteris.Sync.Model;

namespace Eteris.Browser.Tests.Sync;

// A ligação da biblioteca ao Éteris: desligada por omissão, definições que viajam, DPAPI, navegador do sistema.
public class SyncIntegrationTests
{
    [Fact]
    public void SyncPreview_IsOffByDefault_AndOnlyTheSwitchTurnsItOn()
    {
        Assert.False(new UserSettings().SyncPreview);
        Assert.True(new UserSettings().With("syncPreview", "true")!.SyncPreview);
        Assert.Null(new UserSettings().With("syncPreview", "talvez"));
    }

    [Fact]
    public void SettingsPage_HasTheSyncSection_HiddenUntilTheSwitch()
    {
        var html = SettingsPage.Render(new UserSettings());
        Assert.Contains("<section id=\"sincronizacao\">", html);
        Assert.Contains("href=\"#sincronizacao\"", html);
        Assert.Contains("id=\"syncBody\" hidden", html);
        Assert.Contains("\"syncPreview\":false", html);
        // Sem google-oauth.json: «por configurar», e o botão fica desativado.
        Assert.Contains("\"state\":\"unconfigured\"", html);
        // O código e o ficheiro do perfil vão no corpo de um POST, nunca no endereço.
        Assert.Contains("method=\"post\"", html);
        Assert.DoesNotContain("sync-import?code", html);
    }

    [Fact]
    public void EverySyncableSetting_IsReadAndAcceptedBackByTheSettings()
    {
        var settings = new UserSettings() with { ArchiveConfirmed = true, SiteLanguageChosen = true, SiteLanguage = "en-US" };
        var values = ProfileTransfer.SettingValues(settings);
        Assert.Equal(Collections.SyncableSettings.Order(), values.Keys.Order());
        foreach (var (key, value) in values)
            Assert.True(settings.With(key, value) == settings, $"{key}={value} não volta igual");
        // Nunca viajam: o que é deste computador.
        foreach (var local in new[] { "mica", "hideFromCapture", "sidebar", "sidebarWidth", "sidebarSide", "sidebarPanel", "sidebarCollapsed", "syncPreview" })
            Assert.DoesNotContain(local, Collections.SyncableSettings);
    }

    [Fact]
    public void SettingsChosenOnlyByDefault_DoNotTravel()
    {
        var values = ProfileTransfer.SettingValues(new UserSettings());
        Assert.False(values.ContainsKey("archive"));
        Assert.False(values.ContainsKey("siteLanguage"));
    }

    [Fact]
    public void ImportedNotes_GoBelowTheCurrentOnes_NothingLost()
    {
        Assert.Equal("importadas", ProfileTransfer.MergeNotes("", "importadas", "PC", new DateTime(2026, 11, 8)));
        Assert.Equal("a\nb", ProfileTransfer.MergeNotes("a\nb", "b", "PC", new DateTime(2026, 11, 8)));
        var merged = ProfileTransfer.MergeNotes("minhas\n", "do outro", "PC da sala", new DateTime(2026, 11, 8));
        Assert.Equal("minhas\n\n— Notas importadas de «PC da sala» (08/11/2026) —\ndo outro", merged);
    }

    [Fact]
    public void ImportSummary_IsPlainPortuguese()
    {
        Assert.Equal("Perfil de «PC» importado: 3 favoritos, 1 definição, as notas.",
            ProfileTransfer.Summary(new ProfileTransfer.Imported(3, 1, 0, 0, true, "PC")));
        Assert.Equal("Perfil lido: nada de novo para juntar.", ProfileTransfer.Summary(new ProfileTransfer.Imported(0, 0, 0, 0, false, "")));
        Assert.EndsWith(".eteris", ProfileTransfer.FileName(new DateTime(2026, 11, 8)));
    }

    [Fact]
    public void ImportSummary_SaysWhatWasHeldBack()
    {
        var held = new ProfileTransfer.Imported(1, 0, 0, 0, false, "PC") { Held = ["Desligar «HTTPS primeiro»", "Desligar o Escudo em x.pt"] };
        Assert.Equal("Perfil de «PC» importado: 1 favorito. 2 alterações baixariam a proteção deste computador e ficaram por aplicar.",
            ProfileTransfer.Summary(held));
    }

    [Fact]
    public void ProfileImport_WhatLowersProtection_WaitsForConfirmation()
    {
        var dns = Eteris.Browser.Shields.SecureDns.Providers.Keys.First();
        var current = new UserSettings() with
        {
            HttpsFirst = true, BlockThirdPartyCookies = true, CleanUrls = true, SkipTrackingRedirects = true, ForgetBounceTrackers = true,
            UncloakCname = true, BlockNotificationPrompts = true, ClearOnExit = true, SearchSuggestions = false, SecureDns = dns,
        };
        foreach (var key in new[] { "httpsFirst", "cookies", "cleanUrls", "debounce", "bounceForget", "cname", "notifications", "clearOnExit" })
        {
            Assert.NotNull(ProfileTransfer.WeakensSetting(current, key, "false"));
            Assert.Null(ProfileTransfer.WeakensSetting(current, key, "true"));
        }
        Assert.NotNull(ProfileTransfer.WeakensSetting(current, "suggest", "true"));
        Assert.Contains("DNS cifrado", ProfileTransfer.WeakensSetting(current, "dns", "off"));
        Assert.Null(ProfileTransfer.WeakensSetting(current with { SecureDns = "off" }, "dns", dns)); // ligar protege
        Assert.Null(ProfileTransfer.WeakensSetting(current, "theme", "light"));                     // gosto, não proteção
        // O que já está igual nunca é «baixar».
        foreach (var (key, value) in ProfileTransfer.SettingValues(current))
            Assert.Null(ProfileTransfer.WeakensSetting(current, key, value));

        Assert.Contains("exemplo.pt", ProfileTransfer.WeakensShield("off", "exemplo.pt"));
        Assert.Contains("HTTP", ProfileTransfer.WeakensShield("http", "exemplo.pt"));
        Assert.Null(ProfileTransfer.WeakensShield("strict", "exemplo.pt"));
        Assert.Null(ProfileTransfer.WeakensShield("hide", "exemplo.pt"));

        // A página mostra a lista e só reenvia com «weaker=1» quando o utilizador carrega no botão.
        var html = SettingsPage.Render(new UserSettings());
        Assert.Contains("id=\"importWeaker\"", html);
        Assert.Contains("name=\"weaker\" id=\"syncWeaker\" value=\"0\"", html);
        var handler = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
        Assert.Contains("form.GetValueOrDefault(\"weaker\") == \"1\"", handler);
    }

    [Fact]
    public void ProfileExport_LeavesOpenTabsOut_AndTheSettingsSayWhatGoes()
    {
        // URLs de abas podem ter tokens (redefinição de senha, sessão), e a importação não os usava.
        var code = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Sync", "ProfileTransfer.cs"));
        Assert.DoesNotContain("Collections.Tabs", code);
        Assert.DoesNotContain("session.Urls", code);
        Assert.Contains("as abas abertas não vão", SettingsPage.Render(new UserSettings()));
    }

    [Fact]
    public void Dpapi_RoundTrips_AndPurposesDoNotMix()
    {
        var secret = RandomNumberGenerator.GetBytes(64);
        var tokens = new Dpapi("google-tokens");
        var sealedBytes = tokens.Protect(secret);
        Assert.False(sealedBytes.AsSpan().IndexOf(secret) >= 0);
        Assert.Equal(secret, tokens.Unprotect(sealedBytes));
        Assert.ThrowsAny<CryptographicException>(() => new Dpapi("outro-uso").Unprotect(sealedBytes));
        Assert.Equal([], tokens.Unprotect(tokens.Protect([])));
    }

    [Fact]
    public void SystemBrowser_RecognisesEteris_AndPrefersAnotherRealBrowser()
    {
        Assert.True(SystemBrowser.IsEteris("EterisHTML", null));
        Assert.True(SystemBrowser.IsEteris("XYZ123", "\"C:\\Users\\a\\AppData\\Local\\EterisBrowser\\current\\Eteris.Browser.exe\" -- \"%1\""));
        Assert.False(SystemBrowser.IsEteris("FirefoxURL-308046B0AF4A39CB", "\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -osint -url \"%1\""));
        Assert.Equal("C:\\b\\firefox.exe", SystemBrowser.Choose([("Opera", "C:\\a\\opera.exe"), ("FIREFOX.EXE", "C:\\b\\firefox.exe")]));
        Assert.Equal("C:\\a\\opera.exe", SystemBrowser.Choose([("Opera", "C:\\a\\opera.exe")]));
        Assert.Null(SystemBrowser.Choose([]));
        Assert.Null(SystemBrowser.Executable("\"C:\\sem fim.exe"));
        Assert.Null(SystemBrowser.Executable("C:\\nao\\existe.exe --x"));
        // Só o ecrã de consentimento da Google pode ser aberto por aqui.
        Assert.False(new SystemBrowser().TryOpen("https://exemplo.pt/"));
        Assert.False(new SystemBrowser().TryOpen("file:///C:/Windows/System32/calc.exe"));
    }

    [Fact]
    public void PostedForm_IsDecoded()
    {
        var form = SyncForm.ParseBody("code=ABCD-EFGH+IJ&data=" + Uri.EscapeDataString("RVRT+/=") + "&x");
        Assert.Equal("ABCD-EFGH IJ", form["code"]);
        Assert.Equal("RVRT+/=", form["data"]);
        Assert.False(form.ContainsKey("x"));
    }

    [Fact]
    public void ExceptionToTheEngineRule_IsWrittenDown()
    {
        var doc = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "docs", "sincronizacao.md"), Encoding.UTF8);
        foreach (var part in new[] { "## A exceção de rede", "appDataFolder", "drive.appdata", "PKCE", "127.0.0.1", "O que a Google vê", "Google Cloud Console", "google-oauth.json" })
            Assert.Contains(part, doc);
    }
}
