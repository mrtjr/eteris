namespace Eteris.Browser.Infrastructure;

// Interruptores SÓ para testes automáticos ao vivo (lidos do ambiente do processo que lança o Éteris).
public static class TestSwitches
{
    // ETERIS_TEST_MUTE=1: nenhum som sai dos dois motores durante as medições (mute-audio no Chromium, IsMuted no WebView2).
    public static bool Mute => IsOn(Environment.GetEnvironmentVariable("ETERIS_TEST_MUTE"));

    public static bool IsOn(string? value) => value?.Trim() == "1";
}
