using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class AudioIndicatorTests
{
    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, "")]
    [InlineData(true, true, "")]
    [InlineData(false, true, "")]
    public void Glyph_ShowsSpeakerOrMuted(bool audible, bool muted, string? expected)
        => Assert.Equal(expected, AudioIndicator.Glyph(audible, muted));
}
