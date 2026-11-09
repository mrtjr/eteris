namespace Eteris.Browser.Infrastructure;

// Interruptores SÓ para testes automáticos ao vivo (lidos do ambiente do processo que lança o Éteris).
public static class TestSwitches
{
    // ETERIS_TEST_MUTE=1: nenhum som sai dos dois motores durante as medições (mute-audio no Chromium, IsMuted no WebView2).
    public static bool Mute => IsOn(Environment.GetEnvironmentVariable("ETERIS_TEST_MUTE"));

    // ETERIS_TEST_DEVTOOLS_PORT=<porta>: DevTools do motor em 127.0.0.1 para os testes ao vivo lerem chrome://sandbox
    // e o estado das páginas por CDP (nunca ligado sem esta variável).
    public static int? DevToolsPort => Port(Environment.GetEnvironmentVariable("ETERIS_TEST_DEVTOOLS_PORT"));

    // ETERIS_TEST_CRASH_BEFORE_PAINT=1: o processo termina de repente logo a seguir ao Cef.Initialize, antes da 1.ª página
    // (como uma queda), para provar a rede de segurança do sandbox (Infrastructure/SandboxFallback.cs). Só numa cópia
    // portátil: a versão instalada ignora-a, porque uma variável esquecida no PC de alguém fazia cair o Éteris instalado
    // duas vezes, e isso desligava o sandbox até à versão seguinte.
    public static bool CrashBeforePaint => CrashBeforePaintFor(Environment.GetEnvironmentVariable("ETERIS_TEST_CRASH_BEFORE_PAINT"), IsInstalled);

    public static bool CrashBeforePaintFor(string? value, bool installed) => !installed && IsOn(value);

    // Instalação do Velopack: o Update.exe está na pasta de cima (%LOCALAPPDATA%\<pacote>\current\Eteris.Browser.exe).
    // Com o sandbox, o AppContext.BaseDirectory é a pasta do Eteris.App.dll, a mesma do bootstrap (current\).
    // É a regra do Sync.SyncService.IsInstalled da 0.2.13; ao fundir a 0.2.13 fica uma só.
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
