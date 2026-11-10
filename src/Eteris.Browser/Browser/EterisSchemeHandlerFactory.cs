using System.Net;
using System.Text;
using CefSharp;
using Eteris.Browser.Data;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Browser;

public sealed class EterisSchemeHandlerFactory(bool privateContext = false) : ISchemeHandlerFactory
{
    public const string SchemeName = "eteris";

    public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
    {
        Uri.TryCreate(request.Url, UriKind.Absolute, out var uri);
        if (uri is { Host: "newtab", AbsolutePath: "/wallpaper/coast.jpg" or "/wallpaper/mountains.jpg" or "/wallpaper/coast-live.webm" })
        {
            var file = uri.AbsolutePath["/wallpaper/".Length..];
            var stream = privateContext ? null : typeof(NewTabPage).Assembly
                .GetManifestResourceStream("Eteris.Browser.Browser.Wallpapers." + file);
            if (stream is null) return ResourceHandler.ForErrorMessage("Fundo indisponível", HttpStatusCode.NotFound);

            return file == "coast-live.webm"
                ? new WallpaperVideoResourceHandler(stream, request.GetHeaderByName("Range"))
                : ResourceHandler.FromStream(stream, "image/jpeg", autoDisposeStream: true);
        }
        if (uri is { Host: "newtab", AbsolutePath: "/set" })
        {
            var ok = CanChangeNewTab(request.Url) && InternalPages.Query(uri, "k") is { } key && key.StartsWith("nt", StringComparison.Ordinal)
                     && InternalPages.Query(uri, "v") is { } value && Settings.Set(key, value);
            return Text(ok ? "ok" : "recusado");
        }
        if (uri is { Host: "newtab", AbsolutePath: "/session" })
        {
            if (!CanChangeNewTab(request.Url) || !long.TryParse(InternalPages.Query(uri, "id"), out var sessionId)
                || AppData.SavedSessions().FirstOrDefault(s => s.Id == sessionId) is not { } session)
                return Text("recusado");
            var browserId = browser.Identifier;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => MainWindow.OpenSavedSessionFrom(browserId, session));
            return Text("ok");
        }
        if (uri is { Host: "newtab", AbsolutePath: "/" })
        {
            var isPrivate = privateContext;
            var now = DateTime.Now;
            var engine = SearchEngine.Preferred;
            // A nova aba privada fica sempre escura e não consulta os dados de navegação do perfil normal.
            string html;
            if (isPrivate)
            {
                html = NewTabPage.Render([], engine, now, isPrivate: true, prefs: Settings.Current, notice: SandboxNotice());
            }
            else
            {
                Infrastructure.StartupTrace.MarkOnce("novaaba.pedido");
                var prefs = Settings.Current;
                var blocked = Shields.ShieldStats.SessionBlocked;
                // O aviso da rede de segurança do sandbox (decidido no Prepare): uma cópia feita antes dele não serve.
                var notice = SandboxNotice();
                // Ajuste e12: a 1.ª nova aba já preparada no arranque, se ainda vale (Browser/NewTabPrecompute.cs).
                var ready = NewTabPrecompute.Take(prefs, engine.Name, now, blocked, notice);
                html = ready ?? NewTabPrecompute.Render(engine, now, prefs, blocked, notice);
                Infrastructure.StartupTrace.MarkOnce("novaaba.resposta", ready is null ? "pregerada=0" : "pregerada=1");
            }
            return ResourceHandler.FromString(InternalPages.Themed(html, InternalPages.LightTheme && !isPrivate),
                Encoding.UTF8, false, "text/html");
        }

        if (uri is { Host: "history" }) return History(uri, request.Url);
        // Palavras-passe e preenchimento automático (Vault/VaultRoutes.cs): recusadas na janela privada.
        if (uri is { Host: "passwords" or "autofill" }) return VaultRoute(uri, request, browser);
        if (uri is { Host: "settings" or "about", AbsolutePath: "/clear" }) return ClearData(uri, request.Url, browser);
        if (uri is { Host: "credits", AbsolutePath: "/" }) return Html(CreditsPage.Render());
        if (uri is { Host: "privacy", AbsolutePath: "/" })
            return Html(PrivacyPage.Render(PrivacyPage.Current(), privateContext ? null : AppData.PrivacyEvents(30), DateOnly.FromDateTime(DateTime.Today)));
        // Auto-teste de privacidade: a página de teste é um site a sério (https), servido pelo próprio Éteris.
        if (uri is { Host: "privacy", AbsolutePath: "/teste" })
            return Html("<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url=" + SelfTest.PageUrl + "\"><title>Auto-teste de privacidade</title>");
        if (uri is { Host: "privacy", AbsolutePath: "/clear-report" })
        {
            // Nunca a partir da janela privada (não lê nem mexe no perfil); depois volta à página sem repetir o pedido.
            if (!privateContext && InternalPages.IsAuthorized(request.Url)) AppData.ClearPrivacyReport();
            return Html(PrivacyPage.ReportClearedRedirect);
        }
        if (uri is { Host: "whatsnew", AbsolutePath: "/" }) return Html(WhatsNewPage.Render(SettingsPage.Version));
        if (uri is { Host: "welcome", AbsolutePath: "/" })
            return Html(WelcomePage.Render(Settings.Current, BookmarkImport.Detect(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))));
        // Definições › Sincronização (perfil cifrado, «Entrar com o Google»): nunca a partir da janela privada.
        if (uri is { Host: "settings" } && uri.AbsolutePath.StartsWith("/sync-", StringComparison.Ordinal))
            return privateContext ? Text("recusado") : SyncRoute(uri, request, browser.Identifier);
        if (uri is { Host: "settings" or "about" or "welcome" }) return SettingsRoute(uri, request.Url, privateContext, browser.Identifier);
        if (uri is { Host: "bookmarks" }) return BookmarksRoute(uri, request.Url, browser.Identifier);

        if (uri is { Host: "error", AbsolutePath: "/continue" })
        {
            // «Continuar sem segurança»: este host segue por http:// até fechar o Éteris; «Sempre» guarda a exceção
            // (nunca a partir da janela privada, que não deixa rasto).
            if (ErrorPage.ParseContinue(request.Url) is not { } choice) return Text("recusado");
            Shields.ShieldEngine.Https.Allow(choice.Host, privateContext);
            if (choice.Always && !privateContext) AppData.SetHttpException(choice.Host, true);
            return Html(ErrorPage.ContinueRedirect(choice.Url));
        }
        if (ErrorPage.IsErrorPage(request.Url))
            return Html(ErrorPage.Render(ErrorPage.Code(request.Url), ErrorPage.FailedUrl(request.Url) ?? string.Empty,
                ErrorPage.IsInsecureWarning(request.Url), canRemember: !privateContext, secureDns: Settings.Current.SecureDns != "off"));

        return ResourceHandler.ForErrorMessage("Página interna não encontrada", HttpStatusCode.NotFound);
    }

    private IResourceHandler VaultRoute(Uri uri, IRequest request, IBrowser browser)
    {
        Vault.VaultRoutes.UseProfileHost(() => new WindowsVaultHost());
        var routes = Vault.VaultRoutes.Profile!;
        // O pedido lê-se já (o IRequest só vale durante esta chamada); a resposta vem depois, noutra thread.
        var vaultRequest = new Vault.VaultRequest(uri.Host, uri.AbsolutePath, InternalPages.QueryAll(uri), SyncForm.Parse(request),
            InternalPages.IsAuthorized(request.Url), privateContext, browser.Identifier);
        return new VaultResourceHandler(() => routes.HandleAsync(vaultRequest));
    }

    private bool CanChangeNewTab(string url) => !privateContext && InternalPages.IsAuthorized(url);

    private static IResourceHandler SettingsRoute(Uri uri, string url, bool privateContext, int browserId)
    {
        // Desligar «Pedir o Windows Hello ao preencher palavras-passe» (ou pagamentos) pede o Windows Hello primeiro.
        if (uri.AbsolutePath == "/set" && !privateContext && InternalPages.IsAuthorized(url)
            && InternalPages.Query(uri, "k") is { } guarded && InternalPages.Query(uri, "v") is { } off && Data.UserSettings.NeedsReauth(guarded, off))
            return new VaultResourceHandler(async () =>
            {
                var purpose = guarded == "pwAskHello" ? Vault.ReauthPurpose.ChangePasswordSetting : Vault.ReauthPurpose.ChangePaymentSetting;
                var ok = await ConfirmWithWindows(browserId, purpose).ConfigureAwait(false) && Settings.Set(guarded, off);
                return Vault.VaultReply.Text(ok ? "ok" : "recusado");
            });
        if (uri.AbsolutePath == "/set")
        {
            var ok = InternalPages.IsAuthorized(url)
                     && InternalPages.Query(uri, "k") is { } key && InternalPages.Query(uri, "v") is { } value
                     && SettingsPage.CanSetFromPage(key, value) && Settings.Set(key, value);
            return ResourceHandler.FromString(ok ? "ok" : "recusado", Encoding.UTF8, false, "text/plain");
        }
        if (uri.AbsolutePath == "/update" && InternalPages.IsAuthorized(url))
        {
            _ = Updates.UpdateService.CheckAndDownloadAsync();
            return Text(Updates.UpdateService.Status);
        }
        if (uri.AbsolutePath == "/status") return Text(Updates.UpdateService.Status);
        if (uri.AbsolutePath == "/site-reset" && InternalPages.IsAuthorized(url) && InternalPages.Query(uri, "site") is { Length: > 0 } site)
        {
            AppData.ResetSite(site);
            return Text("ok");
        }
        if (uri.AbsolutePath == "/import" && InternalPages.IsAuthorized(url))
        {
            var source = BookmarkImport.Detect(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                .FirstOrDefault(s => s.Id == InternalPages.Query(uri, "from"));
            if (source is null) return Text("Navegador não encontrado");
            var added = AppData.ImportBookmarks(BookmarkImport.Read(source.Path));
            return Text(added == 0 ? $"Nada de novo em {source.Name}" : $"{added} favoritos importados de {source.Name}");
        }
        return uri.AbsolutePath == "/"
            ? Html(SettingsPage.Render(Settings.Current, AboutInfo(), focusAbout: uri.Host == "about",
                BookmarkImport.Detect(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), AppData.SiteSummaries(),
                isPrivate: privateContext))
            : ResourceHandler.ForErrorMessage("Página interna não encontrada", HttpStatusCode.NotFound);
    }

    // A confirmação do Windows (Windows Hello ou a palavra-passe do Windows), com a janela desta aba como dona.
    private static Task<bool> ConfirmWithWindows(int browserId, Vault.ReauthPurpose purpose)
    {
        Vault.VaultRoutes.UseProfileHost(() => new WindowsVaultHost());
        return Vault.VaultRoutes.Profile!.ConfirmAsync(browserId, purpose);
    }

    private static IResourceHandler SyncRoute(Uri uri, IRequest request, int browserId)
    {
        if (!InternalPages.IsAuthorized(request.Url)) return Text("recusado");
        switch (uri.AbsolutePath)
        {
            case "/sync-export" when InternalPages.Query(uri, "secrets") == "1":
                // Com as palavras-passe, os pagamentos e os documentos: só depois do Windows Hello (sempre, sem graça).
                return new VaultResourceHandler(async () =>
                {
                    var (ok, secrets) = await Sync.SyncChoices.SecretsForExportAsync(true, purpose => ConfirmWithWindows(browserId, purpose),
                        () => Sync.ProfileTransfer.CollectSecrets(Vault.VaultService.Profile, () => System.IO.File.Exists(System.IO.Path.Combine(Infrastructure.AppPaths.Root, Vault.VaultKey.FileName))))
                        .ConfigureAwait(false);
                    if (!ok) return Vault.VaultReply.Json(new { ok = false, message = Sync.SyncChoices.NotConfirmed("nada foi exportado") });
                    try
                    {
                        var exported = Sync.ProfileTransfer.Export(Environment.MachineName, DateTime.Now, secrets);
                        return Vault.VaultReply.Json(new { ok = true, file = System.IO.Path.GetFileName(exported.Path), code = exported.Code, records = exported.Records, secrets = secrets?.Count ?? 0 });
                    }
                    catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
                    {
                        return Vault.VaultReply.Json(new { ok = false, message = "Não foi possível gravar o ficheiro em Transferências." });
                    }
                });
            case "/sync-choose" when InternalPages.Query(uri, "g") is { Length: > 0 } group:
            {
                // Uma linha do tópico «O que passa entre PCs» neste computador (Pagamentos e Documentos pedem o Windows).
                var on = InternalPages.Query(uri, "on") == "1";
                return new VaultResourceHandler(async () =>
                {
                    if (Sync.SyncService.Agent() is not { } agent) return Vault.VaultReply.Json(SettingsPage.SyncData(isPrivate: false));
                    var error = await Sync.SyncChoices.ChooseAsync(agent, group, on, purpose => ConfirmWithWindows(browserId, purpose)).ConfigureAwait(false);
                    Sync.SyncService.Say("choices", error);
                    if (error is null && on) Sync.SyncService.SyncNow();
                    return Vault.VaultReply.Json(SettingsPage.SyncData(isPrivate: false));
                });
            }
            case "/sync-offer" when InternalPages.Query(uri, "a") is "1" or "0":
            {
                // A oferta única «receber também neste?» (pagamentos e documentos): «Sim» pede o Windows Hello.
                var accept = InternalPages.Query(uri, "a") == "1";
                return new VaultResourceHandler(async () =>
                {
                    await Sync.SyncService.AnswerSecretsOfferAsync(accept, purpose => ConfirmWithWindows(browserId, purpose)).ConfigureAwait(false);
                    return Vault.VaultReply.Json(SettingsPage.SyncData(isPrivate: false));
                });
            }
            case "/sync-export":
                try
                {
                    var exported = Sync.ProfileTransfer.Export(Environment.MachineName, DateTime.Now);
                    return Json(new { ok = true, file = System.IO.Path.GetFileName(exported.Path), code = exported.Code, records = exported.Records });
                }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
                {
                    return Json(new { ok = false, message = "Não foi possível gravar o ficheiro em Transferências." });
                }
            case "/sync-import":
            {
                // Formulário enviado por POST (o código e o ficheiro não vão no endereço).
                var form = SyncForm.Parse(request);
                if (form.GetValueOrDefault("code") is not { Length: > 0 } code || form.GetValueOrDefault("data") is not { Length: > 0 } data)
                    return Json(new { ok = false, message = "Escolha o ficheiro e escreva o código." });
                try
                {
                    List<(string Name, int Color, string? Icon)> spaces = [];
                    // «weaker=1» só depois de o utilizador ver a lista do que baixa a proteção e carregar em «Aplicar também».
                    var allowWeaker = form.GetValueOrDefault("weaker") == "1";
                    var result = Sync.ProfileTransfer.Import(Convert.FromBase64String(data), code, s => spaces = s.ToList(), allowWeaker);
                    var summary = Sync.ProfileTransfer.Summary(result);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => MainWindow.AfterProfileImport(spaces, summary));
                    return Json(new { ok = true, message = summary, held = result.Held });
                }
                catch (FormatException e) { return Json(new { ok = false, message = e.Message }); }
                catch (Eteris.Sync.Crypto.WrongCodeException e) { return Json(new { ok = false, message = e.Message }); }
                catch (Eteris.Sync.Crypto.UnsupportedFormatException e) { return Json(new { ok = false, message = e.Message }); }
                // Nada pode sair para o CEF (thread de E/S): o que falhar a meio diz-se na página.
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException
                    or Microsoft.Data.Sqlite.SqliteException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
                {
                    return Json(new { ok = false, message = "A importação parou a meio: o que já entrou ficou; tente outra vez." });
                }
            }
            case "/sync-google":
                // Na pasta de teste não há conta: «Ligar» liga a sincronização por ela.
                if (Sync.SyncService.FolderMode) Sync.SyncService.Start();
                else Sync.GoogleSync.BeginSignIn();
                break;
            case "/sync-google-cancel":
                Sync.GoogleSync.CancelSignIn();
                break;
            case "/sync-google-out":
                // «Sair deste computador»: a sincronização para já aqui (com «forget=1», a cópia local da chave sai
                // também) e os tokens e a conta guardada deste computador saem. Nada é revogado na Google: zero pedidos
                // de rede, e os outros computadores continuam ligados.
                Sync.SyncService.SignOut(InternalPages.Query(uri, "forget") == "1");
                if (!Sync.SyncService.FolderMode) Sync.GoogleSync.SignOut();
                break;
            case "/sync-google-revoke" when !Sync.SyncService.FolderMode:
                // «Retirar o acesso do Éteris à conta Google» (já confirmado na página): revoga em segundo plano; a
                // mensagem chega pela área «revoke». Não existe na pasta de teste (não há Google).
                Sync.SyncService.RevokeAccess();
                break;
            case "/sync-account":
                // A secção Sincronização ficou à vista depois de um gesto do utilizador na página: se há sessão sem
                // conta guardada, pede o nome e o e-mail ao Drive (uma vez; nunca no arranque).
                Sync.GoogleSync.RequestAccount();
                break;
            case "/sync-look":
                // Também só depois de um gesto, com a secção à vista: aparelhos da conta e pedidos de aparelhos novos.
                // A página repete-o de 5 em 5 s enquanto a secção estiver à vista («devices=0»: só os pedidos).
                // Com ela à vista, o pedido de um computador novo pode renovar-se (só memória).
                Sync.SyncService.PairingScreenSeen();
                Sync.SyncService.Look(devices: InternalPages.Query(uri, "devices") != "0");
                break;
            case "/sync-start":
                // «Ligar a sincronização» / «Ligar outra vez» (depois de ler o porquê): também com outra conta Google.
                // «anyway=1»: «Ligar sem acabar de apagar», depois de um apagar do Drive que não acabou.
                Sync.SyncService.Start(switchAccount: true, abandonDeletion: InternalPages.Query(uri, "anyway") == "1");
                break;
            case "/sync-new-code":
                // «Gerar um kit novo…» (cadeia nova): o kit novo abre também o cofre (palavras-passe…); com segredos em
                // jogo, o Windows confirma primeiro (sempre), como ao aprovar um aparelho. Depois o kit fica por guardar
                // (ou «deixou de valer»): não se mostra sozinho.
                return new VaultResourceHandler(async () =>
                {
                    if (Sync.SyncService.Agent() is { } agent)
                    {
                        if (await Sync.SyncChoices.MayRenewKeyAsync(agent, purpose => ConfirmWithWindows(browserId, purpose)).ConfigureAwait(false))
                            Sync.SyncService.Act("code", a => a.NewRecoveryCodeAsync());
                        else Sync.SyncService.Say("code", Sync.SyncChoices.NotConfirmed("o kit continua o mesmo"));
                    }
                    return Vault.VaultReply.Json(SettingsPage.SyncData(isPrivate: false));
                });
            case "/sync-now":
                Sync.SyncService.SyncNow();
                break;
            // ---- o kit de recuperação (0.2.15): só POST, nada no endereço além do testemunho «t» ----
            case "/sync-kit-show":
            {
                // «Ver o kit»: SEMPRE a confirmação do Windows. Conta do Windows sem proteção: primeiro só o conselho;
                // o segundo pedido («ack=1») mostra-o.
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Text("recusado");
                var acked = SyncForm.Parse(request).GetValueOrDefault("ack") == "1";
                return new VaultResourceHandler(async () =>
                {
                    if (Sync.SyncService.Agent() is not { } agent)
                        return Vault.VaultReply.Json(new { ok = false, message = "A sincronização não está disponível nesta compilação." });
                    var shown = await Sync.KitGate.ShowAsync(agent.RevealKit, Sync.SyncService.OpenKit, browserId, acked,
                        purpose => ConfirmWithWindows(browserId, purpose), () => Vault.ReauthGate.Shared.Advice,
                        () => Vault.SecretOnScreen.Show(expired: Sync.SyncService.OpenKit.Close, limit: Sync.KitUnlock.Limit)).ConfigureAwait(false);
                    switch (shown.Step)
                    {
                        case Sync.KitShowStep.AdviceFirst:
                            return Vault.VaultReply.Json(new { ok = true, advice = shown.Advice });
                        case Sync.KitShowStep.Reveal when shown.Kit is { } kit:
                            // A aba sai da frente ou a janela fecha: o kit fecha-se do lado do processo.
                            MainWindow.VaultFrontToken().Register(Sync.SyncService.OpenKit.Close);
                            return Vault.VaultReply.Json(new { ok = true, words = kit.Words, id = kit.KitId, seconds = (int)Sync.KitUnlock.Limit.TotalSeconds, advice = shown.Advice });
                        default:
                            return Vault.VaultReply.Json(new { ok = false, message = Sync.SyncChoices.NotConfirmed("o kit não foi mostrado") });
                    }
                });
            }
            case "/sync-kit-hide":
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Text("recusado");
                Sync.SyncService.OpenKit.Close();
                break;
            case "/sync-kit-copy":
                // Pela área de transferência segura do processo (fora do histórico e da nuvem do Windows; limpa aos 60 s
                // só se ainda for a nossa cópia). Fora da thread de E/S do CEF, como as cópias do cofre.
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Text("recusado");
                return new VaultResourceHandler(() =>
                {
                    if (OpenedKit(browserId) is not { } kit) return Task.FromResult(KitClosed);
                    return Task.FromResult(Vault.SecureClipboard.Shared.Copy(kit.Words)
                        ? Vault.VaultReply.Json(new { ok = true, message = "Copiado. Sai da área de transferência dentro de 1 minuto." })
                        : Vault.VaultReply.Json(new { ok = false, message = "Não foi possível copiar: tente outra vez." }));
                });
            case "/sync-kit-save":
                // «Guardar como ficheiro…»: diálogo do Windows (começa em Transferências); fora de uma pasta local
                // conhecida, avisa antes de gravar e espera por «Guardar mesmo assim».
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Text("recusado");
                return new VaultResourceHandler(async () =>
                {
                    if (OpenedKit(browserId) is null) return KitClosed;
                    var path = await WindowsVaultHost.PickKitSaveAsync(browserId, Sync.RecoveryKit.FileName(DateTime.Now)).ConfigureAwait(false);
                    if (path is null) return Vault.VaultReply.Json(new { ok = true, saved = false });
                    // O kit pode ter fechado enquanto o diálogo estava aberto (prazo, «Sair», cadeia nova).
                    if (OpenedKit(browserId) is not { } kit) return KitClosed;
                    return SaveKit(kit, path, accepted: false, saved: () => Sync.SyncService.Agent()?.MarkKitSaved());
                });
            case "/sync-kit-save-anyway":
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Text("recusado");
                return new VaultResourceHandler(() =>
                {
                    if (OpenedKit(browserId) is not { } kit || Sync.SyncService.OpenKit.TakePendingPath() is not { } path)
                        return Task.FromResult(KitClosed);
                    return Task.FromResult(SaveKit(kit, path, accepted: true, saved: () => Sync.SyncService.Agent()?.MarkKitSaved()));
                });
            case "/sync-kit-print":
            {
                // O documento do kit para o iframe de impressão da página: tal e qual (não passa pelo tema das páginas
                // internas) e sem cache.
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Text("recusado");
                if (OpenedKit(browserId) is not { } kit) return Text("recusado");
                // Um ResourceHandler próprio (o de FromString não aceita cabeçalhos).
                var document = new ResourceHandler("text/html", new System.IO.MemoryStream(Encoding.UTF8.GetBytes(Sync.RecoveryKit.Html(kit.Words, kit.KitId, DateTime.Now))),
                    autoDisposeStream: true, charset: "utf-8");
                document.Headers.Add("Cache-Control", "no-store");
                return document;
            }
            case "/sync-kit-saved":
                // «Já guardei o kit» (depois de imprimir ou copiar): só o kit que a página mostrou («id»); outro kit
                // (cadeia nova entretanto) ou sem id, nada.
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Text("recusado");
                if (SyncForm.Parse(request).GetValueOrDefault("id") is { Length: > 0 } kitId) Sync.SyncService.Agent()?.MarkKitSaved(kitId);
                break;
            case "/sync-kit-later":
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Text("recusado");
                Sync.SyncService.SnoozeKit();
                break;
            case "/sync-kit-decline":
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Text("recusado");
                Sync.SyncService.DeclineKit();
                break;
            case "/sync-kit-from-file":
                // «Abrir o ficheiro do kit…» (ecrã de entrada): o Éteris lê as palavras do ficheiro (a página nunca as vê)
                // e entra com elas.
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Text("recusado");
                return new VaultResourceHandler(async () =>
                {
                    var path = await WindowsVaultHost.PickKitOpenAsync(browserId).ConfigureAwait(false);
                    if (path is null) return Vault.VaultReply.Json(new { ok = true, picked = false });
                    if (!Sync.RecoveryKit.TryReadFile(path, p => new System.IO.FileInfo(p).Length, System.IO.File.ReadAllText, out var secret))
                        return Vault.VaultReply.Json(new { ok = false, message = "Este ficheiro não é um kit de recuperação do Éteris." });
                    string words;
                    try
                    {
                        using var account = Eteris.Sync.Crypto.AccountSecret.FromBytes(secret);
                        words = account.ToWords();
                    }
                    finally
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
                    }
                    Sync.SyncService.Act("join", a => a.JoinWithCodeAsync(words));
                    return Vault.VaultReply.Json(SettingsPage.SyncData(isPrivate: false));
                });
            case "/sync-kit-complete":
            {
                // A palavra do kit que começa pelas letras escritas (a partir de 4).
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Text("recusado");
                var p = SyncForm.Parse(request).GetValueOrDefault("p");
                return Json(new { ok = true, p, words = Sync.RecoveryKit.Completion(p) });
            }
            case "/sync-kit-open":
                // A página confere de 2 em 2 s se o kit à vista continua aberto: só pergunta (nunca abre, fecha nem lê o kit).
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Text("recusado");
                return Json(new { ok = true, open = Sync.SyncService.OpenKit.IsOpen(browserId) });
            case "/sync-pair":
                // «Aprovar noutro computador» e «Pedir outra vez». Com a conta substituída não pede (só «Foi eu», com a
                // confirmação do Windows).
                Sync.SyncService.AskPairingAgain();
                break;
            case "/sync-replaced-join":
                // «Foi eu — juntar este computador»: com segredos ligados aqui, o Windows confirma primeiro (sempre).
                return new VaultResourceHandler(async () =>
                {
                    await Sync.SyncService.JoinAfterReplacedAsync(purpose => ConfirmWithWindows(browserId, purpose)).ConfigureAwait(false);
                    return Vault.VaultReply.Json(SettingsPage.SyncData(isPrivate: false));
                });
            case "/sync-pair-confirm":
                Sync.SyncService.Agent()?.ConfirmPairing();
                break;
            case "/sync-pair-cancel":
                Sync.SyncService.Agent()?.CancelPairing();
                break;
            case "/sync-join":
            {
                // O kit de recuperação vai no corpo de um POST (nunca no endereço).
                var form = SyncForm.Parse(request);
                if (form.GetValueOrDefault("code") is not { Length: > 0 } code) return Json(new { ok = false, message = "Escreva ou cole o kit de recuperação." });
                Sync.SyncService.Act("join", agent => agent.JoinWithCodeAsync(code));
                break;
            }
            case "/sync-device-name":
            {
                // O nome novo vai no corpo de um POST (nunca no endereço).
                var name = SyncForm.Parse(request).GetValueOrDefault("name");
                Sync.SyncService.Act("device-name", agent => agent.RenameDeviceAsync(name));
                break;
            }
            // Aprovar um computador novo não tem rota: faz-se só na barra da janela, escrevendo o código que ele mostra
            // (0.2.15, «number matching»). Recusar é sempre seguro e fica aqui.
            case "/sync-refuse" when InternalPages.Query(uri, "id") is { Length: > 0 } id:
                Sync.SyncService.Act("approve", agent => agent.RefuseAsync(id));
                break;
            case "/sync-requests-show":
                // «Mostrar o pedido na barra» (pedidos em série): a barra volta a abrir sozinha. Sem rede.
                Sync.SyncService.ResumePairingBar();
                break;
            case "/sync-remove" when InternalPages.Query(uri, "id") is { Length: > 0 } id:
            {
                // «shown»: os aparelhos que a página mostrava quando o utilizador decidiu (só esses recebem a chave nova).
                var shown = InternalPages.Query(uri, "shown") is { Length: > 0 } list ? list.Split(',', StringSplitOptions.RemoveEmptyEntries) : null;
                // Remover um aparelho faz uma cadeia nova e o kit de recuperação passa a ser outro (por guardar): com
                // segredos em jogo, o Windows confirma primeiro (sempre).
                return new VaultResourceHandler(async () =>
                {
                    if (Sync.SyncService.Agent() is { } agent)
                    {
                        if (await Sync.SyncChoices.MayRenewKeyAsync(agent, purpose => ConfirmWithWindows(browserId, purpose)).ConfigureAwait(false))
                            Sync.SyncService.Act("devices", a => a.RemoveDeviceAsync(id, shown));
                        else Sync.SyncService.Say("devices", Sync.SyncChoices.NotConfirmed("o aparelho continua na conta"));
                    }
                    return Vault.VaultReply.Json(SettingsPage.SyncData(isPrivate: false));
                });
            }
            case "/sync-lost" when InternalPages.Query(uri, "id") is { Length: > 0 } id:
            {
                // «Perdi um computador / foi roubado»: uma confirmação na página e SEMPRE o Windows (propósito próprio,
                // haja ou não segredos), e depois remover o aparelho e retirar o acesso na Google, por esta ordem.
                var shown = InternalPages.Query(uri, "shown") is { Length: > 0 } list ? list.Split(',', StringSplitOptions.RemoveEmptyEntries) : null;
                return new VaultResourceHandler(async () =>
                {
                    if (await Sync.SyncExit.MayLoseDeviceAsync(purpose => ConfirmWithWindows(browserId, purpose)).ConfigureAwait(false))
                        Sync.SyncService.LostDevice(id, shown);
                    else Sync.SyncService.Say("lost", Sync.SyncChoices.NotConfirmed("o aparelho continua na conta"));
                    return Vault.VaultReply.Json(SettingsPage.SyncData(isPrivate: false));
                });
            }
            case "/sync-start-over":
            {
                // «Começar do zero…» (só antes de entrar): primeiro a palavra escrita, depois SEMPRE o Windows, e só então
                // apagar a conta antiga da nuvem e criar uma nova a partir deste computador.
                var word = InternalPages.Query(uri, "word");
                return new VaultResourceHandler(async () =>
                {
                    if (!Sync.SyncExit.StartOverWordMatches(word)) Sync.SyncService.Say("startover", Sync.SyncExit.StartOverWrongWord);
                    else if (await Sync.SyncExit.MayStartOverAsync(purpose => ConfirmWithWindows(browserId, purpose)).ConfigureAwait(false))
                        Sync.SyncService.StartOver();
                    else Sync.SyncService.Say("startover", Sync.SyncChoices.NotConfirmed("a conta na nuvem não foi apagada"));
                    return Vault.VaultReply.Json(SettingsPage.SyncData(isPrivate: false));
                });
            }
            case "/sync-delete-cloud":
                Sync.SyncService.DeleteCloud();
                break;
            case "/sync-delete-retry":
                Sync.SyncService.RetryDeleteCloud();
                break;
            case "/sync-status":
                break;
            default:
                return ResourceHandler.ForErrorMessage("Página interna não encontrada", HttpStatusCode.NotFound);
        }
        return Json(SettingsPage.SyncData(isPrivate: false));
    }

    private static IResourceHandler Json(object value)
        => ResourceHandler.FromString(System.Text.Json.JsonSerializer.Serialize(value), Encoding.UTF8, false, "text/plain");

    // O kit aberto NESTE separador pela confirmação do Windows, ou null.
    private static Eteris.Sync.Engine.RecoveryKitSecret? OpenedKit(int browserId)
        => Sync.SyncService.Agent() is { } agent ? Sync.KitGate.Opened(agent.RevealKit, Sync.SyncService.OpenKit, browserId) : null;

    private static Vault.VaultReply KitClosed
        => Vault.VaultReply.Json(new { ok = false, closed = true, message = "O kit foi escondido. Carregue em «Ver o kit» outra vez." });

    // Grava o ficheiro do kit (já escolhido) e diz à página o que aconteceu; gravado, o kit conta como guardado.
    private static Vault.VaultReply SaveKit(Eteris.Sync.Engine.RecoveryKitSecret kit, string path, bool accepted, Action saved)
    {
        var folder = Sync.RecoveryKit.FolderOnThisComputer(path);
        switch (Sync.RecoveryKit.Save(kit, path, accepted, folder, Sync.RecoveryKit.WriteFile, DateTime.Now))
        {
            case Sync.KitSaveResult.Saved:
                saved();
                return Vault.VaultReply.Json(new { ok = true, saved = true, state = SettingsPage.SyncData(isPrivate: false) });
            case Sync.KitSaveResult.NeedsConfirm:
                Sync.SyncService.OpenKit.SetPendingPath(path);
                return Vault.VaultReply.Json(new { ok = true, saved = false, warn = Sync.RecoveryKit.FolderWarning(folder) });
            case Sync.KitSaveResult.Cancelled:
                return Vault.VaultReply.Json(new { ok = true, saved = false });
            default:
                return Vault.VaultReply.Json(new { ok = false, message = "Não foi possível gravar o ficheiro." });
        }
    }

    private static SettingsPage.About AboutInfo() => new(
        Cef.ChromiumVersion, Cef.CefSharpVersion, Shields.ShieldEngine.Cosmetic is not null,
        Shields.FilterLists.ForApp().Summary(), Updates.UpdateService.Status,
        Updates.UpdateService.State is not (Updates.UpdateState.Development or Updates.UpdateState.Ready));

    private static IResourceHandler Html(string html)
        => ResourceHandler.FromString(InternalPages.Themed(html, InternalPages.LightTheme), Encoding.UTF8, false, "text/html");

    // Aviso da rede de segurança do sandbox para a nova aba (nulo quando não há nada a dizer).
    private static string? SandboxNotice() => NewTabPrecompute.SandboxNotice();

    private static IResourceHandler Text(string text) => ResourceHandler.FromString(text, Encoding.UTF8, false, "text/plain");

    // Apaga histórico (no período escolhido), cookies e cache do perfil desta aba e, só com a caixa marcada e depois do
    // Windows Hello, o cofre das palavras-passe e do preenchimento automático (nunca a partir da janela privada).
    private IResourceHandler ClearData(Uri uri, string url, IBrowser browser)
    {
        if (!InternalPages.IsAuthorized(url)
            || ClearDataRequest.Parse(InternalPages.Query(uri, "what") ?? "", InternalPages.Query(uri, "range") ?? "", DateTime.Now) is not { } request)
            return Text("recusado");
        if (request.Vault)
        {
            if (privateContext) return Text("recusado");
            Vault.VaultRoutes.UseProfileHost(() => new WindowsVaultHost());
            var routes = Vault.VaultRoutes.Profile!;
            var browserId = browser.Identifier;
            // Primeiro a confirmação: recusada, nada se apaga (nem o histórico, nem os cookies).
            return new VaultResourceHandler(async () => await routes.EraseVaultAsync(browserId).ConfigureAwait(false)
                ? Vault.VaultReply.Text(ClearBrowsingData(request, browser))
                : Vault.VaultReply.Text("Não foi confirmado com o Windows: nada foi apagado."));
        }
        return Text(ClearBrowsingData(request, browser));
    }

    private static string ClearBrowsingData(ClearDataRequest request, IBrowser browser)
    {
        if (request.History)
        {
            var since = request.Since == DateTime.MinValue ? request.Since : request.Since.ToUniversalTime();
            AppData.TryWrite(h => h.DeleteSince(since));
            MainWindow.ForgetBounceVisits(since);
        }
        // As caches da GPU (shaders, programas WebGL) só se apagam com o motor fechado: ao sair (ajuste e3).
        if (request.Cache) Infrastructure.ProfileCleanup.RemoveGpuCachesAtExit();
        if (request.Cookies || request.Cache)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = browser.GetDevToolsClient();
                    if (request.Cookies) await DevToolsCalls.Run(() => client.Network.ClearBrowserCookiesAsync());
                    if (request.Cache) await DevToolsCalls.Run(() => client.Network.ClearBrowserCacheAsync());
                }
                catch (Exception) { }
            });
        }
        return request.Summary;
    }

    private static IResourceHandler BookmarksRoute(Uri uri, string url, int browserId)
    {
        if (uri.AbsolutePath is "/reading-read" or "/reading-remove")
        {
            if (!InternalPages.IsAuthorized(url) || InternalPages.Query(uri, "u") is not { } item) return Text("recusado");
            if (uri.AbsolutePath == "/reading-remove") AppData.RemoveFromReadingList(item);
            else AppData.MarkRead(item, InternalPages.Query(uri, "v") == "true");
            return Text("ok");
        }
        if (uri.AbsolutePath == "/export")
        {
            if (!InternalPages.IsAuthorized(url)) return Text("recusado");
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => MainWindow.WindowOf(browserId)?.ExportBookmarks());
            return Text("ok");
        }
        if (uri.AbsolutePath is "/session-open" or "/session-rename" or "/session-delete")
        {
            if (!InternalPages.IsAuthorized(url) || !long.TryParse(InternalPages.Query(uri, "id"), out var sessionId)) return Text("recusado");
            switch (uri.AbsolutePath)
            {
                case "/session-delete":
                    AppData.DeleteSession(sessionId);
                    return Text("ok");
                case "/session-rename":
                    return InternalPages.Query(uri, "v") is { Length: > 0 and <= 120 } name && AppData.RenameSession(sessionId, name.Trim())
                        ? Text("ok") : Text("recusado");
                default:
                    if (AppData.SavedSessions().FirstOrDefault(s => s.Id == sessionId) is { } session)
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => MainWindow.OpenSavedSessionWindow(session));
                    return Text("ok");
            }
        }
        if (uri.AbsolutePath is "/delete" or "/rename")
        {
            if (!InternalPages.IsAuthorized(url) || InternalPages.Query(uri, "u") is not { } target) return Text("recusado");
            if (uri.AbsolutePath == "/delete") AppData.RemoveBookmark(target);
            else if (InternalPages.Query(uri, "v") is { Length: > 0 and <= 300 } title) AppData.RenameBookmark(target, title);
            return Text("ok");
        }
        return uri.AbsolutePath == "/"
            ? Html(BookmarksPage.Render(AppData.Bookmarks(b => b.All(1000)), AppData.ReadingList(), AppData.SavedSessions()))
            : ResourceHandler.ForErrorMessage("Página interna não encontrada", HttpStatusCode.NotFound);
    }

    private IResourceHandler History(Uri uri, string url)
    {
        if (privateContext)
            return uri.AbsolutePath == "/"
                ? Html(HistoryPage.Render([], DateTime.Now))
                : ResourceHandler.ForErrorMessage("Pedido recusado", HttpStatusCode.Forbidden);

        switch (uri.AbsolutePath)
        {
            case "/delete" when HistoryPage.IsAuthorized(url) && long.TryParse(HistoryPage.Query(uri, "id"), out var id):
                AppData.TryWrite(h => h.DeleteVisit(id));
                return ResourceHandler.FromString("ok", Encoding.UTF8, false, "text/plain");
            case "/clear" when HistoryPage.IsAuthorized(url)
                               && HistoryPage.RangeStart(HistoryPage.Query(uri, "range") ?? "", DateTime.Now) is { } start:
                var from = start == DateTime.MinValue ? start : start.ToUniversalTime();
                AppData.TryWrite(h => h.DeleteSince(from));
                MainWindow.ForgetBounceVisits(from);
                break;
            case "/" or "/clear":
                break;
            default:
                return ResourceHandler.ForErrorMessage("Pedido recusado", HttpStatusCode.Forbidden);
        }
        var visits = AppData.TryRead(h => h.Visits(HistoryPage.MaxVisits));
        return Html(HistoryPage.Render(visits, DateTime.Now, icons: AppData.SiteIcons(visits.Select(v => v.Url))));
    }
}
