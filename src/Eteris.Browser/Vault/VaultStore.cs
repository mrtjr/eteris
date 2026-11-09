using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Vault;

// Um blob que não passa a verificação (trocado de item, de campo, de coleção, de versão, ou outra chave).
public sealed class VaultCorruptException(string message, Exception? inner = null) : Exception(message, inner);

// Os metadados de um item (o que as listas mostram sem decifrar segredos).
public sealed record VaultEntry(VaultCollection Collection, string Id, JsonObject Meta, DateTimeOffset Created, DateTimeOffset Changed, bool HasSecret)
{
    public override string ToString() => $"VaultEntry {Collection} {Id}";
}

// O cofre no disco (docs/palavras-passe.md, A2): <raiz>\cofre.db (SQLite), separado do eteris.db e fora da pasta do
// Chromium. Cada item tem os metadados (meta_ct) e o segredo (secret_ct) cifrados em separado com AES-256-GCM e uma
// chave derivada da chave do cofre; dados associados «eteris-cofre|v1|coleção|id|campo|versão» (um blob trocado de
// item, de campo ou de versão não abre). A origem nunca fica em claro: a procura é por HMAC da origem (origin_key) e do
// domínio registável (site_key). Uma versão anterior do segredo (prev_ct, histórico local de 1 versão). Apagar deixa
// uma lápide sem dados (para a sincronização). secure_delete: o SQLite escreve zeros por cima do que apaga.
public sealed class VaultStore : IDisposable
{
    public const string FileName = "cofre.db";

    private const byte Format = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _connectionString;
    private readonly byte[] _encKey;
    private readonly byte[] _idxKey;

    public VaultStore(string path, ReadOnlySpan<byte> vaultKey)
    {
        if (vaultKey.Length != VaultKey.Length) throw new ArgumentException("A chave do cofre tem 256 bits.", nameof(vaultKey));
        _encKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, vaultKey.ToArray(), 32, salt: null, info: "Eteris.Cofre:enc"u8.ToArray());
        _idxKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, vaultKey.ToArray(), 32, salt: null, info: "Eteris.Cofre:idx"u8.ToArray());
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS items(
                collection TEXT NOT NULL,
                id TEXT NOT NULL,
                origin_key BLOB,
                site_key BLOB,
                meta_ct BLOB,
                meta_version INTEGER NOT NULL DEFAULT 0,
                secret_ct BLOB,
                secret_version INTEGER NOT NULL DEFAULT 0,
                prev_ct BLOB,
                prev_version INTEGER NOT NULL DEFAULT 0,
                created INTEGER NOT NULL,
                changed INTEGER NOT NULL,
                deleted INTEGER,
                PRIMARY KEY (collection, id));
            CREATE INDEX IF NOT EXISTS items_origin ON items(collection, origin_key);
            CREATE INDEX IF NOT EXISTS items_site ON items(collection, site_key);
            PRAGMA user_version = 1;
            """);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_encKey);
        CryptographicOperations.ZeroMemory(_idxKey);
    }

    // Grava (ou substitui) um item. indexOrigin: a origem normalizada para as procuras (só palavras-passe e «nunca»).
    // keepPrevious: o segredo atual passa a «anterior» (histórico de 1 versão) antes de ser substituído.
    public void Put(VaultCollection collection, string id, string? indexOrigin, JsonObject meta, JsonObject? secret, DateTimeOffset now, bool keepPrevious = false)
    {
        var name = VaultCollections.Name(collection);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        long created = now.ToUnixTimeMilliseconds(), metaVersion = 0, secretVersion = 0, prevVersion = 0;
        byte[]? oldSecret = null, prev = null;
        bool alive = false;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT created, meta_version, secret_version, prev_version, secret_ct, prev_ct, deleted IS NULL FROM items WHERE collection = $c AND id = $id";
            read.Parameters.AddWithValue("$c", name);
            read.Parameters.AddWithValue("$id", id);
            using var r = read.ExecuteReader();
            if (r.Read())
            {
                alive = r.GetBoolean(6);
                if (alive) created = r.GetInt64(0);
                (metaVersion, secretVersion, prevVersion) = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
                oldSecret = r.IsDBNull(4) ? null : (byte[])r[4];
                prev = r.IsDBNull(5) ? null : (byte[])r[5];
            }
        }
        if (keepPrevious && alive && oldSecret is not null)
        {
            var plain = Decrypt(oldSecret, name, id, "secret", secretVersion);
            try { prev = Encrypt(plain, name, id, "prev", ++prevVersion); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        else if (!alive)
        {
            prev = null;
        }
        using var write = connection.CreateCommand();
        write.Transaction = tx;
        write.CommandText = """
            INSERT INTO items(collection, id, origin_key, site_key, meta_ct, meta_version, secret_ct, secret_version, prev_ct, prev_version, created, changed, deleted)
            VALUES ($c, $id, $o, $s, $m, $mv, $x, $xv, $p, $pv, $created, $changed, NULL)
            ON CONFLICT(collection, id) DO UPDATE SET origin_key = $o, site_key = $s, meta_ct = $m, meta_version = $mv, secret_ct = $x,
                secret_version = $xv, prev_ct = $p, prev_version = $pv, created = $created, changed = $changed, deleted = NULL
            """;
        write.Parameters.AddWithValue("$c", name);
        write.Parameters.AddWithValue("$id", id);
        write.Parameters.AddWithValue("$o", indexOrigin is null ? DBNull.Value : OriginKey(indexOrigin));
        write.Parameters.AddWithValue("$s", indexOrigin is not null && Origins.Site(indexOrigin) is { } site ? SiteKey(site) : DBNull.Value);
        write.Parameters.AddWithValue("$m", EncryptJson(meta, name, id, "meta", ++metaVersion));
        write.Parameters.AddWithValue("$mv", metaVersion);
        write.Parameters.AddWithValue("$x", secret is null ? DBNull.Value : EncryptJson(secret, name, id, "secret", ++secretVersion));
        write.Parameters.AddWithValue("$xv", secretVersion);
        write.Parameters.AddWithValue("$p", prev is null ? DBNull.Value : prev);
        write.Parameters.AddWithValue("$pv", prevVersion);
        write.Parameters.AddWithValue("$created", created);
        write.Parameters.AddWithValue("$changed", now.ToUnixTimeMilliseconds());
        write.ExecuteNonQuery();
        tx.Commit();
    }

    // Só os metadados (por exemplo, a «última utilização»): o segredo nem se decifra.
    public bool UpdateMeta(VaultCollection collection, string id, JsonObject meta, DateTimeOffset now)
    {
        var name = VaultCollections.Name(collection);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        long version;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT meta_version FROM items WHERE collection = $c AND id = $id AND deleted IS NULL";
            read.Parameters.AddWithValue("$c", name);
            read.Parameters.AddWithValue("$id", id);
            if (read.ExecuteScalar() is not long v) return false;
            version = v + 1;
        }
        using var write = connection.CreateCommand();
        write.Transaction = tx;
        write.CommandText = "UPDATE items SET meta_ct = $m, meta_version = $mv, changed = $changed WHERE collection = $c AND id = $id";
        write.Parameters.AddWithValue("$c", name);
        write.Parameters.AddWithValue("$id", id);
        write.Parameters.AddWithValue("$m", EncryptJson(meta, name, id, "meta", version));
        write.Parameters.AddWithValue("$mv", version);
        write.Parameters.AddWithValue("$changed", now.ToUnixTimeMilliseconds());
        write.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public VaultEntry? Get(VaultCollection collection, string id) => Query(collection, "AND id = $id", ("$id", id)).FirstOrDefault();

    public IReadOnlyList<VaultEntry> List(VaultCollection collection) => Query(collection, string.Empty);

    public JsonObject? ReadSecret(VaultCollection collection, string id) => ReadBlob(collection, id, "secret");

    public JsonObject? ReadPrevious(VaultCollection collection, string id) => ReadBlob(collection, id, "prev");

    public IReadOnlyList<string> FindByOrigin(VaultCollection collection, string origin) => Ids(collection, "origin_key", OriginKey(origin));

    public IReadOnlyList<string> FindBySite(VaultCollection collection, string site) => Ids(collection, "site_key", SiteKey(site));

    // Lápide: fica o ID e a hora (para a sincronização mandar o apagamento); os dados e os índices saem.
    public bool Delete(VaultCollection collection, string id, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE items SET meta_ct = NULL, secret_ct = NULL, prev_ct = NULL, origin_key = NULL, site_key = NULL, deleted = $now, changed = $now
            WHERE collection = $c AND id = $id AND deleted IS NULL
            """;
        command.Parameters.AddWithValue("$c", VaultCollections.Name(collection));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<(string Id, DateTimeOffset Deleted)> Tombstones(VaultCollection collection)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, deleted FROM items WHERE collection = $c AND deleted IS NOT NULL ORDER BY rowid";
        command.Parameters.AddWithValue("$c", VaultCollections.Name(collection));
        using var r = command.ExecuteReader();
        var result = new List<(string, DateTimeOffset)>();
        while (r.Read()) result.Add((r.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1))));
        return result;
    }

    // A lápide já foi enviada: sai de vez.
    public void Purge(VaultCollection collection, string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM items WHERE collection = $c AND id = $id AND deleted IS NOT NULL";
        command.Parameters.AddWithValue("$c", VaultCollections.Name(collection));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // «Apagar tudo»: nenhum item nem lápide; o ficheiro encolhe (VACUUM) e o que estava escrito fica a zeros.
    public void EraseAll()
    {
        using var connection = Open();
        Execute(connection, "DELETE FROM items; VACUUM;");
    }

    private IReadOnlyList<VaultEntry> Query(VaultCollection collection, string filter, params (string Name, object Value)[] args)
    {
        var name = VaultCollections.Name(collection);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, meta_ct, meta_version, created, changed, secret_ct IS NOT NULL FROM items WHERE collection = $c AND deleted IS NULL {filter} ORDER BY rowid";
        command.Parameters.AddWithValue("$c", name);
        foreach (var (n, v) in args) command.Parameters.AddWithValue(n, v);
        using var r = command.ExecuteReader();
        var result = new List<VaultEntry>();
        while (r.Read())
        {
            var id = r.GetString(0);
            var meta = DecryptJson((byte[])r[1], name, id, "meta", r.GetInt64(2));
            result.Add(new VaultEntry(collection, id, meta, DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)), r.GetBoolean(5)));
        }
        return result;
    }

    private JsonObject? ReadBlob(VaultCollection collection, string id, string field)
    {
        var name = VaultCollections.Name(collection);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {field}_ct, {field}_version FROM items WHERE collection = $c AND id = $id AND deleted IS NULL";
        command.Parameters.AddWithValue("$c", name);
        command.Parameters.AddWithValue("$id", id);
        using var r = command.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0)) return null;
        return DecryptJson((byte[])r[0], name, id, field, r.GetInt64(1));
    }

    private IReadOnlyList<string> Ids(VaultCollection collection, string column, byte[] key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM items WHERE collection = $c AND {column} = $k AND deleted IS NULL ORDER BY rowid";
        command.Parameters.AddWithValue("$c", VaultCollections.Name(collection));
        command.Parameters.AddWithValue("$k", key);
        using var r = command.ExecuteReader();
        var result = new List<string>();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    }

    private byte[] OriginKey(string origin) => HMACSHA256.HashData(_idxKey, Encoding.UTF8.GetBytes("o|" + origin));

    private byte[] SiteKey(string site) => HMACSHA256.HashData(_idxKey, Encoding.UTF8.GetBytes("s|" + site));

    private byte[] EncryptJson(JsonObject value, string collection, string id, string field, long version)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try { return Encrypt(plain, collection, id, field, version); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private JsonObject DecryptJson(byte[] blob, string collection, string id, string field, long version)
    {
        var plain = Decrypt(blob, collection, id, field, version);
        try { return JsonNode.Parse(plain) as JsonObject ?? throw new VaultCorruptException("Um item do cofre não é um objeto."); }
        catch (JsonException e) { throw new VaultCorruptException("Um item do cofre não se lê.", e); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] Associated(string collection, string id, string field, long version)
        => Encoding.UTF8.GetBytes($"eteris-cofre|v1|{collection}|{id}|{field}|{version}");

    // [formato][nonce 12][etiqueta 16][texto cifrado]
    private byte[] Encrypt(byte[] plain, string collection, string id, string field, long version)
    {
        var blob = new byte[1 + NonceSize + TagSize + plain.Length];
        blob[0] = Format;
        var nonce = blob.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_encKey, TagSize);
        aes.Encrypt(nonce, plain, blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize), Associated(collection, id, field, version));
        return blob;
    }

    private byte[] Decrypt(byte[] blob, string collection, string id, string field, long version)
    {
        if (blob.Length < 1 + NonceSize + TagSize || blob[0] != Format) throw new VaultCorruptException("Um item do cofre está estragado.");
        var plain = new byte[blob.Length - 1 - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(_encKey, TagSize);
            aes.Decrypt(blob.AsSpan(1, NonceSize), blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize), plain, Associated(collection, id, field, version));
            return plain;
        }
        catch (CryptographicException e)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new VaultCorruptException("Um item do cofre não passa a verificação.", e);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA secure_delete = ON;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
