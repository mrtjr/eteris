using System.IO;
using Eteris.Browser.Shields;
using Eteris.Browser.Tests.Sync;

namespace Eteris.Browser.Tests;

// MOT-01 (revisão final, I-C): o Chromium só guarda a resposta por anfitrião:porta, por isso uma página que pede
// certificados a N subdomínios abria N diálogos modais em cadeia. Um diálogo de cada vez por janela, os outros em fila;
// pedidos de páginas fora da vista recusados em silêncio; três recusas seguidas na mesma página e o resto é recusado
// sem perguntar até à navegação seguinte. Cada pedido tem sempre resposta (o callback do CEF nunca fica pendurado).
public class ClientCertificatePromptsTests
{
    private static readonly IReadOnlyList<ClientCertificateRow> Rows = [new("Ana", "Ana", "01/01/2030", "AB")];

    private sealed class Page
    {
        public bool OnScreen = true;
    }

    // A resposta de um pedido que já tem de estar respondido (nada aqui espera).
    private static string? Value(Task<string?> task)
    {
        Assert.True(task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    private static Task<string?> Ask(ClientCertificatePrompts prompts, Page page, string host = "a.banco.pt")
        => prompts.Ask(page, host, 443, Rows, from: "Banco", isOnScreen: () => page.OnScreen);

    [Fact]
    public void OneDialogAtATime_TheOthersWait()
    {
        var prompts = new ClientCertificatePrompts();
        var page = new Page();
        var first = Ask(prompts, page, "a.banco.pt");
        var second = Ask(prompts, page, "b.banco.pt");
        var shown = prompts.Next();
        Assert.NotNull(shown);
        Assert.Equal("a.banco.pt", shown.Host);
        // Enquanto o primeiro está à vista, não sai outro.
        Assert.Null(prompts.Next());
        Assert.False(second.IsCompleted);
        prompts.Answered(shown, "AB");
        Assert.Equal("AB", Value(first));
        var next = prompts.Next();
        Assert.NotNull(next);
        Assert.Equal("b.banco.pt", next.Host);
        prompts.Answered(next, null);
        Assert.Null(Value(second));
        Assert.Null(prompts.Next());
    }

    [Fact]
    public void PageOffScreen_IsRefusedSilently_OnArrivalAndWhenItsTurnComes()
    {
        var prompts = new ClientCertificatePrompts();
        var hidden = new Page { OnScreen = false };
        var arrival = Ask(prompts, hidden);
        Assert.True(arrival.IsCompleted);
        Assert.Null(Value(arrival));
        Assert.Null(prompts.Next());

        var visible = new Page();
        var shownTask = Ask(prompts, visible, "a.banco.pt");
        var waiting = Ask(prompts, visible, "b.banco.pt");
        var shown = prompts.Next()!;
        // A aba saiu da vista enquanto esperava: a vez dela chega e é recusada sem diálogo.
        visible.OnScreen = false;
        prompts.Answered(shown, null);
        Assert.Null(Value(shownTask));
        Assert.Null(prompts.Next());
        Assert.True(waiting.IsCompleted);
        Assert.Null(Value(waiting));
    }

    [Fact]
    public void ThreeRefusals_RefuseTheRestUntilTheNextNavigation()
    {
        var prompts = new ClientCertificatePrompts();
        var page = new Page();
        var other = new Page();
        var queued = new List<Task<string?>>();
        for (var i = 0; i < 5; i++) queued.Add(Ask(prompts, page, $"s{i}.banco.pt"));
        for (var i = 0; i < ClientCertificatePrompts.RefusalLimit; i++) prompts.Answered(prompts.Next()!, null);
        // A terceira recusa esvazia o que a página ainda tinha na fila.
        Assert.Null(prompts.Next());
        Assert.All(queued, t => Assert.True(t.IsCompleted));
        Assert.All(queued, t => Assert.Null(Value(t)));
        // Os pedidos novos da mesma página já nem entram; os de outra página continuam a perguntar.
        var later = Ask(prompts, page);
        Assert.True(later.IsCompleted);
        Assert.Null(Value(later));
        var fromOther = Ask(prompts, other);
        var otherShown = prompts.Next();
        Assert.NotNull(otherShown);
        Assert.False(fromOther.IsCompleted);

        // A navegação seguinte da aba volta a perguntar.
        prompts.Navigated(page);
        prompts.Answered(otherShown, null);
        var afterNavigation = Ask(prompts, page);
        Assert.NotNull(prompts.Next());
        Assert.False(afterNavigation.IsCompleted);
    }

    [Fact]
    public void AChosenCertificate_ResetsTheRefusalCount()
    {
        var prompts = new ClientCertificatePrompts();
        var page = new Page();
        for (var i = 0; i < ClientCertificatePrompts.RefusalLimit - 1; i++)
        {
            Ask(prompts, page);
            prompts.Answered(prompts.Next()!, null);
        }
        Ask(prompts, page);
        prompts.Answered(prompts.Next()!, "AB");
        for (var i = 0; i < ClientCertificatePrompts.RefusalLimit - 1; i++)
        {
            Ask(prompts, page);
            prompts.Answered(prompts.Next()!, null);
        }
        var still = Ask(prompts, page);
        Assert.NotNull(prompts.Next());
        Assert.False(still.IsCompleted);
    }

    [Fact]
    public void Close_AnswersEverythingWaiting_AndEveryLaterRequest()
    {
        var prompts = new ClientCertificatePrompts();
        var page = new Page();
        var shownTask = Ask(prompts, page, "a.banco.pt");
        var waiting = Ask(prompts, page, "b.banco.pt");
        var shown = prompts.Next()!;
        prompts.Close();
        Assert.Null(Value(waiting));
        Assert.Null(Value(shownTask));
        // O diálogo que estava aberto fecha com a janela: a resposta dele já não muda nada.
        prompts.Answered(shown, "AB");
        Assert.Null(Value(shownTask));
        var late = Ask(prompts, page);
        Assert.True(late.IsCompleted);
        Assert.Null(Value(late));
        Assert.Null(prompts.Next());
    }

    [Fact]
    public void Forget_RefusesWhatAClosedTabLeftInTheQueue()
    {
        var prompts = new ClientCertificatePrompts();
        var page = new Page();
        var closing = new Page();
        Ask(prompts, page);
        var left = Ask(prompts, closing);
        var shown = prompts.Next()!;
        prompts.Forget(closing);
        Assert.True(left.IsCompleted);
        Assert.Null(Value(left));
        prompts.Answered(shown, null);
        Assert.Null(prompts.Next());
    }

    [Fact]
    public void Window_WiresTheQueue()
    {
        string Source(string file) => File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", file));
        var window = Source("MainWindow.xaml.cs");
        Assert.Contains("readonly ClientCertificatePrompts _clientCertificates = new();", window);
        Assert.Contains("chooseCertificate: (host, port, rows) => AskClientCertificate(tab, ", window);
        Assert.Contains("_clientCertificates.Navigated(tab)", window);
        Assert.Contains("_clientCertificates.Close()", window);
        Assert.Contains("_clientCertificates.Forget(tab)", window);
        Assert.Contains("IsTabOnScreen(tab)", window[window.IndexOf("private Task<string?> AskClientCertificate(", StringComparison.Ordinal)..]);
        Assert.Contains("chooseCertificate: (host, port, rows) => AskClientCertificate(", Source("MainWindow.WebPanels.cs"));
    }
}
