using System.Globalization;
using System.Text;
using Eteris.Sync.Engine;

namespace Eteris.Browser.Sync;

// O que a barra de aprovação mostra no computador que já está na conta (0.2.15): nada, «Ver» (um pedido só contado),
// «A ligar…» (este computador respondeu e espera o computador novo) ou a caixa do código (pedido pronto).
internal enum PairingBarKind { None, Waiting, Connecting, Code }

// Title é o título já composto (com o fragmento «receives»); só tem valor em Code. Name é o nome que o computador novo
// indicou (já limpo), só informação: nunca entra no título.
internal sealed record PairingBarView(PairingBarKind Kind, string? Session, string? Name, int AttemptsLeft, string? Title = null);

// Decisão pura (sem WPF, sem estado): a janela pergunta-lhe o que mostrar e a barra desenha.
internal static class PairingBarModel
{
    // «A ligar ao computador novo…» no máximo durante este tempo por resposta: um pedido escrito à mão (sem computador
    // novo nenhum) não deixa a barra presa; o pedido continua à espera em silêncio e a barra volta se o código chegar.
    public const int ConnectingShownMs = 60_000;

    // O que o computador novo recebe ao ser aprovado. A T10 passa o texto certo para cada conta (cartões e documentos).
    public const string DefaultReceives = "as suas palavras-passe";

    public const string Waiting = "Um computador novo quer entrar na sua conta.";
    public const string Connecting = "A ligar ao computador novo…";
    public const string Ask = "Se foi você, escreva o código que ele mostra:";
    public const string RefusedAdvice = "Recusado. Um pedido destes só aparece se alguém entrou no Éteris " +
        "com a sua conta Google. Mude a palavra-passe da Google.";
    public const string ThreeWrong = "Três códigos errados: o pedido foi recusado. Peça outra vez no " +
        "outro computador.";

    // «receives»: o fragmento do que o computador novo recebe. Vem sempre de quem chama.
    public static PairingBarView From(SyncStatus? status, long nowMs, string receives)
    {
        // Pedidos em série: a barra não abre sozinha (o aviso fica nas Definições).
        if (status is not { Phase: SyncPhase.On } || status.PairingQuiet) return new(PairingBarKind.None, null, null, 0);
        // Um pedido de cada vez: o mais antigo (o código escrito só se compara com esse).
        if (status.Requests.OrderBy(r => r.SeenMs).ThenBy(r => r.Session, StringComparer.Ordinal).FirstOrDefault() is { } oldest)
            return new(PairingBarKind.Code, oldest.Session, ShownName(oldest.Name), oldest.AttemptsLeft, Title(receives));
        if (status.AnsweringSinceMs != 0 && nowMs - status.AnsweringSinceMs < ConnectingShownMs)
            return new(PairingBarKind.Connecting, null, null, 0);
        if (status.WaitingRequests > 0) return new(PairingBarKind.Waiting, null, null, 0);
        return new(PairingBarKind.None, null, null, 0);
    }

    public static string Title(string receives) => "Um computador quer entrar na sua conta e receber " + receives;

    public static string WrongCode(int attemptsLeft)
        => attemptsLeft == 1 ? "Código errado. Tem mais 1 tentativa." : $"Código errado. Tem mais {attemptsLeft} tentativas.";

    public static string NameLine(string name) => $"Nome que o outro computador indicou: «{ShownName(name)}»";

    // O nome de um pedido, pronto a mostrar neste computador: sem nenhum carácter numérico (Unicode Nd, Nl e No, de
    // qualquer escrita, incluindo os de largura total). Quem pede escolhe o nome depois de conhecer o código (o nome
    // segue selado no passo 4, o do código), e a barra nunca pode ter um código para copiar. Já limpo
    // (DeviceNameText); vazio → DeviceNameText.Unnamed.
    public static string ShownName(string? name)
    {
        var text = new StringBuilder();
        foreach (var rune in (DeviceNameText.Clean(name) ?? "").EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber))
                text.Append(rune.ToString());
        return DeviceNameText.Shown(text.ToString());
    }
}
