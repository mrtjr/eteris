using Eteris.Browser.Browser;
using Eteris.Browser.Palette;

namespace Eteris.Browser.Tests;

public class ShortcutSheetTests
{
    [Fact]
    public void EveryGroupHasShortcuts()
        => Assert.All(ShortcutSheet.Groups, g => Assert.NotEmpty(g.Items));

    [Theory]
    [InlineData("Ctrl+K")]
    [InlineData("F9")]
    [InlineData("Ctrl+Shift+S")]
    [InlineData("Ctrl+H")]
    [InlineData("Ctrl+Shift+A")]
    [InlineData("Alt+F")]
    [InlineData("Ctrl+Shift+B")]
    [InlineData("Ctrl+.")]
    [InlineData("Alt+Home")]
    public void ListsTheMainShortcuts(string keys)
        => Assert.Contains(ShortcutSheet.Groups.SelectMany(g => g.Items), s => s.Keys.Contains(keys));

    // Um atalho anunciado no «Novidades» (eteris://whatsnew) tem de existir na folha de atalhos (F1).
    [Fact]
    public void WhatsNewShortcuts_AreInTheShortcutSheet()
    {
        var listed = ShortcutSheet.Groups.SelectMany(g => g.Items).SelectMany(s => SplitKeys(s.Keys)).ToHashSet(StringComparer.Ordinal);
        var announced = WhatsNewPage.Highlights.Where(h => h.Keys is not null).SelectMany(h => SplitKeys(h.Keys!)).ToList();

        Assert.NotEmpty(announced);
        Assert.All(announced, keys => Assert.Contains(keys, listed));
    }

    private static IEnumerable<string> SplitKeys(string keys)
        => keys.Split(['·', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void NoDuplicateActions()
    {
        var actions = ShortcutSheet.Groups.SelectMany(g => g.Items).Select(s => s.Keys + s.Action).ToList();
        Assert.Equal(actions.Count, actions.Distinct().Count());
    }
}
