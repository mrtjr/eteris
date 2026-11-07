namespace Eteris.Browser.Browser;

public enum ErrorKind { NameNotResolved, Offline, Refused, Reset, TimedOut, Certificate, SecureChannel, Redirects, Blocked, Crashed, Other, Unreachable }

// O diagnóstico legível de uma falha de carregamento: o que aconteceu (uma frase), porquê é provável e o que fazer.
// Name é o nome do Chromium (ERR_…), útil para quem copia os detalhes e procura ajuda.
public sealed record Diagnosis(ErrorKind Kind, string Title, string Detail, string Glyph, string Cause, string Name,
    IReadOnlyList<string> Tips);

// Mapa dos códigos de erro de rede do Chromium (net/base/net_error_list.h) para o que a pessoa precisa de saber.
public static class ErrorDiagnosis
{
    public const int RendererCrashed = -9001;

    private static readonly Dictionary<int, string> Names = new()
    {
        [-2] = "ERR_FAILED", [-6] = "ERR_FILE_NOT_FOUND", [-7] = "ERR_TIMED_OUT", [-10] = "ERR_ACCESS_DENIED",
        [-20] = "ERR_BLOCKED_BY_CLIENT", [-21] = "ERR_NETWORK_CHANGED", [-27] = "ERR_BLOCKED_BY_RESPONSE",
        [-100] = "ERR_CONNECTION_CLOSED", [-101] = "ERR_CONNECTION_RESET", [-102] = "ERR_CONNECTION_REFUSED",
        [-103] = "ERR_CONNECTION_ABORTED", [-104] = "ERR_CONNECTION_FAILED", [-105] = "ERR_NAME_NOT_RESOLVED",
        [-106] = "ERR_INTERNET_DISCONNECTED", [-107] = "ERR_SSL_PROTOCOL_ERROR", [-109] = "ERR_ADDRESS_UNREACHABLE",
        [-113] = "ERR_SSL_VERSION_OR_CIPHER_MISMATCH", [-118] = "ERR_CONNECTION_TIMED_OUT", [-130] = "ERR_PROXY_CONNECTION_FAILED",
        [-137] = "ERR_NAME_RESOLUTION_FAILED", [-200] = "ERR_CERT_COMMON_NAME_INVALID", [-201] = "ERR_CERT_DATE_INVALID",
        [-202] = "ERR_CERT_AUTHORITY_INVALID", [-203] = "ERR_CERT_CONTAINS_ERRORS", [-204] = "ERR_CERT_NO_REVOCATION_MECHANISM",
        [-206] = "ERR_CERT_REVOKED", [-207] = "ERR_CERT_INVALID", [-208] = "ERR_CERT_WEAK_SIGNATURE_ALGORITHM",
        [-210] = "ERR_CERT_NON_UNIQUE_NAME", [-211] = "ERR_CERT_WEAK_KEY", [-213] = "ERR_CERT_VALIDITY_TOO_LONG",
        [-300] = "ERR_INVALID_URL", [-301] = "ERR_DISALLOWED_URL_SCHEME", [-310] = "ERR_TOO_MANY_REDIRECTS", [-312] = "ERR_UNSAFE_PORT",
        [-320] = "ERR_INVALID_RESPONSE", [-324] = "ERR_EMPTY_RESPONSE",
    };

    public static string NameOf(int code)
        => code == RendererCrashed ? "RENDERER_CRASHED" : Names.TryGetValue(code, out var name) ? name : $"ERR_{-code}";

    public static ErrorKind KindOf(int code) => code switch
    {
        RendererCrashed => ErrorKind.Crashed,
        -105 or -137 => ErrorKind.NameNotResolved,
        // Só o -106 quer dizer «este computador está sem rede»: com o -109 (a máquina não responde, rede local, só IPv6)
        // e o -21 (mudou de Wi-Fi ou de VPN a meio) a ligação funciona — dizer «sem Internet» mandava mexer no Wi-Fi.
        -106 => ErrorKind.Offline,
        -109 => ErrorKind.Unreachable,
        -102 => ErrorKind.Refused,
        -100 or -101 or -103 or -104 or -324 or -320 or -21 => ErrorKind.Reset,
        -7 or -118 => ErrorKind.TimedOut,
        <= -200 and > -300 => ErrorKind.Certificate,
        -107 or -113 => ErrorKind.SecureChannel,
        -310 => ErrorKind.Redirects,
        -20 or -27 or -312 => ErrorKind.Blocked,
        _ => ErrorKind.Other,
    };

    // secureDns: o DNS seguro (DoH) está ligado nas Definições — se o nome não resolve, pode ser a rede a bloqueá-lo.
    public static Diagnosis For(int code, bool secureDns = false)
    {
        var name = NameOf(code);
        return KindOf(code) switch
        {
            ErrorKind.Crashed => new(ErrorKind.Crashed, "Esta página foi abaixo",
                "O processo que a mostrava terminou inesperadamente (falta de memória ou um erro do site). As outras abas não foram afetadas.",
                "☄", "O processo da página terminou", name,
                ["Tente de novo: a página volta a carregar num processo novo.", "Se acontecer sempre neste site, feche abas que não está a usar."]),
            ErrorKind.NameNotResolved => new(ErrorKind.NameNotResolved, "Não encontrámos este site",
                "Verifique se o endereço está bem escrito ou tente pesquisá-lo.", "🔎", "O nome não resolve (DNS)", name,
                secureDns
                    ? ["Confirme a ortografia do endereço.", "O DNS seguro está ligado: algumas redes bloqueiam-no. Experimente desligá-lo em Definições › Privacidade.", "Se o site é novo ou mudou de nome, pode ainda não existir."]
                    : ["Confirme a ortografia do endereço.", "Se o site é novo ou mudou de nome, pode ainda não existir.", "Noutros sites funciona? Então o problema é deste nome, não da sua ligação."]),
            ErrorKind.Offline => new(ErrorKind.Offline, "Está sem ligação à Internet",
                "Verifique o Wi-Fi ou o cabo. Enquanto isso, apanhe a luz.", "✦", "Sem rede", name,
                ["Verifique o Wi-Fi, o cabo ou o modo de avião.", "A página volta a tentar sozinha quando a ligação regressar."]),
            ErrorKind.Refused => new(ErrorKind.Refused, "O site recusou a ligação",
                "O servidor existe, mas não aceitou a ligação: pode estar desligado, em manutenção ou a usar outra porta.", "⛔",
                "Ligação recusada", name,
                ["Tente de novo daqui a pouco.", "Se o endereço tem uma porta (:8080), confirme-a.", "Uma firewall ou um proxy também podem recusar a ligação."]),
            ErrorKind.Unreachable => new(ErrorKind.Unreachable, "Não foi possível chegar a este endereço",
                "A ligação à Internet funciona, mas nenhum caminho leva até esta máquina: pode estar desligada, noutra rede ou só ter IPv6.",
                "⌁", "Endereço inalcançável", name,
                ["Se é um aparelho de casa (impressora, NAS, router), confirme que está ligado e na mesma rede.",
                 "Se usa VPN, ligue-a ou desligue-a e tente de novo.", "Tente de novo daqui a pouco."]),
            ErrorKind.Reset when code == -21 => new(ErrorKind.Reset, "A rede mudou a meio",
                "O computador trocou de rede (outro Wi-Fi, uma VPN, o regresso da suspensão) enquanto a página carregava.", "〰",
                "A rede mudou", name, ["Tente de novo: a ligação nova já deve funcionar."]),
            ErrorKind.Reset => new(ErrorKind.Reset, "A ligação caiu a meio",
                "O site começou a responder, mas a ligação foi cortada antes de a página chegar.", "〰", "Ligação interrompida", name,
                ["Tente de novo: costuma ser passageiro.", "Se acontecer em vários sites, reinicie o router ou verifique a VPN."]),
            ErrorKind.TimedOut => new(ErrorKind.TimedOut, "O site não respondeu",
                "Pode estar em baixo ou a demorar demasiado. Tente de novo daqui a pouco.", "⏳", "Tempo esgotado", name,
                ["Tente de novo daqui a pouco.", "Noutros sites está tudo bem? Então é este site que está lento ou em baixo."]),
            ErrorKind.Certificate => new(ErrorKind.Certificate, "A ligação não é segura",
                "O certificado deste site não é válido. O Éteris não abriu a página para proteger os seus dados.", "🛡",
                CertificateCause(code), name,
                [CertificateTip(code), "Não escreva palavras-passe nem dados de pagamento neste site enquanto o aviso persistir."]),
            ErrorKind.SecureChannel => new(ErrorKind.SecureChannel, "A ligação segura falhou",
                "O site e o Éteris não conseguiram combinar uma ligação cifrada (protocolo antigo ou mal configurado).", "🛡",
                "Falha na negociação TLS", name,
                ["Tente de novo daqui a pouco.", "Se o site é antigo, pode não suportar a cifra moderna que o Éteris exige."]),
            ErrorKind.Redirects => new(ErrorKind.Redirects, "Esta página redireciona sem parar",
                "O site manda o Éteris de um endereço para outro em círculo.", "🔁", "Demasiados redirecionamentos", name,
                ["Tente de novo.", "Se persistir, apagar os dados deste site (no cadeado) costuma resolver."]),
            // Portas reservadas a outros serviços (21, 25, 9…): o Chromium nunca as abre num navegador.
            ErrorKind.Blocked when code == -312 => new(ErrorKind.Blocked, "Esta porta está bloqueada",
                "O endereço usa uma porta reservada a outros serviços (correio, FTP…), que os navegadores não abrem por segurança.", "🛡",
                "Porta reservada", name, ["Confirme o número da porta no endereço.", "Se é um serviço seu, mude-o para outra porta (como 8080)."]),
            ErrorKind.Blocked => new(ErrorKind.Blocked, "Esta página foi bloqueada",
                "O pedido foi travado antes de sair (pelo escudo ou pelo próprio site, que não deixa ser aberto assim).", "🛡",
                "Bloqueado", name, ["Se confia no site, desligue o escudo para ele no cadeado e tente de novo."]),
            _ => new(ErrorKind.Other, "Não foi possível abrir a página", $"O Chromium devolveu o erro {code}.", "⚠",
                "Erro do Chromium", name, ["Tente de novo.", "Se persistir, copie os detalhes e procure o nome do erro."]),
        };
    }

    private static string CertificateCause(int code) => code switch
    {
        -200 => "O certificado é de outro site",
        -201 => "O certificado expirou (ou o relógio do computador está errado)",
        -202 => "O certificado não é de uma entidade conhecida",
        -206 => "O certificado foi revogado",
        -208 or -211 => "O certificado usa cifra fraca",
        _ => "Certificado inválido",
    };

    private static string CertificateTip(int code) => code switch
    {
        -201 => "Confirme que a data e a hora do Windows estão certas.",
        -202 => "Redes de empresas e antivírus que «inspecionam» o HTTPS causam este erro; noutras redes deve abrir.",
        -200 => "Confirme o endereço: pode ter escrito o nome de outro site parecido.",
        _ => "Tente mais tarde: o site pode estar a renovar o certificado.",
    };

    // O texto de «Copiar detalhes»: o que é preciso para pedir ajuda, sem nada da sessão (cookies, histórico).
    public static string Details(int code, string url, string version, DateTime when)
    {
        var diagnosis = For(code);
        return $"Endereço: {url}\nErro: {diagnosis.Name} ({code})\nDiagnóstico: {diagnosis.Cause}\n"
               + $"Quando: {when:yyyy-MM-dd HH:mm:ss}\nÉteris {version}";
    }
}
