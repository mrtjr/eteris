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
    // (como uma queda), para provar a rede de segurança do sandbox (Infrastructure/SandboxFallback.cs).
    public static bool CrashBeforePaint => IsOn(Environment.GetEnvironmentVariable("ETERIS_TEST_CRASH_BEFORE_PAINT"));

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
