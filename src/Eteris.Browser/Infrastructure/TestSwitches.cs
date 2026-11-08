namespace Eteris.Browser.Infrastructure;

// Interruptores SÓ para testes automáticos ao vivo (lidos do ambiente do processo que lança o Éteris).
public static class TestSwitches
{
    // ETERIS_TEST_MUTE=1: nenhum som sai dos dois motores durante as medições (mute-audio no Chromium, IsMuted no WebView2).
    public static bool Mute => IsOn(Environment.GetEnvironmentVariable("ETERIS_TEST_MUTE"));

    // ETERIS_TEST_DEVTOOLS_PORT=<porta>: DevTools do motor em 127.0.0.1 para os testes ao vivo lerem chrome://sandbox
    // e o estado das páginas por CDP (nunca ligado sem esta variável).
    public static int? DevToolsPort => Port(Environment.GetEnvironmentVariable("ETERIS_TEST_DEVTOOLS_PORT"));

    public static bool IsOn(string? value) => value?.Trim() == "1";

    public static int? Port(string? value)
        => int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
           && port is >= 1024 and <= 65535 ? port : null;
}
