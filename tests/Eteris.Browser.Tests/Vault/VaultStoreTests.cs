using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Eteris.Browser.Sync;
using Eteris.Browser.Tests.Sync;
using Eteris.Browser.Vault;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Tests.Vault;

// A2 (docs/palavras-passe.md): o cofre local — <raiz>\cofre.db (SQLite) com cada campo em AES-256-GCM e a chave do
// cofre em <raiz>\cofre.key, embrulhada pela DPAPI do utilizador (entropia «Eteris.Sync:cofre»). Só dados inventados.
public sealed class VaultStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eteris-cofre-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private const string Password = "Inventada-Palavra-9!";
    private const string Note = "Nota-secreta-de-teste";
    private const string User = "utilizador.teste@exemplo.test";
    private const string Host = "conta.banco-exemplo.test";
    private const string Card = "4111111111111111";
    private const string Iban = "PT50000201231234567890154";
    private const string Cpf = "52998224725";

    public VaultStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private VaultService Service() => new(_root, new Dpapi(VaultKey.Purpose), () => _now);

    private static LoginItem Login(string password = Password, string id = "")
        => new(id, "https://" + Host + "/entrar", User, password, Note, default, default, null, null, false, null);

    [Fact]
    public void Files_LiveInTheRoot_OutsideTheChromiumProfile()
    {
        using var vault = Service();
        vault.SaveLogin(Login());
        Assert.True(File.Exists(Path.Combine(_root, "cofre.db")));
        Assert.True(File.Exists(Path.Combine(_root, "cofre.key")));
        Assert.Equal("cofre.db", VaultStore.FileName);
        Assert.Equal("cofre.key", VaultKey.FileName);
    }

    // Nenhum texto em claro no disco: nem o segredo, nem o site, nem o utilizador, em Latin-1, UTF-8 ou UTF-16.
    [Fact]
    public void Disk_HasNoPlainText()
    {
        using (var vault = Service())
        {
            vault.SaveLogin(Login());
            vault.SaveLogin(Login("Outra-Inventada-7?") with { Id = vault.Logins().Single().Id });
            vault.Save(new CardItem("", "Titular Inventado", Card, 8, 2029, "Cartao-alcunha", default, null));
            vault.Save(new IbanItem("", Iban, "Conta-alcunha", default));
            vault.Save(new IdDocItem("", IdDocType.TaxId, Cpf, "Titular Inventado", "BR", null, null, null, null));
            vault.Save(new ContactItem("", "Casa", "Nome-Inventado", "Apelido-Inventado", null, "Rua-Inventada", "1", null, "Bairro-Inventado", "Cidade-Inventada", null, "01310-100", "BR", "contacto@exemplo.test", "+55 11 0000-0000", default, null));
            vault.Save(TravelItem.Vehicle("", "Marca", "Modelo", 2020, "Titular Inventado", "ABC1D23", "SP", "VIN-INVENTADO-123"));
            vault.AddNeverSave("https://nunca.exemplo.test");
        }
        SqliteConnection.ClearAllPools();
        var secrets = new[]
        {
            Password, "Outra-Inventada-7?", Note, User, Host, "banco-exemplo", Card, "1111", Iban, Cpf, "Titular Inventado", "Cartao-alcunha",
            "Conta-alcunha", "Nome-Inventado", "Rua-Inventada", "Bairro-Inventado", "Cidade-Inventada", "contacto@exemplo.test", "ABC1D23",
            "VIN-INVENTADO", "nunca.exemplo", "Visa",
        };
        // Os ids dos itens estão em claro (128 bits aleatórios em hexadecimal, VaultIds) e, em cerca de 1 corrida em 40,
        // um deles tinha «1111» (os 4 últimos dígitos do cartão): falso positivo, não fuga. Só esses ids exatos saem do
        // texto antes de procurar — e confirma-se que são mesmo 32 algarismos hexadecimais (nada do site nem do utilizador).
        var ids = ItemIds(Path.Combine(_root, VaultStore.FileName));
        Assert.Equal(7, ids.Count);
        Assert.All(ids, id => Assert.Matches("^[0-9a-f]{32}$", id));
        // Tudo o que está na pasta do cofre (cofre.db, cofre.key e, se lá ficassem, o -wal, o -journal ou temporários).
        foreach (var file in Directory.GetFiles(_root))
        {
            var bytes = File.ReadAllBytes(file);
            var texts = new[] { Encoding.Latin1.GetString(bytes), Encoding.UTF8.GetString(bytes), Encoding.Unicode.GetString(bytes) }
                .Select(t => WithoutIds(t, ids)).ToArray();
            foreach (var secret in secrets)
                Assert.DoesNotContain(texts, t => t.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    // A máscara tira só os ids: um id com «1111» no meio (como o que fez o teste falhar) deixa de contar, e um «1111»
    // fora dele continua a ser apanhado.
    [Fact]
    public void Disk_HasNoPlainText_MasksOnlyTheRandomIds()
    {
        const string id = "33d47f813a424df271111a0ab59d5364";
        Assert.DoesNotContain("1111", WithoutIds("..LLcontacts" + id + "..", [id]), StringComparison.Ordinal);
        Assert.Contains("1111", WithoutIds("..LLcards" + id + "..1111..", [id]), StringComparison.Ordinal);
        Assert.Contains("1111", WithoutIds("..LLcards4111111111111111..", [id]), StringComparison.Ordinal);
    }

    private static string WithoutIds(string text, IEnumerable<string> ids)
        => ids.Aggregate(text, (t, id) => t.Replace(id, new string('·', id.Length), StringComparison.Ordinal));

    private static List<string> ItemIds(string db)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM items";
        using var r = command.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    [Fact]
    public void Login_RoundTrips_AndTheListHasNoSecret()
    {
        using var vault = Service();
        var id = vault.SaveLogin(Login());
        var entry = Assert.Single(vault.Logins());
        Assert.Equal(id, entry.Id);
        Assert.Equal("https://" + Host, entry.Origin);
        Assert.Equal(User, entry.Username);
        Assert.False(entry.Meta.ContainsKey("password"));
        Assert.False(entry.Meta.ContainsKey("note"));
        var full = vault.GetLogin(id)!;
        Assert.Equal(Password, full.Password);
        Assert.Equal(Note, full.Note);
        Assert.Equal(_now, full.Created);
    }

    // Um blob trocado de item, de campo ou de coleção não abre (dados associados coleção|id|campo|versão).
    [Fact]
    public void MovedBlob_FailsVerification()
    {
        string a, b;
        using (var vault = Service())
        {
            a = vault.SaveLogin(Login());
            b = vault.SaveLogin(Login("Outra-Inventada-7?") with { Origin = "https://outro.exemplo.test" });
        }
        Sql($"UPDATE items SET secret_ct = (SELECT secret_ct FROM items WHERE id = '{a}') WHERE id = '{b}'");
        using (var vault = Service())
        {
            Assert.Throws<VaultCorruptException>(() => vault.GetLogin(b));
            Assert.Equal(Password, vault.GetLogin(a)!.Password);
        }
        Sql($"UPDATE items SET meta_ct = secret_ct WHERE id = '{a}'");
        using (var vault = Service())
            Assert.Throws<VaultCorruptException>(() => vault.Logins());
    }

    [Fact]
    public void BumpedVersion_FailsVerification()
    {
        string id;
        using (var vault = Service()) id = vault.SaveLogin(Login());
        Sql($"UPDATE items SET secret_version = secret_version + 1 WHERE id = '{id}'");
        using var again = Service();
        Assert.Throws<VaultCorruptException>(() => again.GetLogin(id));
    }

    // A chave do cofre só sai da DPAPI deste utilizador com a mesma entropia; outra não abre (e não é substituída).
    [Fact]
    public void OtherEntropy_DoesNotOpen_AndTheKeyIsKept()
    {
        using (var vault = Service()) vault.SaveLogin(Login());
        var keyFile = Path.Combine(_root, VaultKey.FileName);
        var before = File.ReadAllBytes(keyFile);
        Assert.Throws<VaultUnavailableException>(() => VaultKey.LoadOrCreate(keyFile, new Dpapi("outro")));
        using (var other = new VaultService(_root, new Dpapi("outro"), () => _now))
            Assert.Throws<VaultUnavailableException>(() => other.Logins());
        Assert.Equal(before, File.ReadAllBytes(keyFile));
        using var vault2 = Service();
        Assert.Single(vault2.Logins());
    }

    [Fact]
    public void Key_IsRandom256Bits_AndWrapped()
    {
        var keyFile = Path.Combine(_root, VaultKey.FileName);
        var key = VaultKey.LoadOrCreate(keyFile, new Dpapi(VaultKey.Purpose));
        Assert.Equal(32, key.Length);
        Assert.Equal(key, VaultKey.LoadOrCreate(keyFile, new Dpapi(VaultKey.Purpose)));
        var disk = File.ReadAllBytes(keyFile);
        Assert.False(disk.AsSpan().IndexOf(key) >= 0);
        var other = Path.Combine(_root, "outra.key");
        Assert.NotEqual(key, VaultKey.LoadOrCreate(other, new Dpapi(VaultKey.Purpose)));
    }

    // Procura por origem sem a origem no disco: HMAC da origem (exata) e do domínio registável.
    [Fact]
    public void Suggestions_ByOriginHmac_ExactFirstThenSameSite()
    {
        using var vault = Service();
        vault.SaveLogin(Login() with { Origin = "https://www.exemplo.test", Username = "a@exemplo.test" });
        vault.SaveLogin(Login() with { Origin = "https://conta.exemplo.test", Username = "b@exemplo.test" });
        vault.SaveLogin(Login() with { Origin = "https://outro-site.test", Username = "c@exemplo.test" });
        vault.SaveLogin(Login() with { Origin = "http://conta.exemplo.test", Username = "d@exemplo.test" });
        vault.SaveLogin(Login() with { Origin = "android://AbC==@com.exemplo.app/", Username = "e@exemplo.test" });
        var found = vault.LoginsFor("https://conta.exemplo.test/entrar");
        Assert.Equal(["b@exemplo.test", "a@exemplo.test"], found.Select(f => f.Username));
        Assert.Equal([OriginMatch.Exact, OriginMatch.SameSite], found.Select(f => f.Match));
        Assert.Empty(vault.LoginsFor("https://exemplo-test.evil"));
        Assert.Equal(["d@exemplo.test"], vault.LoginsFor("http://conta.exemplo.test").Select(f => f.Username));
    }

    // Histórico local de uma versão: a palavra-passe anterior fica (só a última).
    [Fact]
    public void History_KeepsOnePreviousPassword()
    {
        using var vault = Service();
        var id = vault.SaveLogin(Login("Primeira-1!"));
        Assert.Null(vault.PreviousPassword(id));
        vault.SaveLogin(Login("Segunda-2!") with { Id = id });
        Assert.Equal("Primeira-1!", vault.PreviousPassword(id));
        vault.SaveLogin(Login("Terceira-3!") with { Id = id });
        Assert.Equal("Segunda-2!", vault.PreviousPassword(id));
        Assert.Equal("Terceira-3!", vault.GetLogin(id)!.Password);
        // Mudar só a nota não mexe no histórico.
        vault.SaveLogin(vault.GetLogin(id)! with { Note = "outra nota" });
        Assert.Equal("Segunda-2!", vault.PreviousPassword(id));
    }

    // Mesma origem e utilizador: é o mesmo item (atualiza, não duplica).
    [Fact]
    public void SameOriginAndUser_UpdatesTheSameItem()
    {
        using var vault = Service();
        var id = vault.SaveLogin(Login("Primeira-1!"));
        _now = _now.AddHours(1);
        Assert.Equal(id, vault.SaveLogin(Login("Segunda-2!")));
        var item = vault.GetLogin(id)!;
        Assert.Equal("Segunda-2!", item.Password);
        Assert.Equal(_now.AddHours(-1), item.Created);
        Assert.Equal(_now, item.Changed);
        Assert.Single(vault.Logins());
    }

    // Revisão: apagar tira a linha de vez (a sincronização vê o apagamento pela falta do item): nem lápide, nem quantos
    // itens se apagaram, nem quando. As lápides de versões anteriores saem ao abrir.
    [Fact]
    public void Delete_RemovesTheRow_AndOldTombstonesGoAway()
    {
        string id;
        using (var vault = Service())
        {
            id = vault.SaveLogin(Login());
            _now = _now.AddMinutes(5);
            Assert.True(vault.Delete(VaultCollection.Logins, id));
            Assert.Empty(vault.Logins());
            Assert.Null(vault.GetLogin(id));
            Assert.Empty(vault.LoginsFor("https://" + Host));
            Assert.Empty(vault.Tombstones(VaultCollection.Logins));
            Assert.False(vault.Delete(VaultCollection.Logins, id));
        }
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM items";
            Assert.Equal(0L, command.ExecuteScalar());
            // Uma lápide como as da versão anterior.
            command.CommandText = "INSERT INTO items(collection, id, created, changed, deleted) VALUES ('logins', 'antiga', 1, 1, 1)";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        using (var vault = Service())
            Assert.Empty(vault.Tombstones(VaultCollection.Logins));
    }

    // Revisão: a chave do cofre não fica em cópias que ninguém apaga (HKDF direto do span; DPAPI apaga os buffers nativos).
    [Fact]
    public void TheVaultKey_IsNotCopiedIntoUnclearedArrays()
    {
        var root = Eteris.Browser.Tests.Sync.GoogleSyncTests.FindRoot();
        var store = File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", "Vault", "VaultStore.cs"));
        Assert.DoesNotContain("vaultKey.ToArray()", store);
        var dpapi = File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", "Sync", "Dpapi.cs"));
        Assert.Contains("Zero(output.Data, output.Size);", dpapi);
        Assert.Contains("Zero(Data, Size);", dpapi);
    }

    [Fact]
    public void EraseAll_LeavesNothing()
    {
        using (var vault = Service())
        {
            vault.SaveLogin(Login());
            vault.Save(new CardItem("", null, Card, 1, 2030, null, default, null));
            vault.EraseAll();
            foreach (var collection in VaultCollections.All)
            {
                Assert.Empty(vault.List(collection));
                Assert.Empty(vault.Tombstones(collection));
            }
        }
        SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(Path.Combine(_root, VaultStore.FileName));
        using var vault2 = Service();
        Assert.Empty(vault2.Logins());
        Assert.True(bytes.Length < 64 * 1024);
    }

    // Cartões: o número só sai com GetCard; a lista tem a marca, os últimos quatro e a validade. Nunca um CVC.
    [Fact]
    public void Card_ListHasOnlyTheMask_AndTheNumberNeedsGet()
    {
        using var vault = Service();
        var id = vault.Save(new CardItem("", "Titular Inventado", "4111 1111 1111 1111", 8, 2029, "Pessoal", default, null));
        var entry = Assert.Single(vault.List(VaultCollection.Cards));
        Assert.Equal("Visa", entry.Meta["brand"]!.GetValue<string>());
        Assert.Equal("1111", entry.Meta["last4"]!.GetValue<string>());
        Assert.False(entry.Meta.ContainsKey("number"));
        Assert.Equal(Card, vault.GetCard(id)!.Number);
        Assert.Throws<ArgumentException>(() => vault.Save(new CardItem("", null, "4111 1111 1111 1112", 8, 2029, null, default, null)));
    }

    [Fact]
    public void OtherCollections_RoundTrip()
    {
        using var vault = Service();
        var iban = vault.Save(new IbanItem("", "pt50 0002 0123 1234 5678 9015 4", "Conta", default));
        Assert.Equal(Iban, vault.GetIban(iban)!.Iban);
        Assert.Equal("PT50 •••• 0154", vault.List(VaultCollection.Ibans).Single().Meta["masked"]!.GetValue<string>());
        var doc = vault.Save(new IdDocItem("", IdDocType.TaxId, "529.982.247-25", "Titular", "BR", null, null, new DateOnly(2030, 1, 31), "n"));
        var docBack = vault.GetIdDoc(doc)!;
        Assert.Equal(Cpf, docBack.Number);
        Assert.Equal(new DateOnly(2030, 1, 31), docBack.Expires);
        Assert.Equal("taxId", vault.List(VaultCollection.IdDocs).Single().Meta["type"]!.GetValue<string>());
        var contact = vault.Save(new ContactItem("", "Casa", "Nome", "Apelido", null, "Rua", "1", "2.º Esq.", "Bairro", "Lisboa", "Lisboa", "1000-001", "PT", "a@exemplo.test", "+351 210 000 000", default, null));
        Assert.Equal("2.º Esq.", vault.GetContact(contact)!.Complement);
        var loyalty = vault.Save(TravelItem.Loyalty("", "Companhia", "Programa", "123456789", "Nome"));
        Assert.Equal("123456789", vault.GetTravel(loyalty)!.Number);
        Assert.False(vault.List(VaultCollection.Travel).Single().Meta.ContainsKey("number"));
    }

    [Fact]
    public void NeverSave_IsByOrigin()
    {
        using var vault = Service();
        vault.AddNeverSave("https://nunca.exemplo.test/entrar");
        Assert.True(vault.IsNeverSave("https://nunca.exemplo.test/outra"));
        Assert.False(vault.IsNeverSave("https://outro.nunca.exemplo.test"));
        Assert.Equal(["https://nunca.exemplo.test"], vault.NeverSaveSites().Select(s => s.Origin));
        vault.RemoveNeverSave("https://nunca.exemplo.test");
        Assert.False(vault.IsNeverSave("https://nunca.exemplo.test"));
    }

    // A abertura é preguiçosa: construir o serviço não toca no disco; o arranque do Éteris nem o constrói.
    [Fact]
    public void Opening_IsLazy()
    {
        using (var vault = Service())
        {
            Assert.False(vault.IsOpen);
            Assert.False(File.Exists(Path.Combine(_root, VaultStore.FileName)));
            Assert.False(File.Exists(Path.Combine(_root, VaultKey.FileName)));
            vault.Logins();
            Assert.True(vault.IsOpen);
        }
        var root = GoogleSyncTests.FindRoot();
        foreach (var startup in new[] { "App.xaml.cs", "MainWindow.xaml.cs", "Program.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", startup));
            foreach (var name in new[] { "VaultService", "VaultStore", "VaultKey", "cofre." })
                Assert.DoesNotContain(name, source);
        }
    }

    // Nada do cofre vai para registos: nenhum Debug/Trace/Console/StartupTrace em Vault/.
    [Fact]
    public void VaultSources_NeverLog()
    {
        var folder = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Vault");
        foreach (var file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (var call in new[] { "Debug.", "Trace.", "Console.", "StartupTrace.", "Logger" })
                Assert.False(source.Contains(call, StringComparison.Ordinal), $"{Path.GetFileName(file)} usa {call}");
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, VaultStore.FileName), Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private void Sql(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
