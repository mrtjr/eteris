using Eteris.Browser.Data;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

public class ChromeLayoutTests
{
    [Fact]
    public void Top_HasTitleBarAndToolbar()
    {
        var layout = ChromeLayout.Resolve(verticalTabs: false, compact: false, hidden: false);
        Assert.Equal(ChromeMode.Top, layout.Mode);
        Assert.Equal(ChromeLayout.TitleBarHeight, layout.TitleHeight);
        Assert.Equal(ChromeLayout.ToolbarBarHeight, layout.ToolbarHeight);
        Assert.True(layout.ShowsTitleBar);
        Assert.False(layout.CaptionInToolbar);
        Assert.False(layout.TabsInToolbar);
        Assert.Equal(ChromeLayout.TitleBarHeight, layout.CaptionHeight);
    }

    // Modo compacto: a barra de título desaparece e as abas, o endereço e os botões da janela partilham uma linha.
    [Fact]
    public void Compact_MergesTabsAndAddressInOneBar()
    {
        var layout = ChromeLayout.Resolve(verticalTabs: false, compact: true, hidden: false);
        Assert.Equal(ChromeMode.Compact, layout.Mode);
        Assert.Equal(0, layout.TitleHeight);
        Assert.Equal(ChromeLayout.CompactBarHeight, layout.ToolbarHeight);
        Assert.False(layout.ShowsTitleBar);
        Assert.True(layout.TabsInToolbar);
        Assert.True(layout.CaptionInToolbar);
        Assert.Equal(ChromeLayout.CompactBarHeight, layout.CaptionHeight);
    }

    // A página ganha a altura inteira da barra de título.
    [Fact]
    public void Compact_GivesThePageMoreRoomThanTop()
    {
        var top = ChromeLayout.Resolve(false, false, false);
        var compact = ChromeLayout.Resolve(false, true, false);
        Assert.True(compact.TitleHeight + compact.ToolbarHeight < top.TitleHeight + top.ToolbarHeight - 40);
    }

    [Fact]
    public void VerticalTabs_WinOverCompact()
    {
        var layout = ChromeLayout.Resolve(verticalTabs: true, compact: true, hidden: false);
        Assert.Equal(ChromeMode.Side, layout.Mode);
        Assert.True(layout.ShowsSideBar);
        Assert.True(layout.CaptionInToolbar);
        Assert.False(layout.TabsInToolbar);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Hidden_WinsOverEverything(bool verticalTabs, bool compact)
    {
        var layout = ChromeLayout.Resolve(verticalTabs, compact, hidden: true);
        Assert.Equal(ChromeMode.Hidden, layout.Mode);
        Assert.Equal(0, layout.TitleHeight + layout.ToolbarHeight + layout.CaptionHeight + layout.PageRadius);
        Assert.False(layout.ShowsToolbar);
        Assert.False(layout.ShowsSideBar);
    }

    // O rato tocou no topo em modo foco: as barras do modo normal aparecem, mas sem zona de arrastar nem cantos.
    [Theory]
    [InlineData(false, false, ChromeMode.Top)]
    [InlineData(false, true, ChromeMode.Compact)]
    [InlineData(true, false, ChromeMode.Side)]
    public void Peek_ShowsTheUsualBars_WithoutCaptionOrRadius(bool verticalTabs, bool compact, ChromeMode expected)
    {
        var usual = ChromeLayout.Resolve(verticalTabs, compact, hidden: false);
        var peek = ChromeLayout.Resolve(verticalTabs, compact, hidden: true, peek: true);
        Assert.Equal(expected, peek.Mode);
        Assert.True(peek.Peeking);
        Assert.Equal(usual.TitleHeight, peek.TitleHeight);
        Assert.Equal(usual.ToolbarHeight, peek.ToolbarHeight);
        Assert.Equal(0, peek.CaptionHeight);
        Assert.Equal(0, peek.PageRadius);
        Assert.False(usual.Peeking);
    }

    [Fact]
    public void Peek_WithoutHidden_ChangesNothing()
        => Assert.Equal(ChromeLayout.Resolve(false, false, false), ChromeLayout.Resolve(false, false, hidden: false, peek: true));

    [Fact]
    public void OmniboxWidth_FixedOnlyInCompact_GrowsWithFocus()
    {
        Assert.Null(ChromeLayout.OmniboxWidth(ChromeMode.Top, focused: true));
        Assert.Null(ChromeLayout.OmniboxWidth(ChromeMode.Side, focused: false));
        var idle = ChromeLayout.OmniboxWidth(ChromeMode.Compact, focused: false);
        var typing = ChromeLayout.OmniboxWidth(ChromeMode.Compact, focused: true);
        Assert.NotNull(idle);
        Assert.True(typing > idle);
    }

    [Fact]
    public void CompactOmnibox_GivesWayToTheTabs_InNarrowWindows()
    {
        // Linha larga: a largura própria, maior com o foco.
        Assert.Equal(ChromeLayout.CompactOmniboxWidth, ChromeLayout.OmniboxWidth(ChromeMode.Compact, false, 1400));
        Assert.Equal(ChromeLayout.CompactOmniboxFocusedWidth, ChromeLayout.OmniboxWidth(ChromeMode.Compact, true, 1400));
        // Meia janela num ecrã de 1920 (≈ 560 px para endereço e abas): as abas ficam com lugar para duas legíveis.
        var idle = ChromeLayout.OmniboxWidth(ChromeMode.Compact, false, 560)!.Value;
        var typing = ChromeLayout.OmniboxWidth(ChromeMode.Compact, true, 560)!.Value;
        Assert.True(560 - idle >= ChromeLayout.CompactTabsMinWidth || idle == ChromeLayout.CompactOmniboxMinWidth);
        Assert.True(560 - typing >= ChromeLayout.CompactTabsMinWidth || typing == ChromeLayout.CompactOmniboxMinWidth);
        Assert.True(ChromeLayout.CompactTabsMinWidth >= 2 * Eteris.Browser.Tabs.TabLayout.MinTabWidth);
        // Muito estreita: o endereço nunca desce do mínimo.
        Assert.Equal(ChromeLayout.CompactOmniboxMinWidth, ChromeLayout.OmniboxWidth(ChromeMode.Compact, true, 100));
    }

    [Fact]
    public void DensitySetting_DefaultsNormal_AndValidates()
    {
        Assert.Equal("normal", new UserSettings().Density);
        Assert.Equal("compact", new UserSettings().With("density", "compact")!.Density);
        Assert.Null(new UserSettings().With("density", "tiny"));
    }
}
