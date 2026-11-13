using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Omnibox;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;
using Eteris.Browser.Watching;

namespace Eteris.Browser.Tests;

// Os scripts que o Éteris injeta nas páginas são texto C#: um erro de sintaxe só aparecia com a app a correr
// (o modo de leitura deixou de abrir assim). Aqui cada um é compilado pelo Node; sem Node instalado, o teste falha
// (NodeRunner).
public class InjectedScriptsSyntaxTests
{
    public static TheoryData<string, string> Scripts => new()
    {
        { "leitor (deteção)", ReaderScripts.Detect },
        { "leitor (abrir/fechar)", ReaderScripts.ToggleWith("sepia", 22, false) },
        { "seguir links pelo teclado", LinkHints.Script },
        { "escurecer (ligar)", DarkPages.Live(true) },
        { "escurecer (desligar)", DarkPages.Live(false) },
        { "multimédia (estado)", MediaInfo.Script },
        { "multimédia (tocar/pausar)", MediaInfo.ToggleScript },
        { "multimédia (pausar)", MediaInfo.PauseScript },
        { "abas quentes (vídeo a tocar)", WarmTabs.PlayingVideoScript },
        { "sono (pergunta antes de adormecer)", SleepProbe.Script },
        { "áudio", AudioIndicator.AudibleScript },
        { "filtros cosméticos", CosmeticScripts.Inject(".anuncio { display: none !important; }") },
        { "filtros cosméticos (antes da página)", CosmeticScripts.InjectEarly(".anuncio { display: none !important; }") },
        { "corpo async (DevTools)", Eteris.Browser.Browser.PageScript.Expression("return await Promise.resolve(1);", asyncBody: true) },
        { "vídeo só em H.264 (deteção)", VideoEngine.WatchScript },
        { "vídeo só em H.264 (deteção tardia)", VideoEngine.LateWatchScript },
        { "vídeo (posição antes da troca)", VideoEngine.PositionScript },
        { "último clique (pop-unders)", PopupPolicy.ActivationScript },
        { "escurecer (antes da página)", DarkPages.EarlyScript },
        { "espreitar (extração do artigo)", LinkPreview.Script },
        { "espreitar (Alt+clique)", LinkPreview.AltClickScript },
        { "Global Privacy Control (navigator)", PrivacySignals.GpcScript },
        { "dispositivos sem permissão (navigator.mediaDevices)", PrivacySignals.DevicesScript },
        { "registo da sessão (GPC e dispositivos)", PrivacySignals.SessionScript },
        { "auto-teste de privacidade (medições)", Eteris.Browser.Browser.PageScript.Expression(SelfTest.MeasureScript, asyncBody: true) },
        { "auto-teste de privacidade (resultados)", SelfTest.RenderScript([new("gpc", "GPC", true, "a «b» 'c' </script>"), new("canvas", "Canvas", null, "x")]) },
        { "auto-teste de privacidade (falha)", SelfTest.FailedScript("tempo esgotado") },
        { "scripts de um site (guarda do topo)", HostScriptRegistry.Guard("www.publico.pt",
            ScriptletScripts.Wrap("testMark(1)", "https://www.publico.pt/") + "\n;\n" + CosmeticScripts.InjectEarly(".a { display: none }") + DarkPages.EarlyScript)! },
        { "cofre (mundo isolado)", Eteris.Browser.Vault.VaultScripts.Cofre("eabc123") },
        { "script base (por protocolo)", HostScriptRegistry.ForProtocols(["http:", "https:"], VideoEngine.WatchScript + "\n;\n" + PopupPolicy.ActivationScript) },
        { "esconder elemento (escolha)", Eteris.Browser.Browser.PageScript.Expression(PickerScript.Pick, asyncBody: true) },
        { "imagem na imagem (janela escondida)", UnseenWindow.PictureInPictureScript },
        { "codecs da plataforma (sonda)", PlatformCodecs.ProbeScript },
        { "troca de página (1.º fotograma)", Eteris.Browser.Browser.PageScript.Expression(PageSwap.FirstPaintScript, asyncBody: true) },
        { "vigia (leitura)", WatchScripts.Read("#preco") },
        { "vigia (leitura sem pedaço)", WatchScripts.Read(null) },
        { "vigia (leitura, seletor com aspas)", WatchScripts.Read("a[title=\"x'y\"] </script>") },
        { "vigia (escolher pedaço)", Eteris.Browser.Browser.PageScript.Expression(WatchScripts.PickPiece("Clique no pedaço a vigiar · Esc para cancelar"), asyncBody: true) },
        { "recorte (escolher pedaço)", Eteris.Browser.Browser.PageScript.Expression(WatchScripts.PickPiece("Clique no pedaço a recortar · Esc para cancelar",
            "Este pedaço repete-se na página e não dá para o recortar sozinho · escolha outro ou Esc para cancelar"), asyncBody: true) },
        { "recorte (isolar)", ClipScripts.Isolate("#preco") },
        { "recorte (isolar, seletor com aspas)", ClipScripts.Isolate("a[title=\"x'y\"] </script>") },
    };

    // As páginas internas, como são servidas: o script sem atributos de cada uma.
    [Theory]
    [InlineData("error")]
    [InlineData("history")]
    [InlineData("settings")]
    [InlineData("bookmarks")]
    [InlineData("welcome")]
    [InlineData("whatsnew")]
    [InlineData("vigias")]
    public void InternalPageScript_IsValidJavaScript(string page)
    {
        var html = page switch
        {
            "error" => ErrorPage.Render(-105, "https://exemplo.pt/"),
            "history" => HistoryPage.Render([], new DateTime(2026, 11, 11)),
            "settings" => SettingsPage.Render(new Eteris.Browser.Data.UserSettings()),
            "bookmarks" => BookmarksPage.Render([]),
            "welcome" => WelcomePage.Render(new Eteris.Browser.Data.UserSettings(), []),
            "whatsnew" => WhatsNewPage.Render(SettingsPage.Version),
            "vigias" => WatchesPage.Render([], _ => [], new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc)),
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };
        var match = Regex.Match(html, @"<script>(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success, page + ": sem <script>");
        CheckSyntax(page, match.Groups[1].Value);
    }

    // O script da página eteris://privacy/ (relatório, promessas e auto-teste), como é servido.
    [Fact]
    public void PrivacyPageScript_IsValidJavaScript()
    {
        var html = PrivacyPage.Render(new PrivacyPage.State(new Dictionary<string, string>(), PrivacySignals.Headers, true, true, true, null,
            "pt-PT", false, 1, 0), [], new DateOnly(2026, 11, 7));
        var match = Regex.Match(html, @"<script>(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success);
        CheckSyntax("privacidade", match.Groups[1].Value);
    }

    // As páginas do cofre (eteris://passwords/ e eteris://autofill/), como são servidas.
    [Theory]
    [InlineData("passwords")]
    [InlineData("autofill")]
    public void VaultPageScript_IsValidJavaScript(string page)
    {
        var html = page == "passwords"
            ? PasswordsPage.Render(new { token = "t", logins = Array.Empty<object>(), never = Array.Empty<object>(), pwOffer = true, pwSuggest = true, pwAskHello = false })
            : AutofillPage.Render(new
            {
                token = "t", cards = Array.Empty<object>(), ibans = Array.Empty<object>(), contacts = Array.Empty<object>(), docs = Array.Empty<object>(),
                travel = Array.Empty<object>(), docTypes = Array.Empty<object>(), addressForms = new Dictionary<string, object>(), afPayments = true,
            });
        var match = Regex.Match(html, @"<script>(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success);
        CheckSyntax(page, match.Groups[1].Value);
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void InjectedScript_IsValidJavaScript(string name, string script) => CheckSyntax(name, script);

    [Fact]
    public void NewTabScript_IsValidJavaScript()
    {
        var html = NewTabPage.Render([], SearchEngine.DuckDuckGo, DateTime.Today);
        var match = Regex.Match(html, @"<script>(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success);
        CheckSyntax("nova aba", match.Groups[1].Value);
    }

    private static void CheckSyntax(string name, string script) => NodeRunner.CheckSyntax(name, script);
}
