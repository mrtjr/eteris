using System.Windows;

namespace Eteris.Browser.Theme;

// «Reduzir movimento»: com os «Efeitos de animação» do Windows desligados (SystemParameters.ClientAreaAnimation) ou em
// alto contraste, o que se mexe sozinho fica parado — o anel da Pausa (MainWindow.Pause.cs) não respira e o texto diz
// «Respire devagar». As outras animações do Éteris podem passar a lê-lo daqui. O gancho das provas ao vivo
// (ETERIS_TEST_SEM_MOVIMENTO=1, Infrastructure/TestSwitches.cs) força-o sem mexer nas definições do Windows.
public static class Motion
{
    public static bool Reduced
        => IsReduced(SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast, Infrastructure.TestSwitches.ReducedMotion);

    public static bool IsReduced(bool clientAreaAnimation, bool highContrast, bool forced = false)
        => forced || !clientAreaAnimation || highContrast;
}
