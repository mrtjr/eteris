using System.Globalization;
using System.Reflection;
using System.Text;
using Eteris.Browser.Sync;
using Eteris.Sync.Engine;
using Eteris.Sync.Pairing;

namespace Eteris.Browser.Tests.Sync;

// O que a barra de aprovação mostra (0.2.15, T8), sem janela: lógica pura sobre o estado da sincronização.
public class PairingBarModelTests
{
    private const long Now = 1_762_560_000_000;

    private static SyncStatus On(IReadOnlyList<HostRequest>? requests = null) => TestStatus.Of(SyncPhase.On, last: Now, requests: requests);

    [Fact]
    public void NoSync_OrNotOn_ShowsNothing()
    {
        var ready = new HostRequest("s", "Portátil", 3, Now);
        Assert.Equal(PairingBarKind.None, PairingBarModel.From(null, Now, PairingBarModel.DefaultReceives).Kind);
        foreach (var phase in new[] { SyncPhase.Off, SyncPhase.Starting, SyncPhase.Choose, SyncPhase.Pairing, SyncPhase.Stopped })
        {
            var status = TestStatus.Of(phase, requests: [ready]) with { WaitingRequests = 1, AnsweringSinceMs = Now };
            Assert.Equal(PairingBarKind.None, PairingBarModel.From(status, Now, PairingBarModel.DefaultReceives).Kind);
        }
        Assert.Equal(PairingBarKind.None, PairingBarModel.From(On(), Now, PairingBarModel.DefaultReceives).Kind);
    }

    // Pedidos em série: com a barra calada nenhum estado abre sozinho (o pedido continua nas Definições).
    [Fact]
    public void QuietBar_ShowsNothing()
    {
        var status = On([new HostRequest("s", "Portátil", 3, Now)]) with { WaitingRequests = 1, AnsweringSinceMs = Now, PairingQuiet = true };
        Assert.Equal(PairingBarKind.None, PairingBarModel.From(status, Now, PairingBarModel.DefaultReceives).Kind);
        Assert.Equal(PairingBarKind.Code, PairingBarModel.From(status with { PairingQuiet = false }, Now, PairingBarModel.DefaultReceives).Kind);
    }

    [Fact]
    public void CountedRequest_ShowsWaiting()
    {
        var view = PairingBarModel.From(On() with { WaitingRequests = 1 }, Now, PairingBarModel.DefaultReceives);
        Assert.Equal(PairingBarKind.Waiting, view.Kind);
        Assert.Null(view.Session);
        Assert.Null(view.Title);
    }

    [Fact]
    public void Answering_ShowsConnecting_ForSixtySeconds()
    {
        var answering = On() with { AnsweringSinceMs = Now };
        Assert.Equal(60_000, PairingBarModel.ConnectingShownMs);
        Assert.Equal(PairingBarKind.Connecting, PairingBarModel.From(answering, Now, PairingBarModel.DefaultReceives).Kind);
        Assert.Equal(PairingBarKind.Connecting, PairingBarModel.From(answering, Now + 59_999, PairingBarModel.DefaultReceives).Kind);
        Assert.Equal(PairingBarKind.None, PairingBarModel.From(answering, Now + 61_000, PairingBarModel.DefaultReceives).Kind);
        // Com um pedido só contado ao lado, volta a «Ver».
        Assert.Equal(PairingBarKind.Waiting, PairingBarModel.From(answering with { WaitingRequests = 1 }, Now + 61_000, PairingBarModel.DefaultReceives).Kind);
    }

    [Fact]
    public void ReadyRequests_ShowTheOldestOnly()
    {
        var status = On([new HostRequest("b", "Tablet", 3, Now), new HostRequest("a", "Portátil", 2, Now - 5_000)])
            with { WaitingRequests = 2, AnsweringSinceMs = Now };
        var view = PairingBarModel.From(status, Now, PairingBarModel.DefaultReceives);
        Assert.Equal(PairingBarKind.Code, view.Kind);
        Assert.Equal("a", view.Session);
        Assert.Equal("Portátil", view.Name);
        Assert.Equal(2, view.AttemptsLeft);
        // Com a mesma hora, pela sessão: sempre o mesmo pedido à vista.
        var tie = PairingBarModel.From(On([new HostRequest("d", "Tablet", 3, Now), new HostRequest("c", "Portátil", 3, Now)]), Now, PairingBarModel.DefaultReceives);
        Assert.Equal("c", tie.Session);
    }

    // Quem pede escolhe o nome depois de conhecer o código (o nome segue selado no passo 4): o nome de um pedido nunca
    // mostra caracteres numéricos, de nenhuma escrita (Unicode Nd, Nl, No; largura total; fora do plano básico).
    [Theory]
    [InlineData("Código 482 913", "Código")]
    [InlineData("４８２９１３", DeviceNameText.Unnamed)]
    [InlineData("Portátil ٤٨٢ ⅨⅢ ½ \U0001D7D2\U0001D7D6 sala", "Portátil sala")]
    [InlineData("Portátil da sala", "Portátil da sala")]
    [InlineData("   ", DeviceNameText.Unnamed)]
    [InlineData(null, DeviceNameText.Unnamed)]
    public void RequestName_NeverShowsDigits(string? name, string shown)
    {
        Assert.Equal(shown, PairingBarModel.ShownName(name));
        var view = PairingBarModel.From(On([new HostRequest("s", name ?? "", 3, Now)]), Now, PairingBarModel.DefaultReceives);
        Assert.Equal(shown, view.Name);
        Assert.DoesNotContain(PairingBarModel.NameLine(name ?? "").EnumerateRunes(), r => Rune.GetUnicodeCategory(r)
            is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber);
    }

    // O código por extenso também não cabe: o nome mostrado corta em 16 caracteres (com «…»). Seis algarismos por
    // extenso precisam de pelo menos 17 em português e 23 em inglês; os numerais chineses (um carácter por algarismo)
    // saem como os algarismos. Um nome normal fica igual.
    [Theory]
    [InlineData("quatro oito dois nove um três", "quatro oito dois…")]
    [InlineData("four eight two nine one three", "four eight two n…")]
    [InlineData("四八二九一三", DeviceNameText.Unnamed)]
    [InlineData("Portátil 四八二 九一三 sala", "Portátil sala")]
    [InlineData("Portátil da sala", "Portátil da sala")]
    [InlineData("Portátil da sala grande", "Portátil da sala…")]
    [InlineData("Portátil da sal  a", "Portátil da sal…")]
    [InlineData("１２３", DeviceNameText.Unnamed)]
    public void RequestName_IsCutShortEnoughToHideSpelledOutCodes(string name, string shown)
    {
        Assert.Equal(16, PairingBarModel.ShownNameMax);
        Assert.Equal(shown, PairingBarModel.ShownName(name));
        if (shown != DeviceNameText.Unnamed)
            Assert.True(shown.TrimEnd('…').EnumerateRunes().Count() <= PairingBarModel.ShownNameMax);
        Assert.Equal(shown, PairingBarModel.From(On([new HostRequest("s", name, 3, Now)]), Now, PairingBarModel.DefaultReceives).Name);
        Assert.Equal($"Nome que o outro computador indicou: «{shown}»", PairingBarModel.NameLine(name));
        // Nunca as seis palavras do código.
        foreach (var spelled in new[] { "quatro oito dois nove um três", "four eight two nine one three" })
            Assert.False(spelled.Split(' ').All(w => shown.Contains(w, StringComparison.Ordinal)));
    }

    [Fact]
    public void Title_UsesTheReceivesParameter()
    {
        var status = On([new HostRequest("s", "Portátil", 3, Now)]);
        Assert.Equal("as suas palavras-passe", PairingBarModel.DefaultReceives);
        Assert.EndsWith("receber as suas palavras-passe", PairingBarModel.From(status, Now, PairingBarModel.DefaultReceives).Title);
        Assert.Equal("Um computador quer entrar na sua conta e receber as suas palavras-passe, cartões e documentos",
            PairingBarModel.From(status, Now, "as suas palavras-passe, cartões e documentos").Title);
        // Sem estado estático que se possa escrever (o fragmento vem sempre de quem chama).
        const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Assert.DoesNotContain(typeof(PairingBarModel).GetFields(all), f => !f.IsLiteral && !f.IsInitOnly);
        Assert.DoesNotContain(typeof(PairingBarModel).GetProperties(all), p => p.CanWrite);
    }

    // Os textos da barra, à letra (a T15 confere-os aqui).
    [Fact]
    public void Texts_ToTheLetter()
    {
        Assert.Equal("Um computador novo quer entrar na sua conta.", PairingBarModel.Waiting);
        Assert.Equal("A ligar ao computador novo…", PairingBarModel.Connecting);
        Assert.Equal("Um computador quer entrar na sua conta e receber as suas palavras-passe", PairingBarModel.Title(PairingBarModel.DefaultReceives));
        Assert.Equal("Se foi você, escreva o código que ele mostra:", PairingBarModel.Ask);
        Assert.Equal("Recusado. Um pedido destes só aparece se alguém entrou no Éteris com a sua conta Google. Mude a palavra-passe da Google.",
            PairingBarModel.RefusedAdvice);
        Assert.Equal("Três códigos errados: o pedido foi recusado. Peça outra vez no outro computador.", PairingBarModel.ThreeWrong);
        Assert.Equal("Código errado. Tem mais 2 tentativas.", PairingBarModel.WrongCode(2));
        Assert.Equal("Código errado. Tem mais 1 tentativa.", PairingBarModel.WrongCode(1));
        Assert.Equal("Nome que o outro computador indicou: «Portátil»", PairingBarModel.NameLine("Portátil"));
    }

    // ---- aprovar nas Definições só com o teclado (T8K) ---------------------------------------------------------------

    // A nota da página para cada resposta diz o mesmo que a barra (null = nada a dizer: o aviso de aprovado é do agente,
    // e «Busy» é outra aprovação a meio).
    [Theory]
    [InlineData(PairingAnswer.WrongCode, 2, "Código errado. Tem mais 2 tentativas.")]
    [InlineData(PairingAnswer.WrongCode, 1, "Código errado. Tem mais 1 tentativa.")]
    [InlineData(PairingAnswer.Refused, 0, "Três códigos errados: o pedido foi recusado. Peça outra vez no outro computador.")]
    [InlineData(PairingAnswer.NotConfirmed, 3, "Não foi confirmado com o Windows: o pedido continua à espera.")]
    [InlineData(PairingAnswer.Incomplete, 3, "Escreva os 6 algarismos que o outro computador mostra.")]
    [InlineData(PairingAnswer.Gone, 0, "Este pedido já não está à espera.")]
    [InlineData(PairingAnswer.Approved, 0, null)]
    [InlineData(PairingAnswer.Busy, 3, null)]
    public void PageNote_SaysWhatTheBarSays(PairingAnswer answer, int attemptsLeft, string? expected)
    {
        Assert.Equal(expected, PairingBarModel.PageNote(answer, attemptsLeft));
        // Os sete valores estão todos cobertos acima.
        Assert.Equal(7, Enum.GetValues<PairingAnswer>().Length);
    }

    // Aprovar fora da barra só quando a barra o deixaria: sincronização ligada e barra não calada (pedidos em série).
    [Fact]
    public void MayApproveHere_OnlyWhenOn_AndNotQuiet()
    {
        Assert.True(PairingBarModel.MayApproveHere(TestStatus.Of(SyncPhase.On)));
        Assert.False(PairingBarModel.MayApproveHere(TestStatus.Of(SyncPhase.On) with { PairingQuiet = true }));
        foreach (var phase in new[] { SyncPhase.Choose, SyncPhase.Off, SyncPhase.Stopped })
            Assert.False(PairingBarModel.MayApproveHere(TestStatus.Of(phase)));
        Assert.False(PairingBarModel.MayApproveHere(null));
    }

    // Os textos novos da página, à letra (a T15 confere-os aqui).
    [Fact]
    public void PageTexts_AreTheBarsTexts()
    {
        Assert.Equal("Código que o outro computador mostra", PairingBarModel.CodeBoxName);
        Assert.Equal("Escreva os 6 algarismos que o outro computador mostra.", PairingBarModel.TypeSixDigits);
        Assert.Equal("Este pedido já não está à espera.", PairingBarModel.GoneNote);
    }
}
