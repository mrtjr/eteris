using System.Globalization;
using System.IO;
using System.Text;

namespace Eteris.Browser.Data;

// Resultado de NotesStore.SaveOver: gravado como estava, juntado com o que outra janela gravou, ou por ler.
public enum NotesSave { Saved, Merged, Unreadable }

// Bloco de notas do Éteris: um único texto, guardado neste computador (notas.md no perfil) e, com a sincronização
// ligada, nos outros aparelhos da conta (cifrado).
public sealed class NotesStore(string path)
{
    // As notas foram gravadas (a sincronização compara daqui a 10 s).
    public static event Action? Saved;

    public string Load() => TryLoad() ?? string.Empty;

    // null = o ficheiro existe mas não se leu agora (bloqueado por outro programa): não é o mesmo que notas vazias.
    public string? TryLoad()
    {
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // Escrita atómica: um ficheiro temporário substitui o anterior, para um fecho a meio não deixar notas cortadas.
    public void Save(string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        Saved?.Invoke();
    }

    // Grava o texto desta janela por cima do que ela leu (baseText). Se outra janela gravou entretanto, junta-se ao
    // texto do disco só o que esta escreveu (Merge) — nada do que a outra gravou se perde. Notas que não se leram
    // (ficheiro preso por outro programa, ou baseText nulo) não se gravam por cima.
    public (NotesSave Result, string Text) SaveOver(string? baseText, string text, DateTime when)
    {
        var disk = TryLoad();
        if (disk is null || baseText is null) return (NotesSave.Unreadable, text);
        if (disk == baseText)
        {
            Save(text);
            return (NotesSave.Saved, text);
        }
        var merged = Merge(baseText, disk, text, when);
        if (merged != disk) Save(merged);
        return (NotesSave.Merged, merged);
    }

    // O texto do disco (gravado por outra janela) mais as linhas que só esta janela escreveu (não estão nem no disco
    // nem no texto que leu), no fim, com uma linha a dizer de onde vêm. Uma linha que a outra janela apagou não volta.
    public static string Merge(string baseText, string disk, string mine, DateTime when)
    {
        if (mine == baseText || mine == disk) return disk;
        var known = new HashSet<string>(Lines(disk), StringComparer.Ordinal);
        known.UnionWith(Lines(baseText));
        var added = Lines(mine).Where(line => line.Length > 0 && !known.Contains(line)).ToList();
        if (added.Count == 0) return disk;
        if (disk.Length == 0) return string.Join("\n", added) + "\n";
        return disk.TrimEnd('\r', '\n') + "\n\n— Escrito noutra janela (" + when.ToString("HH:mm", CultureInfo.InvariantCulture) + ") —\n"
               + string.Join("\n", added) + "\n";
    }

    private static IEnumerable<string> Lines(string text) => text.Split('\n').Select(line => line.TrimEnd('\r'));

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
