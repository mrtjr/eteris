namespace Eteris.Browser.Permissions;

// «Permitir» sem «Lembrar»: só em memória, até fechar o Éteris (as da janela privada à parte, esquecidas quando a última
// janela privada fecha), como Shields.LocalNetworkGrants. Desde que o Éteris repõe no motor o que não guardou (MOT-03),
// é o que evita um cartão novo em cada pedido de notificações, da área de transferência ou de várias transferências.
// Câmara, microfone, ecrã e localização ficam de fora (Applies): continuam a perguntar em cada pedido, para um clique
// apanhado de surpresa nunca valer mais do que uma vez. A rede local tem as suas (LocalNetworkGrants).
public static class PermissionGrants
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Site, SitePermission Kind, bool Private), byte> Granted = new();

    public static bool Applies(SitePermission kind)
        => kind is SitePermission.Notifications or SitePermission.Clipboard or SitePermission.MultipleDownloads;

    public static void Allow(string site, SitePermission kind, bool isPrivate)
    {
        if (Applies(kind)) Granted[(site, kind, isPrivate)] = 0;
    }

    public static bool IsAllowed(string site, SitePermission kind, bool isPrivate) => Granted.ContainsKey((site, kind, isPrivate));

    public static void Forget(string site, SitePermission kind)
    {
        Granted.TryRemove((site, kind, false), out _);
        Granted.TryRemove((site, kind, true), out _);
    }

    public static void Forget(string site)
    {
        foreach (var key in Granted.Keys.Where(k => k.Site == site).ToList()) Granted.TryRemove(key, out _);
    }

    // As do site (todas as chaves host[:porta] que lhe pertencem), só do contexto indicado (normal ou privado).
    public static void ForgetWhere(Func<string, bool> belongs, bool isPrivate)
    {
        foreach (var key in Granted.Keys.Where(k => k.Private == isPrivate && belongs(k.Site)).ToList()) Granted.TryRemove(key, out _);
    }

    public static void ForgetPrivate()
    {
        foreach (var key in Granted.Keys.Where(k => k.Private).ToList()) Granted.TryRemove(key, out _);
    }
}
