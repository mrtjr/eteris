using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

// Rede de segurança do sandbox (docs/sandbox.md, «Rede de segurança»): o Éteris abre sempre, sem sandbox se for preciso.
public class SandboxFallbackTests
{
    private const string V = "0.2.14";

    private static SandboxStart Start(SandboxFallbackState previous, string version = V, bool available = true,
        bool manualOff = false, bool retried = false, bool tweaks = false)
        => SandboxFallback.Begin(previous, version, available, manualOff, retried, tweaks);

    // Simula uma queda: o estado gravado no Begin fica no disco tal como está (Starting = true).
    private static SandboxFallbackState Crash(SandboxStart start) => start.State;

    [Fact]
    public void FirstStart_UsesTheSandboxAndMarksItself()
    {
        var start = Start(new());
        Assert.True(start.UseSandbox);
        Assert.Equal(SandboxNotice.None, start.Notice);
        Assert.True(start.State.Starting);
        Assert.True(start.State.WithSandbox);
        Assert.Equal(0, start.State.Failures);
    }

    [Fact]
    public void TwoCrashesInARow_DisableTheSandboxForThisVersion_WithANotice()
    {
        var s1 = Start(new());
        var s2 = Start(Crash(s1));
        Assert.True(s2.UseSandbox);
        Assert.Equal(1, s2.State.Failures);
        var s3 = Start(Crash(s2));
        Assert.False(s3.UseSandbox);
        Assert.Equal(SandboxNotice.DisabledAfterFailures, s3.Notice);
        Assert.Equal(V, s3.State.DisabledFor);
        // Os arranques seguintes desta versão continuam sem sandbox (e a avisar), mesmo que corram bem.
        var s4 = Start(SandboxFallback.Healthy(s3.State));
        Assert.False(s4.UseSandbox);
        Assert.Equal(SandboxNotice.DisabledAfterFailures, s4.Notice);
    }

    [Fact]
    public void AGoodStartInBetween_ResetsTheCount()
    {
        var s1 = Start(new());
        var s2 = Start(Crash(s1));
        var s3 = Start(SandboxFallback.Healthy(s2.State));
        Assert.Equal(0, s3.State.Failures);
        var s4 = Start(Crash(s3));
        Assert.True(s4.UseSandbox);
        Assert.Equal(1, s4.State.Failures);
    }

    [Fact]
    public void NextVersion_TriesTheSandboxAgain()
    {
        var disabled = Start(Crash(Start(Crash(Start(new())))));
        Assert.False(disabled.UseSandbox);
        var next = Start(SandboxFallback.Healthy(disabled.State), version: "0.2.15");
        Assert.True(next.UseSandbox);
        Assert.Equal(SandboxNotice.None, next.Notice);
        Assert.Null(next.State.DisabledFor);
        Assert.Equal(0, next.State.Failures);
        // E se a versão nova também cai duas vezes, volta a desligar (para ela).
        var again = Start(Crash(Start(Crash(next), version: "0.2.15")), version: "0.2.15");
        Assert.False(again.UseSandbox);
        Assert.Equal("0.2.15", again.State.DisabledFor);
    }

    // O ETERIS_TEST_CRASH_BEFORE_PAINT só serve as provas numa cópia portátil: numa instalação (Update.exe na pasta de
    // cima) é ignorado, para uma variável esquecida não fazer cair o Éteris instalado duas vezes e desligar o sandbox até
    // à versão seguinte.
    [Theory]
    [InlineData("1", false, true)]
    [InlineData(" 1 ", false, true)]
    [InlineData("1", true, false)]
    [InlineData("0", false, false)]
    [InlineData(null, false, false)]
    [InlineData(null, true, false)]
    public void CrashBeforePaint_IsInertInAnInstall(string? value, bool installed, bool expected)
        => Assert.Equal(expected, TestSwitches.CrashBeforePaintFor(value, installed));

    // Uma só regra de instalação, a do TestSwitches.IsInstalled (desde a fusão da 0.2.13).
    [Fact]
    public void CrashBeforePaint_ReadsTheInstallFromTheSwitches()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Infrastructure", "TestSwitches.cs"));
        Assert.Matches(@"CrashBeforePaint\s*=>\s*CrashBeforePaintFor\(Environment\.GetEnvironmentVariable\(""ETERIS_TEST_CRASH_BEFORE_PAINT""\),\s*IsInstalled\)", source);
        Assert.Matches(@"Reauth\s*=>\s*ReauthFor\(Environment\.GetEnvironmentVariable\(""ETERIS_TEST_REAUTH""\),\s*IsInstalled\)", source);
    }

    // A pasta de teste da sincronização, o ETERIS_TEST_REAUTH e o ETERIS_TEST_CRASH_BEFORE_PAINT decidem «instalado» pela
    // mesma regra: o SyncService já não tem a sua cópia (eram duas iguais até à fusão da 0.2.13).
    [Fact]
    public void InstallRule_LivesOnlyInTheSwitches()
    {
        var sync = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Sync", "SyncService.cs"));
        Assert.DoesNotContain("Update.exe\")", sync);
        Assert.DoesNotContain("bool IsInstalled", sync);
        Assert.Matches(@"TestFolderFor\(Environment\.GetEnvironmentVariable\(""ETERIS_TEST_SYNC_FOLDER""\),\s*TestSwitches\.IsInstalled\)", sync);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    // Uma queda sem sandbox (manual) não é culpa dele: não conta.
    [Fact]
    public void CrashesWithoutTheSandbox_DoNotCount()
    {
        var s1 = Start(new(), manualOff: true);
        Assert.False(s1.UseSandbox);
        Assert.Equal(SandboxNotice.None, s1.Notice);
        var s2 = Start(Crash(s1), manualOff: true);
        var s3 = Start(Crash(s2));
        Assert.True(s3.UseSandbox);
        Assert.Equal(0, s3.State.Failures);
    }

    // --eteris-no-sandbox continua a desligá-lo sempre; com o interruptor da DLL cliente (o .NET não arrancou com sandbox)
    // avisa só nesse arranque.
    [Fact]
    public void ManualSwitch_AndTheClientRetry()
    {
        Assert.False(Start(new(), manualOff: true).UseSandbox);
        var retried = Start(new(), manualOff: true, retried: true);
        Assert.False(retried.UseSandbox);
        Assert.Equal(SandboxNotice.RetriedWithoutSandbox, retried.Notice);
        // Sem o --eteris-no-sandbox, o interruptor da DLL sozinho não muda nada.
        Assert.True(Start(new(), retried: true).UseSandbox);
        var next = Start(SandboxFallback.Healthy(retried.State));
        Assert.True(next.UseSandbox);
        Assert.Equal(SandboxNotice.None, next.Notice);
    }

    [Fact]
    public void WithoutSandboxSupport_NothingChanges()
    {
        var start = Start(new(), available: false);
        Assert.False(start.UseSandbox);
        Assert.Equal(SandboxNotice.None, start.Notice);
    }

    // Filhos a morrer no arranque: contam como um arranque falhado, mesmo que a página apareça depois.
    [Fact]
    public void FailedStart_CountsLikeACrash()
    {
        var s1 = Start(new());
        var s2 = Start(SandboxFallback.Failed(s1.State));
        Assert.Equal(1, s2.State.Failures);
        var s3 = Start(SandboxFallback.Failed(s2.State));
        Assert.False(s3.UseSandbox);
        Assert.Equal(SandboxNotice.DisabledAfterFailures, s3.Notice);
        // Sem sandbox, uma falha dos filhos não conta.
        Assert.Equal(0, SandboxFallback.Failed(Start(new(), manualOff: true).State).Failures);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]          // RESULT_CODE_KILLED (o Chromium fechou-o)
    [InlineData(3u, false)]          // RESULT_CODE_KILLED_BAD_MESSAGE
    [InlineData(7012u, false)]       // SBOX_FATAL_MEMORY_EXCEEDED: a página gastou memória a mais
    [InlineData(7006u, true)]        // SBOX_FATAL_INTEGRITY
    [InlineData(7011u, true)]        // SBOX_FATAL_MITIGATION
    [InlineData(7013u, true)]        // SBOX_FATAL_WARMUP
    [InlineData(0x80000003u, true)]  // ponto de paragem (CHECK do Chromium)
    [InlineData(0xC0000005u, true)]  // acesso inválido
    [InlineData(0xC0000135u, true)]  // DLL em falta
    [InlineData(0xC0000409u, true)]  // pilha corrompida / fail-fast
    public void ChildExitCodes(uint code, bool counts) => Assert.Equal(counts, SandboxFallback.IsChildFailure(code));

    [Theory]
    [InlineData(0, false)] // TS_ABNORMAL_TERMINATION
    [InlineData(1, false)] // TS_PROCESS_WAS_KILLED
    [InlineData(2, false)] // TS_PROCESS_CRASHED (já visto pela vigia, com o código de saída)
    [InlineData(3, false)] // TS_PROCESS_OOM
    [InlineData(4, true)]  // TS_LAUNCH_FAILED
    [InlineData(5, true)]  // TS_INTEGRITY_FAILURE
    public void PageLaunchFailures(int status, bool counts) => Assert.Equal(counts, SandboxFallback.IsLaunchFailure(status));

    // ---------- Em dois degraus (D-18 do plano de desempenho): primeiro os ajustes, só depois o sandbox ----------

    // A 1.ª queda com ajustes antes da 1.ª página desliga-os nesta versão (o sandbox continua); a 2.ª, já sem eles,
    // desliga o sandbox.
    [Fact]
    public void TwoSteps_FirstCrashWithTweaks_TurnsThemOff_SecondTurnsTheSandboxOff()
    {
        var s1 = Start(new(), tweaks: true);
        Assert.True(s1.State.WithTweaks);
        Assert.False(SandboxFallback.TweaksSuspended(s1.State with { Starting = false }, V));
        // Caiu: o Program.Main do arranque seguinte vê-o antes de ler um ajuste.
        Assert.True(SandboxFallback.TweaksSuspended(Crash(s1), V));
        var s2 = Start(Crash(s1), tweaks: false);
        Assert.True(s2.UseSandbox);
        Assert.Equal(SandboxNotice.None, s2.Notice);
        Assert.Equal(V, s2.State.TweaksDisabledFor);
        Assert.False(s2.State.WithTweaks);
        Assert.Equal(1, s2.State.Failures);
        var s3 = Start(Crash(s2));
        Assert.False(s3.UseSandbox);
        Assert.Equal(SandboxNotice.DisabledAfterFailures, s3.Notice);
    }

    // Os ajustes desligados por precaução ficam desligados nos arranques bons seguintes desta versão (senão voltariam a
    // cair, um arranque sim, outro não), mesmo que o chamador ainda os peça; a versão seguinte volta a tentar.
    [Fact]
    public void TweaksStayOffForThisVersion_AndComeBackInTheNext()
    {
        var s2 = Start(Crash(Start(new(), tweaks: true)));
        var s3 = Start(SandboxFallback.Healthy(s2.State), tweaks: true);
        Assert.Equal(0, s3.State.Failures);
        Assert.Equal(V, s3.State.TweaksDisabledFor);
        Assert.False(s3.State.WithTweaks);
        Assert.True(SandboxFallback.TweaksSuspended(SandboxFallback.Healthy(s3.State), V));
        var next = Start(SandboxFallback.Healthy(s3.State), version: "0.2.15", tweaks: true);
        Assert.Null(next.State.TweaksDisabledFor);
        Assert.True(next.State.WithTweaks);
        Assert.False(SandboxFallback.TweaksSuspended(SandboxFallback.Healthy(s3.State), "0.2.15"));
    }

    // Sem ajustes antes da 1.ª página (a configuração de omissão) não há 1.º degrau: as quedas contam para o sandbox,
    // como antes, e nada se desliga nos ajustes.
    [Fact]
    public void WithoutTweaks_TheCrashCountsOnlyForTheSandbox()
    {
        var s2 = Start(Crash(Start(new())));
        Assert.Null(s2.State.TweaksDisabledFor);
        Assert.False(SandboxFallback.TweaksSuspended(Crash(Start(new())), V));
        Assert.False(Start(Crash(s2)).UseSandbox);
        // Uma queda sem sandbox (desligado à mão) não conta para nada, nem para os ajustes.
        var manual = Start(new(), manualOff: true, tweaks: true);
        Assert.False(SandboxFallback.TweaksSuspended(Crash(manual), V));
        Assert.Null(Start(Crash(manual), tweaks: true).State.TweaksDisabledFor);
    }

    // Filhos a falhar logo no arranque (por exemplo a placa gráfica com uma funcionalidade do E14): o mesmo 1.º degrau.
    [Fact]
    public void TwoSteps_ChildFailures_AlsoTurnTheTweaksOff()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, SandboxFallback.FileName);
            var guard = new StartupGuard(file, () => TimeSpan.FromSeconds(2));
            Assert.True(guard.Begin(V, true, false, false, tweaks: true).State.WithTweaks);
            for (var i = 0; i < SandboxFallback.ChildFailuresToCount; i++) guard.ChildFailed(0xC0000005);
            guard.Flush();
            var state = StartupGuard.Read(file);
            Assert.Equal(1, state.Failures);
            Assert.Equal(V, state.TweaksDisabledFor);
            Assert.True(SandboxFallback.TweaksSuspended(state, V));
            var next = new StartupGuard(file, () => TimeSpan.Zero).Begin(V, true, false, false, tweaks: false);
            Assert.True(next.UseSandbox);
            Assert.False(next.State.WithTweaks);
            // Sem ajustes, a mesma falha não desliga nada além do sandbox à 2.ª.
            Assert.Null(SandboxFallback.Failed(new SandboxFallbackState { WithSandbox = true }, V).TweaksDisabledFor);
        }
        finally { Directory.Delete(dir, true); }
    }

    // O 1.º degrau decide-se no início do Program.Main, antes da 1.ª leitura de um ajuste (o e10, antes do Velopack.Run),
    // e só lê o disco com ajustes antes da 1.ª página ligados e o sandbox possível (regra R-2: nada de novo na omissão).
    [Fact]
    public void TwoSteps_AreCheckedAtTheTopOfMain()
    {
        var main = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Program.cs"));
        var check = main.IndexOf("StartupGuard.SuspendTweaksAfterAFailedStart(args)", StringComparison.Ordinal);
        Assert.True(check > 0);
        Assert.True(check < main.IndexOf("PerfTuning.IsOn(", StringComparison.Ordinal));
        Assert.True(check < main.IndexOf("velopack.Run()", StringComparison.Ordinal));
        var guard = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Infrastructure", "StartupGuard.cs"));
        Assert.Matches(@"if \(!PerfTuning\.AnyBeforeFirstPaint \|\| !CefConfigurator\.SandboxSupported \|\| SandboxInfo\.Available == 0\) return false;", guard);
        var prepare = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Infrastructure", "CefConfigurator.cs"));
        Assert.Contains("PerfTuning.AnyBeforeFirstPaint);", prepare);
    }

    // O lado com ficheiros: marcador no disco antes do motor, limpo pela 1.ª página, filhos a falhar no arranque.
    [Fact]
    public void Guard_WritesTheMarker_AndClearsItOnTheFirstPage()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, SandboxFallback.FileName);
            var guard = new StartupGuard(file, () => TimeSpan.FromSeconds(1));
            Assert.True(guard.Begin(V, available: true, manualOff: false, retried: false).UseSandbox);
            Assert.True(StartupGuard.Read(file).Starting);
            guard.Healthy();
            guard.Flush();
            Assert.False(StartupGuard.Read(file).Starting);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Guard_TwoCrashedStarts_ThenOpensWithoutSandbox()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, SandboxFallback.FileName);
            new StartupGuard(file, () => TimeSpan.Zero).Begin(V, true, false, false);  // caiu
            new StartupGuard(file, () => TimeSpan.Zero).Begin(V, true, false, false);  // caiu outra vez
            var third = new StartupGuard(file, () => TimeSpan.Zero);
            var start = third.Begin(V, true, false, false);
            Assert.False(start.UseSandbox);
            Assert.Equal(SandboxNotice.DisabledAfterFailures, third.Notice);
            Assert.NotNull(SandboxFallback.NoticeText(third.Notice));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Guard_ChildFailuresEarly_CountEvenAfterTheFirstPage()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, SandboxFallback.FileName);
            var guard = new StartupGuard(file, () => TimeSpan.FromSeconds(5));
            guard.Begin(V, true, false, false);
            guard.Healthy();
            guard.ChildFailed(0);                 // saída normal
            guard.ChildFailed(0xC0000005);
            guard.ChildFailed(0xC0000005, at: SandboxFallback.ChildWindow + TimeSpan.FromSeconds(1)); // tarde demais
            guard.RenderProcessTerminated(2);     // a vigia já viu esta
            guard.ChildFailed(0x80000003);
            Assert.Equal(2, guard.ChildFailures);
            guard.RenderProcessTerminated(4);     // TS_LAUNCH_FAILED
            Assert.Equal(3, guard.ChildFailures);
            guard.Flush();
            var state = StartupGuard.Read(file);
            Assert.False(state.Starting);
            Assert.Equal(1, state.Failures);
            // Uma 1.ª página depois disso já não zera a contagem.
            guard.Healthy();
            guard.Flush();
            Assert.Equal(1, StartupGuard.Read(file).Failures);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Guard_IsInertWithoutSandboxSupport_AndToleratesABrokenFile()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, SandboxFallback.FileName);
            var guard = new StartupGuard(file, () => TimeSpan.Zero);
            Assert.False(guard.Begin(V, available: false, false, false).UseSandbox);
            Assert.False(guard.Active);
            Assert.False(File.Exists(file));
            guard.ChildFailed(0xC0000005);
            guard.ChildFailed(0xC0000005);
            guard.ChildFailed(0xC0000005);
            guard.Healthy();
            guard.Flush();
            Assert.False(File.Exists(file));

            File.WriteAllText(file, "{ partido");
            Assert.True(new StartupGuard(file, () => TimeSpan.Zero).Begin(V, true, false, false).UseSandbox);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NewTab_ShowsTheNotice_AlsoInPrivate()
    {
        var engine = SearchEngine.DuckDuckGo;
        var text = SandboxFallback.NoticeText(SandboxNotice.DisabledAfterFailures)!;
        Assert.Contains("isolamento", NewTabPage.Render([], engine, DateTime.Now, notice: text));
        Assert.Contains("isolamento", NewTabPage.Render([], engine, DateTime.Now, isPrivate: true, notice: text));
        Assert.DoesNotContain("isolamento das p", NewTabPage.Render([], engine, DateTime.Now));
        Assert.Null(SandboxFallback.NoticeText(SandboxNotice.None));
    }

    // O mesmo interruptor dos dois lados: a DLL cliente acrescenta-os ao voltar a arrancar o Éteris.
    [Fact]
    public void ClientDll_UsesTheSameSwitches()
    {
        var cpp = File.ReadAllText(Path.Combine(Root(), "build", "sandbox", "client", "eteris_client.cpp"));
        Assert.Contains($"kNoSandboxSwitch[] = L\"{SandboxInfo.NoSandboxSwitch}\"", cpp);
        Assert.Contains($"kSandboxRetrySwitch[] = L\"{SandboxFallback.RetrySwitch}\"", cpp);
        // Nos dois caminhos de falha do .NET (hostfxr em falta e erro do host) tenta de novo antes do aviso.
        Assert.Equal(2, Regex.Matches(cpp, @"if \(RelaunchWithoutSandbox\(argc, argv\)\) return 0;\s*\n\s*(ReportStartupFailure|wchar_t hex)").Count);
        // Os ganchos de teste só existem compilados à parte.
        Assert.Matches(@"#if defined\(ETERIS_TEST_HOOKS\)[\s\S]*ETERIS_TEST_DOTNET_FAILS_WITH_SANDBOX[\s\S]*#else", cpp);
        var verify = File.ReadAllText(Path.Combine(Root(), "build", "sandbox", "verify-client.ps1"));
        Assert.Contains("test_hooks", verify);
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eteris-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
