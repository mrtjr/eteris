using System.IO;
using System.Text;

namespace Eteris.Browser.Data;

// Bloco de notas do Éteris: um único texto, guardado só neste computador (notas.md no perfil).
public sealed class NotesStore(string path)
{
    public string Load()
    {
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty; }
        catch (IOException) { return string.Empty; }
    }

    // Escrita atómica: um ficheiro temporário substitui o anterior, para um fecho a meio não deixar notas cortadas.
    public void Save(string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    // Linha para «Inserir esta página»: título e endereço, numa linha nova no fim (ou onde estiver o cursor).
    public static string PageLine(string title, string url)
    {
        var clean = Shields.UrlCleaner.Clean(url);
        var name = string.Join(' ', (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return name.Length > 0 && name != clean ? $"- {name} — {clean}" : $"- {clean}";
    }

    public static (string Text, int Caret) Insert(string text, int caret, string line)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var before = text[..caret];
        var after = text[caret..];
        // A linha fica sempre sozinha: quebra antes se o cursor estiver a meio de uma linha, e uma (só uma) depois.
        var prefix = before.Length == 0 || before.EndsWith('\n') ? string.Empty : "\n";
        var inserted = prefix + line + "\n";
        var rest = after.StartsWith('\n') ? after[1..] : after;
        return (before + inserted + rest, before.Length + inserted.Length);
    }
}
