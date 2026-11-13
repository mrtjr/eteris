using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Eteris.Browser.Browser;
using Eteris.Browser.Omnibox;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

// Proposta 3 («Pele nova»): a folha comum das páginas internas (Browser/EterisSkin.cs) — barras de 10 px sem setas, o
// polegar com contraste de pelo menos 3:1 nos dois temas, «reduzir movimento» e o alto contraste do Windows.
public class EterisSkinTests
{
    // As páginas internas que vêm de um modelo embutido (Browser/*.html).
    public static readonly string[] Templates =
        ["newtab", "history", "settings", "bookmarks", "error", "passwords", "autofill", "privacy", "welcome", "whatsnew"];

    public static TheoryData<string> TemplateNames() => [.. Templates];

    internal static string Template(string page)
    {
        using var stream = typeof(InternalPages).Assembly.GetManifestResourceStream($"Eteris.Browser.Browser.{page}.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // No Chromium 121 e seguintes, scrollbar-color ou scrollbar-width desligam o ::-webkit-scrollbar do elemento.
    [Fact]
    public void Sheet_NeverUsesTheStandardScrollbarProperties()
    {
        Assert.DoesNotContain("scrollbar-color", EterisSkin.Css);
        Assert.DoesNotContain("scrollbar-width", EterisSkin.Css);
    }

    [Fact]
    public void Sheet_HasThinRoundedBarsWithoutArrows()
    {
        Assert.Contains("::-webkit-scrollbar { width: 10px; height: 10px; background: transparent; }", EterisSkin.Css);
        Assert.Contains("::-webkit-scrollbar-button { display: none; }", EterisSkin.Css);
        Assert.Contains("::-webkit-scrollbar-track, ::-webkit-scrollbar-corner { background: transparent; }", EterisSkin.Css);
        Assert.Contains("border-radius: 999px;", EterisSkin.Css);
        Assert.Contains("::-webkit-scrollbar-thumb:hover { background-color: var(--sb-thumb-hover); }", EterisSkin.Css);
    }

    [Fact]
    public void Sheet_RespectsReducedMotionHighContrastAndFocus()
    {
        var motion = EterisSkin.Css.IndexOf("@media (prefers-reduced-motion: reduce)", StringComparison.Ordinal);
        Assert.True(motion > 0);
        Assert.Contains("transition-duration: .01ms !important", EterisSkin.Css[motion..]);
        Assert.Contains("animation-duration: .01ms !important", EterisSkin.Css[motion..]);
        Assert.Contains("scroll-behavior: auto !important", EterisSkin.Css[motion..]);
        // Com especificidade zero (:where): uma página que já tem o seu foco (input { outline: none } com a borda íris do
        // Histórico) fica com ele, sem um segundo anel por cima; as outras ganham o anel (sem --accent, o --focus-ring
        // do tema: FocusRing_HasAtLeastThreeToOneContrast).
        Assert.Contains(":where(:focus-visible) { outline: 2px solid var(--accent, var(--focus-ring)); outline-offset: 2px; }", EterisSkin.Css);
        Assert.DoesNotMatch(@"(^|[\s}]):focus-visible", EterisSkin.Css);
        foreach (var radius in new[] { "--r-s: 6px", "--r-m: 10px", "--r-l: 14px", "--r-xl: 22px" })
            Assert.Contains(radius, EterisSkin.Css);
    }

    // Alto contraste do Windows (forced-colors): basta uma regra ::-webkit-scrollbar a valer para o Blink desenhar a barra
    // à mão — com «all: unset» por cima ficava uma calha vazia, sem polegar nem setas (medido no Chrome 157 com
    // --force-high-contrast). As regras das barras só valem fora do alto contraste; aí ficam as do sistema.
    [Fact]
    public void Scrollbars_OnlyOutsideForcedColors()
    {
        const string Gate = "@media not (forced-colors: active) {";
        var gate = EterisSkin.Css.IndexOf(Gate, StringComparison.Ordinal);
        Assert.True(gate > 0, "as barras têm de estar dentro de " + Gate);
        var end = EterisSkin.Css.IndexOf("\n}", gate, StringComparison.Ordinal);
        if (end < 0) end = EterisSkin.Css.Length;
        var inside = EterisSkin.Css[gate..end];
        Assert.Equal(Regex.Matches(EterisSkin.Css, "::-webkit-scrollbar").Count, Regex.Matches(inside, "::-webkit-scrollbar").Count);
        Assert.Equal(6, Regex.Matches(inside, "::-webkit-scrollbar").Count);
        Assert.DoesNotContain("all: unset", EterisSkin.Css);
        Assert.DoesNotContain("@media (forced-colors: active)", EterisSkin.Css);
    }

    // O anel de foco, sem --accent na página (a de erro, o Histórico e as Novidades não o definem, e com a cor íris de
    // origem o Themed não o injeta): o --focus-ring do tema, com pelo menos 3:1 contra o fundo da página nos dois temas —
    // e contra o branco do centro da página de erro no claro. O ciano de antes ficava a 1,6:1 no claro.
    public static TheoryData<string, bool> PagesWithoutAccent()
    {
        var data = new TheoryData<string, bool>();
        foreach (var page in Templates.Where(p => !Template(p).Contains("--accent", StringComparison.Ordinal)))
        {
            data.Add(page, false);
            data.Add(page, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(PagesWithoutAccent))]
    public void FocusRing_HasAtLeastThreeToOneContrast(string page, bool light)
    {
        var rule = Regex.Match(EterisSkin.Css, light ? @"html\.light \{([^}]*)\}" : @":root \{([^}]*)\}").Groups[1].Value;
        var match = Regex.Match(rule, @"--focus-ring:\s*#([0-9a-fA-F]{6})");
        Assert.True(match.Success, $"--focus-ring em falta ({(light ? "claro" : "escuro")})");
        var ring = (Color)ColorConverter.ConvertFromString("#" + match.Groups[1].Value);
        var backgrounds = new List<Color> { Background(page, light) ?? throw new InvalidOperationException($"{page} sem --bg") };
        if (light && page == "error") backgrounds.Add(Colors.White);
        foreach (var background in backgrounds)
        {
            var ratio = Accent.Contrast(ring, background);
            Assert.True(ratio >= 3, $"{page} ({(light ? "claro" : "escuro")}) contra {background}: {ratio:0.00}:1");
        }
    }

    [Fact]
    public void FocusRing_CoversThePagesWithoutAccent()
        => Assert.Equal(["error", "history", "whatsnew"], PagesWithoutAccent().Select(row => (string)row[0]).Distinct().Order());

    // Vem antes do <meta charset> da página: só ASCII.
    [Fact]
    public void Style_IsAsciiAndCarriesItsId()
    {
        Assert.All(EterisSkin.Style, c => Assert.True(c < 128, $"«{c}» não é ASCII"));
        Assert.StartsWith("<style id=\"eteris-skin\">", EterisSkin.Style);
        Assert.EndsWith("</style>", EterisSkin.Style);
    }

    // O polegar, composto sobre o fundo de cada página (--bg) nos dois temas, em repouso e ao pairar: pelo menos 3:1.
    public static TheoryData<string, bool> PagesAndThemes()
    {
        var data = new TheoryData<string, bool>();
        foreach (var page in Templates.Where(p => Background(p, light: false) is not null))
        {
            data.Add(page, false);
            data.Add(page, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(PagesAndThemes))]
    public void Thumb_HasAtLeastThreeToOneContrast(string page, bool light)
    {
        var background = Background(page, light)!.Value;
        foreach (var token in new[] { "--sb-thumb", "--sb-thumb-hover" })
        {
            var (color, alpha) = Token(token, light);
            var shown = Color.FromRgb(Mix(color.R, background.R, alpha), Mix(color.G, background.G, alpha), Mix(color.B, background.B, alpha));
            var ratio = Accent.Contrast(shown, background);
            Assert.True(ratio >= 3, $"{page} ({(light ? "claro" : "escuro")}), {token}: {ratio:0.00}:1");
        }
    }

    // A nova aba com paisagem (ou a privada) é escura também no tema claro: aí o polegar é o claro da pele, que se vê na
    // fotografia (a prova ao vivo mostrou o polegar escuro perdido na paisagem). Sem paisagem, no claro, fica o da pele.
    [Fact]
    public void NewTab_WithAWallpaper_KeepsTheLightThumb()
    {
        var html = Template("newtab");
        var rule = "html.light:has(> body:not([data-wallpaper=\"none\"])), html.light:has(> body.private) {";
        var at = html.IndexOf(rule, StringComparison.Ordinal);
        Assert.True(at > 0, "falta a regra do polegar da nova aba com paisagem");
        var block = html[at..html.IndexOf('}', at)];
        var (dark, alpha) = Token("--sb-thumb", light: false);
        Assert.Contains(FormattableString.Invariant($"--sb-thumb: rgba({dark.R}, {dark.G}, {dark.B}, {alpha:.00})"), block);
        var (hover, hoverAlpha) = Token("--sb-thumb-hover", light: false);
        Assert.Contains(FormattableString.Invariant($"--sb-thumb-hover: rgba({hover.R}, {hover.G}, {hover.B}, {hoverAlpha:.00})"), block);
    }

    [Fact]
    public void Thumb_FollowsTheTheme()
    {
        Assert.NotEqual(Token("--sb-thumb", light: false), Token("--sb-thumb", light: true));
        Assert.Contains(Templates, p => Background(p, light: false) is not null);
    }

    private static byte Mix(byte top, byte under, double alpha) => (byte)Math.Round(alpha * top + (1 - alpha) * under);

    // O valor do token na regra :root (escuro) ou html.light (claro) da folha.
    private static (Color Color, double Alpha) Token(string name, bool light)
    {
        var rule = Regex.Match(EterisSkin.Css, light ? @"html\.light \{([^}]*)\}" : @":root \{([^}]*)\}").Groups[1].Value;
        var match = Regex.Match(rule, Regex.Escape(name) + @":\s*rgba\((\d+),\s*(\d+),\s*(\d+),\s*([\d.]+)\)");
        Assert.True(match.Success, $"{name} em falta ({(light ? "claro" : "escuro")})");
        byte At(int i) => byte.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture);
        return (Color.FromRgb(At(1), At(2), At(3)), double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture));
    }

    // O --bg da página: o 1.º (o do :root) no escuro, o do html.light no claro. Nulo numa página sem --bg (a nova aba,
    // com a paisagem por trás).
    private static Color? Background(string page, bool light)
    {
        var html = Template(page);
        var at = light ? html.IndexOf("html.light {", StringComparison.Ordinal) : html.IndexOf(":root {", StringComparison.Ordinal);
        if (at < 0) return null;
        var match = Regex.Match(html[at..], @"--bg:\s*#([0-9a-fA-F]{6})");
        if (!match.Success) return null;
        return (Color)ColorConverter.ConvertFromString("#" + match.Groups[1].Value);
    }

    // Nenhuma página interna nem o leitor desliga a pele com as propriedades normalizadas.
    public static TheoryData<string> Sources()
    {
        var data = new TheoryData<string>();
        foreach (var page in Templates) data.Add(page);
        data.Add("leitor");
        data.Add("créditos");
        return data;
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void InternalPages_NeverOptOutOfTheSkin(string source)
    {
        var text = source switch
        {
            "leitor" => ReaderScripts.Toggle,
            "créditos" => CreditsPage.Render(),
            _ => Template(source),
        };
        Assert.DoesNotContain("scrollbar-color", text);
        Assert.DoesNotContain("scrollbar-width", text);
    }

    // Todas as páginas internas, incluindo a de erro já preenchida e a nova aba privada, levam a pele logo a seguir ao
    // <head>, uma só vez, nos dois temas.
    public static TheoryData<string, bool> RenderedPages()
    {
        var data = new TheoryData<string, bool>();
        foreach (var page in Templates.Append("erro preenchido").Append("nova aba privada").Append("créditos"))
        {
            data.Add(page, false);
            data.Add(page, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RenderedPages))]
    public void EveryInternalPage_CarriesTheSkin(string page, bool light)
    {
        var source = page switch
        {
            "erro preenchido" => ErrorPage.Render(-105, "https://nao-existe.invalid/"),
            "nova aba privada" => NewTabPage.Render([], SearchEngine.DuckDuckGo, new DateTime(2026, 11, 12, 15, 0, 0), isPrivate: true),
            "créditos" => CreditsPage.Render(),
            _ => Template(page),
        };
        var html = InternalPages.Themed(source, light, Accent.IrisStops);
        var head = html.IndexOf("<head>", StringComparison.Ordinal);
        Assert.True(head >= 0);
        Assert.Equal(head + "<head>".Length, html.IndexOf(EterisSkin.Style, StringComparison.Ordinal));
        Assert.Single(Regex.Matches(html, "id=\"eteris-skin\""));
    }
}
