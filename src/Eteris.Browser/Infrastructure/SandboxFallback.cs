using System.Globalization;

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
//   - Cada falha contada deixa escrito de onde veio (Origins, no mesmo estado): sem isso, um sandbox desligado «até à
//     versão seguinte» não dizia a ninguém o que tinha caído (risco R-2 do plano do sandbox da 0.2.14).
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

    // De onde veio cada falha contada em Failures: uma linha por falha, a mais recente no fim, separadas por '\n'.
    //   «<hora UTC> <versão> queda-antes-da-1a-pagina[ com-ajustes]»: o arranque anterior não chegou à 1.ª página nem a
    //     um fecho normal; a hora e a versão são as do arranque seguinte, que é quem dá por isso.
    //   «<hora UTC> <versão> filhos <tipo>=<código de saída>@<segundos desde o arranque>s …»: os ChildFailuresToCount
    //     filhos que falharam (SandboxFallback.ChildOrigin; «renderer=nao-lancado-<estado>» é uma página que o motor não
    //     conseguiu lançar).
    // No máximo OriginsToKeep linhas, e apagado quando a contagem é reposta (um arranque bom com sandbox, a versão
    // seguinte). Só texto fixo, tipos do Chromium de uma lista fechada e números: nunca endereços, títulos, caminhos nem
    // nada das páginas. É um texto e não uma lista para a leitura do estado, que corre em todos os arranques antes do
    // motor, não ganhar tipos novos (regra R-2: nada de novo antes da 1.ª página num arranque normal).
    public string? Origins { get; init; }
}

public enum SandboxNotice
{
    None,
    // Desligado por precaução nesta versão (FailuresToDisable arranques falhados seguidos com ele).
    DisabledAfterFailures,
    // A DLL cliente voltou a arrancar o Éteris sem sandbox porque o .NET não arrancou com ele (só este arranque).
    RetriedWithoutSandbox,
}

// O estado do isolamento das páginas para eteris://privacy/ (INF-10): ligado, ou desligado e porquê.
public enum Isolation
{
    On,
    // Um interruptor na linha de comandos (--eteris-no-sandbox, --no-sandbox) ou o sandbox não ficou ativo.
    OffBySwitch,
    // Desligado por precaução depois de arranques falhados nesta versão (a pessoa pode voltar a ligar).
    OffAfterFailures,
    // A DLL cliente arrancou o Éteris sem sandbox só neste arranque.
    OffRetried,
    // Esta publicação não tem o isolamento (CefSharp oficial, sem o bootstrap).
    OffInThisBuild,
}

public readonly record struct SandboxStart(SandboxFallbackState State, bool UseSandbox, SandboxNotice Notice);

public static class SandboxFallback
{
    public const int FailuresToDisable = 2;
    public const int ChildFailuresToCount = 3;
    public static readonly TimeSpan ChildWindow = TimeSpan.FromSeconds(30);

    // O registo de onde veio cada falha contada (SandboxFallbackState.Origins): quantas linhas ficam e o máximo de cada uma.
    public const int OriginsToKeep = 8;
    public const int MaxOriginLength = 200;
    public const string CrashOrigin = "queda-antes-da-1a-pagina";
    public const string ChildrenOrigin = "filhos";

    // Posto pela DLL cliente (com --eteris-no-sandbox) quando volta a arrancar o Éteris depois de o .NET falhar com o sandbox.
    public const string RetrySwitch = "--eteris-sandbox-retry";

    // Ficheiro do estado, na pasta do perfil (AppPaths.Root).
    public const string FileName = "sandbox-fallback.json";

    // Início de um arranque. «available»: este Éteris pode ter sandbox (CefSharp do Éteris e ponteiro do bootstrap);
    // «manualOff»: --eteris-no-sandbox na linha de comandos; «retried»: veio do novo arranque da DLL cliente; «tweaks»:
    // este arranque corre com ajustes de desempenho antes da 1.ª página (já sem eles se TweaksSuspended os desligou).
    public static SandboxStart Begin(SandboxFallbackState previous, string version, bool available, bool manualOff, bool retried,
                                     bool tweaks = false, Func<DateTime>? clock = null)
    {
        var state = previous;
        // Versão nova: volta a tentar com o sandbox e com os ajustes, do zero.
        if (state.DisabledFor is { } disabled && !string.Equals(disabled, version, StringComparison.Ordinal))
            state = state with { DisabledFor = null, Failures = 0, Origins = null };
        if (state.TweaksDisabledFor is { } tweaksOff && !string.Equals(tweaksOff, version, StringComparison.Ordinal))
            state = state with { TweaksDisabledFor = null };
        // O arranque anterior não chegou à 1.ª página nem a um fecho normal: caiu. Só conta se corria com o sandbox; se
        // corria também com ajustes antes da 1.ª página, é o 1.º degrau: os ajustes desligam-se nesta versão.
        // «clock» (a hora da linha em Origins) só é chamado aqui, quando a queda conta.
        if (state.Starting && state.WithSandbox)
        {
            state = state with
            {
                Failures = state.Failures + 1,
                Origins = AddOrigin(state.Origins, clock, version, state.WithTweaks ? CrashOrigin + " com-ajustes" : CrashOrigin),
            };
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

    // O arranque correu bem (a 1.ª página apareceu, ou fecho normal): um arranque com sandbox bom zera a contagem e, com
    // ela, o registo de onde vieram as falhas. Os ajustes desligados por precaução continuam desligados até à versão
    // seguinte (senão voltariam a cair a seguir).
    public static SandboxFallbackState Healthy(SandboxFallbackState begun)
        => begun.WithSandbox ? begun with { Starting = false, Failures = 0, Origins = null } : begun with { Starting = false };

    // O arranque falhou sem cair (filhos a morrer logo no arranque, por exemplo a placa gráfica com uma funcionalidade
    // do E14): conta já, como se tivesse caído, e com ajustes antes da 1.ª página é o 1.º degrau (desliga-os em «version»).
    // «origin»: de onde veio, para Origins («filhos» e o ChildOrigin de cada um); «clock»: a hora dessa linha.
    public static SandboxFallbackState Failed(SandboxFallbackState begun, string? version = null, string? origin = null,
                                              Func<DateTime>? clock = null)
    {
        if (!begun.WithSandbox) return begun with { Starting = false };
        var failed = begun with
        {
            Starting = false,
            Failures = begun.Failures + 1,
            Origins = AddOrigin(begun.Origins, clock, version, origin ?? ChildrenOrigin),
        };
        return begun.WithTweaks && version is not null ? failed with { TweaksDisabledFor = failed.TweaksDisabledFor ?? version } : failed;
    }

    // Código de saída de um processo filho que conta como falha: exceções do Windows (NTSTATUS de erro, 0xC…: acesso
    // inválido, DLL em falta, pilha…), o ponto de paragem dos CHECK do Chromium (0x80000003) e os códigos com que o
    // sandbox mata um filho que não consegue fechar (sandbox/win/src/sandbox_types.h: SBOX_FATAL_INTEGRITY 7006 a
    // SBOX_FATAL_MITIGATION 7011 e SBOX_FATAL_WARMUP 7013). Não contam: saídas normais (0) e as do Chromium ao fechar
    // ou matar um filho de propósito (1–5), nem o limite de memória do processo (SBOX_FATAL_MEMORY_EXCEEDED, 7012), nem a
    // falta de memória de um processo (0xE0000008, o código com que o Chromium termina um processo sem memória): como o
    // 7012, uma página consegue provocá-la (INF-01).
    public static bool IsChildFailure(uint exitCode)
        => exitCode != 0xE0000008u && (exitCode >= 0xC0000000u || exitCode == 0x80000003u || exitCode is >= 7006 and <= 7011 or 7013);

    // Conteúdo web: tudo o que não é uma página interna (eteris://) nem about: (INF-01). Depois de uma destas confirmada
    // numa moldura principal, as quedas dos filhos deixam de contar: é o conteúdo que as pode provocar.
    public static bool IsWebContent(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        var colon = url.IndexOf(':');
        var scheme = (colon < 0 ? url : url[..colon]).ToLowerInvariant();
        return scheme is not ("eteris" or "about");
    }

    // O arranque anterior falhou (caiu, ou contou como falhado) e o sandbox ainda não foi desligado por isso: o arranque
    // a seguir repõe a sessão com todas as abas a dormir (INF-01), para uma página não voltar a correr sozinha.
    public static bool PreviousStartFailed(SandboxFallbackState previous)
        => previous.DisabledFor is null && (previous.Failures > 0 || (previous.Starting && previous.WithSandbox));

    // Porque é que o isolamento está (ou não) ligado, para a linha «Páginas isoladas» de eteris://privacy/ (INF-10).
    // «supported»/«available»: esta publicação pode ter sandbox; «active»: o Éteris pediu-o ao motor; «engineNoSandbox»:
    // há um --no-sandbox do Chromium nos argumentos (o que o Éteris pediu não é o que ficou em vigor).
    public static Isolation IsolationOf(bool supported, bool available, bool active, SandboxNotice notice, bool manualOff, bool engineNoSandbox)
    {
        if (!supported || !available) return Isolation.OffInThisBuild;
        if (notice == SandboxNotice.DisabledAfterFailures) return Isolation.OffAfterFailures;
        if (notice == SandboxNotice.RetriedWithoutSandbox) return Isolation.OffRetried;
        if (manualOff || engineNoSandbox || !active) return Isolation.OffBySwitch;
        return Isolation.On;
    }

    // «Voltar a ligar» (D6): o estado depois de a pessoa pedir ao sandbox para voltar a ser tentado no arranque seguinte.
    // Os ajustes de desempenho desligados por precaução não se tocam.
    public static SandboxFallbackState Reenabled(SandboxFallbackState state)
        => state with { Starting = false, DisabledFor = null, Failures = 0, Origins = null };

    // Estados de terminação de uma página (CefTerminationStatus da CEF; o enum do CefSharp 156 só vai até 3): o motor
    // não conseguiu lançar o processo (TS_LAUNCH_FAILED = 4) ou matou-o por falha de integridade do código
    // (TS_INTEGRITY_FAILURE = 5). Estes não deixam um código de saída de falha para a vigia dos filhos ver.
    public static bool IsLaunchFailure(int terminationStatus) => terminationStatus is 4 or 5;

    // Junta uma linha a Origins («<hora UTC> <versão> <o quê>») e fica só com as últimas OriginsToKeep, cada uma com
    // MaxOriginLength carateres no máximo (também as que já lá estavam: o ficheiro pode ter sido mexido à mão).
    public static string AddOrigin(string? origins, Func<DateTime>? clock, string? version, string what)
    {
        var when = (clock?.Invoke() ?? DateTime.UtcNow).ToUniversalTime();
        var line = string.Create(CultureInfo.InvariantCulture, $"{when:yyyy-MM-ddTHH:mm:ss}Z {SafeVersion(version)} {what}");
        return string.Join('\n', (origins ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).Append(line)
            .Select(l => l.Length > MaxOriginLength ? l[..MaxOriginLength] : l).TakeLast(OriginsToKeep));
    }

    // Um filho que falhou, para Origins: «<tipo>=<código de saída>@<segundos desde o arranque>s». O código vai em
    // hexadecimal quando é um NTSTATUS (0xC0000005) e em decimal quando é do sandbox (7006).
    public static string ChildOrigin(string? type, uint exitCode, TimeSpan at)
    {
        var code = exitCode < 0x10000 ? exitCode.ToString(CultureInfo.InvariantCulture) : "0x" + exitCode.ToString("X8", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{SafeKind(type)}={code}@{Seconds(at)}s");
    }

    // Uma página que o motor não conseguiu lançar (IsLaunchFailure), para Origins: o estado de terminação da CEF.
    public static string LaunchOrigin(int terminationStatus, TimeSpan at)
        => string.Create(CultureInfo.InvariantCulture, $"renderer=nao-lancado-{terminationStatus}@{Seconds(at)}s");

    // O tipo de um processo filho pela linha de comandos, para Origins: só o --type= do Chromium e, nos utilitários, o
    // serviço, os dois de listas fechadas (SafeKind); «?» se não se conseguir ler. Só os argumentos contam: o programa (o
    // 1.º elemento, entre aspas se tiver espaços) é um caminho, e um caminho pode ter qualquer texto.
    public static string ChildKind(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return "?";
        var end = commandLine[0] == '"' ? commandLine.IndexOf('"', 1) : commandLine.IndexOf(' ');
        return end < 0 ? "?" : SafeKind(StartupTrace.ChildType(commandLine[(end + 1)..]));
    }

    // Só se escreve o que é destas duas listas (os tipos de processo do Chromium e os serviços dos utilitários que
    // arrancam com o Éteris); o resto fica «?», ou «utility» sem o serviço.
    private static string SafeKind(string? type)
    {
        if (type is null) return "?";
        var colon = type.IndexOf(':');
        var kind = colon < 0 ? type : type[..colon];
        if (kind is not ("renderer" or "gpu-process" or "utility" or "crashpad-handler")) return "?";
        var known = kind == "utility" && colon >= 0
                    && type[(colon + 1)..] is "NetworkService" or "StorageService" or "AudioService" or "VideoCaptureService" or "DataDecoderService";
        return known ? type : kind;
    }

    // A versão tal como vem do Éteris («0.2.14»); outra coisa qualquer fica «?».
    private static string SafeVersion(string? version)
        => version is { Length: > 0 and <= 24 } && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? version : "?";

    private static int Seconds(TimeSpan at) => (int)Math.Clamp(at.TotalSeconds, 0, 999);

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
