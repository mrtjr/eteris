using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B4 «Promessas verificáveis»: eteris://privacy/ e «Esconder o Éteris do Windows Recall e de capturas».
public class PrivacyPageTests
{
    private static Dictionary<string, string> EngineArgs() => new(CefConfigurator.Build("p").CefCommandLineArgs);

    private static PrivacyPage.State Good(Dictionary<string, string>? args = null) => new(
        args ?? EngineArgs(), PrivacySignals.Headers, GpcScript: true, ThirdPartyCookiesBlocked: true, WebRtcProtected: true,
        SecureDns: null, Language: "pt-PT", HideFromCapture: false, VisibleWindows: 1, HiddenWindows: 0);

    private static PrivacyPage.Promise Find(PrivacyPage.State state, string id) => Assert.Single(PrivacyPage.Promises(state), p => p.Id == id);

    [Fact]
    public void Promises_WithTheRealEngineConfiguration_AreKept()
    {
        var state = Good();
        foreach (var id in new[] { "gpc-header", "gpc-js", "ad-apis", "quiet", "no-google", "cookies", "webrtc" })
            Assert.True(Find(state, id).Kept, id);
    }

    // As promessas são lidas do estado, não escritas à mão: tirar um interruptor faz a linha ficar ✘ e dizer qual.
    [Fact]
    public void AdApis_MissingSwitch_IsBrokenAndNamed()
    {
        var args = EngineArgs();
        args["disable-features"] = string.Join(',', EngineHardening.Switch(args, "disable-features").Where(f => f != "BrowsingTopics"));
        var promise = Find(Good(args), "ad-apis");
        Assert.False(promise.Kept);
        Assert.Contains("BrowsingTopics", promise.Detail);
    }

    // Revisão aos 50: a política de IP do WebRTC fixada na linha de comandos e sem o mDNS do WebRTC (UDP 5353 à escuta,
    // aviso da firewall do Windows); faltar qualquer um deixa a linha ✘ a dizer o quê.
    [Fact]
    public void WebRtc_NeedsThePolicySwitchAndNoMdns()
    {
        var noSwitch = EngineArgs();
        noSwitch.Remove(EngineHardening.WebRtcPolicySwitch);
        Assert.False(Find(Good(noSwitch), "webrtc").Kept);
        Assert.Contains("desde o arranque", Find(Good(noSwitch), "webrtc").Detail);
        var mdns = EngineArgs();
        mdns["disable-features"] = string.Join(',', EngineHardening.Switch(mdns, "disable-features").Where(f => f != "WebRtcHideLocalIpsWithMdns"));
        Assert.False(Find(Good(mdns), "webrtc").Kept);
        Assert.Contains("firewall", Find(Good(mdns), "webrtc").Detail);
        Assert.False(Find(Good() with { WebRtcProtected = false }, "webrtc").Kept);
    }

    // «No arranque, nada sai para a Google»: lida da linha de comandos do motor; sem um dos interruptores fica ✘ e diz qual.
    [Theory]
    [InlineData("NetworkTimeServiceQuerying")]
    [InlineData("AimEnabled")]
    public void NoGoogle_MissingSwitch_IsBrokenAndNamed(string feature)
    {
        Assert.True(Find(Good(), "no-google").Kept);
        var args = EngineArgs();
        args["disable-features"] = string.Join(',', EngineHardening.Switch(args, "disable-features").Where(f => f != feature));
        var promise = Find(Good(args), "no-google");
        Assert.False(promise.Kept);
        Assert.Contains(feature, promise.Detail);
    }

    // Por omissão (sincronização desligada) a promessa é só a do motor; com a sincronização ligada (escolha do
    // utilizador) continua cumprida e diz a exceção: só os pedidos da sincronização, cifrados, para o Drive dele, e quando.
    [Fact]
    public void NoGoogle_WithSyncOn_StaysKept_AndStatesTheException()
    {
        var off = Find(Good(), "no-google");
        Assert.True(off.Kept);
        Assert.DoesNotContain("sincronização", off.Detail);
        var on = Find(Good() with { SyncOn = true }, "no-google");
        Assert.True(on.Kept);
        // A exceção é o que fecha a lista dos momentos; a frase do que a Google vê fecha a promessa (0.2.15).
        Assert.Contains(PrivacyPage.SyncException, on.Detail);
        Assert.EndsWith(PrivacyPage.SyncException + " " + PrivacyPage.SyncSees, on.Detail);
        Assert.EndsWith(PrivacyPage.SyncSees, on.Detail);
        Assert.Contains("30 s depois de abrir", on.Detail);
        Assert.Contains("cifrado", on.Detail);
        // A lista é a de verdade: o menu é o «⋯», a renovação da sessão e as ações do utilizador também contam, e não
        // há um «e mais nada» que a lista não cumpra.
        Assert.Contains("menu do Éteris (⋯)", on.Detail);
        Assert.Contains("oauth2.googleapis.com", on.Detail);
        Assert.Contains("nome e o e-mail", on.Detail);
        Assert.Contains("de 2 em 2 s", on.Detail);
        Assert.DoesNotContain("e mais nada", on.Detail);
        Assert.DoesNotContain("menu da conta", on.Detail);
    }

    // 0.2.15 (T8): voltar à janela do Éteris também procura pedidos, com o limite de ActivationLook.
    [Fact]
    public void SyncException_NamesComingBackToTheWindow_AndItsLimit()
    {
        Assert.Contains("volta à janela do Éteris", PrivacyPage.SyncException);
        Assert.Contains("no máximo uma vez a cada 5 minutos, ou logo se uma ronda já tiver encontrado um pedido", PrivacyPage.SyncException);
    }

    // 0.2.15 (T12): «Sair deste computador» não fala com a Google; «Retirar o acesso…» revoga e desliga todos. A
    // variante confere-se pela fonte: a rota de sair não revoga.
    [Fact]
    public void SyncException_SaysSignOutIsLocal_AndRemovingAccessDisconnectsEveryone()
    {
        var factory = Sync.GoogleSyncTests.Src("Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs");
        Assert.DoesNotContain("Revoke", Sync.GoogleSyncTests.CaseBlock(factory, "case \"/sync-google-out\":"));
        var text = PrivacyPage.SyncException;
        Assert.Contains("retirar o acesso do Éteris à conta Google (que desliga todos os computadores)", text);
        Assert.DoesNotContain("sair (que retira a autorização na Google)", text);
        Assert.EndsWith("Sair deste computador não fala com a Google.", text);
        Assert.DoesNotContain("sair deste computador e", text);
        Assert.Contains("entrar, sincronizar agora, aprovar ou recusar um computador", text);
        // Pedidos imediatos que o plano não listava: gerar um kit novo troca a chave no Drive (/sync-new-code); ligar
        // uma linha do tópico ou aceitar a oferta faz logo uma ronda (SyncService.SyncNow).
        Assert.Contains("gerar um kit novo", text);
        Assert.Contains("ligar uma linha de «O que passa entre PCs» ou aceitar pagamentos e documentos", text);
        Assert.Contains("if (error is null && on) Sync.SyncService.SyncNow();", factory);
        Assert.Contains("a.NewRecoveryCodeAsync()", Sync.GoogleSyncTests.CaseBlock(factory, "case \"/sync-new-code\":"));
    }

    // R13: cada número da exceção é o da constante do código; se uma mudar, este teste obriga a mudar o texto.
    [Fact]
    public void SyncException_NumbersMatchTheCode()
    {
        var text = PrivacyPage.SyncException;
        Assert.Contains("30 s depois de abrir", text);
        Assert.Equal(30, Eteris.Sync.Engine.SyncSchedule.StartupDelay.TotalSeconds);
        Assert.Contains("10 s depois de uma alteração", text);
        Assert.Equal(10, Eteris.Sync.Engine.SyncSchedule.ChangeDelay.TotalSeconds);
        Assert.Contains("de 10 em 10 minutos", text);
        Assert.Equal(10, Eteris.Sync.Engine.SyncSchedule.Period.TotalMinutes);
        Assert.Contains("de 2 em 2 s", text);
        Assert.Contains("public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);",
            Sync.GoogleSyncTests.Src("Eteris.Sync", "Pairing", "Pairing.cs"));
        Assert.Contains("no máximo 10 minutos", text);
        Assert.Equal(10, Eteris.Sync.Pairing.PairingProtocol.Lifetime.TotalMinutes);
        Assert.Contains("a cada 5 minutos", text);
        Assert.Equal(TimeSpan.FromMinutes(5), Eteris.Browser.Sync.ActivationLook.Interval);
    }

    // A frase da chave (plano §3.5, com a condição do Windows que o código tem). Vive no bloco sempre visível, não na
    // promessa das palavras-passe.
    [Fact]
    public void SyncKey_SaysWhereTheKeyLives()
    {
        var key = PrivacyPage.SyncKey;
        Assert.Contains("nunca vai para a Google nem para o projeto Éteris", key);
        Assert.Contains("kit de recuperação", key);
        Assert.Contains("não se recupera, por ninguém", key);
        Assert.DoesNotContain("*", key);
        foreach (var syncOn in new[] { false, true })
            Assert.DoesNotContain(key, Find(Good() with { SyncOn = syncOn }, "passwords").Detail);
    }

    [Fact]
    public void WithSyncOff_ThePromisesSayNothingAboutTheException()
    {
        foreach (var p in PrivacyPage.Promises(Good()))
        {
            Assert.DoesNotContain(PrivacyPage.SyncException, p.Detail);
            Assert.DoesNotContain(PrivacyPage.SyncSees, p.Detail);
        }
        Assert.Contains("Com a sincronização desligada, não saem deste computador.", Find(Good(), "passwords").Detail);
    }

    private static string PrivacyTemplate()
        => Sync.GoogleSyncTests.Src("Eteris.Browser", "Browser", "privacy.html");

    // R15 (S33): o bloco «Se ligar a sincronização» está sempre na página, com a sincronização ligada ou desligada.
    [Fact]
    public void PrivacyPage_AlwaysHasTheSyncBlock()
    {
        foreach (var syncOn in new[] { false, true })
        {
            var html = PrivacyPage.Render(Good() with { SyncOn = syncOn });
            Assert.Contains("<section id=\"sincronizacao\"", html);
            Assert.Contains("\"sync\":{\"title\":", html);
        }
        Assert.Equal("Favoritos, Espaços, definições, decisões do Escudo, notas, palavras-passe, contactos e viagens; pagamentos e documentos de identificação só se os ligar. O histórico, as abas abertas, cookies, cache e permissões dos sites não vão.",
            PrivacyPage.SyncGoes);
        Assert.Equal("A Google vê que usa o Éteris, quando sincroniza, quantos ficheiros há e o tamanho aproximado deles. Não vê o que está lá dentro.",
            PrivacyPage.SyncSees);
        Assert.Equal("Se ligar a sincronização", PrivacyPage.SyncBlockTitle);
        var page = PrivacyTemplate();
        foreach (var part in new[] { "title", "goes", "sees", "key" })
            Assert.Matches(@"\.textContent = data\.sync\." + part + ";", page);
        Assert.DoesNotContain("innerHTML", page);
        Assert.True(page.IndexOf("<section id=\"sincronizacao\"", StringComparison.Ordinal) < page.IndexOf("<h2>Promessas</h2>", StringComparison.Ordinal));
    }

    // R15 e a outra metade do teste 16 da T14: quem ainda não entrou lê o que vai para a Google, e a ligação das
    // Definições aponta para este bloco.
    [Fact]
    public void SignedOut_CanReadWhatGoesToGoogle_BeforeSigningIn()
    {
        var html = PrivacyPage.Render(Good());
        Assert.Contains("<section id=\"sincronizacao\"", html);
        // O conteúdo não depende do estado: SyncBlock é uma propriedade estática, sem State.
        Assert.NotNull(typeof(PrivacyPage).GetProperty(nameof(PrivacyPage.SyncBlock))?.GetGetMethod());
        Assert.Contains("href=\"eteris://privacy/#sincronizacao\"", Sync.GoogleSyncTests.Src("Eteris.Browser", "Browser", "settings.html"));
    }

    // R15: o bloco é uma explicação, não uma promessa medida, e não dá à página nenhum poder de rede novo.
    [Fact]
    public void SyncBlock_IsNotAPromise_AndAddsNoNetworkPower()
    {
        string[] ids = ["gpc-header", "gpc-js", "ad-apis", "quiet", "passwords", "no-google", "cookies", "jars", "referrer", "language",
            "hints", "lan", "bounce", "cname", "webrtc", "dns", "recall", "gpu-cache", "engine-tuning"];
        foreach (var syncOn in new[] { false, true })
            Assert.Equal(ids, PrivacyPage.Promises(Good() with { SyncOn = syncOn }).Select(p => p.Id));
        var page = PrivacyTemplate();
        Assert.Contains("content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'\"", page);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "Content-Security-Policy"));
        Assert.DoesNotContain("fetch(", page);
        Assert.DoesNotContain("XMLHttpRequest", page);
    }

    [Fact]
    public void Quiet_WithoutBackgroundNetworkingSwitch_IsBroken()
    {
        var args = EngineArgs();
        args.Remove("disable-background-networking");
        Assert.False(Find(Good(args), "quiet").Kept);
    }

    [Fact]
    public void GpcHeader_ReadsTheHeadersTheHandlerSends()
    {
        Assert.True(Find(Good(), "gpc-header").Kept);
        Assert.False(Find(Good() with { Headers = [("DNT", "1")] }, "gpc-header").Kept);
        Assert.False(Find(Good() with { Headers = [("Sec-GPC", "1"), ("DNT", "1")] }, "gpc-header").Kept);
    }

    [Fact]
    public void LiveChecks_AreDeclaredForGpcAndAdApis()
    {
        Assert.Equal("gpc", Find(Good(), "gpc-js").Live);
        Assert.Equal("ads", Find(Good(), "ad-apis").Live);
    }

    [Fact]
    public void Cookies_Off_IsBrokenWithAWayToFixIt()
    {
        var promise = Find(Good() with { ThirdPartyCookiesBlocked = false }, "cookies");
        Assert.False(promise.Kept);
        Assert.Equal("eteris://settings/#privacidade", promise.Fix);
    }

    [Fact]
    public void Optional_DnsAndRecallOff_AreNeitherKeptNorBroken()
    {
        Assert.Null(Find(Good(), "dns").Kept);
        Assert.Null(Find(Good(), "recall").Kept);
        Assert.True(Find(Good() with { SecureDns = "Quad9" }, "dns").Kept);
    }

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(2, 1, false)]
    [InlineData(0, 0, false)]
    public void Recall_On_ChecksEveryVisibleWindow(int visible, int hidden, bool kept)
        => Assert.Equal(kept, Find(Good() with { HideFromCapture = true, VisibleWindows = visible, HiddenWindows = hidden }, "recall").Kept);

    [Fact]
    public void Render_FillsTheTemplateWithThePromises()
    {
        var html = PrivacyPage.Render(Good());
        Assert.DoesNotContain("{{DATA}}", html);
        Assert.Contains("\"id\":\"ad-apis\"", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("navigator.globalPrivacyControl === true", html);
        Assert.Contains("'browsingTopics' in document", html);
    }

    [Fact]
    public void Page_OpensFromTheCommandLineLikeTheOtherInternalPages()
        => Assert.Equal(PrivacyPage.Url, StartupArgs.InitialUrl([PrivacyPage.Url]));

    // Desligado por omissão (também na janela privada): ligado, as capturas de ecrã do próprio utilizador ficam vazias.
    [Fact]
    public void HideFromCapture_IsOffByDefault()
    {
        Assert.False(new UserSettings().HideFromCapture);
        Assert.True(new UserSettings().With("hideFromCapture", "true")!.HideFromCapture);
        Assert.Null(new UserSettings().With("hideFromCapture", "talvez"));
    }

    [Theory]
    [InlineData(false, 26100, CaptureShield.WdaNone)]
    [InlineData(true, 26100, CaptureShield.WdaExcludeFromCapture)]
    [InlineData(true, 19041, CaptureShield.WdaExcludeFromCapture)]
    [InlineData(true, 18363, CaptureShield.WdaMonitor)]
    public void Affinity_ExcludesFromCaptureWhereWindowsSupportsIt(bool hide, int build, uint expected)
        => Assert.Equal(expected, CaptureShield.AffinityFor(hide, build));

    [Fact]
    public void CaptureTexts_WarnThatCapturesBecomeEmpty()
    {
        Assert.Contains("vazio", CaptureText.Toast(true));
        Assert.Contains("Recall", CaptureText.BadgeTip(false));
        Assert.Contains("voltar a mostrar", CaptureText.BadgeTip(true));
    }

    [Fact]
    public void SettingsPage_OffersTheToggleWithItsWarningAndLinksToThePromises()
    {
        var html = SettingsPage.Render(new UserSettings { HideFromCapture = true });
        Assert.Contains("data-key=\"hideFromCapture\"", html);
        Assert.Contains("também ficam vazias", html);
        Assert.Contains("\"hideFromCapture\":true", html);
        Assert.Contains("href=\"eteris://privacy/\"", html);
    }
}
