using System.IO;
using System.Text.Json;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Revisão do bloco A (abas e Espaços): fechar a última aba de um Espaço, «Feito» sem volta, duplicadas trancadas,
// «Seen» estragado, Espaços que adormeciam trancadas e abas com som, prazo do arquivo contado com o Espaço posto de
// lado, apagar Espaços com trancadas, atalhos dos Espaços, Radar privado, adiadas que voltavam a cada minuto e o
// arquivo automático sem confirmação.
public sealed class ReviewTabsSpacesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-review-tabs-" + Guid.NewGuid() + ".json");
    private static readonly DateTime T0 = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    public void Dispose() => File.Delete(_path);

    private static TabsViewModel WithTabs(params string[] urls)
    {
        var vm = new TabsViewModel();
        foreach (var url in urls) vm.NewTab(url);
        return vm;
    }

    // ---------- Fechar a última aba de um Espaço ----------

    [Fact]
    public void ClosingTheLastTabOfAWorkspace_LeavesANewTab_InsteadOfClosingTheWindow()
    {
        var vm = WithTabs("https://a.pt/");
        vm.NewTabAddress = NewTabPage.PrivateUrl;
        vm.NewWorkspace("Trabalho");
        var work = vm.NewTab("https://w.pt/");
        vm.Close(vm.Tabs[0]);
        Assert.Same(work, Assert.Single(vm.Tabs));

        Assert.False(vm.Close(work), "com outros Espaços a janela não fecha");
        var fresh = Assert.Single(vm.Tabs);
        Assert.Equal(NewTabPage.PrivateUrl, fresh.Address);
        Assert.Same(fresh, vm.Active);
        Assert.Equal(2, vm.Workspaces.Count);
        Assert.Equal("https://a.pt/", Assert.Single(vm.TabsOf(vm.Workspaces[0])).Address);
        Assert.Equal("https://w.pt/", vm.RecentlyClosed[0].Address);
    }

    [Fact]
    public void ClosingTheLastTab_WithoutWorkspaces_StillClosesTheWindow()
    {
        var vm = WithTabs("https://a.pt/");
        Assert.True(vm.Close(vm.Tabs[0]));
        Assert.Empty(vm.Tabs);
    }

    [Fact]
    public void DoneLastTab_InAWorkspace_LeavesANewTab()
    {
        var vm = WithTabs("https://a.pt/");
        vm.NewWorkspace("Trabalho");
        var work = vm.NewTab("https://w.pt/");
        vm.Close(vm.Tabs[0]);
        vm.CloseDone(work);
        Assert.True(Assert.Single(vm.Tabs).IsNewTab);
    }

    // ---------- «Feito» sem lugar em «Feitas hoje» ----------

    [Fact]
    public void CloseDone_WithRemember_GoesToRecentlyClosed_EvenLocked()
    {
        var vm = WithTabs("https://a.pt/", "https://b.pt/");
        var tab = vm.Tabs[1];
        tab.IsLocked = true;
        vm.CloseDone(tab, remember: true);
        Assert.DoesNotContain(tab, vm.Tabs);
        Assert.Equal("https://b.pt/", Assert.Single(vm.RecentlyClosed).Address);
        Assert.Equal("https://b.pt/", vm.ReopenClosed()!.Address);
    }

    [Fact]
    public void CloseDone_ByDefault_StillLeavesRecentlyClosedAlone()
    {
        var vm = WithTabs("https://a.pt/", "https://b.pt/");
        vm.CloseDone(vm.Tabs[1]);
        Assert.Empty(vm.RecentlyClosed);
    }

    // ---------- Duplicadas trancadas ----------

    [Fact]
    public void CloseDuplicates_DoesNotCountALockedDuplicateThatStays()
    {
        var vm = WithTabs("https://a.pt/", "https://a.pt");
        vm.Active = vm.Tabs[0];
        vm.Tabs[1].IsLocked = true;
        Assert.Equal(0, vm.CloseDuplicates());
        Assert.Equal(2, vm.Tabs.Count);

        vm.NewTab("https://a.pt/");
        vm.Active = vm.Tabs[0];
        Assert.Equal(1, vm.CloseDuplicates());
        Assert.Equal(2, vm.Tabs.Count);
    }

    // ---------- «Seen» fora do intervalo ----------

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(253_402_300_800_000L)]
    public void Restore_IgnoresASeenOutOfRange_InsteadOfCrashingAtStartup(long seen)
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var vm = new TabsViewModel();
        vm.Restore(["https://a.pt/", "https://b.pt/"], 0, metas: [new SessionTabMeta(Seen: seen), new SessionTabMeta(Name: "B", Seen: seen)]);
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Equal("B", vm.Tabs[1].CustomTitle);
        Assert.True(vm.Tabs[1].LastActiveUtc >= before);
    }

    [Fact]
    public void SessionFile_WithAHugeSeen_LoadsAndRestores()
    {
        File.WriteAllText(_path, """{"Urls":["https://a.pt/"],"ActiveIndex":0,"Metas":[{"Locked":true,"Seen":9223372036854775807}]}""");
        var session = new SessionStore(_path).Load();
        Assert.NotNull(session);
        var vm = new TabsViewModel();
        vm.Restore(session!.Urls, session.ActiveIndex, metas: session.Metas);
        Assert.True(Assert.Single(vm.Tabs).IsLocked);
    }

    // ---------- Mudar de Espaço: trancadas e abas com som ficam acordadas ----------

    [Fact]
    public void SwitchingWorkspace_KeepsLockedAndAudibleTabsAwake_ButPutsTheOthersToSleep()
    {
        var vm = WithTabs("https://plain.pt/", "https://locked.pt/", "https://music.pt/", "https://napping.pt/");
        var (plain, locked, music, napping) = (vm.Tabs[0], vm.Tabs[1], vm.Tabs[2], vm.Tabs[3]);
        locked.IsLocked = true;
        music.IsAudible = true;
        napping.IsSleeping = true;
        napping.IsLocked = true;

        vm.NewWorkspace("Trabalho");
        var fresh = vm.Active!;

        Assert.True(plain.IsSleeping);
        Assert.False(locked.IsSleeping);
        Assert.False(music.IsSleeping);
        Assert.True(napping.IsSleeping, "uma trancada que já dormia não acorda");
        Assert.Equal([locked, music], vm.AwakeOutOfView());

        vm.SwitchWorkspace(vm.Workspaces[0]);
        // Só a aba que estava em uso no «Trabalho» fica acordada lá (decisão D3).
        Assert.Equal([fresh], vm.AwakeOutOfView());
        Assert.False(locked.IsSleeping);
        Assert.False(music.IsSleeping);
    }

    // DAD-03: ao mudar de Espaço, ficam acordadas as abas que a janela segura (captura, multimédia a tocar, a página
    // que pediu para ficar).
    [Fact]
    public void SwitchingWorkspace_KeepsAwakeTheTabsTheWindowHolds()
    {
        var vm = WithTabs("https://plain.pt/", "https://chamada.pt/");
        var call = vm.Tabs[1];
        vm.KeepsAwake = tab => ReferenceEquals(tab, call);
        vm.Active = vm.Tabs[0];
        vm.NewWorkspace("Trabalho");
        Assert.False(call.IsSleeping);
        Assert.Contains(call, vm.AwakeOutOfView());
    }

    // DES-01, decisão D3 (a): a aba em uso do Espaço que sai fica acordada e passa a seguir o sono por tempo, que
    // pergunta à página antes de adormecer (um texto por enviar não se perde ao mudar de Espaço). As outras de fundo
    // adormecem como antes.
    [Fact]
    public void SwitchingWorkspace_KeepsTheActiveTabAwake()
    {
        var vm = WithTabs("https://fundo.pt/", "https://escrever.pt/");
        var (background, writing) = (vm.Tabs[0], vm.Tabs[1]);
        vm.Active = writing;
        vm.NewWorkspace("Trabalho");
        Assert.False(writing.IsSleeping);
        Assert.True(background.IsSleeping);
        Assert.Equal([writing], vm.AwakeOutOfView());
    }

    // ---------- O tempo posto de lado não conta para o arquivo ----------

    [Fact]
    public void ReturningToAParkedWorkspace_DiscountsTheTimeItWasAway()
    {
        var now = T0;
        var vm = new TabsViewModel { UtcNow = () => now };
        var a = vm.NewTab("https://a.pt/");
        var b = vm.NewTab("https://b.pt/");
        a.LastActiveUtc = T0 - TimeSpan.FromDays(2);
        b.LastActiveUtc = T0;

        vm.NewWorkspace("Viagem");
        now = T0 + TimeSpan.FromDays(8);
        vm.SwitchWorkspace(vm.Workspaces[0]);

        Assert.Equal(now - TimeSpan.FromDays(2), a.LastActiveUtc);
        Assert.Equal(now, b.LastActiveUtc);
        Assert.False(ArchivePolicy.ShouldArchive(false, false, false, false, false, false, a.LastActiveUtc, now, ArchivePolicy.DefaultDays));
    }

    [Fact]
    public void ParkedTime_SurvivesTheSession()
    {
        var now = T0;
        var vm = new TabsViewModel { UtcNow = () => now };
        var a = vm.NewTab("https://a.pt/");
        a.LastActiveUtc = T0;
        vm.NewWorkspace("Viagem");
        var (spaces, index) = vm.WorkspaceSnapshot();
        Assert.Equal(new DateTimeOffset(T0).ToUnixTimeMilliseconds(), spaces![0].Left);

        now = T0 + TimeSpan.FromDays(10);
        var restored = new TabsViewModel { UtcNow = () => now };
        restored.NewTab(NewTabPage.Url);
        restored.RestoreWorkspaces(spaces, index);
        restored.SwitchWorkspace(restored.Workspaces[0]);
        Assert.Equal(now, Assert.Single(restored.Tabs, t => t.Address == "https://a.pt/").LastActiveUtc);
    }

    [Fact]
    public void OldSessionWithoutLeft_CountsFromTheLastVisitToTheWorkspace()
    {
        var seen = new DateTimeOffset(T0).ToUnixTimeMilliseconds();
        var older = new DateTimeOffset(T0 - TimeSpan.FromDays(3)).ToUnixTimeMilliseconds();
        var spaces = new List<SessionSpace>
        {
            new("Geral", 0),
            new("Viagem", 1, null, new Session(["https://a.pt/", "https://b.pt/"], 1, Metas: [new SessionTabMeta(Seen: older), new SessionTabMeta(Seen: seen)])),
        };
        var now = T0 + TimeSpan.FromDays(9);
        var vm = new TabsViewModel { UtcNow = () => now };
        vm.NewTab(NewTabPage.Url);
        vm.RestoreWorkspaces(spaces, 0);
        vm.SwitchWorkspace(vm.Workspaces[1]);
        Assert.Equal(now - TimeSpan.FromDays(3), vm.Tabs[0].LastActiveUtc);
    }

    // ---------- Apagar um Espaço com trancadas ----------

    [Fact]
    public void RemovingAWorkspace_KeepsItsLockedTabs_InTheWorkspaceInView()
    {
        var vm = WithTabs("https://a.pt/");
        var work = vm.NewWorkspace("Projeto");
        var keep = vm.NewTab("https://keep.pt/");
        keep.IsLocked = true;
        keep.SetIdentity("Formulário", null, 2, "a meio");
        vm.NewTab("https://gone.pt/");

        var removal = vm.RemoveWorkspaceWithTabs(work);
        Assert.NotNull(removal);
        Assert.Equal(1, removal!.KeptLocked);
        Assert.DoesNotContain(keep, removal.Closed);
        Assert.Single(vm.Workspaces);
        Assert.Contains(keep, vm.Tabs);
        Assert.True(keep.IsLocked);
        Assert.Equal("Formulário", keep.CustomTitle);
        Assert.Equal("a meio", keep.Note);
        Assert.DoesNotContain(vm.RecentlyClosed, c => c.Address == "https://keep.pt/");
        Assert.Contains(vm.RecentlyClosed, c => c.Address == "https://gone.pt/");
        Assert.Equal(2, vm.Current.TabCount);
    }

    // ---------- Atalhos dos Espaços na paleta ----------

    [Fact]
    public void ShortcutFor_MatchesWhatTheKeyDoes()
    {
        Assert.Equal("Ctrl+Alt+1", Workspace.ShortcutFor(0, 3));
        Assert.Equal("Ctrl+Alt+3", Workspace.ShortcutFor(2, 3));
        Assert.Equal("Ctrl+Alt+9", Workspace.ShortcutFor(8, 9));
        Assert.Equal("Ctrl+Alt+8", Workspace.ShortcutFor(7, 10));
        Assert.Null(Workspace.ShortcutFor(8, 10));
        Assert.Equal("Ctrl+Alt+9", Workspace.ShortcutFor(9, 10));
        Assert.Null(Workspace.ShortcutFor(10, 12));
        Assert.Null(Workspace.ShortcutFor(3, 3));
        // Cada rótulo leva mesmo ao Espaço que o mostra.
        for (var count = 1; count <= 12; count++)
            for (var i = 0; i < count; i++)
                if (Workspace.ShortcutFor(i, count) is { } keys)
                    Assert.Equal(i, Workspace.IndexForNumber(int.Parse(keys[^1..]), count));
    }

    // ---------- Radar na janela privada ----------

    [Fact]
    public void PrivateRadar_HasOtherWorkspaces_ButNothingFromDisk()
    {
        var scopes = TabRadar.ScopesFor(privateWindow: true);
        Assert.Contains(RadarScope.Spaces, scopes);
        Assert.DoesNotContain(RadarScope.Done, scopes);
        Assert.DoesNotContain(RadarScope.Snoozed, scopes);
        Assert.DoesNotContain(RadarScope.Archived, scopes);
        Assert.Equal(RadarScope.All, scopes[0]);
        Assert.Equal(Enum.GetValues<RadarScope>(), TabRadar.ScopesFor(privateWindow: false));
        Assert.Equal("Noutros Espaços", TabRadar.ScopeLabel(RadarScope.Spaces));
        Assert.Equal(10, Enum.GetValues<RadarScope>().Select(TabRadar.ScopeLabel).Distinct().Count());
    }

    // ---------- Adiadas cuja linha não se apaga ----------

    [Fact]
    public void WokenSnoozes_DoNotComeBackEveryMinute()
    {
        var woken = new WokenSnoozes();
        var due = new[] { 1L, 2L, 3L };
        Assert.Equal(due, woken.Pending(due, id => id));
        woken.Remember(2);
        Assert.Equal([1L, 3L], woken.Pending(due, id => id));
    }

    // ---------- Arquivo automático só com confirmação ----------

    [Fact]
    public void AutoArchive_NeedsTheUserToPickADeadline()
    {
        var fresh = new UserSettings();
        Assert.False(fresh.ArchiveConfirmed);
        Assert.Equal(ArchivePolicy.DefaultDays, fresh.ArchiveDays);
        var picked = fresh.With("archive", "7")!;
        Assert.True(picked.ArchiveConfirmed);
        Assert.Equal(7, picked.ArchiveDays);
        Assert.True(fresh.With("archive", "0")!.ArchiveConfirmed);
        Assert.Null(fresh.With("archive", "-1"));

        // Um ficheiro de definições antigo (sem a escolha) não liga o arquivo.
        var old = JsonSerializer.Deserialize<UserSettings>("""{"ArchiveDays":7}""")!;
        Assert.False(old.ArchiveConfirmed);
        var roundTrip = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(picked))!;
        Assert.True(roundTrip.ArchiveConfirmed);
    }

    // ---------- Reabrir janela fechada leva identidade e tranca ----------

    [Fact]
    public void ClosedWindow_KeepsNamesNotesAndLocks()
    {
        var vm = WithTabs("https://a.pt/", "https://b.pt/");
        vm.Tabs[1].IsLocked = true;
        vm.Tabs[1].SetIdentity("Banco", null, 3, "pagar");
        var windows = new ClosedWindows();
        windows.Push(new ClosedWindow(vm.Tabs.Select(t => t.Address).ToList(), vm.Tabs.Select(t => t.Title).ToList(), 0,
            Metas: TabsViewModel.MetaSnapshotOf(vm.Tabs, null)));
        var meta = windows.Pop()!.Metas![1];
        Assert.True(meta.Locked);
        Assert.Equal("Banco", meta.Name);
        Assert.Equal("pagar", meta.Note);
        Assert.Equal(3, meta.Color);
    }
}
