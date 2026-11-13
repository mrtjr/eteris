using System.Security.Cryptography;
using System.Text;

namespace Eteris.Browser.Infrastructure;

// Os nomes da instância única e do canal entre instâncias (INF-06). Antes o canal chamava-se «Eteris.Browser» no perfil
// normal para todos: dois utilizadores do Windows a usar o Éteris ao mesmo tempo disputavam o mesmo pipe (a lista de
// atalhos e os links iam parar à instância do outro, ou a nenhuma). Agora o canal leva o hash da pasta do perfil (também no
// normal) e a sessão do Windows. O mutex continua em Local\ (em Global\ outra conta podia ocupar o nome e o Mutex lançava
// no arranque) e, no perfil normal, com o nome de sempre, para uma cópia antiga e uma nova se verem.
public static class InstanceNames
{
    public const string Base = "Eteris.Browser";

    // 12 carateres hexadecimais da pasta do perfil normalizada (sem barra final, maiúsculas e minúsculas iguais).
    public static string Hash(string root)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(AppPaths.Normalize(root).ToLowerInvariant())))[..12];

    public static string Mutex(string root, bool customProfile) => customProfile ? Base + "." + Hash(root) : Base;

    public static string Channel(string root, int sessionId) => Base + "." + Hash(root) + ".s" + sessionId;
}
