using System.Text.Json;

namespace Eteris.Browser.Tabs;

public enum MediaState { None, Playing, Paused }

// O que uma aba está a tocar, para o centro de multimédia da barra de ferramentas.
public sealed record MediaInfo(MediaState State, bool Audible, string? Title, string? Artist)
{
    public static readonly MediaInfo Nothing = new(MediaState.None, false, null, null);

    // Média «a sério»: com som (vídeos mudos em reprodução automática ficam de fora) e que já começou a tocar.
    // Devolve JSON (ou "" sem média); o título e o artista vêm da Media Session API quando a página os dá.
    public const string Script = """
        (function () {
          try {
            var all = Array.from(document.querySelectorAll('video,audio')).filter(function (m) {
              return !m.muted && m.volume > 0 && (!m.paused || m.currentTime > 0) && !(m.duration > 0 && m.duration < 2);
            });
            if (!all.length) return '';
            var playing = all.filter(function (m) { return !m.paused && !m.ended; });
            var md = navigator.mediaSession && navigator.mediaSession.metadata;
            return JSON.stringify({
              s: playing.length ? 'playing' : 'paused',
              a: playing.some(function (m) { return m.readyState > 2; }),
              t: (md && md.title) || '', r: (md && md.artist) || ''
            });
          } catch (e) { return ''; }
        })()
        """;

    // Pausa tudo o que toca; se nada toca, retoma o média que ia mais adiantado.
    public const string ToggleScript = """
        (function () {
          var all = Array.from(document.querySelectorAll('video,audio')).filter(function (m) { return !m.paused || m.currentTime > 0; });
          var playing = all.filter(function (m) { return !m.paused && !m.ended; });
          if (playing.length) { playing.forEach(function (m) { m.pause(); }); return 'paused'; }
          var next = all.sort(function (a, b) { return b.currentTime - a.currentTime; })[0];
          if (next) { next.play(); return 'playing'; }
          return '';
        })()
        """;

    public const string PauseScript = "document.querySelectorAll('video,audio').forEach(function (m) { if (!m.paused) m.pause(); })";

    // A Pausa (Ctrl+., MainWindow.Pause.cs): pausa só o que está a tocar e marca-o com data-eteris-pausa; devolve quantos
    // pausou. Tira primeiro as marcas que tenham ficado de uma Pausa anterior (um «Retomar» que ninguém carregou): o
    // «Retomar» seguinte só toca o que esta pausou. Corre em todos os motores da janela; nos que não tocam nada não mexe.
    // Só o documento principal: o som de um iframe de outro site fica só calado (SetAudioMuted), não pausado.
    public const string PauseMarkScript = """
        (function () {
          try {
            var n = 0;
            Array.prototype.forEach.call(document.querySelectorAll('video,audio'), function (m) {
              if (m.dataset) delete m.dataset.eterisPausa;
              if (!m.paused && !m.ended) { m.pause(); m.dataset.eterisPausa = '1'; n++; }
            });
            return n;
          } catch (e) { return 0; }
        })()
        """;

    // «Retomar o que tocava» (o botão do aviso, com gesto do utilizador): toca só o que a Pausa marcou e tira a marca.
    public const string ResumeMarkedScript = """
        (function () {
          try {
            var n = 0;
            Array.prototype.forEach.call(document.querySelectorAll('video,audio'), function (m) {
              if (!m.dataset || !m.dataset.eterisPausa) return;
              delete m.dataset.eterisPausa;
              n++;
              var p = m.play();
              if (p && p.catch) p.catch(function () {});
            });
            return n;
          } catch (e) { return 0; }
        })()
        """;

    public static MediaInfo Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Nothing;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var state = root.TryGetProperty("s", out var s) && s.GetString() == "playing" ? MediaState.Playing : MediaState.Paused;
            var audible = state == MediaState.Playing && root.TryGetProperty("a", out var a) && a.ValueKind == JsonValueKind.True;
            return new MediaInfo(state, audible, Text(root, "t"), Text(root, "r"));
        }
        catch (JsonException) { return Nothing; }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text
            ? text.Length > 120 ? text[..120] : text
            : null;
}
