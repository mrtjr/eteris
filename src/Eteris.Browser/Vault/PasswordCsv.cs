using System.Globalization;
using System.IO;
using System.Text;

namespace Eteris.Browser.Vault;

// Uma palavra-passe lida de um CSV (origem já normalizada).
public sealed record CsvLogin(
    int Line, string Origin, string Username, string Password, string? Note, string? Realm, string? FormAction,
    DateTimeOffset? Created, DateTimeOffset? Used, DateTimeOffset? Changed)
{
    public override string ToString() => $"CsvLogin linha {Line}";
}

public enum CsvRowError { Syntax, MissingPassword, LongPassword, LongUsername, LongNote, MissingUrl, InvalidUrl, LongUrl }

// Uma linha recusada: o endereço e o utilizador como vinham (para a lista de erros), nunca a palavra-passe.
public sealed record CsvRejected(int Line, CsvRowError Error, string? Url, string? Username)
{
    public override string ToString() => $"CsvRejected linha {Line} {Error}";
}

public enum CsvFileError { TooLarge, TooManyRows, NotPasswords }

public sealed record CsvReadResult(IReadOnlyList<CsvLogin> Logins, IReadOnlyList<CsvRejected> Rejected, CsvFileError? FileError);

// Mesma origem e mesmo utilizador, outra palavra-passe. ExistingId null: o conflito é com uma linha anterior do
// próprio ficheiro.
public sealed record ImportConflict(CsvLogin Incoming, string? ExistingId);

public sealed record ImportPlan(IReadOnlyList<CsvLogin> Add, int Duplicates, IReadOnlyList<ImportConflict> Conflicts, IReadOnlyList<CsvRejected> Invalid);

// «a do ficheiro» (a atual fica no histórico do item: nenhuma se perde; a nota e os outros campos do item guardado ficam) ou «a atual».
public enum ConflictChoice { UseFile, KeepCurrent }

// Kept: as diferentes do ficheiro em que o utilizador escolheu «Manter as atuais» (a pergunta já resolveu o conflito:
// o resumo não diz «em conflito», que parecia deixar algo por resolver).
public sealed record ImportSummary(int Imported, int Replaced, int Kept, int Invalid, int Duplicates)
{
    // «312 importadas, 4 mantidas (as atuais), 2 inválidas».
    public string Text
    {
        get
        {
            var parts = new List<string>();
            if (Imported > 0) parts.Add(Count(Imported, "importada", "importadas"));
            if (Replaced > 0) parts.Add(Count(Replaced, "substituída", "substituídas"));
            if (Kept > 0) parts.Add(Kept == 1 ? "1 mantida (a atual)" : $"{Kept} mantidas (as atuais)");
            if (Invalid > 0) parts.Add(Count(Invalid, "inválida", "inválidas"));
            if (Duplicates > 0) parts.Add(Duplicates == 1 ? "1 já existia" : $"{Duplicates} já existiam");
            return parts.Count == 0 ? "Nada para importar" : string.Join(", ", parts);
        }
    }

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}

// Importar e exportar palavras-passe em CSV (docs/palavras-passe.md, A4) como o Chromium
// (components/password_manager/core/browser/import/csv_password_sequence.cc, password_importer.cc,
// export/password_csv_writer.cc) e o Firefox (LoginCSVImport.sys.mjs, LoginExport.sys.mjs). Nunca se leem os
// ficheiros de senhas de outros navegadores: só o CSV que o utilizador exportou.
public static class PasswordCsv
{
    // Limites do Chromium.
    public const int MaxFileBytes = 1000 * 1024;
    public const int MaxRows = 3000;
    public const int MaxPassword = 1000;
    public const int MaxUsername = 1000;
    public const int MaxNote = 1000;
    public const int MaxUrl = 2048;

    private enum Column { Origin, Username, Password, Note, Realm, FormAction, Created, Used, Changed }

    private static readonly Dictionary<string, Column> Labels = new(StringComparer.Ordinal)
    {
        ["url"] = Column.Origin, ["website"] = Column.Origin, ["origin"] = Column.Origin, ["hostname"] = Column.Origin, ["login_uri"] = Column.Origin,
        ["username"] = Column.Username, ["user"] = Column.Username, ["login"] = Column.Username, ["account"] = Column.Username, ["login_username"] = Column.Username,
        ["password"] = Column.Password, ["login_password"] = Column.Password,
        ["httprealm"] = Column.Realm, ["formactionorigin"] = Column.FormAction,
        ["timecreated"] = Column.Created, ["timelastused"] = Column.Used, ["timepasswordchanged"] = Column.Changed,
    };

    // Várias colunas de nota: fica a de maior prioridade.
    private static readonly string[] NoteLabels = ["note", "notes", "comment", "comments"];

    public static CsvReadResult Read(byte[] bytes)
    {
        if (bytes.Length > MaxFileBytes) return new([], [], CsvFileError.TooLarge);
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var records = Parse(text).Where(r => !(r.Fields.Count == 1 && r.Fields[0].Length == 0 && !r.Broken)).ToList();
        if (records.Count == 0 || records[0].Broken) return new([], [], CsvFileError.NotPasswords);

        var header = records[0].Fields.Select(f => f.Trim().ToLowerInvariant()).ToList();
        var map = new Dictionary<Column, int>();
        for (var i = 0; i < header.Count; i++)
            if (Labels.TryGetValue(header[i], out var column)) map.TryAdd(column, i);
        var note = NoteLabels.Select(label => header.IndexOf(label)).FirstOrDefault(i => i >= 0, -1);
        if (note >= 0) map[Column.Note] = note;
        if (!map.ContainsKey(Column.Origin) || !map.ContainsKey(Column.Password)) return new([], [], CsvFileError.NotPasswords);
        if (records.Count - 1 > MaxRows) return new([], [], CsvFileError.TooManyRows);

        var logins = new List<CsvLogin>();
        var rejected = new List<CsvRejected>();
        foreach (var record in records.Skip(1))
        {
            string Field(Column c) => map.TryGetValue(c, out var i) && i < record.Fields.Count ? record.Fields[i] : string.Empty;
            var url = Field(Column.Origin).Trim();
            var username = Field(Column.Username);
            if (record.Broken) { rejected.Add(new(record.Line, CsvRowError.Syntax, null, null)); continue; }
            var password = Field(Column.Password);
            var noteText = Field(Column.Note);
            CsvRowError? error = password.Length == 0 ? CsvRowError.MissingPassword
                : password.Length > MaxPassword ? CsvRowError.LongPassword
                : username.Length > MaxUsername ? CsvRowError.LongUsername
                : noteText.Length > MaxNote ? CsvRowError.LongNote
                : url.Length == 0 ? CsvRowError.MissingUrl
                : url.Length > MaxUrl ? CsvRowError.LongUrl
                : Origins.Normalize(url) is null ? CsvRowError.InvalidUrl
                : null;
            if (error is { } e) { rejected.Add(new(record.Line, e, url.Length > 200 ? url[..200] : url, username.Length > 200 ? username[..200] : username)); continue; }
            var realm = Field(Column.Realm);
            var action = Field(Column.FormAction).Trim();
            logins.Add(new CsvLogin(record.Line, Origins.Normalize(url)!, username, password, noteText.Length == 0 ? null : noteText,
                realm.Length == 0 ? null : realm, action.Length == 0 ? null : Origins.Normalize(action) ?? action,
                Time(Field(Column.Created)), Time(Field(Column.Used)), Time(Field(Column.Changed))));
        }
        return new(logins, rejected, null);
    }

    // Duplicado igual (no cofre ou antes no ficheiro): ignora. Mesma origem e utilizador com outra palavra-passe: conflito.
    public static ImportPlan Plan(CsvReadResult read, VaultService vault)
    {
        var add = new List<CsvLogin>();
        var conflicts = new List<ImportConflict>();
        var duplicates = 0;
        var seen = new Dictionary<(string, string), string>();
        foreach (var login in read.Logins)
        {
            var key = (login.Origin, login.Username);
            if (seen.TryGetValue(key, out var earlier))
            {
                if (earlier == login.Password) duplicates++;
                else conflicts.Add(new(login, null));
                continue;
            }
            seen[key] = login.Password;
            if (vault.FindLogin(login.Origin, login.Username) is { } id)
            {
                if (vault.GetLogin(id)?.Password == login.Password) duplicates++;
                else conflicts.Add(new(login, id));
            }
            else
            {
                add.Add(login);
            }
        }
        return new(add, duplicates, conflicts, read.Rejected);
    }

    public static ImportSummary Apply(ImportPlan plan, VaultService vault, Func<ImportConflict, ConflictChoice> choose)
    {
        foreach (var login in plan.Add) vault.SaveLogin(ToItem(login));
        int replaced = 0, kept = 0;
        foreach (var conflict in plan.Conflicts)
        {
            if (choose(conflict) == ConflictChoice.UseFile)
            {
                // Só a palavra-passe (e o que o ficheiro traz) muda: a nota, a última utilização e o formulário do item
                // guardado ficam (VAU-11); a palavra-passe anterior vai para o histórico.
                var incoming = conflict.Incoming;
                var existing = vault.GetLogin(conflict.ExistingId ?? vault.FindLogin(incoming.Origin, incoming.Username) ?? string.Empty);
                vault.SaveLogin(existing is null
                    ? ToItem(incoming)
                    : existing with
                    {
                        Password = incoming.Password,
                        Generated = false,
                        Note = JoinNotes(existing.Note, incoming.Note),
                        Used = incoming.Used ?? existing.Used,
                        FormAction = incoming.FormAction ?? existing.FormAction,
                        Realm = incoming.Realm ?? existing.Realm,
                    });
                replaced++;
            }
            else
            {
                kept++;
            }
        }
        return new(plan.Add.Count, replaced, kept, plan.Invalid.Count, plan.Duplicates);
    }

    // Uma nota «em falta» é nula ou vazia. Sem nota no ficheiro fica a guardada; sem nota guardada fica a do ficheiro;
    // iguais, uma só; diferentes, as duas (a do ficheiro primeiro) se couberem em MaxNote, senão fica a guardada.
    internal static string? JoinNotes(string? stored, string? incoming)
    {
        if (string.IsNullOrEmpty(incoming)) return stored;
        if (string.IsNullOrEmpty(stored) || stored == incoming) return incoming;
        var both = incoming + "\n\n" + stored;
        return both.Length <= MaxNote ? both : stored;
    }

    private static LoginItem ToItem(CsvLogin c)
        => new(string.Empty, c.Origin, c.Username, c.Password, c.Note, c.Created ?? default, c.Changed ?? default, c.Used, c.FormAction, false, c.Realm);

    // O formato do Chrome (name,url,username,password,note), que o Chrome, o Brave e o Firefox importam; ordenado como o
    // Chromium. O texto tem todas as palavras-passe em claro: quem chama pede a confirmação do Windows antes e avisa.
    public static string Write(IEnumerable<LoginItem> logins)
    {
        var rows = logins.Select(l => new[] { ShownName(l.Origin), Origins.IsAndroid(l.Origin) ? l.Origin : l.Origin + "/", l.Username, l.Password, l.Note ?? string.Empty })
            .OrderBy(r => r, Comparer<string[]>.Create((a, b) =>
            {
                for (var i = 0; i < a.Length; i++)
                    if (string.CompareOrdinal(a[i], b[i]) is var c and not 0) return c;
                return 0;
            }));
        var text = new StringBuilder("name,url,username,password,note\n");
        foreach (var row in rows) text.Append(string.Join(',', row.Select(Escape))).Append('\n');
        return text.ToString();
    }

    // Exportar para uma pasta que sincroniza com a nuvem leva o ficheiro em claro para lá: avisa-se com o nome.
    public static string? CloudFolder(string path, Func<string, string?> environment)
    {
        var full = path.Replace('/', '\\');
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (environment(name) is { Length: > 0 } root && full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return "OneDrive";
        var segments = full.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments.SkipLast(1))
        {
            if (segment.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) || segment.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase)) return "OneDrive";
            if (segment.Equals("Dropbox", StringComparison.OrdinalIgnoreCase) || segment.StartsWith("Dropbox (", StringComparison.OrdinalIgnoreCase)) return "Dropbox";
            if (segment is "Google Drive" or "My Drive" or "O meu disco" or "Meu Drive" or "GoogleDrive") return "Google Drive";
        }
        return null;
    }

    // O nome que o Chrome mostra: o anfitrião sem «www.»; numa app Android, o pacote.
    private static string ShownName(string origin)
    {
        if (Origins.IsAndroid(origin))
        {
            var at = origin.IndexOf('@');
            return at >= 0 ? origin[(at + 1)..].TrimEnd('/') : origin;
        }
        var host = Origins.DisplayHost(origin) ?? origin;
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static string Escape(string field)
        => field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;

    private static DateTimeOffset? Time(string text)
        => long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms > 0 && ms < 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private sealed record Record(int Line, List<string> Fields, bool Broken);

    // RFC 4180: campos entre aspas com "" para aspas e quebras de linha lá dentro; CRLF ou LF entre registos.
    private static IEnumerable<Record> Parse(string text)
    {
        var i = 0;
        var line = 1;
        while (i < text.Length)
        {
            var start = line;
            var fields = new List<string>();
            var broken = false;
            var field = new StringBuilder();
            var ended = false;
            while (!ended)
            {
                if (i < text.Length && text[i] == '"')
                {
                    i++;
                    var closed = false;
                    while (i < text.Length)
                    {
                        var c = text[i++];
                        if (c == '"')
                        {
                            if (i < text.Length && text[i] == '"') { field.Append('"'); i++; continue; }
                            closed = true;
                            break;
                        }
                        if (c == '\n') line++;
                        field.Append(c);
                    }
                    if (!closed) { broken = true; ended = true; fields.Add(field.ToString()); break; }
                    // Depois das aspas de fecho só pode vir separador ou fim de linha.
                    if (i < text.Length && text[i] is not (',' or '\r' or '\n'))
                    {
                        broken = true;
                        while (i < text.Length && text[i] != '\n') i++;
                    }
                }
                else
                {
                    while (i < text.Length && text[i] is not (',' or '\r' or '\n')) field.Append(text[i++]);
                }
                fields.Add(field.ToString());
                field.Clear();
                if (i >= text.Length) { ended = true; }
                else if (text[i] == ',') { i++; }
                else
                {
                    if (text[i] == '\r') i++;
                    if (i < text.Length && text[i] == '\n') i++;
                    line++;
                    ended = true;
                }
            }
            yield return new Record(start, fields, broken);
        }
    }
}
