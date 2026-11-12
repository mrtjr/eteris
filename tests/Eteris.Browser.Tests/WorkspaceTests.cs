using System.IO;
using System.Windows.Input;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;
using Eteris.Browser.Theme;
using System.Windows.Media;

namespace Eteris.Browser.Tests;

// Espaços: ambientes de abas na mesma janela (Tabs/Workspace.cs, TabsViewModel.Workspaces.cs, sessão).
public sealed class WorkspaceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-spaces-" + Guid.NewGuid() + ".json");

    public void Dispose() => File.Delete(_path);

    private static TabsViewModel WithTabs(params string[] urls)
    {
        var vm = new TabsViewModel();
        foreach (var url in urls) vm.NewTab(url);
        return vm;
    }

    [Fact]
    public void StartsWithOnePristineWorkspace_HoldingTheStrip()
    {
        var vm = WithTabs("https://a.pt/", "https://b.pt/");
        var only = Assert.Single(vm.Workspaces);
        Assert.True(only.IsCurrent);
        Assert.True(only.IsPristine);
        Assert.Equal(Workspace.DefaultName, only.Name);
        Assert.False(vm.HasWorkspaces);
        Assert.Equal(2, only.TabCount);
        Assert.Same(only, vm.WorkspaceOf(vm.Tabs[0]));
    }

    [Fact]
    public void NewWorkspace_SwitchesToIt_WithAFreshTab_AndPutsTheBackgroundOnesToSleep()
    {
        var vm = WithTabs("https://a.pt/", "https://b.pt/");
        var old = vm.Tabs.ToList();
        var work = vm.NewWorkspace("Trabalho");

        Assert.True(work.IsCurrent);
        Assert.False(vm.Workspaces[0].IsCurrent);
        Assert.Equal("Trabalho", work.Name);
        Assert.Equal(1, work.ColorIndex); // a primeira cor livre
        var fresh = Assert.Single(vm.Tabs);
        Assert.True(fresh.IsNewTab);
        Assert.Same(fresh, vm.Active);
        // A de fundo adormece; a que estava em uso fica acordada (decisão D3: o sono por tempo pergunta-lhe antes).
        Assert.True(old[0].IsSleeping);
        Assert.False(old[1].IsSleeping);
        Assert.Equal(old, vm.TabsOf(vm.Workspaces[0]));
        Assert.Equal(2, vm.Workspaces[0].TabCount);
    }

    [Fact]
    public void SwitchingBack_RestoresTabsOrderAndActive()
    {
        var vm = WithTabs("https://a.pt/", "https://b.pt/", "https://c.pt/");
        vm.Active = vm.Tabs[1];
        var old = vm.Tabs.ToList();
        var work = vm.NewWorkspace("Trabalho");
        vm.NewTab("https://w.pt/");
        var workTabs = vm.Tabs.ToList();

        Assert.True(vm.SwitchWorkspace(vm.Workspaces[0]));
        Assert.Equal(old, vm.Tabs);
        Assert.Same(old[1], vm.Active);
        Assert.False(old[1].IsSleeping);
        Assert.True(old[0].IsSleeping);
        Assert.Equal(workTabs, vm.TabsOf(work));
        // A aba nova de fundo adormece; a que estava em uso no «Trabalho» fica acordada (decisão D3).
        Assert.True(workTabs[0].IsSleeping);
        Assert.False(workTabs[1].IsSleeping);
        Assert.False(vm.SwitchWorkspace(vm.Workspaces[0]), "já era o Espaço à vista");
    }

    [Fact]
    public void SwitchByNumberAndOffset_WrapAround()
    {
        var vm = WithTabs("https://a.pt/");
        vm.NewWorkspace("Dois");
        vm.NewWorkspace("Três");
        Assert.True(vm.SwitchWorkspaceByNumber(1));
        Assert.Equal(Workspace.DefaultName, vm.Current.Name);
        Assert.True(vm.SwitchWorkspaceByNumber(9));
        Assert.Equal("Três", vm.Current.Name);
        Assert.True(vm.SwitchWorkspaceOffset(1));
        Assert.Equal(Workspace.DefaultName, vm.Current.Name);
        Assert.True(vm.SwitchWorkspaceOffset(-1));
        Assert.Equal("Três", vm.Current.Name);
        Assert.False(vm.SwitchWorkspaceByNumber(0));
    }

    [Fact]
    public void MoveToWorkspace_TakesTheTabOut_SleepingAndUngrouped_AndActivatesTheNeighbour()
    {
        var vm = WithTabs("https://a.pt/", "https://b.pt/", "https://c.pt/");
        var work = vm.NewWorkspace("Trabalho", activate: false);
        var moved = vm.Tabs[1];
        vm.NewGroup(moved);
        vm.Active = moved;

        Assert.True(vm.MoveToWorkspace(moved, work));
        Assert.DoesNotContain(moved, vm.Tabs);
        Assert.Equal("https://c.pt/", vm.Active!.Address);
        Assert.True(moved.IsSleeping);
        Assert.Null(moved.Group);
        Assert.Equal([moved], vm.TabsOf(work));
        Assert.Equal(1, work.TabCount);
        Assert.Equal(2, vm.Current.TabCount);
        Assert.False(vm.MoveToWorkspace(moved, work), "já lá está");
    }

    [Fact]
    public void MoveToWorkspace_LastTab_LeavesAFreshTabBehind_AndPinnedGoWithThePinned()
    {
        var vm = WithTabs("https://a.pt/");
        var work = vm.NewWorkspace("Trabalho", activate: false);
        var only = vm.Tabs[0];
        Assert.True(vm.MoveToWorkspace(only, work));
        Assert.True(Assert.Single(vm.Tabs).IsNewTab);

        var pinned = vm.NewTab("https://p.pt/");
        vm.TogglePin(pinned);
        Assert.True(vm.MoveToWorkspace(pinned, work));
        Assert.Equal([pinned, only], vm.TabsOf(work));
    }

    [Fact]
    public void RemoveWorkspace_ClosesItsTabsIntoRecentlyClosed_AndKeepsTheLastOne()
    {
        var vm = WithTabs("https://a.pt/");
        Assert.False(vm.RemoveWorkspace(vm.Workspaces[0]), "o último Espaço fica");

        var work = vm.NewWorkspace("Trabalho");
        vm.NewTab("https://w.pt/");
        vm.NewTab("https://x.pt/");
        Assert.True(vm.RemoveWorkspace(work));
        Assert.Single(vm.Workspaces);
        Assert.True(vm.Current.IsCurrent);
        Assert.Equal("https://a.pt/", Assert.Single(vm.Tabs).Address);
        Assert.Equal(["https://x.pt/", "https://w.pt/"], vm.RecentlyClosed.Select(c => c.Address));
    }

    [Fact]
    public void Rules_NameIconColorMonogram()
    {
        Assert.Equal("Trabalho  e estudo"[..8] + " e estudo", Workspace.NormalizeName("  Trabalho \n e   estudo "));
        Assert.Null(Workspace.NormalizeName("   "));
        Assert.Equal(Workspace.MaxNameLength, Workspace.NormalizeName(new string('x', 80))!.Length);
        Assert.Equal("💼", Workspace.NormalizeIcon("💼"));
        Assert.Null(Workspace.NormalizeIcon("🦄"));
        Assert.Equal(1, Workspace.NormalizeColor(9));
        Assert.Equal(7, Workspace.NormalizeColor(-1));
        Assert.Equal("T", Workspace.MonogramOf("trabalho"));
        Assert.Equal("É", Workspace.MonogramOf("«éteris»"));
        Assert.Equal("E", Workspace.MonogramOf("…"));
        Assert.Equal(2, Workspace.NextColor([0, 1, 3]));
        Assert.Equal(0, Workspace.NextColor(Enumerable.Range(0, 8)));
        Assert.Equal("Espaço 2", Workspace.DefaultNameFor(2));
        Assert.Equal(Workspace.DefaultName, Workspace.DefaultNameFor(1));
        Assert.Equal("1 aba", Workspace.CountText(1));
        Assert.Equal("3 abas", Workspace.CountText(3));
        Assert.Equal(-1, Workspace.IndexForNumber(1, 0));
        Assert.Equal(2, Workspace.IndexForNumber(9, 3));
        Assert.Equal(1, Workspace.IndexForNumber(2, 3));

        var space = new Workspace("Casa", 3, "🏠");
        Assert.Equal("🏠", space.ChipText);
        space.Rename("Lar", icon: null);
        Assert.Equal("L", space.ChipText);
        Assert.Equal(3, space.ColorIndex);
        Assert.False(space.IsPristine);
    }

    [Fact]
    public void Snapshot_IsNullWhileOnlyThePristineDefaultExists()
    {
        var vm = WithTabs("https://a.pt/");
        Assert.Null(vm.WorkspaceSnapshot().Spaces);
        vm.Current.Rename("Pessoal");
        var (spaces, index) = vm.WorkspaceSnapshot();
        Assert.Equal("Pessoal", Assert.Single(spaces!).Name);
        Assert.Equal(0, index);
    }

    [Fact]
    public void Snapshot_ThenRestore_BringsEveryWorkspaceBack_WithTabsGroupsSplitAndActive()
    {
        var vm = WithTabs("https://a.pt/", "https://b.pt/");
        var work = vm.NewWorkspace("Trabalho", icon: "💼");
        vm.NewTab("https://w1.pt/");
        vm.Close(vm.Tabs[0]); // a aba nova que veio com o Espaço
        vm.NewTab("https://w2.pt/");
        vm.NewTab("https://w3.pt/");
        vm.NewGroup(vm.Tabs[1]);
        vm.Tabs[2].SetIdentity("Relatório", null, null, "entregar sexta");
        vm.Active = vm.Tabs[2];
        vm.SwitchWorkspace(vm.Workspaces[0]);
        work.StoredSplit = new SessionSplit(1, 2, "stacked");

        var (spaces, index) = vm.WorkspaceSnapshot();
        Assert.Equal(0, index);
        Assert.Equal(2, spaces!.Count);
        Assert.Null(spaces[0].Tabs);
        var saved = spaces[1].Tabs!;
        Assert.Equal(["https://w1.pt/", "https://w2.pt/", "https://w3.pt/"], saved.Urls);
        Assert.Equal(2, saved.ActiveIndex);
        Assert.Equal("💼", spaces[1].Icon);

        var restored = new TabsViewModel();
        restored.Restore(["https://a.pt/", "https://b.pt/"], 1);
        restored.RestoreWorkspaces(spaces, index);
        Assert.Equal(2, restored.Workspaces.Count);
        Assert.True(restored.Workspaces[0].IsCurrent);
        var again = restored.Workspaces[1];
        Assert.Equal(("Trabalho", 1, "💼"), (again.Name, again.ColorIndex, again.Icon));
        Assert.Equal(3, again.TabCount);
        Assert.Equal("stacked", again.StoredSplit!.Orientation);

        Assert.True(restored.SwitchWorkspace(again));
        Assert.Equal(["https://w1.pt/", "https://w2.pt/", "https://w3.pt/"], restored.Tabs.Select(t => t.Address));
        Assert.Equal("Relatório", restored.Active!.CustomTitle);
        Assert.Equal("entregar sexta", restored.Active.Note);
        Assert.NotNull(restored.Tabs[1].Group);
        Assert.Equal("Espaço 3", restored.NewWorkspace().Name);
    }

    [Fact]
    public void SessionFile_RoundTripsSpaces_AndFiltersTheirTabs()
    {
        var store = new SessionStore(_path);
        var work = new Session(["javascript:x", "https://w.pt/", "eteris://newtab/"], 1, ["X", "W", "Nova"]);
        store.Save(["https://a.pt/"], 0, spaces: [new SessionSpace("Geral", 0), new SessionSpace("Trabalho", 2, "💼", work)], spaceIndex: 0);

        var session = store.Load()!;
        Assert.Equal(["https://a.pt/"], session.Urls);
        Assert.Equal(0, session.SpaceIndex);
        Assert.Equal(2, session.Spaces!.Count);
        Assert.Null(session.Spaces[0].Tabs);
        var tabs = session.Spaces[1].Tabs!;
        Assert.Equal(["https://w.pt/", "eteris://newtab/"], tabs.Urls);
        Assert.Equal(["W", "Nova"], tabs.Titles);
        Assert.Equal(0, tabs.ActiveIndex);
        Assert.Equal("💼", session.Spaces[1].Icon);
    }

    [Fact]
    public void SessionFile_WithoutSpaces_ReadsAsBefore_AndNoSpacesFieldIsWrittenByDefault()
    {
        var store = new SessionStore(_path);
        store.Save(["https://a.pt/"], 0);
        Assert.DoesNotContain("Spaces", File.ReadAllText(_path));
        var session = store.Load()!;
        Assert.Null(session.Spaces);
        Assert.Equal(0, session.SpaceIndex);

        File.WriteAllText(_path, """{"Urls":["https://a.pt/"],"ActiveIndex":0,"Spaces":[{"Name":"","ColorIndex":1}],"SpaceIndex":7}""");
        var odd = new SessionStore(_path).Load()!;
        Assert.Null(odd.Spaces); // um Espaço sem nome não conta
    }

    [Fact]
    public void SessionFile_KeepsSpacesWithWebTabs_EvenWhenTheStripOnlyHasInternalPages()
    {
        var store = new SessionStore(_path);
        store.Save(["eteris://newtab/"], 0, spaces: [new SessionSpace("Geral", 0), new SessionSpace("Trabalho", 1, null, new Session(["https://w.pt/"], 0))]);
        var session = store.Load();
        Assert.NotNull(session);
        Assert.Equal(["https://w.pt/"], session.Spaces![1].Tabs!.Urls);

        store.Save(["eteris://newtab/"], 0, spaces: [new SessionSpace("Geral", 0), new SessionSpace("Trabalho", 1, null, new Session(["eteris://settings/"], 0))]);
        Assert.Null(store.Load());
    }

    // Ctrl+Alt+1…9 e Ctrl+Alt+←/→, nunca com AltGr (Alt da direita: AltGr+2 escreve «@») nem com Shift.
    [Fact]
    public void Shortcuts_DigitsArrowsAndNumPad_ButNeverAltGrOrShift()
    {
        Assert.Equal(WorkspaceMove.To(2), WorkspaceShortcut.Resolve(Key.D2, shift: false, leftAltDown: true));
        Assert.Equal(WorkspaceMove.To(9), WorkspaceShortcut.Resolve(Key.NumPad9, false, true));
        Assert.Equal(WorkspaceMove.Next, WorkspaceShortcut.Resolve(Key.Right, false, true));
        Assert.Equal(WorkspaceMove.Previous, WorkspaceShortcut.Resolve(Key.Left, false, true));
        Assert.Null(WorkspaceShortcut.Resolve(Key.D2, shift: false, leftAltDown: false));
        Assert.Null(WorkspaceShortcut.Resolve(Key.D2, shift: true, leftAltDown: true));
        Assert.Null(WorkspaceShortcut.Resolve(Key.D0, false, true));
        Assert.Null(WorkspaceShortcut.Resolve(Key.W, false, true));
    }

    // «Mover N abas para o Espaço…»: saem pela ordem da faixa, a ativa passa para a vizinha de cada vez.
    [Fact]
    public void MoveToWorkspace_SeveralIncludingTheActive_KeepsTheirOrderInTheTarget()
    {
        var vm = WithTabs("https://a.pt/", "https://b.pt/", "https://c.pt/", "https://d.pt/");
        var work = vm.NewWorkspace("Trabalho", activate: false);
        var (b, c) = (vm.Tabs[1], vm.Tabs[2]);
        vm.Active = b;
        Assert.Equal(2, new[] { b, c }.Count(t => vm.MoveToWorkspace(t, work)));
        Assert.Equal(["https://a.pt/", "https://d.pt/"], vm.Tabs.Select(t => t.Address));
        Assert.Equal("https://d.pt/", vm.Active!.Address);
        Assert.Equal([b, c], vm.TabsOf(work));
        vm.SwitchWorkspace(work);
        Assert.Same(b, vm.Active);
    }

    // O editor: um nome vazio mantém o anterior; o emoji sai quando não vem nenhum; a cor fica se não vier.
    [Fact]
    public void Rename_EmptyNameKeepsTheOld_IconIsReplacedOrCleared_ColorOptional()
    {
        var space = new Workspace("Trabalho", 2, "💼");
        space.Rename("   ", icon: "💼");
        Assert.Equal(("Trabalho", 2, "💼"), (space.Name, space.ColorIndex, space.Icon));
        space.Rename("Escritório", 5, null);
        Assert.Equal(("Escritório", 5, (string?)null), (space.Name, space.ColorIndex, space.Icon));
        Assert.Equal("E", space.ChipText);
        Assert.Equal("Escritório · 0 abas", space.Summary);
    }

    // A cor do Espaço tinge a barra: no escuro fica abaixo do limite de luminância (texto legível), no claro fica clara.
    [Fact]
    public void Tint_WithWorkspace_IsSubtleAndKeepsContrast()
    {
        var violet = TabGroup.ColorAt(0);
        Assert.Equal(Color.FromRgb(0x8B, 0x5C, 0xF6), violet);
        var dark = SiteTint.WithWorkspace(SiteTint.Base, violet, light: false);
        Assert.NotEqual(SiteTint.Base, dark);
        Assert.True(SiteTint.Luminance(dark) <= SiteTint.MaxChromeLuminance);
        Assert.True(dark.B > dark.G, "puxa para o violeta");
        var lightChrome = Color.FromRgb(0xF6, 0xF7, 0xFB);
        var light = SiteTint.WithWorkspace(lightChrome, TabGroup.ColorAt(6), light: true);
        Assert.NotEqual(lightChrome, light);
        Assert.True(SiteTint.Luminance(light) > 0.8, "continua clara");
        // Um site muito claro (amarelo) num Espaço: o resultado continua escuro o suficiente para a barra.
        var loud = SiteTint.WithWorkspace(Color.FromRgb(0x20, 0x20, 0x10), Color.FromRgb(0xFF, 0xFF, 0x00), light: false);
        Assert.True(SiteTint.Luminance(loud) <= SiteTint.MaxChromeLuminance);
    }

    // Radar › «Noutros Espaços»: as abas dos outros Espaços, com o nome do Espaço, «Ir para o Espaço» pela linha.
    [Fact]
    public void Radar_SpacesScope_ListsTabsOfTheOtherWorkspaces()
    {
        var vm = WithTabs("https://a.pt/");
        var work = vm.NewWorkspace("Trabalho", icon: "💼");
        vm.NewTab("https://w.pt/");
        vm.Tabs[1].Title = "Relatório";
        vm.Close(vm.Tabs[0]);
        vm.SwitchWorkspace(vm.Workspaces[0]);
        var others = vm.TabsInOtherWorkspaces();
        var (space, tab) = Assert.Single(others);
        Assert.Same(work, space);

        var rows = TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.Spaces, RadarSort.Recent, "", spaces: others);
        var row = Assert.Single(rows);
        Assert.Same(tab, row.Tab);
        Assert.Same(work, row.Space);
        Assert.Equal("Relatório", row.Title);
        Assert.Equal("w.pt · 💼 Trabalho", row.Subtitle);
        // A única aba do «Trabalho» era a que estava em uso: fica acordada (decisão D3).
        Assert.Equal("No Espaço «Trabalho»", row.Status);
        Assert.Empty(TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.Spaces, RadarSort.Recent, "xyz", spaces: others));
        Assert.Contains("Novo Espaço", TabRadar.Status(0, RadarScope.Spaces, ""));
        Assert.DoesNotContain(TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.All, RadarSort.Recent, "", spaces: others), r => r.Space is not null);
    }

    // Paleta e Radar: as abas dos outros Espaços vêm pela ordem do seletor e, dentro de cada um, pela ordem da faixa.
    [Fact]
    public void TabsInOtherWorkspaces_FollowSelectorAndStripOrder()
    {
        var vm = WithTabs("https://a.pt/");
        var two = vm.NewWorkspace("Dois");
        vm.NewTab("https://d1.pt/");
        vm.NewTab("https://d2.pt/");
        var three = vm.NewWorkspace("Três");
        vm.NewTab("https://t1.pt/");
        Assert.DoesNotContain(vm.TabsInOtherWorkspaces(), p => ReferenceEquals(p.Space, three));
        vm.SwitchWorkspace(vm.Workspaces[0]);
        var others = vm.TabsInOtherWorkspaces();
        Assert.Equal([two, two, two, three, three], others.Select(p => p.Space));
        Assert.Equal(["eteris://newtab/", "https://d1.pt/", "https://d2.pt/", "eteris://newtab/", "https://t1.pt/"], others.Select(p => p.Tab.Address));
    }

    [Fact]
    public void WhatsNew_AnnouncesSpaces_WithTheShortcut()
    {
        var highlight = Assert.Single(WhatsNewPage.Highlights, h => h.Title == "Espaços");
        Assert.Equal("Ctrl+Alt+1 … 9", highlight.Keys);
    }
}
