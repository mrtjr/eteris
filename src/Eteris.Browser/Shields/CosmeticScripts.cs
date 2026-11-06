using System.Text.Json;

namespace Eteris.Browser.Shields;

public static class CosmeticScripts
{
    // Devolve "classes\nids" com tudo o que existe na página, para as regras genéricas (##.classe, ###id).
    public const string CollectClassesAndIds = """
        (function () {
          var classes = new Set(), ids = new Set();
          document.querySelectorAll('[class],[id]').forEach(function (e) {
            if (e.id) ids.add(e.id);
            if (typeof e.className === 'string') e.className.split(/\s+/).forEach(function (c) { if (c) classes.add(c); });
          });
          return Array.from(classes).join(' ') + '\n' + Array.from(ids).join(' ');
        })()
        """;

    // O CSS entra como literal JSON (o codificador por omissão escapa ", <, > e &): nada na lista sai da string.
    public static string Inject(string css) => $$"""
        (function (css) {
          var style = document.createElement('style');
          style.setAttribute('data-eteris-shield', '');
          style.textContent = css;
          (document.head || document.documentElement).appendChild(style);
        })({{JsonSerializer.Serialize(css)}});
        """;

    public static (string[] Classes, string[] Ids) ParseClassesAndIds(string raw)
    {
        var lines = raw.Split('\n');
        static string[] Words(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (Words(lines[0]), lines.Length > 1 ? Words(lines[1]) : []);
    }
}
