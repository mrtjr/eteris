using System.IO;
using System.Text.Json;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

// Página de erro com diagnóstico legível: o código do Chromium vira «o que aconteceu» e «o que fazer».
public class ErrorDiagnosisTests
{
    [Theory]
    [InlineData(-105, ErrorKind.NameNotResolved, "O nome não resolve (DNS)")]
    [InlineData(-137, ErrorKind.NameNotResolved, "O nome não resolve (DNS)")]
    [InlineData(-102, ErrorKind.Refused, "Ligação recusada")]
    [InlineData(-106, ErrorKind.Offline, "Sem rede")]
    [InlineData(-109, ErrorKind.Unreachable, "Endereço inalcançável")]
    [InlineData(-21, ErrorKind.Reset, "A rede mudou")]
    [InlineData(-7, ErrorKind.TimedOut, "Tempo esgotado")]
    [InlineData(-118, ErrorKind.TimedOut, "Tempo esgotado")]
    [InlineData(-101, ErrorKind.Reset, "Ligação interrompida")]
    [InlineData(-324, ErrorKind.Reset, "Ligação interrompida")]
    [InlineData(-201, ErrorKind.Certificate, "O certificado expirou (ou o relógio do computador está errado)")]
    [InlineData(-202, ErrorKind.Certificate, "O certificado não é de uma entidade conhecida")]
    [InlineData(-200, ErrorKind.Certificate, "O certificado é de outro site")]
    [InlineData(-107, ErrorKind.SecureChannel, "Falha na negociação TLS")]
    [InlineData(-310, ErrorKind.Redirects, "Demasiados redirecionamentos")]
    [InlineData(-20, ErrorKind.Blocked, "Bloqueado")]
    [InlineData(-312, ErrorKind.Blocked, "Porta reservada")]
    [InlineData(-9001, ErrorKind.Crashed, "O processo da página terminou")]
    [InlineData(-999, ErrorKind.Other, "Erro do Chromium")]
    public void For_MapsChromiumCodesToAReadableCause(int code, ErrorKind kind, string cause)
    {
        var diagnosis = ErrorDiagnosis.For(code);
        Assert.Equal(kind, diagnosis.Kind);
        Assert.Equal(cause, diagnosis.Cause);
        Assert.NotEmpty(diagnosis.Tips);
    }

    [Theory]
    [InlineData(-105, "ERR_NAME_NOT_RESOLVED")]
    [InlineData(-102, "ERR_CONNECTION_REFUSED")]
    [InlineData(-201, "ERR_CERT_DATE_INVALID")]
    [InlineData(-7, "ERR_TIMED_OUT")]
    [InlineData(-9001, "RENDERER_CRASHED")]
    [InlineData(-999, "ERR_999")]
    public void NameOf_GivesTheChromiumName(int code, string name) => Assert.Equal(name, ErrorDiagnosis.NameOf(code));

    // Com o DNS seguro ligado, o «nome não resolve» lembra que a rede pode estar a bloqueá-lo.
    [Fact]
    public void NameNotResolved_WithSecureDns_MentionsIt()
    {
        Assert.Contains(ErrorDiagnosis.For(-105, secureDns: true).Tips, t => t.Contains("DNS seguro"));
        Assert.DoesNotContain(ErrorDiagnosis.For(-105).Tips, t => t.Contains("DNS seguro"));
    }

    [Fact]
    public void Details_HaveWhatIsNeededToAskForHelp()
    {
        var text = ErrorDiagnosis.Details(-105, "https://nao-existe.pt/", "0.2.8", new DateTime(2026, 11, 7, 14, 30, 5));
        Assert.Equal("Endereço: https://nao-existe.pt/\nErro: ERR_NAME_NOT_RESOLVED (-105)\nDiagnóstico: O nome não resolve (DNS)\n"
                     + "Quando: 2026-11-07 14:30:05\nÉteris 0.2.8", text);
    }

    [Fact]
    public void Render_CarriesDiagnosisTipsAndDetails()
    {
        var data = Data(ErrorPage.Render(-102, "https://recusa.pt/"));
        Assert.Equal("Ligação recusada", data.GetProperty("cause").GetString());
        Assert.Equal("ERR_CONNECTION_REFUSED", data.GetProperty("name").GetString());
        Assert.True(data.GetProperty("tips").GetArrayLength() > 0);
        Assert.Contains("https://recusa.pt/", data.GetProperty("details").GetString());
        Assert.True(data.GetProperty("retry").GetBoolean());
    }

    [Fact]
    public void Render_OfflineKinds_StartTheGameAndRetryWhenBack()
    {
        Assert.True(Data(ErrorPage.Render(-106, "https://x.pt/")).GetProperty("offline").GetBoolean());
        // Só o -106 é «sem rede»: com o -109 e o -21 a ligação funciona, e o evento «online» nunca chegaria.
        Assert.False(Data(ErrorPage.Render(-21, "https://x.pt/")).GetProperty("offline").GetBoolean());
        Assert.False(Data(ErrorPage.Render(-109, "http://10.0.0.50/")).GetProperty("offline").GetBoolean());
        Assert.False(Data(ErrorPage.Render(-105, "https://x.pt/")).GetProperty("offline").GetBoolean());
    }

    // Os botões existem na página: «Tentar de novo» e «Copiar detalhes».
    [Fact]
    public void Template_HasRetryAndCopy()
    {
        var html = ErrorPage.Render(-105, "https://x.pt/");
        Assert.Contains(">Tentar de novo<", html);
        Assert.Contains(">Copiar detalhes<", html);
        Assert.Contains("navigator.clipboard.writeText(data.details)", html);
    }

    // Proposta 3 (arrumações): «Noutros sites funciona?» passa a dizer de quem é o problema.
    [Fact]
    public void NameNotResolved_SaysWhoseProblemItIs()
    {
        Assert.Contains("Os outros sites abrem? Então o problema é deste endereço, não da ligação.", ErrorDiagnosis.For(-105).Tips);
        Assert.DoesNotContain(ErrorDiagnosis.For(-105).Tips, t => t.Contains("Noutros sites funciona"));
    }

    // O nome não resolve: o botão principal é «Pesquisar «nome»» no motor escolhido (só com o nome do site, nunca o
    // caminho nem a consulta), e «Queria dizer X?» só com um site do histórico ou dos favoritos a 2 letras ou menos.
    [Fact]
    public void NameNotResolved_OffersToSearchTheName_AndASuggestionFromHistory()
    {
        var data = Data(ErrorPage.Render(-105, "https://www.publico.ptt/desporto?id=7", engine: Omnibox.SearchEngine.Brave,
            knownHosts: () => ["github.com", "publico.pt"]));
        var search = data.GetProperty("search");
        Assert.Equal("Pesquisar «publico.ptt»", search.GetProperty("label").GetString());
        Assert.Equal("https://search.brave.com/search?q=publico.ptt", search.GetProperty("url").GetString());
        var suggestion = data.GetProperty("suggestion");
        Assert.Equal("publico.pt", suggestion.GetProperty("host").GetString());
        Assert.Equal("https://publico.pt/desporto?id=7", suggestion.GetProperty("url").GetString());
    }

    [Fact]
    public void NameNotResolved_WithoutHistory_OrFarFromIt_HasNoSuggestion()
    {
        Assert.Equal(JsonValueKind.Null, Data(ErrorPage.Render(-105, "https://publico.ptt/", engine: Omnibox.SearchEngine.DuckDuckGo))
            .GetProperty("suggestion").ValueKind);
        var far = Data(ErrorPage.Render(-105, "https://exemplo.pt/", engine: Omnibox.SearchEngine.DuckDuckGo, knownHosts: () => ["publico.pt"]));
        Assert.Equal(JsonValueKind.Null, far.GetProperty("suggestion").ValueKind);
        Assert.Equal("https://duckduckgo.com/?q=exemplo.pt", far.GetProperty("search").GetProperty("url").GetString());
    }

    // As outras falhas não pesquisam nem leem o histórico; o aviso do «HTTPS primeiro» também não.
    [Theory]
    [InlineData(-102, false)]
    [InlineData(-106, false)]
    [InlineData(-105, true)]
    public void OtherFailures_NeitherSearchNorReadHistory(int code, bool insecure)
    {
        var read = false;
        var data = Data(ErrorPage.Render(code, "https://publico.ptt/", insecure, engine: Omnibox.SearchEngine.DuckDuckGo,
            knownHosts: () => { read = true; return ["publico.pt"]; }));
        Assert.False(read);
        Assert.False(data.TryGetProperty("search", out var search) && search.ValueKind != JsonValueKind.Null);
        Assert.False(data.TryGetProperty("suggestion", out var suggestion) && suggestion.ValueKind != JsonValueKind.Null);
    }

    // A página: o botão «Pesquisar» passa à frente e «Tentar de novo» fica discreto; a sugestão é uma ligação. A janela
    // privada não lê o histórico nem os favoritos (a fábrica não lhe passa os sites conhecidos).
    [Fact]
    public void Template_ShowsSearchFirst_AndThePrivateWindowNeverReadsHistory()
    {
        var html = ErrorPage.Render(-105, "https://x.pt/");
        Assert.True(html.IndexOf("<button id=\"search\" hidden>", StringComparison.Ordinal) < html.IndexOf("<button id=\"retry\"", StringComparison.Ordinal));
        Assert.Contains("location.href = data.search.url", html);
        Assert.Contains("retry.classList.add('quiet')", html);
        Assert.Contains("Queria dizer <a id=\"suggestLink\"></a>?", html);
        var factory = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
        Assert.Contains("knownHosts: privateContext ? null : ErrorPage.KnownHosts", factory);
        Assert.Contains("engine: SearchEngine.Preferred", factory);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static JsonElement Data(string html)
    {
        var start = html.IndexOf("id=\"data\">", StringComparison.Ordinal) + "id=\"data\">".Length;
        return JsonDocument.Parse(html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)]).RootElement;
    }
}
