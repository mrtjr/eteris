using System.IO;
using System.Text.Json.Nodes;
using Eteris.Browser.Data;
using Eteris.Browser.Sync;
using Eteris.Browser.Vault;
using Eteris.Sync.Engine;
using Eteris.Sync.Model;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Tests.Sync;

// O perfil do Éteris visto pela sincronização (BrowserProfile): as regras (o que é ilegível, o que se valida, o que conta
// como aplicado) provam-se com um duplo em memória (IProfileData); o AppData, o Settings e o SiteRules são estáticos e ficam
// presos ao perfil do processo, por isso as chamadas vivas (LiveProfileData) ficam fixadas por texto e pela prova ao vivo.
public sealed class BrowserProfileTests
{
    [Fact]
    public void Read_WhatDoesNotRead_IsUnreadable_NotEmpty()
    {
        var file = Path.Combine(Path.GetTempPath(), "eteris-perfil-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            File.WriteAllText(file, new string('x', 4096));
            var data = new MemoryProfileData
            {
                BookmarksReader = () => new BookmarkStore(file).All(100_000),
                Unreadable = true,
            };
            var read = new BrowserProfile(data).Read();
            Assert.Equal(new[] { Collections.Bookmarks, Collections.Shield, Collections.Notes, Collections.Spaces }, read.Unreadable);
            Assert.All(read.Items, item => Assert.Equal(Collections.Settings, item.Collection));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    [Fact]
    public void Live_UsesTheReadsThatThrow()
    {
        var text = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Sync", "BrowserProfile.cs"));
        Assert.Contains("AppData.ReadBookmarks(", text, StringComparison.Ordinal);
        Assert.Contains("AppData.ReadHttpExceptions()", text, StringComparison.Ordinal);
        Assert.Contains("Shields.SiteRules.ReadAll()", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AppData.Bookmarks(", text, StringComparison.Ordinal);
        // O BrowserProfile só fala com o perfil por IProfileData: as chamadas vivas ficam no LiveProfileData.
        var rules = text[..text.IndexOf("internal sealed class LiveProfileData", StringComparison.Ordinal)];
        Assert.DoesNotContain("MainWindow.Apply", rules.Replace("MainWindow.ApplySynced", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("AppData.Set", rules, StringComparison.Ordinal);
    }

    private static LocalChange Put(string collection, string key, JsonObject? data = null) => new(collection, key, data ?? new JsonObject());

    private static LocalChange Remove(string collection, string key) => new(collection, key, null);

    [Fact]
    public void Apply_ThenRead_GivesBackEveryCollection()
    {
        var data = new MemoryProfileData();
        var profile = new BrowserProfile(data);
        var applied = profile.Apply(
        [
            Put(Collections.Bookmarks, "https://exemplo.test/a", new JsonObject { ["title"] = "A" }),
            Put(Collections.Shield, "off|exemplo.test|"),
            Put(Collections.Shield, "strict|exemplo.test|"),
            Put(Collections.Shield, "http|exemplo.test|"),
            Put(Collections.Shield, "hide|exemplo.test|.anuncio"),
            Put(Collections.Shield, "rule|exemplo.test|rastreador.test"),
            Put(Collections.Settings, "httpsFirst", new JsonObject { ["value"] = "false" }),
        ]);
        Assert.Equal(7, applied.Count);
        var items = profile.Read().Items;
        Assert.Equal("A", items.Single(i => i.Collection == Collections.Bookmarks).Data["title"]!.GetValue<string>());
        foreach (var key in new[] { "off|exemplo.test|", "strict|exemplo.test|", "http|exemplo.test|", "hide|exemplo.test|.anuncio", "rule|exemplo.test|rastreador.test" })
            Assert.Contains(items, i => i.Collection == Collections.Shield && i.Key == key);
        Assert.Equal("false", items.Single(i => i.Collection == Collections.Settings && i.Key == "httpsFirst").Data["value"]!.GetValue<string>());

        // O mesmo favorito com outro título muda-o; um título de 400 caracteres fica com 300.
        profile.Apply([Put(Collections.Bookmarks, "https://exemplo.test/a", new JsonObject { ["title"] = "B" })]);
        Assert.Equal("B", profile.Read().Items.Single(i => i.Collection == Collections.Bookmarks).Data["title"]!.GetValue<string>());
        profile.Apply([Put(Collections.Bookmarks, "https://exemplo.test/a", new JsonObject { ["title"] = new string('t', 400) })]);
        Assert.Equal(300, profile.Read().Items.Single(i => i.Collection == Collections.Bookmarks).Data["title"]!.GetValue<string>().Length);

        // As remoções: o favorito e os cinco do Escudo saem (6); a remoção de uma definição não se aplica.
        var removed = profile.Apply(
        [
            Remove(Collections.Bookmarks, "https://exemplo.test/a"),
            Remove(Collections.Shield, "off|exemplo.test|"),
            Remove(Collections.Shield, "strict|exemplo.test|"),
            Remove(Collections.Shield, "http|exemplo.test|"),
            Remove(Collections.Shield, "hide|exemplo.test|.anuncio"),
            Remove(Collections.Shield, "rule|exemplo.test|rastreador.test"),
            Remove(Collections.Settings, "httpsFirst"),
        ]);
        Assert.Equal(6, removed.Count);
        var after = profile.Read().Items;
        Assert.Empty(after.Where(i => i.Collection is Collections.Bookmarks or Collections.Shield).Select(i => i.Collection + " " + i.Key));
    }

    [Fact]
    public void Apply_MalformedChanges_AreLeftUnapplied_WithoutThrowing()
    {
        var data = new MemoryProfileData();
        var profile = new BrowserProfile(data);
        var settings = data.Settings;
        var changes = new List<LocalChange>
        {
            Put(Collections.Bookmarks, "javascript:alert(1)", new JsonObject { ["title"] = "x" }),
            Put(Collections.Bookmarks, "https://exemplo.test/b", new JsonObject { ["title"] = 5 }),
            Put(Collections.Settings, "naoExiste", new JsonObject { ["value"] = "x" }),
            Put(Collections.Settings, "httpsFirst", new JsonObject { ["value"] = true }),
            Put(Collections.Settings, "httpsFirst", new JsonObject { ["value"] = "talvez" }),
            Put(Collections.Shield, "rule|não é anfitrião|x.test"),
            Put(Collections.Shield, "rule|exemplo.test|não é anfitrião"),
            Put(Collections.Shield, "inventado|exemplo.test|"),
            Put(Collections.Shield, "hide|exemplo.test|"),
            Put(Collections.Shield, "hide|exemplo.test|" + new string('s', 2001)),
            Put(Collections.Shield, "off|exemplo.test"),
            Put("tabs", "qualquer", new JsonObject()),
        };
        Assert.Empty(profile.Apply(changes));
        Assert.Empty(data.Bookmarks);
        Assert.Empty(data.ShieldKeys()!);
        Assert.Equal(settings, data.Settings);
    }

    // Auditoria, SYN-14: o que baixa a proteção e chega da conta aplica-se como antes mas deixa um aviso.
    [Fact]
    public void SyncedChange_ThatLowersProtection_IsDescribed()
    {
        static LocalChange Setting(string key, string value) => new(Collections.Settings, key, new JsonObject { ["value"] = value });
        var now = new UserSettings();
        Assert.Equal("Desligar «HTTPS primeiro»", BrowserProfile.Weakening(now, Setting("httpsFirst", "false")));
        var withDns = new UserSettings { SecureDns = Eteris.Browser.Shields.SecureDns.Providers.Keys.First() };
        Assert.Equal("Desligar o «DNS cifrado»", BrowserProfile.Weakening(withDns, Setting("dns", "off")));
        Assert.Null(BrowserProfile.Weakening(now, Setting("dns", "off")));                       // já estava desligado
        Assert.Null(BrowserProfile.Weakening(now, Setting("theme", "light")));
        Assert.Equal("Desligar o Escudo em exemplo.test", BrowserProfile.Weakening(now, new LocalChange(Collections.Shield, "off|exemplo.test|", new JsonObject())));
        Assert.Contains("exemplo.test", BrowserProfile.Weakening(now, new LocalChange(Collections.Shield, "http|exemplo.test|", new JsonObject())));
        Assert.Equal("Desligar o modo estrito em exemplo.test", BrowserProfile.Weakening(now, new LocalChange(Collections.Shield, "strict|exemplo.test|", null)));
        Assert.Null(BrowserProfile.Weakening(now, new LocalChange(Collections.Shield, "strict|exemplo.test|", new JsonObject())));
        Assert.Null(BrowserProfile.Weakening(now, new LocalChange(Collections.Shield, "off|exemplo.test|", null)));   // voltar a ligar
        Assert.Null(BrowserProfile.Weakening(now, new LocalChange(Collections.Bookmarks, "https://exemplo.test/", null)));
    }

    [Fact]
    public void SyncedChange_ThatLowersProtection_LeavesANoticeOnce()
    {
        var profile = new BrowserProfile(new MemoryProfileData());
        var applied = profile.Apply(
        [
            new LocalChange(Collections.Settings, "httpsFirst", new JsonObject { ["value"] = "false" }),
            new LocalChange(Collections.Shield, "off|exemplo.test|", new JsonObject()),
            new LocalChange(Collections.Shield, "off|não é anfitrião|", new JsonObject()),      // recusada: não entra no aviso
            new LocalChange(Collections.Settings, "theme", new JsonObject { ["value"] = "light" }),
        ]);
        Assert.Equal(3, applied.Count);
        Assert.Equal("Noutro computador da conta baixou-se a proteção: Desligar «HTTPS primeiro»; Desligar o Escudo em exemplo.test. Se não foi você, reveja as proteções e os aparelhos ligados.",
            profile.TakeNotice());
        Assert.Null(profile.TakeNotice());
        // Junto com o cofre: o aviso do perfil chega ao agente pelo ProfileWithVault.
        profile.Apply([new LocalChange(Collections.Shield, "http|exemplo.test|", new JsonObject())]);
        var both = new ProfileWithVault(profile, new VaultSyncProfile(() => throw new InvalidOperationException("não devia abrir"), () => false), () => null);
        Assert.Contains("exemplo.test", both.TakeNotice());
    }
}

// O perfil em memória: favoritos num dicionário, as Definições numa propriedade, o Escudo num conjunto de «tipo|site|valor».
// As quatro leituras podem falhar (Unreadable) ou ser trocadas (BookmarksReader).
internal sealed class MemoryProfileData : IProfileData
{
    private readonly HashSet<string> _shield = new(StringComparer.Ordinal);

    public Dictionary<string, string> Bookmarks { get; } = new(StringComparer.Ordinal);
    public UserSettings Settings { get; set; } = new();
    public bool Unreadable { get; set; }
    public Func<IReadOnlyList<Bookmark>>? BookmarksReader { get; set; }
    public string NotesText { get; set; } = string.Empty;
    public List<(string Name, int Color, string? Icon)> SpaceList { get; } = [];
    public Dictionary<string, JsonObject> AppliedSpaces { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<Bookmark> ReadBookmarks()
        => BookmarksReader?.Invoke() ?? Bookmarks.Select(b => new Bookmark(b.Key, b.Value)).ToList();

    public List<string>? ShieldKeys() => Unreadable ? null : [.. _shield];

    public string? Notes() => Unreadable ? null : NotesText;

    public IReadOnlyList<(string Name, int Color, string? Icon)>? Spaces() => Unreadable ? null : SpaceList;

    public bool IsBookmarked(string url) => Bookmarks.ContainsKey(url);

    public void AddBookmark(string url, string title) => Bookmarks[url] = title;

    public void RenameBookmark(string url, string title) => Bookmarks[url] = title;

    public void RemoveBookmark(string url) => Bookmarks.Remove(url);

    public bool SetSetting(string key, string value)
    {
        Settings = Settings.With(key, value)!;
        return true;
    }

    public bool HasShield(string kind, string host, string value) => _shield.Contains($"{kind}|{host}|{value}");

    public void SetShield(string kind, string host, string value, bool on)
    {
        var key = $"{kind}|{host}|{value}";
        if (on) _shield.Add(key);
        else _shield.Remove(key);
    }

    public bool ApplyNotes(string text)
    {
        NotesText = text;
        return true;
    }

    public bool ApplySpace(string name, JsonObject? data)
    {
        if (data is null) AppliedSpaces.Remove(name);
        else AppliedSpaces[name] = data;
        return true;
    }
}
