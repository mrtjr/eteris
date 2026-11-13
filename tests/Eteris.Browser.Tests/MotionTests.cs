using Eteris.Browser.Infrastructure;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

// «Reduzir movimento» (Theme/Motion.cs): com os «Efeitos de animação» do Windows desligados ou em alto contraste, nada se
// mexe sozinho (o anel da Pausa fica parado). O sistema real nunca se lê aqui: os valores são injetados.
public class MotionTests
{
    [Fact]
    public void WithAnimationsOnAndNoHighContrast_MotionIsAllowed()
        => Assert.False(Motion.IsReduced(clientAreaAnimation: true, highContrast: false));

    [Fact]
    public void WithoutClientAreaAnimations_MotionIsReduced()
        => Assert.True(Motion.IsReduced(clientAreaAnimation: false, highContrast: false));

    [Fact]
    public void InHighContrast_MotionIsReduced()
    {
        Assert.True(Motion.IsReduced(clientAreaAnimation: true, highContrast: true));
        Assert.True(Motion.IsReduced(clientAreaAnimation: false, highContrast: true));
    }

    // O gancho das provas ao vivo (ETERIS_TEST_SEM_MOVIMENTO), para provar o anel parado e o anel a respirar sem mexer nas
    // definições do Windows: =1 força «reduzir movimento», =0 força o movimento; só num perfil de teste e numa cópia não
    // instalada. Sem a variável, manda o Windows.
    [Fact]
    public void TheTestHook_ForcesEitherWay_OnlyInATestProfileNotInstalled()
    {
        Assert.True(Motion.IsReduced(clientAreaAnimation: true, highContrast: false, forced: true));
        Assert.False(Motion.IsReduced(clientAreaAnimation: false, highContrast: false, forced: false));
        Assert.True(Motion.IsReduced(clientAreaAnimation: false, highContrast: false, forced: null));
        Assert.True(TestSwitches.ReducedMotionFor("1", installed: false, customProfile: true));
        Assert.False(TestSwitches.ReducedMotionFor("0", installed: false, customProfile: true));
        Assert.Null(TestSwitches.ReducedMotionFor("1", installed: false, customProfile: false));
        Assert.Null(TestSwitches.ReducedMotionFor("0", installed: true, customProfile: true));
        Assert.Null(TestSwitches.ReducedMotionFor("talvez", installed: false, customProfile: true));
        Assert.Null(TestSwitches.ReducedMotionFor(null, installed: false, customProfile: true));
    }
}
