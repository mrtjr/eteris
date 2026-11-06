namespace Eteris.Browser.Tabs;

public static class AudioIndicator
{
    // Verdadeiro se o documento principal tem média a tocar com som (o silêncio do separador não altera m.muted).
    public const string AudibleScript = """
        (function () {
          try {
            return Array.from(document.querySelectorAll('video,audio'))
              .some(function (m) { return !m.paused && !m.ended && m.readyState > 2 && !m.muted && m.volume > 0; });
          } catch (e) { return false; }
        })()
        """;

    public static string? Glyph(bool isAudible, bool isMuted)
        => isMuted ? "" : isAudible ? "" : null;
}
