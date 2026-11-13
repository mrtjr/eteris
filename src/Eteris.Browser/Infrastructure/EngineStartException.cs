namespace Eteris.Browser.Infrastructure;

// O Cef.Initialize devolveu falso: o motor não arrancou. O código é o de saída do Chromium (Cef.GetExitCode); o fecho normal
// (o Chromium recebeu o pedido de um segundo processo do mesmo perfil e saiu de propósito) não é uma falha.
public sealed class EngineStartException(int exitCode) : Exception($"O motor não arrancou (código {exitCode}).")
{
    public int ExitCode { get; } = exitCode;

    public bool IsNormalExit => ExitCode == (int)CefSharp.Enums.ResultCode.NormalExitProcessNotified;
}
