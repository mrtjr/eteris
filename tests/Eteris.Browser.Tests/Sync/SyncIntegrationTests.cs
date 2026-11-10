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
        Eteris.Sync.Engine.SyncStatus Status(Eteris.Sync.Engine.SyncPhase phase, bool running = false, long last = 0, string? error = null,
            IReadOnlyList<Eteris.Sync.Engine.HostRequest>? requests = null, Eteris.Sync.Engine.GuestPairing? pairing = null)
            => TestStatus.Of(phase, running, last, error, requests, pairing);
        Eteris.Sync.Engine.KitInfo Kit(Eteris.Sync.Engine.KitState state, bool declined = false, int devices = 1)
            => new("K7QF", state, 0, 0, declined, devices);
        Assert.Equal("Sincronização desligada", SyncService.Summary(null, now));
        Assert.Equal("Sincronizado há 2 min", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, last: now - 125_000), now));
        Assert.Equal("Sincronizado agora mesmo", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, last: now - 5_000), now));
        Assert.Equal("A sincronizar…", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, running: true, last: now), now));
        Assert.Equal("Por sincronizar: o Éteris tenta outra vez sozinho", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, error: "Sem ligação"), now));
        Assert.Equal("Sincronização em pausa: entre outra vez",
            SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, error: "A autorização da Google expirou") with { NeedsSignIn = true }, now));
        Assert.Equal("Um computador novo pede para entrar",
            SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, last: now) with { WaitingRequests = 1 }, now));
        Assert.Equal("Guarde o kit de recuperação", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On) with { Kit = new Eteris.Sync.Engine.KitInfo("K7QF", Eteris.Sync.Engine.KitState.Pending, 0, 0, false, 1) }, now));
        Assert.Equal("Guarde o kit de recuperação", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On) with { Kit = Kit(Eteris.Sync.Engine.KitState.Stale) }, now));
        Assert.Equal("Sincronização ligada", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On) with { Kit = Kit(Eteris.Sync.Engine.KitState.Saved) }, now));
        Assert.Equal("Sincronização ligada", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On) with { Kit = Kit(Eteris.Sync.Engine.KitState.Pending, declined: true) }, now));
        // Com 2 ou mais computadores não há barra, mas a linha do menu fica.
        Assert.Equal("Guarde o kit de recuperação", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On) with { Kit = Kit(Eteris.Sync.Engine.KitState.Pending, devices: 2) }, now));
        Assert.Equal("Falta juntar este computador à conta", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.Choose), now));
        // O computador novo mostra o código e o outro escreve-o (T7, PROPOSTA P1).
        Assert.Equal("Escreva o código 123 456 no outro computador", SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.Pairing, pairing: new("123 456", false, null)), now));
        // Um pedido pronto: a linha do menu nunca leva o código (o computador antigo pede-o, não o mostra).
        Assert.Equal("Um computador novo pede para entrar",
            SyncService.Summary(Status(Eteris.Sync.Engine.SyncPhase.On, requests: [new("s", "Portátil", 3, now)]), now));
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
        Assert.Contains("const deleteError = (msg && msg.area === 'delete' ? msg.text : '') || (s.deleting && !s.running ? s.deleteError || '' : '');", page, StringComparison.Ordinal);
        Assert.Contains("note($('syncDeleteNote'), deleteError || (s.deleting ? 'A apagar os dados do Éteris do Google Drive…' : ''), !!deleteError);", page, StringComparison.Ordinal);
        Assert.DoesNotContain("note($('syncDeleteNote'), msg && msg.area === 'delete' ? msg.text : '', true);", page, StringComparison.Ordinal);
        Assert.Contains("$('syncStart').disabled = !!s.deleting && !!s.running;", page, StringComparison.Ordinal);
        // Reaberta com o apagar por acabar (ainda sem «running»): a página pergunta de 1,5 em 1,5 s até acabar (ou até
        // dizer porque não acabou), em vez de ficar 20 s em «A apagar…» (visto na prova ao vivo).
        // Com o apagar a falhar (deleteError), a sondagem rápida para: a página diz porquê e oferece «Tentar de novo».
        Assert.Contains("|| (s.deleting && !s.deleteError && !(d.message && d.message.area === 'delete'))", page, StringComparison.Ordinal);

        var now = 1_762_560_000_000L;
        var deleting = new Eteris.Sync.Engine.SyncStatus(Eteris.Sync.Engine.SyncPhase.Stopped, true, 0, null, "A apagar", null, [], [], false, 0) { Deleting = true };
        Assert.Equal("A apagar os dados do Drive…", SyncService.Summary(deleting, now));
        Assert.Contains("deleting = True", SyncService.StatusData(deleting, now).ToString(), StringComparison.Ordinal);
    }

    // Um apagar que não acabou (falha de rede ou inesperada): a página e o menu dizem porquê em vez de «A apagar…» para
    // sempre, «Tentar de novo» volta a tentar sem outra confirmação, e «Ligar» passa a «Ligar sem acabar de apagar».
    [Fact]
    public void DeleteCloud_ThatFails_IsShown_WithTryAgain_AndStartAnyway()
    {
        var src = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser");
        var handler = File.ReadAllText(Path.Combine(src, "Browser", "EterisSchemeHandlerFactory.cs")).Replace("\r\n", "\n");
        Assert.Contains("case \"/sync-delete-retry\":\n                Sync.SyncService.RetryDeleteCloud();", handler, StringComparison.Ordinal);
        Assert.Contains("Sync.SyncService.Start(switchAccount: true, abandonDeletion: InternalPages.Query(uri, \"anyway\") == \"1\");", handler, StringComparison.Ordinal);

        var service = File.ReadAllText(Path.Combine(src, "Sync", "SyncService.cs"));
        Assert.Contains("if (abandonDeletion) agent.AbandonCloudDeletion();", service, StringComparison.Ordinal);
        // «Ligar…» é uma ação nova: a mensagem de um apagar que falhou antes não fica na nota (visto na prova ao vivo:
        // depois de «Ligar sem acabar de apagar», a nota ainda dizia «Ainda não se conseguiu apagar…»).
        // (Desde a 0.2.15 o corpo vive em StartAsync, que a entrada pela Google também espera; Start só o lança.)
        Assert.Contains("public static void Start(bool switchAccount = false, bool abandonDeletion = false) => _ = Task.Run(() => StartAsync(switchAccount, abandonDeletion));",
            service, StringComparison.Ordinal);
        var start = Regex.Match(service, @"internal static async Task StartAsync\(bool switchAccount = false, bool abandonDeletion = false\)\s*\{(?<b>.*?)await agent\.StartAsync", RegexOptions.Singleline).Groups["b"].Value;
        Assert.Contains("lock (Gate) _message = null;", start, StringComparison.Ordinal);
        Assert.Contains("catch (Exception e) { return \"Não foi possível concluir (\" + e.Message + \").\"; }", service, StringComparison.Ordinal);

        var page = File.ReadAllText(Path.Combine(src, "Browser", "settings.html"));
        Assert.Contains("<button class=\"action\" id=\"syncDeleteRetry\" hidden>Tentar de novo</button>", page, StringComparison.Ordinal);
        Assert.Contains("show('syncDeleteRetry', !!s.deleting && !!deleteError && !s.running);", page, StringComparison.Ordinal);
        Assert.Contains("askGoogle('sync-delete-retry');", page, StringComparison.Ordinal);
        Assert.Contains("const deleteStuck = !!s.deleting && !!s.deleteError && !s.running;", page, StringComparison.Ordinal);
        Assert.Contains("$('syncStart').textContent = deleteStuck ? 'Ligar sem acabar de apagar'", page, StringComparison.Ordinal);
        Assert.Contains("askGoogle('sync-start', $('syncStart').dataset.anyway ? '&anyway=1' : '')", page, StringComparison.Ordinal);

        var now = 1_762_560_000_000L;
        var failed = new Eteris.Sync.Engine.SyncStatus(Eteris.Sync.Engine.SyncPhase.Stopped, false, 0, null, "A apagar", null, [], [], false, 0)
        {
            Deleting = true,
            DeleteError = "Ainda não se conseguiu apagar tudo do Google Drive (sem ligação ao Google Drive).",
        };
        Assert.Equal("Por apagar do Drive: veja Definições › Sincronização", SyncService.Summary(failed, now));
        Assert.Contains("deleteError = Ainda não se conseguiu", SyncService.StatusData(failed, now).ToString(), StringComparison.Ordinal);
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

    [Fact]
    public void DeviceName_StartsFromTheDefault_AndIsChangedByPost()
    {
        var root = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser");
        var service = File.ReadAllText(Path.Combine(root, "Sync", "SyncService.cs"), Encoding.UTF8);
        Assert.Contains("DeviceNameText.Default(", service);
        var routes = File.ReadAllText(Path.Combine(root, "Browser", "EterisSchemeHandlerFactory.cs"), Encoding.UTF8);
        var start = routes.IndexOf("\"/sync-device-name\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "falta a rota /sync-device-name");
        var end = routes.IndexOf("break;", start, StringComparison.Ordinal);
        var block = routes[start..end];
        Assert.Contains("SyncForm.Parse(request)", block);
        Assert.DoesNotContain("InternalPages.Query", block);

        var page = File.ReadAllText(Path.Combine(root, "Browser", "settings.html"), Encoding.UTF8);
        Assert.Contains("Nome deste computador", page);
        Assert.Contains("Mudar", page);
        Assert.DoesNotContain("innerHTML", page);
        Assert.DoesNotContain("insertAdjacentHTML", page);
        Assert.DoesNotContain("outerHTML", page);
        Assert.Contains("id=\"syncNameItem\"", page);
        // O nome (escolhido aqui, mas também o que se recebeu antes desta versão) entra na página só como texto.
        Assert.Contains("$('syncNameText').textContent = s.deviceName", page);

        foreach (var file in new[] { Path.Combine(root, "Sync", "BrowserProfile.cs"), Path.Combine(root, "Data", "UserSettings.cs") })
            Assert.DoesNotContain("DeviceName", File.ReadAllText(file, Encoding.UTF8));
    }

    // ---- barra de aprovação (0.2.15, T8) ---------------------------------------------------------------------------

    private static string BrowserSrc(params string[] parts) => Path.Combine([GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", .. parts]);

    // O corpo de um método (da assinatura até à chaveta que fecha ao nível de 4 espaços).
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"não encontrei {signature}");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"não encontrei o fim de {signature}");
        return code[start..end];
    }

    private static IEnumerable<string> BrowserFilesWith(string text)
        => Directory.EnumerateFiles(BrowserSrc(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f, Encoding.UTF8).Contains(text, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(BrowserSrc(), f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    [Fact]
    public void PairingBar_IsCreatedInCode_AndGoesThroughTheArbiter()
    {
        Assert.DoesNotContain("PairingBar", File.ReadAllText(BrowserSrc("MainWindow.xaml"), Encoding.UTF8));
        var glue = File.ReadAllText(BrowserSrc("MainWindow.PairingBar.cs"), Encoding.UTF8);
        Assert.Contains("AddToOverlay(", glue);
        Assert.Contains("RegisterBar(Sync.WindowBar.PairingRequest", glue);
        Assert.Contains("RefreshBars(", glue);
        Assert.DoesNotContain("SyncService.Changed +=", glue);
        Assert.Empty(BrowserFilesWith("WindowBarKind"));
        Assert.False(File.Exists(BrowserSrc("Sync", "WindowBars.cs")));
        // Só o «show» da candidata mexe na visibilidade da barra.
        Assert.Equal(2, Regex.Matches(glue, @"\.Visibility\s*=").Count);
        var show = Body(glue, "private void ShowPairingBar(");
        Assert.Equal(2, Regex.Matches(show, @"\.Visibility\s*=").Count);
        // O gancho no construtor tem nome neutro (MainWindow.xaml.cs continua sem SyncService).
        var window = File.ReadAllText(BrowserSrc("MainWindow.xaml.cs"), Encoding.UTF8);
        Assert.Contains("InitPairingBar();", window);
        Assert.DoesNotContain("SyncService", window);
        Assert.Contains("PlacePairingBar(", File.ReadAllText(BrowserSrc("MainWindow.Sidebar.cs"), Encoding.UTF8));
    }

    [Fact]
    public void PairingBarState_NeverCreatesTheAgent()
    {
        var service = File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8);
        var body = Body(service, "internal static PairingBarView PairingBarState()");
        Assert.Contains("_agent", body);
        Assert.Contains("PairingBarModel.From(", body);
        Assert.DoesNotContain("Agent()", body);
        // O único chamador no produto (a T10 troca-lhe só o terceiro argumento).
        Assert.Equal(["Sync/SyncService.cs"], BrowserFilesWith("PairingBarModel.From("));
        Assert.Single(Regex.Matches(service, @"PairingBarModel\.From\("));
    }

    // Voltar à janela procura pedidos, mas pelo limitador (5 minutos, ou logo com um pedido contado).
    [Fact]
    public void Activation_GoesThroughTheLimiter()
    {
        Assert.DoesNotContain("SyncService", File.ReadAllText(BrowserSrc("MainWindow.xaml.cs"), Encoding.UTF8));
        Assert.Contains("WindowActivated(IsPrivate)", File.ReadAllText(BrowserSrc("MainWindow.PairingBar.cs"), Encoding.UTF8));
        var body = Body(File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8), "public static void WindowActivated(bool privateWindow)");
        var pass = body.IndexOf("TryPass(", StringComparison.Ordinal);
        Assert.True(pass >= 0, "WindowActivated não passa pelo limitador");
        Assert.True(body.IndexOf("Look(", StringComparison.Ordinal) > pass, "Look( antes do limitador");
        Assert.DoesNotContain("r.code", File.ReadAllText(BrowserSrc("Browser", "settings.html"), Encoding.UTF8));
    }

    // O «teste de varrimento» dos momentos de rede da sincronização: quem procura pedidos de computadores novos é uma
    // lista fechada. Quem acrescentar um momento de rede acrescenta aqui o ficheiro (e na página de privacidade).
    [Fact]
    public void NetworkMoments_ClosedListOfCallers()
    {
        Assert.Equal(["Browser/EterisSchemeHandlerFactory.cs", "MainWindow.BrowserMenu.cs", "MainWindow.PairingBar.cs"],
            BrowserFilesWith("SyncService.Look("));
        Assert.Equal(["MainWindow.PairingBar.cs", "Sync/SyncService.cs"], BrowserFilesWith("WindowActivated("));
        var service = File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8);
        // Dentro do serviço: a definição e a chamada da ativação da janela, mais nada.
        Assert.Equal(2, Regex.Matches(service, @"\bLook\(").Count);
        Assert.Contains("Look();", Body(service, "public static void WindowActivated(bool privateWindow)"));
        // A janela liga a ativação uma vez, no gancho do construtor.
        var glue = File.ReadAllText(BrowserSrc("MainWindow.PairingBar.cs"), Encoding.UTF8);
        Assert.Single(Regex.Matches(glue, @"WindowActivated\("));
    }

    // Antes da 1.ª ronda desta abertura (a janela ativa-se no arranque, antes da 1.ª página) e em janelas privadas, a
    // ativação não faz nada: nem rede, nem criar o agente, nem ler o disco.
    [Fact]
    public void Activation_IsFreeBeforeTheFirstRound_AndInPrivateWindows()
    {
        var service = File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8);
        var body = Body(service, "public static void WindowActivated(bool privateWindow)");
        var firstUse = new[] { "Enabled", "_agent" }.Min(t => body.IndexOf(t, StringComparison.Ordinal));
        Assert.True(firstUse > 0);
        foreach (var guard in new[] { "privateWindow", "_firstRoundDone" })
        {
            var at = body.IndexOf(guard, body.IndexOf('{'), StringComparison.Ordinal);
            Assert.True(at >= 0 && at < firstUse, $"{guard} tem de vir antes de Enabled e de _agent");
        }
        Assert.DoesNotContain("Agent()", body);
        // A marca da 1.ª ronda: depois da ronda do agendador e da ronda do gesto («Ligar», entrar); nunca no arranque.
        Assert.Contains("_firstRoundDone = true;", Body(service, "private static void Tick()"));
        Assert.Contains("_firstRoundDone = true;", Body(service, "internal static async Task StartAsync("));
        Assert.DoesNotContain("_firstRoundDone", Body(service, "public static void AfterFirstPaint()"));

        var glue = File.ReadAllText(BrowserSrc("MainWindow.PairingBar.cs"), Encoding.UTF8);
        var init = Body(glue, "private void InitPairingBar()");
        Assert.True(init.IndexOf("if (IsPrivate) return;", StringComparison.Ordinal) is var guardAt && guardAt >= 0
            && guardAt < init.IndexOf("RegisterBar(", StringComparison.Ordinal));
        foreach (var never in new[] { "SyncService.Enabled", "Agent()", "Look(" })
            Assert.DoesNotContain(never, init);
    }

    // Com a barra calada (pedidos em série), um pedido contado deixa de dar direito a procurar logo ao voltar à janela.
    [Fact]
    public void QuietBar_ActivationFallsBackToFiveMinutes()
    {
        var body = Body(File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8), "public static void WindowActivated(bool privateWindow)");
        var quiet = body.IndexOf("PairingQuiet", StringComparison.Ordinal);
        Assert.True(quiet >= 0 && quiet < body.IndexOf("TryPass(", StringComparison.Ordinal));
    }

    // O aviso dos pedidos em série vive nas Definições, com as três saídas; nada de aprovar por uma página.
    [Fact]
    public void QuietNotice_LivesInSettings()
    {
        var page = File.ReadAllText(BrowserSrc("Browser", "settings.html"), Encoding.UTF8);
        Assert.Contains("Vários pedidos para entrar na sua conta foram recusados ou ficaram sem resposta, e por isso a barra deixou de abrir sozinha. "
            + "Se não foi você, alguém tem a sua conta Google: mude a palavra-passe da Google e retire o acesso do Éteris.", page);
        foreach (var needed in new[] { "sync-requests-show", "sync-refuse", "syncQuietRevoke", "Mostrar o pedido na barra", "Retirar o acesso do Éteris", "Não fui eu", "s.pairingQuiet" })
            Assert.Contains(needed, page);
        Assert.DoesNotContain("sync-approve", page);
        Assert.DoesNotContain("innerHTML", page);

        var routes = File.ReadAllText(BrowserSrc("Browser", "EterisSchemeHandlerFactory.cs"), Encoding.UTF8);
        var syncRoute = Body(routes, "private static IResourceHandler SyncRoute(");
        Assert.Contains("case \"/sync-requests-show\":", syncRoute);
        Assert.Contains("Sync.SyncService.ResumePairingBar();", syncRoute);

        var service = File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8);
        var resume = Body(service, "public static void ResumePairingBar()");
        Assert.DoesNotContain("Agent()", resume);
        Assert.Contains("_agent", resume);
        Assert.Contains("pairingQuiet = status.PairingQuiet", service);
    }

    // Plano §10.1 S1: nenhum caminho entrega a chave sem o código escrito E sem a porta do Windows.
    [Fact]
    public void ApproveWithCode_HasOneProductCaller_BehindTheWindowsGate()
    {
        Assert.Equal(["Sync/SyncService.cs"], BrowserFilesWith("ApproveWithCodeAsync("));
        var service = File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8);
        Assert.Single(Regex.Matches(service, @"ApproveWithCodeAsync\("));
        var body = Body(service, "public static Task<PairingAnswer> ApprovePairingAsync(");
        Assert.Contains("ApproveWithCodeAsync(session, typed, () => SyncChoices.MayApproveAsync(agent, confirm))", body);
        // A barra e a rota das Definições chamam-no (pelo ApprovePairingAsync), cada uma com a porta do Windows da sua
        // janela.
        var glue = File.ReadAllText(BrowserSrc("MainWindow.PairingBar.cs"), Encoding.UTF8);
        Assert.Contains("SyncService.ApprovePairingAsync(", glue);
        Assert.Contains("ReauthGate.Shared.RequestAsync(new WindowInteropHelper(this).Handle, purpose, VaultFrontToken())", glue);
    }

    // Aprovar nas Definições só com o teclado (T8K): a rota recebe o código no corpo de um POST e segue o caminho da
    // barra (ApprovePairingAsync, com a porta do Windows da janela da aba); nunca chama o agente diretamente.
    [Fact]
    public void PageApproval_GoesThroughTheBarsPath()
    {
        var routes = File.ReadAllText(BrowserSrc("Browser", "EterisSchemeHandlerFactory.cs"), Encoding.UTF8);
        var route = RouteCase(routes, "/sync-request-code");
        Assert.Contains("string.Equals(request.Method, \"POST\"", route);
        Assert.Contains("SyncForm.Parse(request)", route);
        Assert.Contains("Sync.SyncService.ApprovePairingFromPageAsync(session, typed, purpose => ConfirmWithWindows(browserId, purpose))", route);
        Assert.DoesNotContain("InternalPages.Query(uri, \"code\")", route);
        Assert.DoesNotContain("Query(uri, \"id\")", route);
        Assert.DoesNotContain("ApproveWithCodeAsync", route);
        // Só pelo SyncRoute: com o testemunho da página e nunca numa janela privada.
        var syncRoute = Body(routes, "private static IResourceHandler SyncRoute(");
        Assert.Contains("case \"/sync-request-code\":", syncRoute);
        const string authorized = "if (!InternalPages.IsAuthorized(request.Url)) return Text(\"recusado\");";
        Assert.True(syncRoute.IndexOf(authorized, StringComparison.Ordinal) is >= 0 and var at
            && at < syncRoute.IndexOf("switch (", StringComparison.Ordinal), "o SyncRoute não começa pelo testemunho");
        Assert.Contains("return privateContext ? Text(\"recusado\") : SyncRoute(uri, request, browser.Identifier);", routes);
        // Listas fechadas: a barra e o serviço aprovam; a rota só chama a variante da página.
        Assert.Equal(["MainWindow.PairingBar.cs", "Sync/SyncService.cs"], BrowserFilesWith("ApprovePairingAsync("));
        Assert.Equal(["Browser/EterisSchemeHandlerFactory.cs", "Sync/SyncService.cs"], BrowserFilesWith("ApprovePairingFromPageAsync("));
    }

    // As mesmas regras da barra: só com a sincronização ligada e a barra não calada, e as respostas pela nota da página
    // (nunca a mensagem crua de uma exceção inesperada).
    [Fact]
    public void PageApproval_SameRulesAsTheBar()
    {
        var service = File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8);
        var body = Body(service, "public static async Task ApprovePairingFromPageAsync(");
        Before(body, "PairingBarModel.MayApproveHere(_agent?.Status)", "ApprovePairingAsync(session, typed, confirm)");
        foreach (var needed in new[] { "Say(\"approve\"", "catch (SyncUserException", "catch (OperationCanceledException", "SyncChoices.CouldNotFinish" })
            Assert.Contains(needed, body);
        Assert.DoesNotContain("ApproveWithCodeAsync", body);
        Assert.DoesNotContain("e.Message +", body);
    }

    // ---- o computador novo (0.2.15, T7) -----------------------------------------------------------------------------

    // O texto de um «case» do switch das rotas: de «case "<rota>"» até ao «case» seguinte.
    private static string RouteCase(string routes, string path)
    {
        var start = routes.IndexOf("case \"" + path + "\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"não encontrei a rota {path}");
        var end = routes.IndexOf("case \"", start + 6, StringComparison.Ordinal);
        return end < 0 ? routes[start..] : routes[start..end];
    }

    // Um membro com corpo de expressão: da assinatura ao primeiro «;».
    private static string Expression(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"não encontrei {signature}");
        return code[start..code.IndexOf(';', start)];
    }

    private static void Before(string code, string first, string second)
    {
        var a = code.IndexOf(first, StringComparison.Ordinal);
        var b = code.IndexOf(second, StringComparison.Ordinal);
        Assert.True(a >= 0, $"falta {first}");
        Assert.True(b >= 0, $"falta {second}");
        Assert.True(a < b, $"{first} devia vir antes de {second}");
    }

    // «Foi eu — juntar este computador» passa sempre pela confirmação do Windows; com a conta substituída, nem a rota
    // antiga de pedir nem «Pedir outra vez» criam um pedido.
    [Fact]
    public void ReplacedJoin_HasNoWayAroundTheGate()
    {
        var routes = File.ReadAllText(BrowserSrc("Browser", "EterisSchemeHandlerFactory.cs"), Encoding.UTF8);
        Assert.Contains("JoinAfterReplacedAsync(purpose => ConfirmWithWindows(browserId, purpose))", RouteCase(routes, "/sync-replaced-join"));
        Assert.Contains("AskPairingAgain()", RouteCase(routes, "/sync-pair"));
        Assert.DoesNotContain("Agent()?.RequestPairing()", routes);

        var service = File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8);
        Before(Body(service, "public static async Task<bool> JoinAfterReplacedAsync("), "SyncChoices.MayJoinReplacedAsync(", "RequestPairing(");
        Before(Body(service, "public static void AskPairingAgain()"), "ChainReplaced", "RequestPairing(");
        // Os únicos chamadores de RequestPairing no navegador: estes dois.
        Assert.Equal(["Sync/SyncService.cs"], BrowserFilesWith("RequestPairing("));
        Assert.Equal(2, Regex.Matches(service, @"RequestPairing\(").Count);

        var glue = File.ReadAllText(BrowserSrc("MainWindow.GuestPairing.cs"), Encoding.UTF8);
        Assert.Contains("JoinAfterReplacedAsync(", glue);
        Assert.DoesNotContain("RequestPairing(", glue);
        Assert.Contains("ReauthGate.Shared.RequestAsync(new WindowInteropHelper(this).Handle, purpose, VaultFrontToken())", glue);
    }

    // «Não fui eu» no alerta: nada sai para a rede; a barra esconde-se e as Definições abrem onde se retira o acesso.
    [Fact]
    public void NotMe_DoesNothingOnTheNetwork_AndPointsToRevoke()
    {
        var service = File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8);
        var dismiss = Body(service, "internal static void DismissReplacedAlert()");
        foreach (var forbidden in new[] { "Agent()", "Look(", "RequestPairing(", "Start" })
            Assert.DoesNotContain(forbidden, dismiss);
        var glue = File.ReadAllText(BrowserSrc("MainWindow.GuestPairing.cs"), Encoding.UTF8);
        Assert.Contains("DismissReplacedAlert()", glue);
        Assert.Contains("SyncService.RevokeAnchor", glue);
        Assert.DoesNotContain("\"#syncRevokeItem\"", glue);
    }

    // As barras do computador novo nascem em código, na camada, e passam pelo ponto único da T4 (uma de cada vez).
    [Fact]
    public void GuestBars_AreCreatedInCode_AndGoThroughTheArbiter()
    {
        Assert.DoesNotContain("GuestPairingBar", File.ReadAllText(BrowserSrc("MainWindow.xaml"), Encoding.UTF8));
        var glue = File.ReadAllText(BrowserSrc("MainWindow.GuestPairing.cs"), Encoding.UTF8);
        Assert.Contains("AddToOverlay(", glue);
        Assert.Contains("RegisterBar(Sync.WindowBar.AccountReplaced", glue);
        Assert.Contains("RefreshBars(", glue);
        Assert.DoesNotContain("SyncService.Changed +=", glue);
        // Só o «show» comum mexe na visibilidade.
        var show = Body(glue, "private void ShowGuestBar(");
        Assert.Matches(@"Visibility\s*=", show);
        Assert.Empty(Regex.Matches(glue.Replace(show, ""), @"Visibility\s*="));
        var signIn = File.ReadAllText(BrowserSrc("MainWindow.SignInBar.cs"), Encoding.UTF8);
        var wire = Body(signIn, "private void WireSignInBar()");
        Before(wire, "if (IsPrivate) return;", "WireGuestPairing();");
        Assert.True(wire.IndexOf("WireGuestPairing();", StringComparison.Ordinal) < wire.LastIndexOf("RefreshBars();", StringComparison.Ordinal));
        var window = File.ReadAllText(BrowserSrc("MainWindow.xaml.cs"), Encoding.UTF8);
        Assert.DoesNotContain("GuestPairing", window);
        Assert.DoesNotContain("SyncService", window);
        Assert.DoesNotContain("GuestPairing", File.ReadAllText(BrowserSrc("MainWindow.PairingBar.cs"), Encoding.UTF8));
        Assert.Empty(BrowserFilesWith("WindowBarKind"));
        Assert.Contains("PlaceGuestBar(", File.ReadAllText(BrowserSrc("MainWindow.Sidebar.cs"), Encoding.UTF8));
        // O estado lê o campo do agente: nunca o cria (as candidatas perguntam a cada RefreshBars, também no arranque).
        var state = Expression(File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8), "internal static GuestPairingView GuestPairingState()");
        Assert.Contains("_agent", state);
        Assert.DoesNotContain("Agent()", state);
    }

    // A página das Definições com os textos novos do computador novo; o texto antigo («o outro mostra o código») saiu.
    [Fact]
    public void SettingsPage_HasTheGuestTexts()
    {
        var html = SettingsPage.Render(new UserSettings());
        Assert.Contains("Falta um passo no outro computador.", html, StringComparison.Ordinal);
        Assert.Contains("no outro computador e ele aceitou?", html, StringComparison.Ordinal);
        Assert.Contains("A conta de sincronização na nuvem foi apagada ou substituída noutro computador.", html, StringComparison.Ordinal);
        Assert.Contains("Colar o kit", html, StringComparison.Ordinal);
        Assert.DoesNotContain("O outro computador mostra este código", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirme que o outro computador mostra exatamente este código", html, StringComparison.Ordinal);
    }

    // A vaga 3 é uma só candidata (a barra do início de sessão OU o cartão); «Ver código» abre o cartão.
    [Fact]
    public void Slot3_IsOneCandidate_AndShowCodeOpensTheCard()
    {
        var signIn = File.ReadAllText(BrowserSrc("MainWindow.SignInBar.cs"), Encoding.UTF8);
        Assert.Contains("RegisterBar(Sync.WindowBar.SignIn, SignInSlotWants, ShowSignInSlot)", signIn);
        Assert.Single(Regex.Matches(signIn, @"WindowBar\.SignIn\b"));
        var actions = Body(signIn, "private void OnSignInBarAction(");
        var showCode = actions[actions.IndexOf("case SignInBarAction.ShowCode:", StringComparison.Ordinal)..];
        showCode = showCode[..showCode.IndexOf("break;", StringComparison.Ordinal)];
        Assert.Contains("SetGuestCard(true)", showCode);
        var wants = Body(signIn, "private bool SignInBarWants()");
        Assert.Contains("GuestPairing =", wants);
        Assert.Contains("GuestPairingModel.Handles(", wants);

        var glue = File.ReadAllText(BrowserSrc("MainWindow.GuestPairing.cs"), Encoding.UTF8);
        var slot = Body(glue, "private bool SignInSlotWants()");
        Assert.Contains("GuestCardWants()", slot);
        Assert.Contains("SignInBarWants()", slot);
        Assert.Contains("_signInView is not null", Body(glue, "private void ShowSignInSlot("));
        // As ações do cartão: «Sim» e «Não» no agente; «Pedir outra vez» pelo serviço (com a guarda da conta substituída).
        Assert.Contains("ConfirmPairing()", glue);
        Assert.Contains("CancelPairing()", glue);
        Assert.Contains("SyncService.AskPairingAgain()", glue);
        Assert.Contains("SyncService.SetGuestCard(false)", glue);
        Assert.DoesNotContain("SyncService.Look(", glue);
    }

    // O ecrã do pedido à vista (cartão em «à espera» ou a secção Sincronização aberta) deixa o pedido renovar-se: só
    // memória, sem rede, e o agente lê-o com uma validade de 15 s.
    [Fact]
    public void PairingScreenSeen_IsMemoryOnly_AndFeedsTheAgent()
    {
        var service = File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8);
        var seen = Body(service, "public static void PairingScreenSeen()");
        Assert.Contains("Interlocked.Exchange(ref _pairingSeen", seen);
        foreach (var forbidden in new[] { "Agent()", "Look(", "RequestPairing(", "Changed" })
            Assert.DoesNotContain(forbidden, seen);
        var agent = Body(service, "public static SyncAgent? Agent()");
        Assert.Contains("PairingScreenVisible = () => Now() - Interlocked.Read(ref _pairingSeen) < 15_000", agent);
        var routes = File.ReadAllText(BrowserSrc("Browser", "EterisSchemeHandlerFactory.cs"), Encoding.UTF8);
        var look = RouteCase(routes, "/sync-look");
        Before(look, "Sync.SyncService.PairingScreenSeen();", "Sync.SyncService.Look(devices: InternalPages.Query(uri, \"devices\") != \"0\")");
        var glue = File.ReadAllText(BrowserSrc("MainWindow.GuestPairing.cs"), Encoding.UTF8);
        Assert.Contains("SyncService.PairingScreenSeen()", glue);
        Assert.Contains("GuestPairingKind.Waiting", glue);
    }

    // Na página, «Sim» só conta 2 s depois de o código aparecer e nunca recebe o foco.
    [Fact]
    public void Page_SimIsGuarded_AndNeverFocused()
    {
        var page = File.ReadAllText(BrowserSrc("Browser", "settings.html"), Encoding.UTF8);
        Assert.Contains("const CONFIRM_GUARD = 2000;", page);
        var listener = page[page.IndexOf("$('syncPairConfirm').addEventListener", StringComparison.Ordinal)..];
        listener = listener[..listener.IndexOf('\n')];
        Before(listener, "CONFIRM_GUARD", "askGoogle('sync-pair-confirm')");
        Assert.DoesNotContain("focusAfterPaint = 'syncPairConfirm'", page);
        Assert.DoesNotContain("$('syncPairConfirm').focus()", page);
        // A página decide só pelo «guest» do estado; G4 no estado; o kit por cima do campo; «Aprovar…» só sem nada.
        Assert.Contains("const guest = s.guest || 'none';", page);
        Assert.Contains("['waiting', 'code', 'timeUp', 'stillWaiting', 'refused', 'failed'].includes(guest)", page);
        Assert.Contains("$('syncPair').hidden = guest !== 'none';", page);
        Assert.Contains("id=\"syncKitHelp\"", page);
        Assert.Contains("id=\"syncKitPaste\"", page);
        Assert.Contains("guest === 'joined'", page);
        Assert.DoesNotContain("(s.pairing && s.pairing.error)", page);
    }

    // O pedido automático do computador novo está ligado no Éteris (o agente só o faz na ronda do gesto e num computador
    // sem nenhum estado desta conta; AutoPairingTests). É a linha que se reverte se a aprovação com código sair.
    [Fact]
    public void AutoPair_IsOnInTheBrowser()
    {
        var service = File.ReadAllText(BrowserSrc("Sync", "SyncService.cs"), Encoding.UTF8);
        Assert.Contains("AutoPair = true,", Body(service, "public static SyncAgent? Agent()"));
        Assert.Single(Regex.Matches(service, @"AutoPair = true"));
    }

    // Na página, «Foi eu» nunca recebe o foco.
    [Fact]
    public void Page_ReplacedJoin_IsNeverFocused()
    {
        var page = File.ReadAllText(BrowserSrc("Browser", "settings.html"), Encoding.UTF8);
        Assert.Contains("id=\"syncReplacedItem\"", page);
        Assert.Contains("askGoogle('sync-replaced-join')", page);
        Assert.DoesNotContain("focusAfterPaint = 'syncReplacedJoin'", page);
        Assert.DoesNotContain("$('syncReplacedJoin').focus()", page);
        Assert.DoesNotContain("autofocus", page);
    }
}
