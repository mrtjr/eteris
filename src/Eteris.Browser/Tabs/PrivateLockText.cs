using Eteris.Browser.Vault;

namespace Eteris.Browser.Tabs;

// Os textos da tranca das janelas privadas (MainWindow.PrivateLock.cs), num só sítio. Nenhum recebe nada da página (nem
// título, nem endereço, nem o nome de um site): o cartão de uma janela trancada não pode dizer o que está por trás. Todos
// dizem com honestidade o que a tranca é: uma camada de conveniência, não uma cifra. Na interface diz-se sempre «janela(s)
// privada(s)»; «aba trancada» é outra coisa (BrowserTab.IsLocked), e «bloquear» é do Windows (Win+L).
public static class PrivateLockText
{
    // O título da janela (barra de tarefas, Alt+Tab, GetWindowText de qualquer programa) enquanto está trancada.
    public const string WindowTitle = "Janela privada trancada — Éteris";

    public const string CardTitle = "Janela privada trancada";

    public const string Confirming = "A confirmar com o Windows…";

    public const string Honest = "Uma camada de conveniência, não uma cifra: as abas continuam abertas na memória deste computador e as páginas continuam a correr.";

    // O texto da janela do Windows (Windows Hello ou palavra-passe do Windows).
    public const string HelloMessage = "O Éteris quer mostrar as janelas privadas trancadas.";

    // A conta do Windows sem proteção (a CredUI aceitou uma palavra-passe em branco): destranca e diz-o com honestidade.
    public const string Unprotected = "A sua conta do Windows não tem palavra-passe nem Windows Hello: a tranca das janelas privadas não protege nada. Proteja a conta do Windows com um PIN.";

    public const string StillPlaying = "Esta janela continua a tocar som.";

    public const string PauseAll = "Pausar tudo (Ctrl+.)";

    // Sem o nome do site: o cartão não diz o que está por trás.
    public const string Capturing = "Um site desta janela pode continuar a usar a câmara, o microfone ou o ecrã.";

    // A paleta e o menu ⋯.
    public const string CommandTitle = "Trancar janelas privadas";

    public const string CommandDetail = "Esconde as abas privadas até desbloquear com o Windows Hello (não é uma cifra)";

    // A folha de atalhos (Palette/ShortcutSheet.cs).
    public const string ShortcutAction = "Trancar as janelas privadas (desbloqueia com o Windows Hello)";

    // Definições › Privacidade e segurança (Browser/settings.html).
    public const string SettingsTitle = "Trancar as janelas privadas";

    public const string SettingsDetail = "Ao bloquear o Windows, ao suspender o computador e depois de algum tempo sem uso, as janelas privadas escondem as abas atrás de um cartão que só abre com o Windows Hello (ou com a palavra-passe do Windows). Nunca se trancam por falta de uso com som, vídeo ou uma chamada em curso. É uma camada de conveniência, não uma cifra: as abas continuam abertas na memória deste computador e as páginas continuam a correr. Para trancar já: Ctrl+Alt+L ou a paleta (Ctrl+K).";

    public const string MinutesTitle = "Trancar depois de parada";

    public const string MinutesDetail = "Tempo sem uso de uma janela privada até se trancar. «Nunca» deixa só o bloqueio do Windows e a suspensão.";

    // Sem Hello (ou ainda sem saber), a palavra-passe do Windows; a 1.ª vez, até se saber, diz-se «Windows Hello».
    private static bool Hello(ReauthAvailability availability) => availability == ReauthAvailability.Hello;

    public static string CardLine(ReauthAvailability availability)
        => Hello(availability) ? "Desbloqueie com o Windows Hello para ver as abas." : "Desbloqueie com a palavra-passe do Windows para ver as abas.";

    public static string UnlockButton(ReauthAvailability availability)
        => Hello(availability) ? "Desbloquear com o Windows Hello" : "Desbloquear com a palavra-passe do Windows";

    // O estado por baixo do botão (região viva educada). null: sem texto (destrancou, ou uma tranca interrompeu a pergunta).
    public static string? Status(PrivateUnlockResult result) => result switch
    {
        PrivateUnlockResult.Canceled => "Continua trancada: a confirmação foi cancelada.",
        PrivateUnlockResult.Failed => "O Windows não confirmou. Tente outra vez.",
        PrivateUnlockResult.Busy => "Já há uma confirmação do Windows aberta. Termine-a e tente outra vez.",
        _ => null,
    };

    // O aviso numa janela normal depois do comando manual.
    public static string LockedToast(int count, ReauthAvailability availability)
    {
        var with = Hello(availability) ? "com o Windows Hello" : "com a palavra-passe do Windows";
        return count == 1
            ? $"1 janela privada trancada — desbloqueia {with}"
            : $"{count} janelas privadas trancadas — desbloqueiam {with}";
    }

    // A marca no botão «Desbloquear» (ItemStatus de automação) só com o verificador de teste: a prova ao vivo lê-a antes de
    // clicar, para nunca abrir o diálogo verdadeiro do Windows por engano.
    public static string TestMarker(PrivateUnlockTestMode mode) => mode switch
    {
        PrivateUnlockTestMode.Cancel => "via de teste: cancelado",
        _ => "via de teste: indisponível",
    };
}
