using System.IO;
using System.Text;
using Eteris.Browser.Sync;
using Eteris.Browser.Vault;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Tests.Vault;

// A4 (docs/palavras-passe.md): importar e exportar palavras-passe em CSV, como o Chromium e o Firefox os leem.
// Ficheiros sintéticos com o formato de cada um (Chrome/Brave, Firefox, Bitwarden, 1Password); dados inventados.
public sealed class PasswordCsvTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eteris-csv-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    public PasswordCsvTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string Chrome =
        "name,url,username,password,note\n" +
        "exemplo.test,https://www.exemplo.test/entrar,ana@exemplo.test,Inventada-1!,\n" +
        "outro.test,https://outro.test/,\"Teste, Ana\",\"com \"\"aspas\"\" e, vírgula\",\"nota\nem duas linhas\"\n" +
        "com.exemplo.app,android://AbC==@com.exemplo.app/,ana,Inventada-2!,\n";

    private const string Firefox =
        "\"url\",\"username\",\"password\",\"httpRealm\",\"formActionOrigin\",\"guid\",\"timeCreated\",\"timeLastUsed\",\"timePasswordChanged\"\r\n" +
        "\"https://firefox.exemplo.test\",\"ana\",\"Inventada-3!\",,\"https://firefox.exemplo.test\",\"{0b1c}\",\"1762682400000\",\"1762686000000\",\"1762682400000\"\r\n" +
        "\"https://intranet.exemplo.test\",\"ana\",\"Inventada-4!\",\"Zona privada\",,\"{0b1d}\",\"1762682400000\",\"1762682400000\",\"1762682400000\"\r\n";

    private const string Bitwarden =
        "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n" +
        ",,login,Exemplo,nota do cofre,,0,https://bitwarden.exemplo.test/login,ana@exemplo.test,Inventada-5!,\n" +
        ",,note,Só uma nota,texto,,0,,,,\n";

    private const string OnePassword =
        "Title,Url,Username,Password,OTPAuth,Favorite,Archived,Tags,Notes\n" +
        "Exemplo,https://1p.exemplo.test,ana@exemplo.test,Inventada-6!,,false,false,,nota 1p\n";

    [Fact]
    public void Chrome_QuotesNewlinesAndAndroid()
    {
        var read = PasswordCsv.Read(Encoding.UTF8.GetBytes(Chrome));
        Assert.Null(read.FileError);
        Assert.Empty(read.Rejected);
        Assert.Equal(3, read.Logins.Count);
        var first = read.Logins[0];
        Assert.Equal("https://www.exemplo.test", first.Origin);
        Assert.Equal("ana@exemplo.test", first.Username);
        Assert.Equal("Inventada-1!", first.Password);
        Assert.Null(first.Note);
        var second = read.Logins[1];
        Assert.Equal("Teste, Ana", second.Username);
        Assert.Equal("com \"aspas\" e, vírgula", second.Password);
        Assert.Equal("nota\nem duas linhas", second.Note);
        Assert.Equal("android://AbC==@com.exemplo.app/", read.Logins[2].Origin);
    }

    // Brave exporta no mesmo formato do Chrome; com BOM e CRLF também entra.
    [Fact]
    public void Brave_WithBomAndCrLf()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Chrome.Replace("\n", "\r\n", StringComparison.Ordinal))).ToArray();
        var read = PasswordCsv.Read(bytes);
        Assert.Equal(3, read.Logins.Count);
        Assert.Equal("nota\r\nem duas linhas", read.Logins[1].Note);
        Assert.Equal("ana@exemplo.test", read.Logins[0].Username);
    }

    [Fact]
    public void Firefox_KeepsRealmFormActionAndTimes()
    {
        var read = PasswordCsv.Read(Encoding.UTF8.GetBytes(Firefox));
        Assert.Equal(2, read.Logins.Count);
        var a = read.Logins[0];
        Assert.Equal("https://firefox.exemplo.test", a.Origin);
        Assert.Equal("https://firefox.exemplo.test", a.FormAction);
        Assert.Null(a.Realm);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1762682400000), a.Created);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1762686000000), a.Used);
        // httpRealm: autenticação HTTP — guarda-se (para a v2), não se preenche.
        Assert.Equal("Zona privada", read.Logins[1].Realm);
    }

    [Fact]
    public void Bitwarden_LoginRowsOnly()
    {
        var read = PasswordCsv.Read(Encoding.UTF8.GetBytes(Bitwarden));
        var login = Assert.Single(read.Logins);
        Assert.Equal("https://bitwarden.exemplo.test", login.Origin);
        Assert.Equal("Inventada-5!", login.Password);
        Assert.Equal("nota do cofre", login.Note);
        var rejected = Assert.Single(read.Rejected);
        Assert.Equal(CsvRowError.MissingPassword, rejected.Error);
        Assert.Equal(3, rejected.Line);
    }

    [Fact]
    public void OnePassword_CaseInsensitiveHeaders()
    {
        var login = Assert.Single(PasswordCsv.Read(Encoding.UTF8.GetBytes(OnePassword)).Logins);
        Assert.Equal("https://1p.exemplo.test", login.Origin);
        Assert.Equal("nota 1p", login.Note);
    }

    // Várias colunas de nota: «note» > «notes» > «comment» > «comments» (como o Chromium).
    [Fact]
    public void NoteColumns_HavePriorities()
    {
        var read = PasswordCsv.Read(Encoding.UTF8.GetBytes("url,password,comments,notes\nhttps://a.test,x,c,n\n"));
        Assert.Equal("n", read.Logins.Single().Note);
    }

    [Fact]
    public void BadRows_AreCountedWithoutThePassword()
    {
        var csv = "url,username,password\n" +
                  "https://ok.test,ana,Inventada-7!\n" +
                  ",ana,Inventada-8!\n" +
                  "nao e url,ana,Inventada-9!\n" +
                  "ftp://ficheiros.test,ana,Inventada-10!\n" +
                  "https://sem-senha.test,ana,\n" +
                  "https://longa.test,ana," + new string('x', 1001) + "\n" +
                  "https://longo.test," + new string('u', 1001) + ",x\n" +
                  "https://aspas.test,\"ana,x\n" +
                  "\n";
        var read = PasswordCsv.Read(Encoding.UTF8.GetBytes(csv));
        Assert.Single(read.Logins);
        Assert.Equal(
            [CsvRowError.MissingUrl, CsvRowError.InvalidUrl, CsvRowError.InvalidUrl, CsvRowError.MissingPassword, CsvRowError.LongPassword, CsvRowError.LongUsername, CsvRowError.Syntax],
            read.Rejected.Select(r => r.Error));
        Assert.All(read.Rejected, r => Assert.DoesNotContain("Inventada", r.ToString()));
        Assert.Equal(3, read.Rejected[0].Line);
    }

    // Limites do Chromium: ficheiro ≤ 1000 KiB e ≤ 3000 palavras-passe; sem colunas de URL e palavra-passe não é um CSV
    // de palavras-passe.
    [Fact]
    public void FileLimits()
    {
        Assert.Equal(1000 * 1024, PasswordCsv.MaxFileBytes);
        Assert.Equal(CsvFileError.TooLarge, PasswordCsv.Read(new byte[PasswordCsv.MaxFileBytes + 1]).FileError);
        var many = new StringBuilder("url,password\n");
        for (var i = 0; i <= PasswordCsv.MaxRows; i++) many.Append("https://s").Append(i).Append(".test,x\n");
        Assert.Equal(CsvFileError.TooManyRows, PasswordCsv.Read(Encoding.UTF8.GetBytes(many.ToString())).FileError);
        Assert.Equal(CsvFileError.NotPasswords, PasswordCsv.Read(Encoding.UTF8.GetBytes("nome,telefone\nAna,1\n")).FileError);
        Assert.Equal(CsvFileError.NotPasswords, PasswordCsv.Read([]).FileError);
        Assert.Empty(PasswordCsv.Read(new byte[PasswordCsv.MaxFileBytes + 1]).Logins);
    }

    // Exportar no formato do Chrome (que o Chrome, o Brave e o Firefox importam) e voltar a ler dá o mesmo.
    [Fact]
    public void Export_ChromeFormat_RoundTrips()
    {
        var logins = PasswordCsv.Read(Encoding.UTF8.GetBytes(Chrome)).Logins
            .Select(c => new LoginItem("", c.Origin, c.Username, c.Password, c.Note, _now, _now, null, null, false, null)).ToList();
        var text = PasswordCsv.Write(logins);
        Assert.StartsWith("name,url,username,password,note\n", text);
        Assert.Contains("exemplo.test,https://www.exemplo.test/,ana@exemplo.test,Inventada-1!,", text);
        Assert.Contains("\"com \"\"aspas\"\" e, vírgula\"", text);
        Assert.Contains("android://AbC==@com.exemplo.app/", text);
        var again = PasswordCsv.Read(Encoding.UTF8.GetBytes(text)).Logins;
        Assert.Equal(
            logins.Select(l => (l.Origin, l.Username, l.Password, l.Note)).OrderBy(x => x.Origin, StringComparer.Ordinal),
            again.Select(l => (l.Origin, l.Username, l.Password, l.Note)).OrderBy(x => x.Origin, StringComparer.Ordinal));
    }

    // Importar para o cofre: duplicado igual ignora; mesma origem e utilizador com outra palavra-passe pergunta; resumo.
    [Fact]
    public void Import_IntoTheVault_WithConflictsAndSummary()
    {
        using var vault = new VaultService(_root, new Dpapi(VaultKey.Purpose), () => _now);
        vault.SaveLogin(new LoginItem("", "https://www.exemplo.test", "ana@exemplo.test", "Inventada-1!", null, _now, _now, null, null, false, null));
        var keep = vault.SaveLogin(new LoginItem("", "https://outro.test", "Teste, Ana", "A-atual-1!", null, _now, _now, null, null, false, null));
        var csv = Chrome + "dup.test,https://www.exemplo.test/,ana@exemplo.test,Inventada-1!,\n,sem url,x,\n";
        var read = PasswordCsv.Read(Encoding.UTF8.GetBytes(csv));
        var plan = PasswordCsv.Plan(read, vault);
        Assert.Single(plan.Add);
        Assert.Equal(2, plan.Duplicates);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(keep, conflict.ExistingId);
        Assert.Single(plan.Invalid);

        var summary = PasswordCsv.Apply(plan, vault, _ => ConflictChoice.KeepCurrent);
        Assert.Equal("1 importada, 1 em conflito, 1 inválida, 2 já existiam", summary.Text);
        Assert.Equal("A-atual-1!", vault.GetLogin(keep)!.Password);
        Assert.Equal(3, vault.Logins().Count);

        var again = PasswordCsv.Apply(PasswordCsv.Plan(read, vault), vault, _ => ConflictChoice.UseFile);
        Assert.Equal("com \"aspas\" e, vírgula", vault.GetLogin(keep)!.Password);
        // A atual não se perde: fica no histórico do item.
        Assert.Equal("A-atual-1!", vault.PreviousPassword(keep));
        Assert.Equal(0, again.Imported);
        Assert.Equal(1, again.Replaced);
    }

    [Fact]
    public void Summary_Text_PluralsInPortuguese()
    {
        Assert.Equal("312 importadas, 4 em conflito, 2 inválidas", new ImportSummary(312, 0, 4, 2, 0).Text);
        Assert.Equal("Nada para importar", new ImportSummary(0, 0, 0, 0, 0).Text);
        Assert.Equal("1 importada, 3 já existiam", new ImportSummary(1, 0, 0, 0, 3).Text);
    }

    // Exportar para uma pasta que sincroniza com a nuvem (OneDrive, Dropbox, Google Drive): avisa.
    [Fact]
    public void SyncedFolders_AreRecognised()
    {
        var env = new Dictionary<string, string?> { ["OneDrive"] = @"C:\Users\teste\OneDrive", ["USERPROFILE"] = @"C:\Users\teste" };
        Assert.Equal("OneDrive", PasswordCsv.CloudFolder(@"C:\Users\teste\OneDrive\Documentos\senhas.csv", env.GetValueOrDefault));
        Assert.Equal("Dropbox", PasswordCsv.CloudFolder(@"C:\Users\teste\Dropbox\senhas.csv", env.GetValueOrDefault));
        Assert.Equal("Google Drive", PasswordCsv.CloudFolder(@"G:\O meu disco\senhas.csv", env.GetValueOrDefault));
        Assert.Equal("Google Drive", PasswordCsv.CloudFolder(@"C:\Users\teste\Google Drive\senhas.csv", env.GetValueOrDefault));
        Assert.Null(PasswordCsv.CloudFolder(@"C:\Users\teste\Documents\senhas.csv", env.GetValueOrDefault));
        Assert.Null(PasswordCsv.CloudFolder(@"C:\Users\teste\OneDriveX\senhas.csv", env.GetValueOrDefault));
    }
}
