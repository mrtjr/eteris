using System.Security.Cryptography;
using System.Text;

namespace Eteris.Sync.Pairing;

// O código de verificação escrito à mão no computador que já está na conta («number matching»): o computador novo
// mostra-o e a pessoa escreve-o no antigo, que o compara com o que calculou sem nunca o mostrar. Só memória: o código
// escrito não vai para endereços, registos nem para o transporte.
public static class PairingCode
{
    public const int Digits = 6;

    // Códigos errados no mesmo pedido até ele ser recusado.
    public const int MaxWrong = 3;

    // Tira espaços (incluindo o espaço sem quebra), hífenes e pontos; aceita só algarismos ASCII.
    // Devolve exatamente 6 algarismos, ou null se o texto não for um código completo.
    public static string? Normalize(string? typed)
    {
        if (typed is null) return null;
        var digits = new StringBuilder(Digits);
        foreach (var c in typed)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '.') continue;
            if (c is < '0' or > '9' || digits.Length == Digits) return null;
            digits.Append(c);
        }
        return digits.Length == Digits ? digits.ToString() : null;
    }

    // Compara em tempo constante o código escrito, já normalizado, com o código de verificação calculado («482 913»).
    // false se «typed» não normalizar.
    public static bool Matches(string sas, string? typed)
    {
        if (Normalize(typed) is not { } written || Normalize(sas) is not { } expected) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(written), Encoding.ASCII.GetBytes(expected));
    }
}

// O resultado de escrever o código num pedido (SyncAgent.ApproveWithCodeAsync).
public enum PairingAnswer
{
    Approved,      // código certo, porta do Windows passada, a conta foi entregue (passo 5)
    WrongCode,     // 6 algarismos que não batem; ainda há tentativas
    Refused,       // 3.º erro: o pedido foi recusado (x-host) e retirado
    NotConfirmed,  // código certo, mas a porta do Windows foi recusada: não conta como erro e o pedido continua
    Incomplete,    // o texto não tem 6 algarismos: não conta como erro
    Busy,          // já há uma aprovação deste pedido em curso
    Gone,          // o pedido já não existe, expirou ou ainda não tem código
}
