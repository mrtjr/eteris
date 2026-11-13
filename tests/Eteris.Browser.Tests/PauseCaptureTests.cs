using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// O ecrã calmo não esconde que um site continua com a câmara, o microfone ou o ecrã (Tabs/PauseCapture.cs): a Pausa só cala
// a saída e esconde as páginas; a captura de uma chamada continua. «Pode»: o motor não avisa quando a captura acaba
// (CaptureSessions conta até o documento mudar).
public class PauseCaptureTests
{
    [Fact]
    public void NoCapture_SaysNothing()
    {
        Assert.Null(PauseCapture.Notice([]));
        Assert.Null(PauseCapture.Notice(["", "  "]));
        Assert.Equal("Pausa", PauseCapture.ViewName(null));
    }

    [Fact]
    public void OneSite_IsNamed()
        => Assert.Equal("meet.google.com pode continuar a usar a câmara, o microfone ou o ecrã", PauseCapture.Notice(["meet.google.com"]));

    [Fact]
    public void TwoSites_AreBothNamed_WithoutRepeats()
        => Assert.Equal("meet.google.com e teams.microsoft.com podem continuar a usar a câmara, o microfone ou o ecrã",
            PauseCapture.Notice(["meet.google.com", "teams.microsoft.com", "MEET.google.com"]));

    [Fact]
    public void MoreSites_NameTheFirstAndCountTheRest()
    {
        Assert.Equal("a.pt e mais 2 sites podem continuar a usar a câmara, o microfone ou o ecrã", PauseCapture.Notice(["a.pt", "b.pt", "c.pt"]));
        Assert.Equal("a.pt e mais 3 sites podem continuar a usar a câmara, o microfone ou o ecrã", PauseCapture.Notice(["a.pt", "b.pt", "c.pt", "d.pt"]));
    }

    // O leitor de ecrã ouve-o com o nome do ecrã calmo.
    [Fact]
    public void TheViewName_CarriesTheNotice()
        => Assert.Equal("Pausa — a.pt pode continuar a usar a câmara, o microfone ou o ecrã",
            PauseCapture.ViewName(PauseCapture.Notice(["a.pt"])));
}
