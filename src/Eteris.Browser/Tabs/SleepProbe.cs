using System.Text.Json;

namespace Eteris.Browser.Tabs;

// A pergunta que o Éteris faz à página antes de a adormecer por tempo ou por pressão (MainWindow.Sleep.cs): a rolagem
// (para a repor ao acordar), se há texto por enviar, se há Picture-in-Picture e se há um iframe de outro site «com ar
// de leitor». Sem resposta (página sem contexto de script: PDF, página de erro) adormece como sempre.
//
// O que se vê daqui tem limites: só as caixas de texto de várias linhas e o editor com o foco contam como texto por
// enviar (os campos de uma linha também mudam com o preenchimento do cofre, e um editor dentro de um iframe não se vê);
// o som de um iframe de outro site e o Web Audio não se veem (decisão D1: PlayerFrame não isenta).
public sealed record SleepProbe(double Y, bool Dirty, bool PictureInPicture, bool PlayerFrame)
{
    // Decisão D1 (a): um iframe de outro site com ar de leitor não isenta (páginas com um vídeo embutido parado
    // continuariam acordadas); a opção (b) é pôr isto a true.
    public static readonly bool PlayerFrameKeepsAwake = false;

    // Uma página que pediu para ficar acordada não volta a ser perguntada durante este tempo.
    public static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(5);

    public bool KeepsAwake => Dirty || PictureInPicture || (PlayerFrameKeepsAwake && PlayerFrame);

    public const string Script = """
        (function () {
          try {
            var dirty = false, areas = document.querySelectorAll('textarea');
            for (var i = 0; i < areas.length && !dirty; i++) {
              var t = areas[i];
              dirty = !t.disabled && !t.readOnly && t.value.length > 0 && t.value !== t.defaultValue;
            }
            var a = document.activeElement;
            if (!dirty && a && a.isContentEditable && (a.textContent || '').trim().length > 0) dirty = true;
            var player = false, frames = document.querySelectorAll('iframe');
            for (var k = 0; k < frames.length && !player; k++) {
              var frame = frames[k], readable = false;
              try { readable = !!frame.contentDocument; } catch (e) { }
              if (readable) continue;
              var box = frame.getBoundingClientRect();
              if (box.width < 200 || box.height < 150) continue;
              var allow = (frame.getAttribute('allow') || '').toLowerCase();
              player = frame.hasAttribute('allowfullscreen') || allow.indexOf('autoplay') >= 0 || allow.indexOf('fullscreen') >= 0;
            }
            return JSON.stringify({ y: window.scrollY || 0, d: dirty, p: !!document.pictureInPictureElement, f: player });
          } catch (e) { return ''; }
        })()
        """;

    // Só uma string com JSON válido e os quatro campos dá um valor; o resto (sem resposta, lixo) dá null.
    public static SleepProbe? Parse(object? result)
    {
        if (result is not string { Length: > 0 } json) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("y", out var y) || y.ValueKind != JsonValueKind.Number || !y.TryGetDouble(out var scroll)
                || !TryBool(root, "d", out var dirty) || !TryBool(root, "p", out var pip) || !TryBool(root, "f", out var frame))
                return null;
            return new SleepProbe(scroll, dirty, pip, frame);
        }
        catch (JsonException) { return null; }
    }

    private static bool TryBool(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = element.GetBoolean();
        return true;
    }
}
