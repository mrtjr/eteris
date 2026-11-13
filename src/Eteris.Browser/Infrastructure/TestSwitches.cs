namespace Eteris.Browser.Infrastructure;

// Interruptores SÓ para testes automáticos ao vivo (lidos do ambiente do processo que lança o Éteris).
public static class TestSwitches
{
    // Todas as variáveis ETERIS_* do código têm aqui, ou no sítio que a lê, a regra de onde valem (TestSwitchesTests obriga
    // cada nova a declará-la). As de teste só valem num perfil de teste (--user-data-dir): o perfil normal nunca as ouve
    // (VAU-08, INF-11), por muito que uma esteja esquecida no ambiente de quem lança o Éteris.

    // ETERIS_TEST_MUTE=1: nenhum som sai dos dois motores durante as medições (mute-audio no Chromium, IsMuted no WebView2).
    // Só com um perfil de teste.
    public static bool Mute => MuteFor(Environment.GetEnvironmentVariable("ETERIS_TEST_MUTE"), AppPaths.IsCustomProfile);

    public static bool MuteFor(string? value, bool customProfile) => customProfile && IsOn(value);

    // ETERIS_TEST_DEVTOOLS_PORT=<porta>: DevTools do motor em 127.0.0.1 para os testes ao vivo lerem chrome://sandbox
    // e o estado das páginas por CDP (nunca ligado sem esta variável, nem com o perfil normal: uma porta de depuração sem
    // autenticação abria o Éteris de quem a tivesse esquecida no ambiente).
    public static int? DevToolsPort => DevToolsPortFor(Environment.GetEnvironmentVariable("ETERIS_TEST_DEVTOOLS_PORT"), AppPaths.IsCustomProfile);

    public static int? DevToolsPortFor(string? value, bool customProfile) => customProfile ? Port(value) : null;

    // ETERIS_TEST_REAUTH=aceitar|recusar: a confirmação com o Windows (Windows Hello / palavra-passe do Windows) do cofre
    // responde sozinha, sem janela (Vault/WindowsReauth.cs), para as provas ao vivo não precisarem do PIN de ninguém.
    // Como a pasta de teste da sincronização, a versão instalada ignora-a (há um Update.exe do Velopack na pasta de cima) e
    // só vale num perfil de teste (ReauthForProfile). E nem sequer existe na publicação: a constante ETERIS_TEST_HOOKS fica
    // desligada em build/publish.ps1 (-p:EterisTestHooks=false), que confere o binário (VAU-08).
#if ETERIS_TEST_HOOKS
    public static Vault.TestReauthMode? Reauth => ReauthFor(Environment.GetEnvironmentVariable("ETERIS_TEST_REAUTH"), IsInstalled);
#else
    public static Vault.TestReauthMode? Reauth => null;
#endif

    public static Vault.TestReauthMode? ReauthFor(string? value, bool installed) => installed ? null : value?.Trim().ToLowerInvariant() switch
    {
        "aceitar" => Vault.TestReauthMode.Accept,
        "recusar" => Vault.TestReauthMode.Refuse,
        _ => null,
    };

    // A via de teste da confirmação do Windows só vale num perfil de teste (--user-data-dir): contra o perfil de
    // omissão, a porta é sempre a real.
    public static Vault.TestReauthMode? ReauthForProfile(Vault.TestReauthMode? mode, bool customProfile) => customProfile ? mode : null;

    // ETERIS_TEST_CRASH_BEFORE_PAINT=1: o processo termina de repente logo a seguir ao Cef.Initialize, antes da 1.ª página
    // (como uma queda), para provar a rede de segurança do sandbox (Infrastructure/SandboxFallback.cs). Só numa cópia
    // portátil: a versão instalada ignora-a, porque uma variável esquecida no PC de alguém fazia cair o Éteris instalado
    // duas vezes, e isso desligava o sandbox até à versão seguinte.
    public static bool CrashBeforePaint => CrashBeforePaintFor(Environment.GetEnvironmentVariable("ETERIS_TEST_CRASH_BEFORE_PAINT"), IsInstalled);

    public static bool CrashBeforePaintFor(string? value, bool installed) => !installed && IsOn(value);

    // A vigia (Watching/), nas provas ao vivo. As três só valem numa cópia não instalada e num perfil de teste: a vigia
    // semeada escreve na base do perfil e a loja serve ficheiros locais num endereço https.
    // - ETERIS_TEST_SHOP_DIR=<pasta>: os ficheiros da pasta respondem em https://loja-eteris.invalid/ (Browser/
    //   TestShopSchemeHandlerFactory.cs), no contexto do perfil e nos contextos só em memória; um nome .invalid nunca vai à rede.
    // - ETERIS_TEST_WATCH=<endereço>[TAB<seletor>]: cria essa vigia de preço ao arrancar, se ainda não houver uma para o endereço.
    // - ETERIS_TEST_WATCH_SECONDS=<n>: o relógio da vigia passa de 1 min a n s (no máximo 60) e a próxima verificação vem n s
    //   depois, sem o mínimo de 30 min nem o recuo.
    // - ETERIS_TEST_CLIP=<endereço>TAB<seletor>: abre um recorte ao vivo desse pedaço ao arrancar (o gesto do menu precisa
    //   do rato para apontar o pedaço).
    public static string? ShopDir => ShopDirFor(Environment.GetEnvironmentVariable("ETERIS_TEST_SHOP_DIR"), IsInstalled, AppPaths.IsCustomProfile);

    public static (string Url, string? Selector)? WatchSeed
        => WatchSeedFor(Environment.GetEnvironmentVariable("ETERIS_TEST_WATCH"), IsInstalled, AppPaths.IsCustomProfile);

    public static (string Url, string Selector)? ClipSeed
        => ClipSeedFor(Environment.GetEnvironmentVariable("ETERIS_TEST_CLIP"), IsInstalled, AppPaths.IsCustomProfile);

    public static int? WatchSeconds => WatchSecondsFor(Environment.GetEnvironmentVariable("ETERIS_TEST_WATCH_SECONDS"), IsInstalled, AppPaths.IsCustomProfile);

    public static string? ShopDirFor(string? value, bool installed, bool customProfile)
    {
        if (installed || !customProfile || string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var full = System.IO.Path.GetFullPath(value.Trim());
            return System.IO.Directory.Exists(full) ? full : null;
        }
        catch (Exception) { return null; }
    }

    public static (string Url, string? Selector)? WatchSeedFor(string? value, bool installed, bool customProfile)
    {
        if (installed || !customProfile || string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('\t', 2);
        var url = parts[0].Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return null;
        var selector = parts.Length > 1 && parts[1].Trim() is { Length: > 0 } s ? s : null;
        return (url, selector);
    }

    public static (string Url, string Selector)? ClipSeedFor(string? value, bool installed, bool customProfile)
        => WatchSeedFor(value, installed, customProfile) is (var url, { } selector) ? (url, selector) : null;

    public static int? WatchSecondsFor(string? value, bool installed, bool customProfile)
        => !installed && customProfile
           && int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
           && seconds is >= 1 and <= 86400 ? seconds : null;

    // Instalação do Velopack: o Update.exe está na pasta de cima (%LOCALAPPDATA%\<pacote>\current\Eteris.Browser.exe).
    // Com o sandbox, o AppContext.BaseDirectory é a pasta do Eteris.App.dll, a mesma do bootstrap (current\).
    // É a única regra de instalação: a pasta de teste da sincronização (Sync.SyncService.TestFolder), o ETERIS_TEST_REAUTH
    // e o ETERIS_TEST_CRASH_BEFORE_PAINT leem-na daqui.
    public static bool IsInstalled => System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "Update.exe"));

    // Como uma queda: sem fecho, sem relatório do Windows (TerminateProcess), com um código de saída próprio.
    public const uint CrashExitCode = 0xE7E50001;

    public static void CrashNow() => TerminateProcess(-1, CrashExitCode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    public static bool IsOn(string? value) => value?.Trim() == "1";

    public static int? Port(string? value)
        => int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
           && port is >= 1024 and <= 65535 ? port : null;
}
