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
    public void RemoveSiteData_ClearsStorageAndCookiesButKeepsTheRest()
    {
        var def = Directory.CreateDirectory(Path.Combine(_root, "Default")).FullName;
        foreach (var dir in new[] { "Local Storage", "IndexedDB", "Session Storage", "Service Worker", "Network" })
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(def, dir)).FullName, "data"), "x");
        File.WriteAllText(Path.Combine(def, "Network", "Cookies"), "x");
        File.WriteAllText(Path.Combine(def, "Network", "TransportSecurity"), "x");
        File.WriteAllText(Path.Combine(def, "Preferences"), "x");

        ProfileCleanup.RemoveSiteData(_root);

        Assert.False(Directory.Exists(Path.Combine(def, "Local Storage")));
        Assert.False(Directory.Exists(Path.Combine(def, "IndexedDB")));
        Assert.False(File.Exists(Path.Combine(def, "Network", "Cookies")));
        Assert.True(File.Exists(Path.Combine(def, "Network", "TransportSecurity")));
        Assert.True(File.Exists(Path.Combine(def, "Preferences")));
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

    [Fact]
    public void RemoveDownloadedWidevine_DeletesTheGoogleModuleOnly()
    {
        var widevine = Directory.CreateDirectory(Path.Combine(_root, "WidevineCdm", "4.10.3112.0")).FullName;
        File.WriteAllText(Path.Combine(widevine, "manifest.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "Default"));
        Assert.True(ProfileCleanup.RemoveDownloadedWidevine(_root));
        Assert.False(Directory.Exists(Path.Combine(_root, "WidevineCdm")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Default")));
        Assert.False(ProfileCleanup.RemoveDownloadedWidevine(_root));
    }

    [Fact]
    public void Engine_DoesNotDownloadGoogleComponentsInTheBackground()
    {
        var args = Eteris.Browser.Infrastructure.CefConfigurator.Build("p").CefCommandLineArgs;
        Assert.Equal("1", args["disable-component-update"]);
        Assert.Equal("1", args["disable-background-networking"]);
        Assert.Equal("url-source=http://127.0.0.1:9/", args["component-updater"]);
        Assert.Contains("OptimizationGuideModelDownloading", args["disable-features"]);
    }
}
