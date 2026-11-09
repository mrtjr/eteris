using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Sync;
using Eteris.Sync.Model;

namespace Eteris.Browser.Tests.Sync;

// A ligação da biblioteca ao Éteris: sem a opção experimental, definições que viajam, DPAPI, navegador do sistema.
public class SyncIntegrationTests
{
    // A opção experimental saiu: um settings.json antigo com ela continua a abrir (o valor é ignorado, nada se perde),
    // e a página já não a oferece.
    [Fact]
    public void SyncPreview_IsGone_AndAnOldSettingsFileStillOpens()
    {
        Assert.Null(new UserSettings().With("syncPreview", "true"));
        var path = Path.Combine(Path.GetTempPath(), "eteris-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{\"SyncPreview\": true, \"Theme\": \"light\", \"Density\": \"compact\"}");
        try
        {
            var loaded = new SettingsStore(path).Load();
            Assert.Equal("light", loaded.Theme);
            Assert.Equal("compact", loaded.Density);
        }
        finally { File.Delete(path); }
        Assert.DoesNotContain("syncPreview", SettingsPage.Render(new UserSettings()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsPage_HasTheSyncSection_AndWithoutTheClientSaysUnavailable()
    {
        var html = SettingsPage.Render(new UserSettings());
        Assert.Contains("<section id=\"sincronizacao\">", html);
        Assert.Contains("href=\"#sincronizacao\"", html);
        Assert.Contains("id=\"syncBody\" hidden", html);
        // Sem google-oauth.json: «indisponível nesta compilação», e o botão fica desativado.
        Assert.Contains("\"state\":\"unconfigured\"", html);
        Assert.StartsWith("Indisponível nesta compilação", GoogleSync.UnavailableText);
        // O que sincroniza (e o que não) está dito na página.
        Assert.Contains("\"syncWhat\":", html);
        Assert.Contains("O que sincroniza", html);
        Assert.Contains("Não sincroniza: histórico, abas abertas", SyncService.WhatStays);
        // O código e o ficheiro do perfil vão no corpo de um POST, nunca no endereço.
        Assert.Contains("method=\"post\"", html);
        Assert.DoesNotContain("sync-import?code", html);
    }

    // O arranque: a sincronização só se marca depois da 1.ª página, e a primeira ronda fica para 30 s depois (sem
    // sincronização ligada, nada). Nada de rede ao montar o serviço nem ao ler o estado (SyncAgentTests prova-o com o
    // Drive falso); aqui, que o arranque só chama AfterFirstPaint e que ele só marca o agendador. A única exceção é um
    // «Apagar os meus dados…» que o fecho do Éteris interrompeu: esse acaba ao abrir (nunca com uma ronda).
    [Fact]
    public void Startup_OnlySchedulesTheFirstRound_ThirtySecondsAfterTheWindow()
    {
        var src = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser");
        var app = File.ReadAllText(Path.Combine(src, "App.xaml.cs"));
        Assert.Contains("FirstPaint.App.Then(Sync.SyncService.AfterFirstPaint);", app);
        Assert.DoesNotContain("GoogleSync", app);
        Assert.DoesNotContain("SyncService", File.ReadAllText(Path.Combine(src, "MainWindow.xaml.cs")));
        var service = File.ReadAllText(Path.Combine(src, "Sync", "SyncService.cs"));
        var body = Regex.Match(service, @"public static void AfterFirstPaint\(\)\s*\{(?<b>.*?)\r?\n    \}", RegexOptions.Singleline).Groups["b"].Value;
        Assert.Contains("if (!Enabled) return;", body);
        Assert.Contains("Schedule.Startup(Now());", body);
        foreach (var network in new[] { "RunAsync", "StartAsync", "Look(", "CheckRequests", "Refresh", "Task.Run" })
            Assert.DoesNotContain(network, body);
        Assert.Equal(TimeSpan.FromSeconds(30), Eteris.Sync.Engine.SyncSchedule.StartupDelay);
    }

    // A pasta de teste (ETERIS_TEST_SYNC_FOLDER) é só para os testes ao vivo: a versão instalada ignora-a.
    [Fact]
    public void TestFolder_OnlyOutsideTheInstalledVersion()
    {
        Assert.Null(SyncService.TestFolderFor(null, installed: false));
        Assert.Null(SyncService.TestFolderFor("  ", installed: false));
        Assert.Null(SyncService.TestFolderFor(@"C:\pasta", installed: true));
        Assert.Equal(@"C:\pasta", SyncService.TestFolderFor(@" C:\pasta ", installed: false));
    }

    // Com a secção Sincronização à vista, a página volta a procurar pedidos de aparelhos novos de 5 em 5 s — só depois
    // de um gesto, com a sincronização ligada, a página à vista, e até 10 minutos depois do último gesto (só os pedidos,
    // sem reler a lista de aparelhos).
    [Fact]
    public void SettingsPage_LooksForRequestsWhileTheSectionIsVisible_OnlyAfterAGesture()
    {
        var page = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Browser", "settings.html"));
        Assert.Contains("askGoogle('sync-look', ++looks % 6 === 0 ? '' : '&devices=0', true)", page, StringComparison.Ordinal);
        Assert.Contains("if (!looked || !seen || document.visibilityState !== 'visible' || !lastState || !lastState.enabled) return;", page, StringComparison.Ordinal);
        Assert.Contains("if (Date.now() - lastGesture > 10 * 60 * 1000) return;", page, StringComparison.Ordinal);
        Assert.Contains("Sync.SyncService.Look(devices: InternalPages.Query(uri, \"devices\") != \"0\")",
            File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void MenuSummary_SaysWhereTheSyncIs()
    {
        var now = 1_762_560_000_000L;
        Eteris.Sync.Engine.SyncStatus Status(Eteris.Sync.Engine.SyncPhase phase, bool running = false, long last = 0, string? error = null, string? code = null,
            IReadOnlyList<Eteris.Sync.Engine.HostRequest>? requests = null, Eteris.Sync.Engine.GuestPairing? pairing = null)
            => new(phase, running, last, error, null, code, pairing, requests ?? [], [], false, 0);
        Assert.Equal("Sincronização desligada", SyncService.Summary(null, now));
        Assert.Equal("Sincronizado há 2 min", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, last: now - 125_000), now));
        Assert.Equal("Sincronizado agora mesmo", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, last: now - 5_000), now));
        Assert.Equal("A sincronizar…", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, running: true, last: now), now));
        Assert.Equal("Por sincronizar: o Éteris tenta outra vez sozinho", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, error: "Sem ligação"), now));
        Assert.Equal("Sincronização em pausa: entre outra vez",
            SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, error: "A autorização da Google expirou") with { NeedsSignIn = true }, now));
        Assert.Equal("Um computador novo pede para entrar",
            SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, last: now) with { WaitingRequests = 1 }, now));
        Assert.Equal("Guarde o código de recuperação", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, code: "X"), now));
        Assert.Equal("Falta juntar este computador à conta", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.Choose), now));
        Assert.Equal("Confirme o código 123 456", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.Pairing, pairing: new("123 456", false, null)), now));
        Assert.Equal("Pedido de «Portátil» · código 654 321",
            SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, requests: [new("s", "Portátil", "654 321", now)]), now));
        Assert.Equal("Sincronização parada", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.Stopped), now));
    }

    // O estado da sincronização tem o segredo da conta: no disco só cifrado com DPAPI (nada legível no ficheiro).
    [Fact]
    public void SyncState_IsSavedWithDpapi()
    {
        var path = Path.Combine(Path.GetTempPath(), "eteris-sync-state-" + Guid.NewGuid().ToString("N"), "sync.state");
        try
        {
            var store = new DpapiSyncStore(path, new Dpapi("sync-state"));
            store.Save(new Eteris.Sync.Engine.SavedSync { Secret = "segredo-da-conta", Enabled = true, Stopped = "nota legível" });
            var bytes = File.ReadAllBytes(path);
            foreach (var text in new[] { "segredo", "nota", "secret" })
                Assert.DoesNotContain(text, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            Assert.Equal("segredo-da-conta", store.Load()!.Secret);
            Assert.Null(new DpapiSyncStore(path, new Dpapi("google-tokens")).Load()); // outra «entropia» não abre
            store.Delete();
            Assert.Null(store.Load());
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { }
        }
    }

    // «Apagar os meus dados do Éteris no Google Drive»: a rota grava «desligada, a apagar» antes de responder (e antes de
    // qualquer pedido), para o agendador, e só depois apaga em segundo plano; ao abrir, um apagar interrompido acaba
    // (sem ronda nenhuma); a página diz «A apagar…» enquanto corre e o menu também.
    [Fact]
    public void DeleteCloud_IsSavedBeforeAnyRequest_ResumesOnStartup_AndThePageSaysDeleting()
    {
        var src = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser");
        var handler = File.ReadAllText(Path.Combine(src, "Browser", "EterisSchemeHandlerFactory.cs"));
        Assert.Contains("case \"/sync-delete-cloud\":\n                Sync.SyncService.DeleteCloud();", handler.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("agent.DeleteCloudDataAsync()", handler, StringComparison.Ordinal);

        var service = File.ReadAllText(Path.Combine(src, "Sync", "SyncService.cs"));
        var delete = Regex.Match(service, @"public static void DeleteCloud\(\)\s*\{(?<b>.*?)\r?\n    \}", RegexOptions.Singleline).Groups["b"].Value;
        var begin = delete.IndexOf("agent.BeginCloudDeletion();", StringComparison.Ordinal);
        var stop = delete.IndexOf("Schedule.Stop();", StringComparison.Ordinal);
        var act = delete.IndexOf("Act(\"delete\", a => a.FinishCloudDeletionAsync());", StringComparison.Ordinal);
        Assert.True(begin >= 0 && stop > begin && act > stop, delete);
        var startup = Regex.Match(service, @"public static void AfterFirstPaint\(\)\s*\{(?<b>.*?)\r?\n    \}", RegexOptions.Singleline).Groups["b"].Value;
        Assert.Contains("if (Agent()?.DeletePending == true)", startup, StringComparison.Ordinal);
        Assert.True(startup.IndexOf("DeletePending", StringComparison.Ordinal) < startup.IndexOf("if (!Enabled) return;", StringComparison.Ordinal));

        var page = File.ReadAllText(Path.Combine(src, "Browser", "settings.html"));
        Assert.Contains("const deleteError = msg && msg.area === 'delete' ? msg.text : '';", page, StringComparison.Ordinal);
        Assert.Contains("note($('syncDeleteNote'), deleteError || (s.deleting ? 'A apagar os dados do Éteris do Google Drive…' : ''), !!deleteError);", page, StringComparison.Ordinal);
        Assert.DoesNotContain("note($('syncDeleteNote'), msg && msg.area === 'delete' ? msg.text : '', true);", page, StringComparison.Ordinal);
        Assert.Contains("$('syncStart').disabled = !!s.deleting && !!s.running;", page, StringComparison.Ordinal);
        // Reaberta com o apagar por acabar (ainda sem «running»): a página pergunta de 1,5 em 1,5 s até acabar (ou até
        // dizer porque não acabou), em vez de ficar 20 s em «A apagar…» (visto na prova ao vivo).
        Assert.Contains("|| (s.deleting && !(d.message && d.message.area === 'delete'))", page, StringComparison.Ordinal);

        var now = 1_762_560_000_000L;
        var deleting = new Eteris.Sync.Engine.SyncStatus(Eteris.Sync.Engine.SyncPhase.Stopped, true, 0, null, "A apagar", null, null, [], [], false, 0) { Deleting = true };
        Assert.Equal("A apagar os dados do Drive…", SyncService.Summary(deleting, now));
        Assert.Contains("deleting = True", SyncService.StatusData(deleting, now).ToString(), StringComparison.Ordinal);
    }

    // «A apagar» vai no estado guardado (DPAPI), escrito até ao disco antes de se apagar o primeiro ficheiro.
    [Fact]
    public void SyncState_KeepsTheDeleteInProgress_AndIsWrittenThrough()
    {
        var path = Path.Combine(Path.GetTempPath(), "eteris-sync-state-" + Guid.NewGuid().ToString("N"), "sync.state");
        try
        {
            var store = new DpapiSyncStore(path, new Dpapi("sync-state"));
            store.Save(new Eteris.Sync.Engine.SavedSync { Enabled = false, DeletePending = true, Stopped = "A apagar" });
            var loaded = store.Load()!;
            Assert.True(loaded.DeletePending);
            Assert.False(loaded.Enabled);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { }
        }
        var service = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Sync", "SyncService.cs"));
        Assert.Contains("stream.Flush(flushToDisk: true);", service, StringComparison.Ordinal);
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
        foreach (var local in new[] { "mica", "hideFromCapture", "sidebar", "sidebarWidth", "sidebarSide", "sidebarPanel", "sidebarCollapsed" })
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
