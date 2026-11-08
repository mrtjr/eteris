using Eteris.Browser.Data;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

// Teclas perdidas ao escrever depressa na barra de endereço («hps://…», «wschools.com» num SendKeys em rajada): a
// suspeita era o autocompletar inline substituir a seleção entre teclas. Simula a caixa de texto tecla a tecla, com a
// mesma regra de MainWindow.Omnibox.cs (RefreshSuggestions): cada tecla substitui a seleção; com o cursor no fim, o
// texto passa a ser o completado e a cauda fica selecionada. Nenhuma tecla se perde — a perda não vem desta lógica.
public class OmniboxTypingTests
{
    private static readonly HistoryEntry[] History =
    [
        new("https://www.w3schools.com/html/mov_bbb.mp4", "mov_bbb.mp4", 3),
        new("https://httpbin.org/get", "httpbin", 2),
        new("https://hps.example/", "hps", 1),
        new("https://duckduckgo.com/?q=video", "DuckDuckGo", 5),
    ];

    private static string TypeFast(string keys)
    {
        var text = string.Empty;
        int selectionStart = 0, selectionLength = 0;
        foreach (var key in keys)
        {
            text = text[..selectionStart] + key + text[(selectionStart + selectionLength)..];
            var caret = selectionStart + 1;
            selectionStart = caret;
            selectionLength = 0;
            if (caret == text.Length && Suggestions.InlineCompletion(text, History) is { } completion)
            {
                selectionStart = text.Length;
                selectionLength = completion.Length - text.Length;
                text = completion;
            }
        }
        // Enter: vai o que está na caixa (o completado aceite fica, como no Chrome).
        return text;
    }

    [Theory]
    [InlineData("https://www.w3schools.com/html/mov_bbb.mp4")]
    [InlineData("w3schools.com")]
    [InlineData("www.w3schools.com")]
    [InlineData("httpbin.org/get")]
    [InlineData("duck")]
    public void FastTyping_NeverLosesAKey(string typed)
        => Assert.StartsWith(typed, TypeFast(typed), StringComparison.Ordinal);

    [Fact]
    public void TheCompletionOnlyAddsAfterWhatWasTyped()
    {
        Assert.Equal("w3schools.com", TypeFast("w3"));
        Assert.Equal("www.w3schools.com", TypeFast("ww"));
        Assert.Equal("httpbin.org", TypeFast("htt"));
        // Com «:» ou «/» já não há completado: o que foi escrito fica tal e qual.
        Assert.Equal("https:", TypeFast("https:"));
    }
}
