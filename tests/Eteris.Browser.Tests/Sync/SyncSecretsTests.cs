using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Eteris.Sync.Crypto;
using Eteris.Sync.Engine;
using Eteris.Sync.Model;
using Eteris.Sync.Secrets;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// D1 (docs/palavras-passe.md, docs/sincronizacao.md «Coleções secretas»): o selo de cada registo secreto, por dentro da
// cifra do ficheiro. K_seg = HKDF(DEK, "secrets:v1"), K_skey = HKDF(DEK, "secret-keys:v1"); Key opaca
// "s:" + HMAC(K_skey, coleção ‖ chave natural); Data = {"v":1,"n":…,"c":…}; dados associados
// eteris-secret|v1|cadeia|geração|coleção|Id.
public class SyncSecretsTests
{
    private static readonly JsonObject Login = new()
    {
        ["origin"] = "https://contas.exemplo.pt", ["username"] = "maria.teste", ["password"] = "Segredo-Inventado-42",
        ["note"] = "nota inventada", ["created"] = "2026-11-01T10:00:00.000Z",
    };

    private static KeyRing Ring(out AccountSecret secret)
    {
        secret = AccountSecret.Create();
        return KeyRing.Create(secret, 1000);
    }

    [Fact]
    public void Collections_TheSevenSecretOnes_AreKnownAndMarked()
    {
        Assert.Equal(["logins", "loginsNever", "cards", "ibans", "contacts", "idDocs", "travel"], Collections.Secret);
        foreach (var name in Collections.Secret)
        {
            Assert.True(Collections.IsSecret(name));
            Assert.Contains(name, Collections.All);
            Assert.True(SecretSchemas.Fields.ContainsKey(name), name);
        }
        foreach (var name in new[] { Collections.Bookmarks, Collections.Spaces, Collections.Tabs, Collections.Settings, Collections.Shield, Collections.Notes })
            Assert.False(Collections.IsSecret(name));
    }

    [Fact]
    public void Schemas_AreTheOnesOfTheDesign_AndNeverHaveTheSecurityCode()
    {
        Assert.Equal(["origin", "username", "password", "note", "created", "changed", "used", "formAction", "generated", "realm"], SecretSchemas.Fields["logins"].Keys);
        Assert.Equal(["origin", "created"], SecretSchemas.Fields["loginsNever"].Keys);
        Assert.Equal(["name", "number", "expMonth", "expYear", "nickname", "created", "used"], SecretSchemas.Fields["cards"].Keys);
        Assert.Equal(["iban", "nickname", "created"], SecretSchemas.Fields["ibans"].Keys);
        Assert.Equal(["label", "givenName", "familyName", "organization", "street", "number", "complement", "neighborhood", "city", "region",
            "postalCode", "country", "email", "phone", "created", "used"], SecretSchemas.Fields["contacts"].Keys);
        Assert.Equal(["type", "number", "name", "country", "issuer", "issued", "expires", "note"], SecretSchemas.Fields["idDocs"].Keys);
        Assert.Equal(["type", "make", "model", "year", "owner", "plate", "plateRegion", "vin", "company", "program", "number", "name"],
            SecretSchemas.Fields["travel"].Keys);
        foreach (var (_, fields) in SecretSchemas.Fields)
            foreach (var field in fields.Keys)
                Assert.False(SecretSchemas.IsForbidden(field), field);
        foreach (var bad in new[] { "cvc", "CVV", "csc", "cvv2", "card_cvc", "securityCode", "cid", "cvn" })
            Assert.True(SecretSchemas.IsForbidden(bad), bad);
        // O cofre do navegador usa a mesma regra.
        Assert.True(Eteris.Browser.Vault.VaultFields.IsForbidden("cvc2"));
        Assert.Equal(SecretSchemas.IsForbidden("numero"), Eteris.Browser.Vault.VaultFields.IsForbidden("numero"));
    }

    [Fact]
    public void Canonical_KeepsOnlyKnownFields_InOrder_WithoutNulls_AndChecksTheTypes()
    {
        var a = new JsonObject { ["year"] = 2020, ["type"] = "vehicle", ["make"] = "Marca", ["model"] = null, ["futuro"] = "x" };
        var b = new JsonObject { ["make"] = "Marca", ["type"] = "vehicle", ["year"] = 2020 };
        Assert.Equal(SecretSchemas.Canonical("travel", a), SecretSchemas.Canonical("travel", b));
        Assert.DoesNotContain("futuro", SecretSchemas.Canonical("travel", a));
        Assert.True(SecretSchemas.IsValid("travel", a));
        Assert.False(SecretSchemas.IsValid("travel", new JsonObject { ["year"] = "2020" }));
        Assert.False(SecretSchemas.IsValid("cards", new JsonObject { ["expMonth"] = "08" }));
        Assert.False(SecretSchemas.IsValid("logins", new JsonObject { ["generated"] = "sim" }));
        Assert.True(SecretSchemas.IsValid("logins", new JsonObject { ["generated"] = true, ["origin"] = "https://a.pt" }));
    }

    [Fact]
    public void KeyRing_SecretKeys_ComeFromTheDek_AndAreStableBetweenDevices()
    {
        var ring = Ring(out var secret);
        using (secret)
        {
            var envelope = KeyEnvelope.Parse(ring.Envelope.ToBytes());
            using var again = AccountSecret.FromCode(secret.ToCode());
            var other = KeyRing.Open(again, envelope);
            Assert.Equal(ring.SecretKey, other.SecretKey);
            Assert.Equal(ring.SecretKeyId, other.SecretKeyId);
            Assert.NotEqual(ring.SecretKey, ring.SecretKeyId);
            Assert.Equal(32, ring.SecretKey.Length);

            var key = ring.Seals.OpaqueKey("logins", "https://contas.exemplo.pt\nmaria.teste");
            Assert.Matches("^s:[0-9a-f]{32}$", key);
            Assert.Equal(key, other.Seals.OpaqueKey("logins", "https://contas.exemplo.pt\nmaria.teste"));
            Assert.NotEqual(key, ring.Seals.OpaqueKey("cards", "https://contas.exemplo.pt\nmaria.teste"));
            Assert.NotEqual(key, ring.Seals.OpaqueKey("logins", "https://contas.exemplo.pt\nmaria"));
            // Outra conta (ou nova cadeia): outra chave.
            var third = Ring(out var s3);
            using (s3) Assert.NotEqual(key, third.Seals.OpaqueKey("logins", "https://contas.exemplo.pt\nmaria.teste"));
        }
    }

    [Fact]
    public void Seal_RoundTrips_AndHidesEverything()
    {
        var ring = Ring(out var secret);
        using (secret)
        {
            var id = ring.OpaqueId("logins", ring.Seals.OpaqueKey("logins", "k"));
            var sealedData = ring.Seals.Seal("logins", id, new SecretItem("https://contas.exemplo.pt\nmaria.teste", Login));
            Assert.Equal(["v", "n", "c"], sealedData.Select(p => p.Key));
            Assert.Equal(1, sealedData["v"]!.GetValue<int>());
            var text = sealedData.ToJsonString();
            foreach (var plain in new[] { "contas.exemplo", "maria", "Segredo", "inventada", "origin", "password" })
                Assert.DoesNotContain(plain, text, StringComparison.OrdinalIgnoreCase);

            var opened = ring.Seals.Open("logins", id, sealedData);
            Assert.Equal("https://contas.exemplo.pt\nmaria.teste", opened.NaturalKey);
            Assert.Equal(CanonicalJson.Of(Login), CanonicalJson.Of(opened.Data));

            // Dois selos do mesmo conteúdo são diferentes (nonce aleatório).
            Assert.NotEqual(text, ring.Seals.Seal("logins", id, new SecretItem("https://contas.exemplo.pt\nmaria.teste", Login)).ToJsonString());
        }
    }

    [Fact]
    public void Seal_EveryProtectedByte_IsChecked()
    {
        var ring = Ring(out var secret);
        using (secret)
        {
            var sealedData = ring.Seals.Seal("cards", "id1", new SecretItem("c1", new JsonObject { ["number"] = "4111111111111111" }));
            var nonce = Kdf.FromBase64Url(sealedData["n"]!.GetValue<string>());
            var cipher = Kdf.FromBase64Url(sealedData["c"]!.GetValue<string>());
            Assert.Equal(12, nonce.Length);
            for (var i = 0; i < cipher.Length; i++)
            {
                var bad = (byte[])cipher.Clone();
                bad[i] ^= 0x01;
                var tampered = new JsonObject { ["v"] = 1, ["n"] = sealedData["n"]!.GetValue<string>(), ["c"] = Kdf.Base64Url(bad) };
                Assert.Throws<SecretSealException>(() => ring.Seals.Open("cards", "id1", tampered));
            }
            for (var i = 0; i < nonce.Length; i++)
            {
                var bad = (byte[])nonce.Clone();
                bad[i] ^= 0x80;
                var tampered = new JsonObject { ["v"] = 1, ["n"] = Kdf.Base64Url(bad), ["c"] = sealedData["c"]!.GetValue<string>() };
                Assert.Throws<SecretSealException>(() => ring.Seals.Open("cards", "id1", tampered));
            }
            // Versão desconhecida, campos em falta, lixo: não abre (nunca uma exceção de outro tipo).
            Assert.Throws<SecretSealException>(() => ring.Seals.Open("cards", "id1", new JsonObject { ["v"] = 2, ["n"] = sealedData["n"]!.GetValue<string>(), ["c"] = sealedData["c"]!.GetValue<string>() }));
            Assert.Throws<SecretSealException>(() => ring.Seals.Open("cards", "id1", new JsonObject { ["v"] = 1 }));
            Assert.Throws<SecretSealException>(() => ring.Seals.Open("cards", "id1", new JsonObject { ["title"] = "x" }));
            Assert.Throws<SecretSealException>(() => ring.Seals.Open("cards", "id1", null));
            Assert.Throws<SecretSealException>(() => ring.Seals.Open("cards", "id1", new JsonObject { ["v"] = 1, ["n"] = "!!", ["c"] = "??" }));
        }
    }

    [Fact]
    public void Seal_IsBoundToId_Collection_Chain_AndGeneration()
    {
        var ring = Ring(out var secret);
        using (secret)
        {
            var sealedData = ring.Seals.Seal("logins", "id1", new SecretItem("k", Login));
            Assert.Throws<SecretSealException>(() => ring.Seals.Open("logins", "id2", sealedData));
            Assert.Throws<SecretSealException>(() => ring.Seals.Open("cards", "id1", sealedData));
            var otherChain = ring.Seals with { Chain = "0011223344556677" };
            Assert.Throws<SecretSealException>(() => otherChain.Open("logins", "id1", sealedData));
            var otherGeneration = ring.Seals with { Generation = 2 };
            Assert.Throws<SecretSealException>(() => otherGeneration.Open("logins", "id1", sealedData));
            // Outra conta (chave diferente) também não.
            var other = Ring(out var s2);
            using (s2) Assert.Throws<SecretSealException>(() => (other.Seals with { Chain = ring.ChainId }).Open("logins", "id1", sealedData));
            Assert.Equal("Segredo-Inventado-42", ring.Seals.Open("logins", "id1", sealedData).Data["password"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Seal_RefusesTheSecurityCode_AndTooBigRecords()
    {
        var ring = Ring(out var secret);
        using (secret)
        {
            var card = new JsonObject { ["number"] = "4111111111111111", ["cvc"] = "123" };
            var e = Assert.Throws<ArgumentException>(() => ring.Seals.Seal("cards", "id", new SecretItem("c", card)));
            Assert.DoesNotContain("123", e.Message);
            Assert.Throws<ArgumentException>(() => ring.Seals.Seal("cards", "id", new SecretItem("c",
                new JsonObject { ["number"] = "4111111111111111", ["extra"] = new JsonObject { ["card_CVV2"] = "1" } })));
            Assert.Throws<ArgumentException>(() => ring.Seals.Seal("cards", "id", new SecretItem("c",
                new JsonObject { ["lista"] = new JsonArray(new JsonObject { ["securityCode"] = "1" }) })));

            var big = new JsonObject { ["note"] = new string('x', SecretSeal.MaxBytes) };
            Assert.Throws<ArgumentException>(() => ring.Seals.Seal("logins", "id", new SecretItem("k", big)));
            var fits = new JsonObject { ["note"] = new string('x', SecretSeal.MaxBytes - 200) };
            Assert.Equal(SecretSeal.MaxBytes - 200, ring.Seals.Open("logins", "id", ring.Seals.Seal("logins", "id", new SecretItem("k", fits))).Data["note"]!.GetValue<string>().Length);
        }
    }

    [Fact]
    public async Task Engine_PutSecret_TravelsSealed_AndOnlyTheAccountOpensIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "eteris-secrets-" + Guid.NewGuid().ToString("N"));
        try
        {
            long now = 1_762_560_000_000;
            using var secret = AccountSecret.Create();
            var a = await SyncEngine.CreateAsync(new FolderTransport(folder), secret, DeviceIdentity.Create("A"), () => now);
            var natural = "https://contas.exemplo.pt\nmaria.teste";
            a.PutSecret("logins", new SecretItem(natural, Login));
            a.PutSecret("cards", new SecretItem("c0ffee", new JsonObject { ["number"] = "4111111111111111", ["expMonth"] = 8, ["expYear"] = 2029 }));
            var record = a.Get("logins", a.SecretKey("logins", natural))!;
            Assert.StartsWith("s:", record.Key);
            Assert.DoesNotContain("contas", record.Key);
            await a.PushAsync();
            await a.CompactAsync();

            using var again = AccountSecret.FromCode(secret.ToCode());
            var b = await SyncEngine.JoinAsync(new FolderTransport(folder), again, DeviceIdentity.Create("B"), () => now);
            await b.PullAsync();
            var got = b.OpenSecret(b.Get("logins", b.SecretKey("logins", natural))!)!;
            Assert.Equal(natural, got.NaturalKey);
            Assert.Equal("Segredo-Inventado-42", got.Data["password"]!.GetValue<string>());

            // Nada em claro no transporte, no estado do motor (o que o navegador guarda com DPAPI) nem nos lotes.
            var state = Encoding.UTF8.GetString(b.State.ToBytes()) + Encoding.UTF8.GetString(a.State.ToBytes());
            foreach (var plain in new[] { "contas.exemplo", "maria.teste", "Segredo-Inventado", "4111111111111111", "inventada" })
            {
                Assert.DoesNotContain(plain, state, StringComparison.Ordinal);
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    Assert.DoesNotContain(plain, Encoding.UTF8.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
            }

            // Um selo mudado de registo (outro Id) não abre: o registo fica de fora (null), sem exceção.
            var moved = record with { Id = "0123456789abcdef0123456789abcdef" };
            Assert.Null(b.OpenSecret(moved));
            Assert.Null(b.OpenSecret(record with { Key = b.SecretKey("logins", "https://outro.pt\nmaria.teste") }));

            // Campos de um Éteris mais recente ficam no selo quando este aparelho muda o registo.
            var withFuture = (JsonObject)Login.DeepClone();
            withFuture["futuro"] = "fica";
            b.PutSecret("logins", new SecretItem(natural, withFuture));
            b.PutSecret("logins", new SecretItem(natural, Login));
            Assert.Equal("fica", b.OpenSecret(b.Get("logins", b.SecretKey("logins", natural))!)!.Data["futuro"]!.GetValue<string>());

            // Apagar deixa uma lápide que ainda sabe a chave natural (para a nova cadeia a levar).
            b.DeleteSecret("logins", natural);
            var tomb = b.Get("logins", b.SecretKey("logins", natural))!;
            Assert.True(tomb.Deleted);
            Assert.Equal(natural, b.OpenSecret(tomb)!.NaturalKey);
            Assert.DoesNotContain("Segredo", tomb.Data!.ToJsonString());
            Assert.Throws<ArgumentException>(() => b.PutSecret("bookmarks", new SecretItem("x", new JsonObject())));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }
}
