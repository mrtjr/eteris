using System.IO;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public sealed class ProfileCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eteris-prof-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void RemovesChromiumHistoryButKeepsCookiesAndPreferences()
    {
        var def = Directory.CreateDirectory(Path.Combine(_root, "Default")).FullName;
        foreach (var name in new[] { "History", "History-journal", "Top Sites", "Visited Links", "Network Action Predictor", "Shortcuts", "Preferences" })
            File.WriteAllText(Path.Combine(def, name), "x");
        Directory.CreateDirectory(Path.Combine(def, "Network"));
        File.WriteAllText(Path.Combine(def, "Network", "Cookies"), "x");

        var removed = ProfileCleanup.RemoveChromiumHistory(_root);

        Assert.Equal(6, removed);
        Assert.False(File.Exists(Path.Combine(def, "History")));
        Assert.True(File.Exists(Path.Combine(def, "Preferences")));
        Assert.True(File.Exists(Path.Combine(def, "Network", "Cookies")));
    }

    [Fact]
    public void MissingProfile_IsFine()
        => Assert.Equal(0, ProfileCleanup.RemoveChromiumHistory(Path.Combine(_root, "nada")));

    [Fact]
    public void HardeningPrefs_DisableHistoryAndPasswordSaving()
    {
        Assert.Equal(true, Eteris.Browser.Shields.PrivacyPreferences.Hardening["history.saving_disabled"]);
        Assert.Equal(false, Eteris.Browser.Shields.PrivacyPreferences.Hardening["credentials_enable_service"]);
    }
}
