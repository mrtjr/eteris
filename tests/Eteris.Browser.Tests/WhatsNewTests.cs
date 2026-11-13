using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public class WhatsNewTests
{
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    [Theory]
    [InlineData("0.1.0", "0.2.0", false, true)]
    [InlineData("0.2.0", "0.2.0", false, false)]
    [InlineData("", "0.2.0", true, false)]
    public void ShowsOncePerVersionButNotOnFirstRun(string seen, string current, bool firstRun, bool expected)
        => Assert.Equal(expected, WhatsNewPage.ShouldShow(seen, current, firstRun));

    [Fact]
    public void Render_ListsHighlights()
    {
        var html = WhatsNewPage.Render("0.2.0");
        Assert.Contains("\"version\":\"0.2.0\"", html);
        Assert.True(WhatsNewPage.Highlights.Count >= 8);
    }

    // 0.2.18: a Pausa à frente (Ctrl+., na folha de atalhos), curta e só com o que o código faz — cala e pausa o som e o
    // vídeo de todas as janelas, esconde as páginas, e nada recomeça sozinho ao voltar. A seguir, as abas fixadas com
    // casa; a divisória «Versões anteriores» fica depois dos destaques da 0.2.18 (NewCount), antes do 1.º da 0.2.17.
    [Fact]
    public void Highlights_LeadWithThePauseIn0218()
    {
        var pause = WhatsNewPage.Highlights[0];
        Assert.Equal("Pausa", pause.Title);
        Assert.Equal("Ctrl+.", pause.Keys);
        Assert.True(pause.Detail.Length <= 180, pause.Detail);
        Assert.Contains("todas as janelas", pause.Detail);
        Assert.Contains("esconde as páginas", pause.Detail);
        Assert.Contains("Nada recomeça sozinho", pause.Detail);
        Assert.Equal("Abas fixadas com casa", WhatsNewPage.Highlights[1].Title);
        Assert.Equal("Sites completos com o Escudo", WhatsNewPage.PreviousVersionFirstTitle);
        foreach (var s in new[] { "IA", "sempre", "nunca", "Edge", "notifica" })
            Assert.DoesNotContain(s, pause.Title + " " + pause.Detail, StringComparison.Ordinal);
    }

    // 0.2.18: as abas fixadas com casa logo a seguir à Pausa (Alt+Home, na folha de atalhos): só o que o código faz — o
    // ponto no ícone fora de casa, e o clique no ícone da aba aberta ou Alt+Home volta lá.
    [Fact]
    public void Highlights_PinnedHomeFollowsThePause()
    {
        var home = WhatsNewPage.Highlights[1];
        Assert.Equal("Abas fixadas com casa", home.Title);
        Assert.Equal("Alt+Home", home.Keys);
        Assert.True(home.Detail.Length <= 180, home.Detail);
        Assert.Contains("ponto", home.Detail);
        Assert.Contains("Alt+Home", home.Detail);
        Assert.Contains("aba aberta", home.Detail);
        foreach (var s in new[] { "IA", "sempre", "nunca", "Edge", "sincroniz" })
            Assert.DoesNotContain(s, home.Title + " " + home.Detail, StringComparison.Ordinal);
        Assert.InRange(WhatsNewPage.NewCount, 2, 8);
    }

    // 0.2.17: cinco destaques à frente, curtos, que só prometem o que o CHANGELOG da 0.2.17 diz — os sites completos com o
    // Escudo (como numa extensão; os rastreadores continuam bloqueados), o «Sair» do menu (só deste computador, com
    // confirmação, sem apagar a chave), o Google como escolha (o padrão continua o DuckDuckGo), as limpezas que chegam ao
    // motor de vídeo e ao disco, e os certificados de cliente (nada enviado sem escolher). A divisória «Versões
    // anteriores» fica logo a seguir (NewCount), antes do 1.º destaque da 0.2.16.
    [Fact]
    public void Highlights_LeadWithTheShieldFixSignOutGoogleCleanupsAndCertificatesIn0217()
    {
        string[] titles = ["Sites completos com o Escudo", "«Sair» no menu", "Google como motor de pesquisa", "Limpezas que chegam a todo o lado", "Certificados de cliente"];
        var news = WhatsNewPage.Highlights.Skip(WhatsNewPage.NewCount).Take(titles.Length).ToList();
        Assert.Equal(titles, news.Select(h => h.Title));
        Assert.All(news, h => Assert.True(h.Detail.Length <= 180, h.Title));
        Assert.Contains("ge.globo.com", news[0].Detail);
        Assert.Contains("rastreadores continuam bloqueados", news[0].Detail);
        Assert.Contains("só deste computador", news[1].Detail);
        Assert.Contains("nunca apaga a chave", news[1].Detail);
        Assert.Contains("DuckDuckGo", news[2].Detail);
        Assert.Contains("motor de vídeo", news[3].Detail);
        Assert.Contains("nenhum é enviado sem o escolher", news[4].Detail);
        Assert.Equal("Todas as janelas voltam", WhatsNewPage.Highlights[WhatsNewPage.NewCount + titles.Length].Title);
        Assert.All(news, h =>
        {
            foreach (var s in new[] { "sandbox", "isolad", "sempre", "Edge" })
                Assert.DoesNotContain(s, h.Title + " " + h.Detail, StringComparison.OrdinalIgnoreCase);
        });
    }

    // 0.2.16: três destaques à frente, curtos, que só prometem o que o CHANGELOG da 0.2.16 diz — todas as janelas voltam
    // (só as normais), as abas com trabalho em curso não adormecem (também pelo comando da paleta) e «Apagar dados» apaga
    // o armazenamento dos sites e diz o que falhou. O 4.º: as atualizações conferidas (lista assinada pelo autor, chave
    // fora do GitHub, cada pacote conferido depois de descarregado), a valer a partir desta versão — a 0.2.15 que a
    // descarrega ainda não confere.
    [Fact]
    public void Highlights_LeadWithWindowsSleepClearDataAndSignedUpdatesIn0216()
    {
        string[] titles = ["Todas as janelas voltam", "Abas que não perdem trabalho", "Limpar dados apaga mesmo", "Atualizações conferidas"];
        var news = WhatsNewPage.Highlights.Skip(WhatsNewPage.NewCount + 5).Take(titles.Length).ToList();
        Assert.Equal(titles, news.Select(h => h.Title));
        Assert.All(news, h => Assert.True(h.Detail.Length <= 180, h.Title));
        Assert.Contains("janelas normais", news[0].Detail);
        Assert.Contains("encerrar o Windows", news[0].Detail);
        Assert.Contains("Picture-in-Picture", news[1].Detail);
        Assert.Contains("texto por enviar", news[1].Detail);
        Assert.Contains("«Adormecer abas em segundo plano»", news[1].Detail);
        Assert.Contains("«Cookies e dados de sites»", news[2].Detail);
        Assert.Contains("o que não saiu", news[2].Detail);
        Assert.StartsWith("A partir desta versão", news[3].Detail, StringComparison.Ordinal);
        Assert.Contains("assinada pelo autor", news[3].Detail);
        Assert.Contains("fora do GitHub", news[3].Detail);
        Assert.Contains("cada pacote", news[3].Detail);
        Assert.All(news.Take(3), h =>
        {
            foreach (var s in new[] { "atualizaç", "assinad" })
                Assert.DoesNotContain(s, h.Title + " " + h.Detail, StringComparison.OrdinalIgnoreCase);
        });
        Assert.All(news, h =>
        {
            foreach (var s in new[] { "sandbox", "isolad", "sempre", "Edge" })
                Assert.DoesNotContain(s, h.Title + " " + h.Detail, StringComparison.OrdinalIgnoreCase);
        });
    }

    // 0.2.15 («Um login e pronto», T15): quatro destaques a seguir aos da 0.2.16, curtos — entrar com um clique, o
    // computador novo aprovado com um número, o kit de 24 palavras e «Sair deste computador».
    [Fact]
    public void Highlights_LeadWithOneSignInIn0215()
    {
        string[] titles = ["Um login e pronto", "Computador novo, aprovado com um número", "Kit de recuperação em 24 palavras", "Sair só deste computador"];
        var news = WhatsNewPage.Highlights.Skip(WhatsNewPage.NewCount + 9).Take(titles.Length).ToList();
        Assert.Equal(titles, news.Select(h => h.Title));
        Assert.All(news, h => Assert.True(h.Detail.Length <= 180, h.Title));
        Assert.Contains("Entre com o Google", news[0].Detail);
        Assert.Contains("cifrada", news[0].Detail);
    }

    // Os destaques da 0.2.15 só prometem o que o código faz: o kit tem RecoveryCode.WordCount palavras e volta a ver-se
    // com o Windows Hello; sair não desliga os outros; nenhum diz «revoga», «Nova aba» (não oferece a entrada), «sempre»
    // nem fala do Edge.
    [Fact]
    public void Highlights_0215_PromiseOnlyWhatShips()
    {
        var news = WhatsNewPage.Highlights.Skip(WhatsNewPage.NewCount + 9).Take(4).ToList();
        var kit = Assert.Single(news, h => h.Title.StartsWith("Kit de recuperação", StringComparison.Ordinal));
        Assert.Contains("Windows Hello", kit.Detail);
        Assert.Equal(24, Eteris.Sync.Crypto.RecoveryCode.WordCount);
        Assert.Contains("24 palavras", kit.Title + " " + kit.Detail);
        var exit = Assert.Single(news, h => h.Title == "Sair só deste computador");
        Assert.Contains("Sair deste computador", exit.Detail);
        Assert.Contains("Retirar o acesso", exit.Detail);
        Assert.All(news, h =>
        {
            foreach (var s in new[] { "revoga", "Nova aba", "sempre", "Edge" })
                Assert.DoesNotContain(s, h.Title + " " + h.Detail, StringComparison.Ordinal);
        });
    }

    // 0.2.14: abre com o isolamento das páginas, seguido dos 3 destaques do desempenho medido (memória e processador
    // com muitas abas, a janela minimizada e o sono com pouca memória): 4 destaques. A seguir vêm os 3 da 0.2.13, a
    // abrir com a conta Google e a sincronização cifrada, as palavras-passe e o preenchimento, e o pedido à Google dos
    // formulários cortado; os 2 da 0.2.12, a abrir com o teclado do menu (Alt+F e Alt+E) e o menu legível; os 2 da
    // 0.2.11, a abrir com o arranque sem pedidos à Google e o Escudo nos pedidos sem aba; os 5 da 0.2.10, a abrir com o
    // arranque e a memória, seguidos da impressão digital; e depois os 8 da 0.2.9, a abrir com o vídeo no motor principal
    // (codecs do Windows). O destaque antigo que mandava o H.264 para o motor de vídeo saiu, e os destaques das seis
    // versões são curtos.
    // 0.2.15 («Um login e pronto»): os 4 destaques novos vêm à frente e os da 0.2.14 começam a seguir (os índices
    // contam-se a partir do destaque do sandbox, para uma versão nova não obrigar a renumerar).
    // 0.2.16: os 4 destaques novos vêm à frente dos 4 da 0.2.15, e o do sandbox passa ao índice 8.
    // 0.2.17: os 5 destaques novos vêm à frente dos 4 da 0.2.16, e o do sandbox passa ao índice 13.
    // 0.2.18: a Pausa vem à frente dos 5 da 0.2.17, e o do sandbox passa ao índice 14; com a casa das fixadas, 15 (a conta
    // passa a partir do NewCount: 5 da 0.2.17, 4 da 0.2.16 e 4 da 0.2.15).
    [Fact]
    public void Highlights_LeadWithOneSignInThenTheSandboxAndPerformanceThenSyncPasswordsAndFormsThenTheMenuKeyboardThenNothingToGoogleThenStartupThenTheEngineVideo_AndAreShort()
    {
        var at = WhatsNewPage.Highlights.ToList().FindIndex(h => h.Title == WhatsNewPage.SandboxTitle);
        Assert.Equal(WhatsNewPage.NewCount + 13, at);
        var rest = WhatsNewPage.Highlights.Skip(at).ToList();
        Assert.Equal(WhatsNewPage.SandboxTitle, rest[0].Title);
        Assert.Equal(WhatsNewPage.LighterTitle, rest[1].Title);
        Assert.Equal(WhatsNewPage.UnseenTitle, rest[2].Title);
        Assert.Equal(WhatsNewPage.PressureTitle, rest[3].Title);
        Assert.Equal("Conta Google e sincronização cifrada", rest[4].Title);
        Assert.Contains("Opcional", rest[4].Detail);
        Assert.Contains("cifrados no PC", rest[4].Detail);
        Assert.Contains("pasta oculta", rest[4].Detail);
        Assert.Contains("palavras-passe", rest[4].Detail);
        Assert.Equal("Palavras-passe e preenchimento", rest[5].Title);
        Assert.Contains("nunca o CVV", rest[5].Detail);
        Assert.Contains("Windows Hello", rest[5].Detail);
        Assert.Equal("Formulários sem pedidos à Google", rest[6].Title);
        Assert.Contains("verificação de fugas", rest[6].Detail);
        Assert.Contains("0 pedidos", rest[6].Detail);
        Assert.Equal("Menu do Éteris pelo teclado", rest[7].Title);
        Assert.Equal("Alt+F · Alt+E", rest[7].Keys);
        Assert.Contains("Esc", rest[7].Detail);
        Assert.Equal("Menu legível", rest[8].Title);
        Assert.Contains("quadrados", rest[8].Detail);
        Assert.Equal("No arranque, nada sai para a Google", rest[9].Title);
        Assert.Contains("eteris://privacy/", rest[9].Detail);
        Assert.Contains("service workers", rest[10].Detail);
        Assert.Equal("Arranque quase duas vezes mais rápido", rest[11].Title);
        Assert.Contains("0,6 s", rest[11].Detail);
        Assert.Equal("Menos memória", rest[12].Title);
        Assert.Contains(rest.Skip(11).Take(5), h => h.Title == "Impressão digital como a do Chrome");
        Assert.Equal("Vídeo no motor do Éteris", rest[16].Title);
        Assert.DoesNotContain(WhatsNewPage.Highlights, h => h.Title == "Vídeo em todos os sites");
        Assert.All(WhatsNewPage.Highlights.Take(24 + at), x => Assert.True(x.Detail.Length <= 180, x.Title));
    }

    // Os 3 destaques do desempenho da 0.2.14 dizem só o que a medição contra a 0.2.13 deu (docs/paridade.md, «Medição da
    // 0.2.14»): memória −11% (1 aba) a −16% (20 abas); processador parado com 20 abas 1,72% → 0,13% de um núcleo; janela
    // minimizada com uma página animada (sem número: a prova 6 de build/perf/README.md por repetir); e o sono com pouca memória, com a opção em Definições › Abas. Não
    // prometem nada do que ficou em aberto: a janela tapada por outra continua a desenhar e o arranque não ficou mais
    // rápido (custa cerca de 40 ms a mais com o sandbox).
    [Fact]
    public void Highlights_AnnounceTheMeasuredPerformanceOf0214_AndNothingStillOpen()
    {
        var lighter = Assert.Single(WhatsNewPage.Highlights, h => h.Title == WhatsNewPage.LighterTitle);
        Assert.Contains("0.2.13", lighter.Detail);
        Assert.Contains("11% a 16% menos memória", lighter.Detail);
        Assert.Contains("20 abas", lighter.Detail);
        Assert.Contains("1,72% para 0,13%", lighter.Detail);

        var unseen = Assert.Single(WhatsNewPage.Highlights, h => h.Title == WhatsNewPage.UnseenTitle);
        Assert.Contains("minimizada", unseen.Detail);
        // Os números «6,47% → 0,59%» saíram: a prova 6 de build/perf/README.md ainda não foi repetida na publicação final.
        Assert.DoesNotContain("6,47%", unseen.Detail);
        Assert.DoesNotContain("0,59%", unseen.Detail);
        Assert.Contains("Nunca com som", unseen.Detail);

        var pressure = Assert.Single(WhatsNewPage.Highlights, h => h.Title == WhatsNewPage.PressureTitle);
        Assert.Contains("usada há mais tempo", pressure.Detail);
        Assert.Contains("Definições › Abas", pressure.Detail);

        var first = WhatsNewPage.Highlights.ToList().FindIndex(h => h.Title == WhatsNewPage.SandboxTitle);
        var texts = WhatsNewPage.Highlights.Skip(first).Take(4).Select(h => h.Title + " " + h.Detail).ToList();
        Assert.All(texts, t =>
        {
            Assert.DoesNotContain("tapada", t, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("coberta", t, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("arranque", t, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Edge", t, StringComparison.Ordinal);
        });
        Assert.All(WhatsNewPage.Highlights.Skip(first).Take(4), h => Assert.True(h.Detail.Length <= 180, h.Title));
    }

    // O texto do destaque do sono com pouca memória diz onde está a opção: ela existe nas Definições com esse nome de
    // secção («Abas») e o ajuste e13 por trás dela nasce ligado no settings.json (UserSettings.SleepOnLowMemory).
    [Fact]
    public void Highlights_ThePressureOptionExistsInSettingsAndIsOnByDefault()
    {
        var html = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Browser", "settings.html"));
        Assert.Contains("Adormecer também quando falta memória", html);
        Assert.Contains("data-key=\"sleepPressure\"", html);
        Assert.True(new Data.UserSettings().SleepOnLowMemory);
    }

    // As Novidades não podem anunciar um ajuste de desempenho que não está ligado: a janela minimizada (e8) e o sono com
    // pouca memória (e13) só se anunciam enquanto o PerfTuning os ligar por omissão. Se uma prova ao vivo mandar desligar
    // um deles antes do pacote (build/perf/README.md: o restauro do e8 ainda não foi medido), este teste obriga a tirar o
    // destaque no mesmo commit, como o do sandbox segue a omissão do publish.ps1.
    [Fact]
    public void Highlights_AnnounceTheTweaksOnlyWhileTheyAreOnByDefault()
    {
        Assert.Equal(PerfTuning.Defaults.Contains(PerfTuning.HideWhenUnseen),
                     WhatsNewPage.Highlights.Any(h => h.Title == WhatsNewPage.UnseenTitle));
        Assert.Equal(PerfTuning.Defaults.Contains(PerfTuning.SleepUnderPressure),
                     WhatsNewPage.Highlights.Any(h => h.Title == WhatsNewPage.PressureTitle));
    }

    // Desligado da 0.2.10 à 0.2.13, o sandbox sai ligado na publicação da 0.2.14 (docs/sandbox.md, «Como publicar com o
    // sandbox»): as Novidades abrem com ele, num destaque curto, e é o único que fala dele.
    [Fact]
    public void Highlights_AnnounceTheSandboxIn0214()
    {
        // Desde a 0.2.15 já não é o primeiro destaque: procura-se pelo título.
        var first = Assert.Single(WhatsNewPage.Highlights, h => h.Title == WhatsNewPage.SandboxTitle);
        Assert.Contains("sandbox", first.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Chrome", first.Detail);
        Assert.Contains("ficheiros", first.Detail);
        Assert.True(first.Detail.Length <= 180, first.Detail);
        Assert.Single(WhatsNewPage.Highlights, h => h.Title.Contains("sandbox", StringComparison.OrdinalIgnoreCase)
                                                    || h.Detail.Contains("sandbox", StringComparison.OrdinalIgnoreCase)
                                                    || h.Title.Contains("isolad", StringComparison.OrdinalIgnoreCase));
    }

    // As Novidades não podem anunciar o isolamento numa publicação sem ele: o anúncio segue a omissão do
    // build/publish.ps1. Uma versão de recuo sem sandbox (por exemplo uma 0.2.14.1 com -NoSandbox) muda a omissão do
    // publish.ps1, e este teste obriga então a tirar o anúncio (docs/sandbox.md; plano da 0.2.14, recuo R3).
    [Fact]
    public void Highlights_AnnounceTheSandboxOnlyWhenThePublishDefaultIsSandbox()
    {
        var param = SandboxPublishTests.PublishParam(File.ReadAllText(Path.Combine(Root(), "build", "publish.ps1")));
        var announced = WhatsNewPage.Highlights.Any(h => h.Title == WhatsNewPage.SandboxTitle);
        Assert.Equal(SandboxPublishTests.DefaultIsSandbox(param), announced);
    }

    // Os fundos são imagens geradas por IA (Browser/Wallpapers/README.md): as novidades não lhes chamam fotografias,
    // para dizer o mesmo que o crédito no rodapé da nova aba.
    [Fact]
    public void Highlights_CallTheWallpapersAiGeneratedImages()
    {
        Assert.Contains(WhatsNewPage.Highlights, h => h.Detail.Contains("gerada por IA", StringComparison.Ordinal));
        Assert.DoesNotContain(WhatsNewPage.Highlights, h => h.Detail.Contains("fotografia", StringComparison.OrdinalIgnoreCase));
    }

    // A etiqueta «Versão X» não pode cobrir os destaques de outras versões: os da versão em curso vêm primeiro e, a
    // seguir, uma divisória «Versões anteriores» (PRO-16).
    [Fact]
    public void Render_SeparatesTheNewHighlightsFromOlderVersions()
    {
        Assert.InRange(WhatsNewPage.NewCount, 1, 8);
        Assert.Equal(WhatsNewPage.PreviousVersionFirstTitle, WhatsNewPage.Highlights[WhatsNewPage.NewCount].Title);
        Assert.Contains("\"older\":" + WhatsNewPage.NewCount, WhatsNewPage.Render("0.2.15"));
        var template = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Browser", "whatsnew.html"));
        Assert.Contains("Versões anteriores", template);
        Assert.Contains("data.older", template);
    }

    [Fact]
    public void Readme_DoesNotOverpromise()
    {
        var readme = File.ReadAllText(Path.Combine(Root(), "README.md"));
        Assert.DoesNotContain("sem abrir aba nem deixar rasto.", readme);
        Assert.Contains("sem abrir aba nem deixar rasto no histórico (a página é pedida ao site).", readme);
        Assert.Contains("DNS cifrado e apagar dados ao fechar (opcionais)", readme);
    }

    // CAD-05, PRO-09: uma publicação de emergência sem sandbox não anuncia o isolamento que não tem.
    [Fact]
    public void HighlightsFor_WithoutSandbox_ReplacesOnlyThatHighlight()
    {
        var without = WhatsNewPage.HighlightsFor(sandbox: false);
        Assert.Equal(WhatsNewPage.Highlights.Count, without.Count);
        Assert.DoesNotContain(without, h => h.Title == WhatsNewPage.SandboxTitle);
        Assert.Single(without, h => h.Title == WhatsNewPage.NoSandboxTitle);
        Assert.Equal(WhatsNewPage.Highlights.Where(h => h.Title != WhatsNewPage.SandboxTitle), without.Where(h => h.Title != WhatsNewPage.NoSandboxTitle));
        Assert.Same(WhatsNewPage.Highlights, WhatsNewPage.HighlightsFor(sandbox: true));
    }

    [Fact]
    public void Render_WithoutSandbox_DoesNotAnnounceIsolation()
    {
        // O JSON da página escapa os acentos: comparam-se troços ASCII.
        Assert.Contains("isoladas (sandbox)", WhatsNewPage.Render("0.2.15", sandbox: true));
        var html = WhatsNewPage.Render("0.2.15", sandbox: false);
        Assert.DoesNotContain("isoladas (sandbox)", html);
        Assert.DoesNotContain("corre fechada", html);
        Assert.Contains("corre sem isolamento", html);
        Assert.Equal(WhatsNewPage.Render("0.2.15", sandbox: true), WhatsNewPage.Render("0.2.15"));
    }

    [Fact]
    public void WhatsNew_IsRenderedWithTheRealSandboxState()
        => Assert.Contains("WhatsNewPage.Render(SettingsPage.Version, Infrastructure.CefConfigurator.SandboxSupported && Infrastructure.SandboxInfo.Available != 0)",
            File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs")));
}
