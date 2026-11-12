namespace Eteris.Browser.Permissions;

// As regras do cartão de permissão que não dependem da janela (MOT-05): quanto tempo ignora cliques, com que
// «Lembrar» começa e qual dos pedidos à espera aparece a seguir.
public static class PermissionCard
{
    // Um clique ou um Enter logo depois de o cartão aparecer (ou de a janela recuperar o foco) é do que estava por baixo:
    // a página pode pôr o pedido debaixo de um clique que o utilizador ia dar noutro sítio.
    public static readonly TimeSpan ClickGuard = TimeSpan.FromMilliseconds(500);

    // O guarda conta a partir do mais recente entre o momento em que o cartão apareceu e o da última ativação.
    public static bool AcceptsClick(DateTime shownUtc, DateTime activatedUtc, DateTime nowUtc)
        => nowUtc - (shownUtc > activatedUtc ? shownUtc : activatedUtc) >= ClickGuard;

    // «Lembrar» começa desligado para a câmara, o microfone, o ecrã e a localização: um «Permitir» às cegas não fica
    // para sempre (o utilizador marca-o, se quiser). Para os outros tipos começa ligado, como antes.
    public static bool RemembersByDefault(IReadOnlyCollection<SitePermission> kinds)
        => !kinds.Any(k => k is SitePermission.Camera or SitePermission.Microphone or SitePermission.ScreenShare or SitePermission.Location);

    // O primeiro pedido, por ordem de chegada, cuja aba está à vista (-1: nenhum; os pedidos de uma aba escondida esperam
    // por ela).
    public static int Next(IReadOnlyList<bool> onScreen)
    {
        for (var i = 0; i < onScreen.Count; i++)
            if (onScreen[i]) return i;
        return -1;
    }
}
