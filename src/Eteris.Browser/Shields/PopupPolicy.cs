namespace Eteris.Browser.Shields;

public static class PopupPolicy
{
    // Como o Chrome: sem gesto do utilizador não há pop-up. Com gesto, o destino passa pelas listas como
    // navegação, porque o adblock-rust ainda não implementa as regras $popup (apanha os redirecionadores ||dominio^).
    public static bool Allow(bool userGesture, string targetUrl, string? openerUrl, IRequestBlocker blocker, Func<string, bool> isProtected)
    {
        if (!userGesture) return false;
        if (openerUrl is not null && Uri.TryCreate(openerUrl, UriKind.Absolute, out var opener) && !isProtected(opener.Host))
            return true;
        return !blocker.ShouldBlock(targetUrl, openerUrl, "document");
    }
}
