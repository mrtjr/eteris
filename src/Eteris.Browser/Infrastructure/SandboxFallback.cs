namespace Eteris.Browser.Infrastructure;

// Rede de segurança do sandbox (docs/sandbox.md, «Rede de segurança»): se o Éteris não arranca bem com o sandbox, tem de
// abrir na mesma, sem ele, e avisar — o atualizador vive dentro do Éteris, por isso nunca pode ficar sem abrir.
//   - Marcador no perfil (StartupGuard): «a arrancar» gravado antes do Cef.Initialize, limpo quando a 1.ª página aparece
//     (ou num fecho normal). Se no arranque seguinte ainda lá está, o anterior caiu.
//   - Processos filhos (páginas, placa gráfica, utilitários) que morrem com um código de falha logo no arranque também
//     contam: ChildFailuresToCount falhas nos primeiros ChildWindow fazem desse arranque um arranque falhado.
//   - Em dois degraus (D-18 do plano de desempenho da 0.2.14): o 1.º arranque falhado com ajustes de desempenho que mexem
//     no caminho antes da 1.ª página (PerfTuning.AnyBeforeFirstPaint) desliga-os nos arranques seguintes desta versão
//     (PerfTuning.Suspend, já no Program.Main); sem esses ajustes, não há 1.º degrau e a falha só conta para o sandbox.
//   - FailuresToDisable arranques falhados seguidos COM sandbox (o do 1.º degrau incluído): os arranques seguintes desta
//     versão correm sem ele (como com --eteris-no-sandbox) e a nova aba avisa. A versão seguinte volta a tentar os dois.
//   - O .NET que nem arranca é tratado antes, na DLL cliente do bootstrap (build/sandbox/client: novo arranque com
//     --eteris-no-sandbox e RetrySwitch); aqui só se avisa.
// Esta classe é só a lógica (sem ficheiros nem processos): StartupGuard grava o estado e vigia os filhos.
public sealed record SandboxFallbackState
{
    // Um arranque começou e ainda não se deu por bom nem por falhado.
    public bool Starting { get; init; }

    // Esse arranque (o que está marcado em Starting) corre com o sandbox.
    public bool WithSandbox { get; init; }

    // Arranques com sandbox falhados seguidos.
    public int Failures { get; init; }

    // Versão do Éteris em que o sandbox foi desligado por precaução (nula: ligado).
    public string? DisabledFor { get; init; }

    // O arranque marcado em Starting corre com ajustes de desempenho antes da 1.ª página (PerfTuning.AnyBeforeFirstPaint).
    public bool WithTweaks { get; init; }

    // Versão do Éteris em que os ajustes de desempenho foram desligados por precaução (nula: como estiverem).
    public string? TweaksDisabledFor { get; init; }
}

public enum SandboxNotice
{
    None,
    // Desligado por precaução nesta versão (FailuresToDisable arranques falhados seguidos com ele).
    DisabledAfterFailures,
    // A DLL cliente voltou a arrancar o Éteris sem sandbox porque o .NET não arrancou com ele (só este arranque).
    RetriedWithoutSandbox,
}

public readonly record struct SandboxStart(SandboxFallbackState State, bool UseSandbox, SandboxNotice Notice);

public static class SandboxFallback
{
    public const int FailuresToDisable = 2;
    public const int ChildFailuresToCount = 3;
    public static readonly TimeSpan ChildWindow = TimeSpan.FromSeconds(30);

    // Posto pela DLL cliente (com --eteris-no-sandbox) quando volta a arrancar o Éteris depois de o .NET falhar com o sandbox.
    public const string RetrySwitch = "--eteris-sandbox-retry";

    // Ficheiro do estado, na pasta do perfil (AppPaths.Root).
    public const string FileName = "sandbox-fallback.json";

    // Início de um arranque. «available»: este Éteris pode ter sandbox (CefSharp do Éteris e ponteiro do bootstrap);
    // «manualOff»: --eteris-no-sandbox na linha de comandos; «retried»: veio do novo arranque da DLL cliente; «tweaks»:
    // este arranque corre com ajustes de desempenho antes da 1.ª página (já sem eles se TweaksSuspended os desligou).
    public static SandboxStart Begin(SandboxFallbackState previous, string version, bool available, bool manualOff, bool retried,
                                     bool tweaks = false)
    {
        var state = previous;
        // Versão nova: volta a tentar com o sandbox e com os ajustes, do zero.
        if (state.DisabledFor is { } disabled && !string.Equals(disabled, version, StringComparison.Ordinal))
            state = state with { DisabledFor = null, Failures = 0 };
        if (state.TweaksDisabledFor is { } tweaksOff && !string.Equals(tweaksOff, version, StringComparison.Ordinal))
            state = state with { TweaksDisabledFor = null };
        // O arranque anterior não chegou à 1.ª página nem a um fecho normal: caiu. Só conta se corria com o sandbox; se
        // corria também com ajustes antes da 1.ª página, é o 1.º degrau: os ajustes desligam-se nesta versão.
        if (state.Starting && state.WithSandbox)
        {
            state = state with { Failures = state.Failures + 1 };
            if (state.WithTweaks) state = state with { TweaksDisabledFor = state.TweaksDisabledFor ?? version };
        }
        if (state.DisabledFor is null && state.Failures >= FailuresToDisable) state = state with { DisabledFor = version };

        var notice = SandboxNotice.None;
        bool use;
        if (!available) use = false;
        else if (manualOff)
        {
            use = false;
            if (retried) notice = SandboxNotice.RetriedWithoutSandbox;
        }
        else if (state.DisabledFor is not null)
        {
            use = false;
            notice = SandboxNotice.DisabledAfterFailures;
        }
        else use = true;
        // Com os ajustes desligados nesta versão, este arranque não os tem, diga o chamador o que disser.
        var withTweaks = tweaks && state.TweaksDisabledFor is null;
        return new SandboxStart(state with { Starting = true, WithSandbox = use, WithTweaks = withTweaks }, use, notice);
    }

    // O 1.º degrau, lido no início do Program.Main (antes da 1.ª leitura de um ajuste; o Begin só corre no Prepare): os
    // ajustes de desempenho já foram desligados nesta versão, ou o arranque anterior caiu com eles e com o sandbox (o
    // Begin vai gravar isso mesmo). A mesma regra do Begin.
    public static bool TweaksSuspended(SandboxFallbackState previous, string version)
        => string.Equals(previous.TweaksDisabledFor, version, StringComparison.Ordinal)
           || (previous.Starting && previous.WithSandbox && previous.WithTweaks);

    // O arranque correu bem (a 1.ª página apareceu, ou fecho normal): um arranque com sandbox bom zera a contagem. Os
    // ajustes desligados por precaução continuam desligados até à versão seguinte (senão voltariam a cair a seguir).
    public static SandboxFallbackState Healthy(SandboxFallbackState begun)
        => begun with { Starting = false, Failures = begun.WithSandbox ? 0 : begun.Failures };

    // O arranque falhou sem cair (filhos a morrer logo no arranque, por exemplo a placa gráfica com uma funcionalidade
    // do E14): conta já, como se tivesse caído, e com ajustes antes da 1.ª página é o 1.º degrau (desliga-os em «version»).
    public static SandboxFallbackState Failed(SandboxFallbackState begun, string? version = null)
    {
        if (!begun.WithSandbox) return begun with { Starting = false };
        var failed = begun with { Starting = false, Failures = begun.Failures + 1 };
        return begun.WithTweaks && version is not null ? failed with { TweaksDisabledFor = failed.TweaksDisabledFor ?? version } : failed;
    }

    // Código de saída de um processo filho que conta como falha: exceções do Windows (NTSTATUS de erro, 0xC…: acesso
    // inválido, DLL em falta, pilha…), o ponto de paragem dos CHECK do Chromium (0x80000003) e os códigos com que o
    // sandbox mata um filho que não consegue fechar (sandbox/win/src/sandbox_types.h: SBOX_FATAL_INTEGRITY 7006 a
    // SBOX_FATAL_MITIGATION 7011 e SBOX_FATAL_WARMUP 7013). Não contam: saídas normais (0) e as do Chromium ao fechar
    // ou matar um filho de propósito (1–5), nem o limite de memória do processo (SBOX_FATAL_MEMORY_EXCEEDED, 7012).
    public static bool IsChildFailure(uint exitCode)
        => exitCode >= 0xC0000000u || exitCode == 0x80000003u || exitCode is >= 7006 and <= 7011 or 7013;

    // Estados de terminação de uma página (CefTerminationStatus da CEF; o enum do CefSharp 156 só vai até 3): o motor
    // não conseguiu lançar o processo (TS_LAUNCH_FAILED = 4) ou matou-o por falha de integridade do código
    // (TS_INTEGRITY_FAILURE = 5). Estes não deixam um código de saída de falha para a vigia dos filhos ver.
    public static bool IsLaunchFailure(int terminationStatus) => terminationStatus is 4 or 5;

    // Falhas de filhos que contam: só no início do arranque.
    public static bool InWindow(TimeSpan sinceStart) => sinceStart >= TimeSpan.Zero && sinceStart <= ChildWindow;

    public static string? NoticeText(SandboxNotice notice) => notice switch
    {
        SandboxNotice.DisabledAfterFailures =>
            "O isolamento das páginas (sandbox) foi desligado por precaução: o Éteris não arrancou bem com ele nas duas " +
            "últimas vezes. As páginas abrem na mesma, com menos proteção; na próxima versão do Éteris volta a ser ligado.",
        SandboxNotice.RetriedWithoutSandbox =>
            "O isolamento das páginas (sandbox) foi desligado neste arranque: o Éteris não conseguiu arrancar com ele e " +
            "abriu sem ele. As páginas abrem na mesma, com menos proteção; da próxima vez volta a tentar.",
        _ => null,
    };
}
