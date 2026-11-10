using Eteris.Browser.Sync;
using Eteris.Sync.Engine;
using Eteris.Sync.Google;

namespace Eteris.Browser.Tests.Sync;

// O que a barra do início de sessão diz (0.2.15, T4) e o árbitro «uma barra de cada vez». Puro: sem janelas.
// Os enums são internos: nas teorias vão pelo nome.
public class SignInBarModelTests
{
    private const long At = 1_762_560_000_000;

    private static SignInBarView? For(SignInStage stage, SyncStatus? sync = null, string? name = "Ana Teste", bool isPrivate = false, string? manualUrl = null)
        => SignInBarModel.For(new SignInBarInput(new SignInProgress(stage, manualUrl, null, At), sync, name, isPrivate));

    private static SignInBarView? Done(SyncStatus? sync, string? name = "Ana Teste")
        => For(SignInStage.Done, sync, name);

    private static string[] Labels(SignInBarView view) => view.Buttons.Select(b => b.Label).ToArray();

    // Todas as combinações de Done que a regra 4 distingue (com o nome da situação, para as mensagens).
    private static IEnumerable<(string Case, SyncStatus? Sync)> DoneCases() =>
    [
        ("sem estado", null),
        ("a começar", TestStatus.Of(SyncPhase.Starting)),
        ("ligada, ainda sem sucesso", TestStatus.Of(SyncPhase.On, running: true)),
        ("ligada, sucesso antigo", TestStatus.Of(SyncPhase.On, last: At - 1)),
        ("sincronizado", TestStatus.Of(SyncPhase.On, last: At + 10)),
        ("escolher", TestStatus.Of(SyncPhase.Choose)),
        ("à espera de aprovação", TestStatus.Of(SyncPhase.Pairing)),
        ("outra conta", TestStatus.Of(SyncPhase.Stopped) with { OtherAccount = true }),
        ("outra paragem", TestStatus.Of(SyncPhase.Stopped)),
        ("ronda falhada", TestStatus.Of(SyncPhase.On, error: "Sem rede")),
        ("sessão expirada", TestStatus.Of(SyncPhase.On) with { NeedsSignIn = true }),
        ("desligada", TestStatus.Of(SyncPhase.Off)),
    ];

    // A vaga 3 é partilhada com o cartão do computador novo (T7): enquanto ele (ou o alerta) trata do estado, a barra
    // não diz «Falta confirmar…» nem «A sincronizar…». O resto não muda com a marca.
    [Fact]
    public void Slot3_TheBarYieldsToTheCard()
    {
        SignInBarView? WithCard(SyncStatus? sync)
            => SignInBarModel.For(new SignInBarInput(new SignInProgress(SignInStage.Done, null, null, At), sync, "Ana Teste", false) { GuestPairing = true });
        Assert.Equal(SignInBarKind.NeedsApproval, Done(TestStatus.Of(SyncPhase.Pairing))!.Kind);
        Assert.Null(WithCard(TestStatus.Of(SyncPhase.Pairing)));
        Assert.Null(WithCard(TestStatus.Of(SyncPhase.Choose)));
        Assert.Equal(SignInBarKind.Syncing, Done(TestStatus.Of(SyncPhase.On, running: true))!.Kind);
        Assert.Null(WithCard(TestStatus.Of(SyncPhase.On, running: true)));
        Assert.Null(WithCard(null));
        foreach (var (name, sync) in DoneCases())
        {
            var plain = Done(sync);
            if (plain?.Kind is SignInBarKind.NeedsApproval or SignInBarKind.Syncing) continue;
            Assert.True(plain == WithCard(sync) || (plain is not null && WithCard(sync) is { } card && card.Kind == plain.Kind && card.Text == plain.Text), name);
        }
        foreach (var stage in new[] { SignInStage.Waiting, SignInStage.TimedOut, SignInStage.Refused, SignInStage.DriveDenied, SignInStage.NoNetwork })
        {
            var plain = For(stage);
            var card = SignInBarModel.For(new SignInBarInput(new SignInProgress(stage, null, null, At), null, "Ana Teste", false) { GuestPairing = true });
            Assert.Equal(plain?.Kind, card?.Kind);
            Assert.Equal(plain?.Text, card?.Text);
        }
    }

    [Theory]
    [InlineData(nameof(SignInStage.Idle))]
    [InlineData(nameof(SignInStage.Waiting))]
    [InlineData(nameof(SignInStage.TimedOut))]
    [InlineData(nameof(SignInStage.Refused))]
    [InlineData(nameof(SignInStage.DriveDenied))]
    [InlineData(nameof(SignInStage.NoNetwork))]
    [InlineData(nameof(SignInStage.Done))]
    public void Bar_PrivateWindow_NeverShows(string stage)
    {
        var s = Enum.Parse<SignInStage>(stage);
        Assert.Null(For(s, isPrivate: true));
        Assert.Null(For(s, TestStatus.Of(SyncPhase.On, last: At + 10), isPrivate: true, manualUrl: "endereco"));
        foreach (var (_, sync) in DoneCases()) Assert.Null(For(s, sync, isPrivate: true));
    }

    [Fact]
    public void Bar_Idle_IsNull()
    {
        Assert.Null(For(SignInStage.Idle));
        Assert.Null(SignInBarModel.For(new SignInBarInput(SignInProgress.None, TestStatus.Of(SyncPhase.On, last: At + 10), "Ana Teste", false)));
    }

    [Fact]
    public void Bar_Waiting_HasTheExactText_AndOnlyCancel()
    {
        var view = For(SignInStage.Waiting)!;
        Assert.Equal(SignInBarKind.Waiting, view.Kind);
        Assert.Equal("À espera do início de sessão no seu navegador…", view.Text);
        Assert.Null(view.Bold);
        Assert.Equal(["Cancelar"], Labels(view));
        Assert.Equal(SignInBarAction.Cancel, view.Buttons[0].Action);
        Assert.Null(view.HideAfter);
    }

    [Fact]
    public void Bar_NoBrowser_OffersCopyThenCancel_AndNeverPrintsTheAddress()
    {
        const string address = "endereco-secreto?state=abc&code_challenge=xyz&login_hint=ana.teste@example.com";
        var view = For(SignInStage.Waiting, manualUrl: address)!;
        Assert.Equal(SignInBarKind.NoBrowser, view.Kind);
        Assert.Equal("Abra este endereço noutro navegador para entrar na Google.", view.Text);
        Assert.Equal(["Copiar endereço", "Cancelar"], Labels(view));
        Assert.Equal([SignInBarAction.CopyAddress, SignInBarAction.Cancel], view.Buttons.Select(b => b.Action));
        Assert.True(view.Buttons[0].Primary);
        Assert.DoesNotContain("endereco-secreto", view.Text);
        Assert.DoesNotContain("@", view.Text);
        Assert.All(view.Buttons, b => Assert.DoesNotContain("endereco-secreto", b.Label));
        Assert.Null(view.HideAfter);
    }

    [Theory]
    [InlineData(nameof(SignInStage.TimedOut), "O início de sessão demorou demasiado.")]
    [InlineData(nameof(SignInStage.Refused), "A Google não deu autorização ao Éteris.")]
    [InlineData(nameof(SignInStage.DriveDenied), "Entrou, mas sem a permissão do Drive o Éteris não sincroniza.")]
    public void Bar_Failures_HaveTheExactText_AndRetry(string stage, string text)
    {
        var view = For(Enum.Parse<SignInStage>(stage))!;
        Assert.Equal(stage, view.Kind.ToString());
        Assert.Equal(text, view.Text);
        var button = Assert.Single(view.Buttons);
        Assert.Equal("Tentar outra vez", button.Label);
        Assert.Equal(SignInBarAction.Retry, button.Action);
        Assert.True(button.Primary);
    }

    [Fact]
    public void Bar_NoNetwork_HasTheProductText_AndRetry()
    {
        var view = For(SignInStage.NoNetwork)!;
        Assert.Equal(SignInBarKind.NoNetwork, view.Kind);
        Assert.Equal("Sem ligação à Google. Tente de novo.", view.Text);
        var button = Assert.Single(view.Buttons);
        Assert.Equal(new SignInBarButton("Tentar outra vez", SignInBarAction.Retry, Primary: true), button);
    }

    [Theory]
    [InlineData(nameof(SignInStage.TimedOut))]
    [InlineData(nameof(SignInStage.Refused))]
    [InlineData(nameof(SignInStage.DriveDenied))]
    [InlineData(nameof(SignInStage.NoNetwork))]
    public void Bar_Failures_HideAfterErrorHold(string stage)
        => Assert.Equal(SignInBarModel.ErrorHold, For(Enum.Parse<SignInStage>(stage))!.HideAfter);

    [Fact]
    public void Bar_Done_Syncing_NamesTheAccount_InBold()
    {
        foreach (var sync in new[] { null, TestStatus.Of(SyncPhase.Starting), TestStatus.Of(SyncPhase.On, running: true) })
        {
            var view = Done(sync)!;
            Assert.Equal(SignInBarKind.Syncing, view.Kind);
            Assert.Equal("Sessão iniciada como Ana Teste. A sincronizar…", view.Text);
            Assert.Equal("Ana Teste", view.Bold);
            Assert.Empty(view.Buttons);
            Assert.Null(view.HideAfter);
        }
        // Sem nome nenhum: a frase sem nome (EM ABERTO 4). Espaços não contam como nome.
        foreach (var name in new[] { null, "", "   " })
        {
            var view = Done(null, name)!;
            Assert.Equal("Sessão iniciada. A sincronizar…", view.Text);
            Assert.Null(view.Bold);
        }
        // Nome comprido: cortado a 40 caracteres, com reticências.
        var longName = new string('a', 30) + " " + new string('b', 30);
        var cut = Done(null, longName)!;
        Assert.Equal(40, cut.Bold!.Length);
        Assert.EndsWith("…", cut.Bold);
        Assert.Equal($"Sessão iniciada como {cut.Bold}. A sincronizar…", cut.Text);
        // Aparado.
        Assert.Equal("Ana Teste", Done(null, "  Ana Teste ")!.Bold);
    }

    [Fact]
    public void Bar_Done_Synced_HidesByItself()
    {
        foreach (var last in new[] { At, At + 10 })
        {
            var view = Done(TestStatus.Of(SyncPhase.On, last: last))!;
            Assert.Equal(SignInBarKind.Synced, view.Kind);
            Assert.Equal("Sincronizado.", view.Text);
            Assert.Null(view.Bold);
            Assert.Empty(view.Buttons);
            Assert.Equal(SignInBarModel.SyncedHold, view.HideAfter);
        }
    }

    [Fact]
    public void Bar_Done_OldSuccess_IsStillSyncing()
    {
        var view = Done(TestStatus.Of(SyncPhase.On, last: At - 1))!;
        Assert.Equal(SignInBarKind.Syncing, view.Kind);
        // A correr outra ronda depois de um sucesso: ainda não diz «Sincronizado.».
        Assert.Equal(SignInBarKind.Syncing, Done(TestStatus.Of(SyncPhase.On, running: true, last: At + 10))!.Kind);
    }

    [Fact]
    public void Bar_Done_NeedsApproval_OffersTheCode()
    {
        foreach (var phase in new[] { SyncPhase.Choose, SyncPhase.Pairing })
        {
            var view = Done(TestStatus.Of(phase))!;
            Assert.Equal(SignInBarKind.NeedsApproval, view.Kind);
            Assert.Equal("Sessão iniciada como Ana Teste. Falta confirmar no outro computador.", view.Text);
            Assert.Equal("Ana Teste", view.Bold);
            Assert.Equal(["Ver código", "Fechar"], Labels(view));
            Assert.Equal(SignInBarAction.ShowCode, view.Buttons[0].Action);
            Assert.True(view.Buttons[0].Primary);
            Assert.Null(view.HideAfter);
        }
        Assert.Equal("Sessão iniciada. Falta confirmar no outro computador.", Done(TestStatus.Of(SyncPhase.Pairing), name: null)!.Text);
    }

    [Fact]
    public void Bar_Done_OtherAccount_OpensSettings()
    {
        var view = Done(TestStatus.Of(SyncPhase.Stopped) with { OtherAccount = true })!;
        Assert.Equal(SignInBarKind.OtherAccount, view.Kind);
        Assert.Equal("Entrou com outra conta Google. A sincronização ficou em pausa para não misturar contas.", view.Text);
        Assert.Null(view.Bold);
        Assert.Equal(["Abrir Definições", "Fechar"], Labels(view));
        Assert.Equal(SignInBarAction.OpenSettings, view.Buttons[0].Action);
        Assert.True(view.Buttons[0].Primary);
        Assert.Null(view.HideAfter);

        Assert.Null(Done(TestStatus.Of(SyncPhase.Stopped)));
    }

    [Fact]
    public void Bar_Done_RoundFailed_ShowsNothing()
    {
        Assert.Null(Done(TestStatus.Of(SyncPhase.On, error: "Sem ligação")));
        Assert.Null(Done(TestStatus.Of(SyncPhase.Starting, error: "Sem ligação")));
        Assert.Null(Done(TestStatus.Of(SyncPhase.On, last: At + 10) with { NeedsSignIn = true }));
        Assert.Null(Done(TestStatus.Of(SyncPhase.Off)));
    }

    [Fact]
    public void Bar_NeverShowsTheEmail_WhenThereIsAName()
    {
        foreach (var stage in Enum.GetValues<SignInStage>())
            foreach (var (_, sync) in DoneCases())
                foreach (var url in new[] { null, "endereco?login_hint=ana.teste@example.com" })
                    if (For(stage, sync, "Ana Teste", manualUrl: url) is { } view)
                    {
                        Assert.DoesNotContain("@", view.Text);
                        Assert.All(view.Buttons, b => Assert.DoesNotContain("@", b.Label));
                    }
    }

    [Fact]
    public void Bar_StatesThatStay_CarryClose()
    {
        var close = new SignInBarButton("Fechar", SignInBarAction.Close);
        foreach (var view in new[] { Done(TestStatus.Of(SyncPhase.Stopped) with { OtherAccount = true })!, Done(TestStatus.Of(SyncPhase.Pairing))! })
        {
            Assert.Null(view.HideAfter);
            Assert.Equal(close, view.Buttons[^1]);
        }
        var others = new[]
        {
            For(SignInStage.Waiting)!, For(SignInStage.Waiting, manualUrl: "endereco")!, Done(null)!,
            Done(TestStatus.Of(SyncPhase.On, last: At + 10))!, For(SignInStage.TimedOut)!, For(SignInStage.Refused)!,
            For(SignInStage.DriveDenied)!, For(SignInStage.NoNetwork)!,
        };
        Assert.All(others, v => Assert.DoesNotContain(v.Buttons, b => b.Action == SignInBarAction.Close));
    }

    [Fact]
    public void Bar_Done_NeverOffersCancel()
    {
        foreach (var (name, sync) in DoneCases())
            if (Done(sync) is { } view)
                Assert.False(view.Buttons.Any(b => b.Action == SignInBarAction.Cancel), $"«Cancelar» em Done ({name})");
        // Só enquanto se espera.
        foreach (var stage in new[] { SignInStage.TimedOut, SignInStage.Refused, SignInStage.DriveDenied, SignInStage.NoNetwork })
            Assert.DoesNotContain(For(stage)!.Buttons, b => b.Action == SignInBarAction.Cancel);
    }

    [Fact]
    public void Bar_Done_Synced_Hooks_ReplaceTheTextAndAddALink()
    {
        var synced = TestStatus.Of(SyncPhase.On, last: At + 10);
        SignInBarView? WithHooks(SignInStage stage, SyncStatus? sync, string? text, SignInBarButton? link)
            => SignInBarModel.For(new SignInBarInput(new SignInProgress(stage, null, null, At), sync, "Ana Teste", false)
                { SyncedText = text, SyncedLink = link });

        var plain = WithHooks(SignInStage.Done, synced, null, null)!;
        Assert.Equal("Sincronizado.", plain.Text);
        Assert.Empty(plain.Buttons);
        Assert.Equal(SignInBarModel.SyncedHold, plain.HideAfter);

        Assert.Equal("Texto de teste", WithHooks(SignInStage.Done, synced, "Texto de teste", null)!.Text);

        var link = new SignInBarButton("Ligação de teste", SignInBarAction.Close);
        var linked = WithHooks(SignInStage.Done, synced, null, link)!;
        Assert.Equal(SignInBarKind.Synced, linked.Kind);
        Assert.Equal("Sincronizado.", linked.Text);
        Assert.Equal([link], linked.Buttons);
        Assert.Equal(SignInBarModel.SyncedLinkHold, linked.HideAfter);

        // Os ganchos não mudam nenhum outro estado.
        foreach (var (stage, sync) in new (SignInStage, SyncStatus?)[]
        {
            (SignInStage.Done, null), (SignInStage.Done, TestStatus.Of(SyncPhase.Pairing)),
            (SignInStage.Done, TestStatus.Of(SyncPhase.Stopped) with { OtherAccount = true }),
            (SignInStage.Waiting, null), (SignInStage.TimedOut, null), (SignInStage.NoNetwork, null),
        })
            Assert.Equal(WithHooks(stage, sync, null, null), WithHooks(stage, sync, "Texto de teste", link), ViewComparer.Instance);
    }

    // O árbitro: o número mais baixo ganha.
    [Fact]
    public void Bars_OneAtATime_LowestNumberWins()
    {
        Assert.Equal(WindowBar.SignIn, WindowBars.Pick([WindowBar.SessionExpired, WindowBar.SignIn, WindowBar.KitReminder]));
        Assert.Equal(WindowBar.PairingRequest, WindowBars.Pick([WindowBar.PairingRequest, WindowBar.SignIn]));
        Assert.Equal(WindowBar.AccountReplaced, WindowBars.Pick([WindowBar.KitReminder, WindowBar.AccountReplaced]));
        Assert.Null(WindowBars.Pick([]));
        Assert.Equal([1, 2, 3, 4, 5], Enum.GetValues<WindowBar>().Select(b => (int)b));
    }

    private sealed class Candidate(List<(WindowBar, bool)> log, WindowBar kind, bool wants)
    {
        public bool Wants { get; set; } = wants;
        public int Asked { get; private set; }

        public bool Ask()
        {
            Asked++;
            return Wants;
        }

        public void Show(bool on) => log.Add((kind, on));
    }

    private static (WindowBarSet Set, List<(WindowBar Kind, bool On)> Log, Dictionary<WindowBar, Candidate> Bars) NewSet(params (WindowBar Kind, bool Wants)[] bars)
    {
        var set = new WindowBarSet();
        var log = new List<(WindowBar, bool)>();
        var candidates = new Dictionary<WindowBar, Candidate>();
        foreach (var (kind, wants) in bars)
        {
            var candidate = new Candidate(log, kind, wants);
            candidates[kind] = candidate;
            set.Register(kind, candidate.Ask, candidate.Show);
        }
        return (set, log, candidates);
    }

    [Fact]
    public void BarSet_OnlyTheLowestNumberIsShown()
    {
        var (set, log, bars) = NewSet((WindowBar.KitReminder, true), (WindowBar.SessionExpired, true), (WindowBar.SignIn, true));
        Assert.Equal(WindowBar.SignIn, set.Refresh());
        Assert.Equal([(WindowBar.SignIn, true)], log.Where(e => e.On));
        Assert.Contains((WindowBar.SessionExpired, false), log);
        Assert.Contains((WindowBar.KitReminder, false), log);
        Assert.Equal(3, log.Count);
        Assert.All(bars.Values, b => Assert.Equal(1, b.Asked)); // uma pergunta a cada uma

        // A vencedora deixa de querer: a seguinte aparece.
        log.Clear();
        bars[WindowBar.SignIn].Wants = false;
        Assert.Equal(WindowBar.SessionExpired, set.Refresh());
        Assert.Equal((WindowBar.SessionExpired, true), log[^1]);
    }

    [Fact]
    public void BarSet_HidesTheLosersBeforeShowingTheWinner()
    {
        var (set, log, _) = NewSet((WindowBar.SignIn, true), (WindowBar.KitReminder, true), (WindowBar.PairingRequest, false), (WindowBar.SessionExpired, true));
        Assert.Equal(WindowBar.SignIn, set.Refresh());
        Assert.Equal((WindowBar.SignIn, true), log[^1]);
        // Os outros, por ordem de número.
        Assert.Equal([WindowBar.PairingRequest, WindowBar.SessionExpired, WindowBar.KitReminder], log.Take(log.Count - 1).Select(e => e.Kind));
        Assert.All(log.Take(log.Count - 1), e => Assert.False(e.On));
    }

    [Fact]
    public void BarSet_NobodyWants_HidesAll_ReturnsNull()
    {
        var (set, log, _) = NewSet((WindowBar.SignIn, false), (WindowBar.KitReminder, false));
        Assert.Null(set.Refresh());
        Assert.Equal(2, log.Count);
        Assert.All(log, e => Assert.False(e.On));

        Assert.Null(new WindowBarSet().Refresh());
    }

    [Fact]
    public void BarSet_RegisteringTheSameKindTwice_Throws()
    {
        var set = new WindowBarSet();
        set.Register(WindowBar.SignIn, () => true, _ => { });
        Assert.Throws<InvalidOperationException>(() => set.Register(WindowBar.SignIn, () => false, _ => { }));
    }

    [Fact]
    public void BarSet_ReentrantRefresh_IsIgnored()
    {
        var set = new WindowBarSet();
        var shows = new List<bool>();
        var inner = new List<WindowBar?>();
        set.Register(WindowBar.SignIn, () => true, on =>
        {
            shows.Add(on);
            inner.Add(set.Refresh());
        });
        var asked = 0;
        set.Register(WindowBar.KitReminder, () =>
        {
            asked++;
            inner.Add(set.Refresh());
            return true;
        }, _ => { });
        Assert.Equal(WindowBar.SignIn, set.Refresh());
        Assert.Equal([true], shows);
        Assert.Equal(1, asked);
        Assert.All(inner, r => Assert.Null(r));
        Assert.Equal(2, inner.Count);

        // Depois, uma chamada normal volta a funcionar.
        Assert.Equal(WindowBar.SignIn, set.Refresh());
        Assert.Equal([true, true], shows);
    }

    // ---- sessão expirada (T11) ---------------------------------------------------------------------------------------

    [Fact]
    public void Bar_Expired_HasTheExactText_EnterThenNotNow()
    {
        var view = SignInBarModel.Expired(true, false);
        Assert.NotNull(view);
        Assert.Equal(SignInBarKind.Expired, view.Kind);
        Assert.Equal("A sincronização está em pausa: a Google pediu para entrar outra vez.", view.Text);
        Assert.Null(view.Bold);
        Assert.Null(view.HideAfter);
        Assert.Equal(["Entrar", "Agora não"], Labels(view));
        Assert.Equal([SignInBarAction.SignInAgain, SignInBarAction.NotNow], view.Buttons.Select(b => b.Action));
        Assert.True(view.Buttons[0].Primary);
        Assert.False(view.Buttons[1].Primary);
        Assert.Null(SignInBarModel.Expired(false, false));
    }

    [Fact]
    public void Bar_Expired_NeverInAPrivateWindow()
    {
        Assert.Null(SignInBarModel.Expired(true, true));
        Assert.Null(SignInBarModel.Expired(false, true));
    }

    // As duas candidatas montadas como a janela as monta (MainWindow.SignInBar.cs), sobre o árbitro da T4.
    private static (WindowBarSet Set, List<(WindowBar Kind, bool On)> Log) SignInAndExpired(SignInBarInput input, bool expired)
    {
        var set = new WindowBarSet();
        var log = new List<(WindowBar, bool)>();
        set.Register(WindowBar.SignIn, () => SignInBarModel.For(input) is not null, on => log.Add((WindowBar.SignIn, on)));
        set.Register(WindowBar.SessionExpired, () => SignInBarModel.Expired(expired, false) is not null, on => log.Add((WindowBar.SessionExpired, on)));
        return (set, log);
    }

    [Fact]
    public void Bar_Expired_YieldsToASignInInProgress()
    {
        var input = new SignInBarInput(new SignInProgress(SignInStage.Waiting, null, null, At), null, "Ana Teste", false);
        var (set, log) = SignInAndExpired(input, expired: true);
        Assert.Equal(WindowBar.SignIn, set.Refresh());
        Assert.Equal([(WindowBar.SignIn, true)], log.Where(e => e.On));
        Assert.Contains((WindowBar.SessionExpired, false), log);
        Assert.Equal("À espera do início de sessão no seu navegador…", SignInBarModel.For(input)!.Text);
    }

    [Fact]
    public void Bar_Expired_ShowsWhenNothingElseWants()
    {
        var input = new SignInBarInput(SignInProgress.None, null, "Ana Teste", false);
        Assert.Equal(SignInStage.Idle, input.Progress.Stage);
        var (set, log) = SignInAndExpired(input, expired: true);
        Assert.Equal(WindowBar.SessionExpired, set.Refresh());
        // O controlo é partilhado: a do início de sessão esconde-se antes de a sessão expirada se mostrar.
        Assert.Equal([(WindowBar.SignIn, false), (WindowBar.SessionExpired, true)], log);
    }

    [Fact]
    public void Bar_NothingWanted_IsNull()
    {
        var (set, log) = SignInAndExpired(new SignInBarInput(SignInProgress.None, null, "Ana Teste", false), expired: false);
        Assert.Null(set.Refresh());
        Assert.Equal([(WindowBar.SignIn, false), (WindowBar.SessionExpired, false)], log);
    }

    // ---- lembrete do kit de recuperação (T5) -----------------------------------------------------------------------

    [Fact]
    public void KitReminder_View_AlwaysHasTheThreeButtons()
    {
        string[] labels = [RecoveryReminder.Save, RecoveryReminder.Later, RecoveryReminder.Decline];
        SignInBarAction[] actions = [SignInBarAction.SaveKit, SignInBarAction.KitLater, SignInBarAction.KitDecline];

        var view = SignInBarModel.KitReminder(new KitReminderView(true, true, false))!;
        Assert.Equal(SignInBarKind.KitReminder, view.Kind);
        Assert.Equal(RecoveryReminder.Title + " " + RecoveryReminder.Body, view.Text);
        Assert.Equal(RecoveryReminder.Title, view.Bold);
        Assert.Equal(labels, view.Buttons.Select(b => b.Label));
        Assert.Equal(actions, view.Buttons.Select(b => b.Action));
        Assert.Equal([true, false, false], view.Buttons.Select(b => b.Primary));
        Assert.Null(view.HideAfter);

        var stale = SignInBarModel.KitReminder(new KitReminderView(true, true, true))!;
        Assert.Equal(SignInBarKind.KitReminder, stale.Kind);
        Assert.Equal(RecoveryReminder.StaleText, stale.Text);
        Assert.Null(stale.Bold);
        Assert.Equal(labels, stale.Buttons.Select(b => b.Label));
        Assert.Equal(actions, stale.Buttons.Select(b => b.Action));
        Assert.Null(stale.HideAfter);

        Assert.Null(SignInBarModel.KitReminder(null));
        Assert.Null(SignInBarModel.KitReminder(new KitReminderView(true, false, false)));
    }

    [Fact]
    public void KitReminder_IsTheLowestPriorityCandidate()
    {
        foreach (var other in new[] { WindowBar.PairingRequest, WindowBar.AccountReplaced, WindowBar.SignIn, WindowBar.SessionExpired })
        {
            var (set, log, _) = NewSet((WindowBar.KitReminder, true), (other, true));
            Assert.Equal(other, set.Refresh());
            Assert.Contains((WindowBar.KitReminder, false), log);
            Assert.DoesNotContain((WindowBar.KitReminder, true), log);
        }
        var (alone, aloneLog, _) = NewSet((WindowBar.KitReminder, true), (WindowBar.SignIn, false));
        Assert.Equal(WindowBar.KitReminder, alone.Refresh());
        Assert.Contains((WindowBar.KitReminder, true), aloneLog);
    }

    // Duas vistas iguais (os botões comparam-se elemento a elemento).
    private sealed class ViewComparer : IEqualityComparer<SignInBarView?>
    {
        public static readonly ViewComparer Instance = new();

        public bool Equals(SignInBarView? a, SignInBarView? b)
            => a is null || b is null
                ? a is null && b is null
                : a.Kind == b.Kind && a.Text == b.Text && a.Bold == b.Bold && a.HideAfter == b.HideAfter && a.Buttons.SequenceEqual(b.Buttons);

        public int GetHashCode(SignInBarView? view) => view?.Text.GetHashCode() ?? 0;
    }
}
