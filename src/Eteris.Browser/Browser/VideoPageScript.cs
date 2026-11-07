using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Eteris.Browser.Browser;

// Scripts nas páginas do motor de vídeo (WebView2): o único sítio do Éteris que lhes injeta JavaScript.
//
// A regra do sandbox (docs/sandbox.md, «Regras para o código») manda o JavaScript do motor principal pelo DevTools
// (Browser/PageScript.cs) porque as APIs de scripts do CefSharp passam pelo processo filho .NET, que não corre dentro
// do sandbox. O WebView2 não tem esse processo filho (é o motor do Edge, com os processos e o sandbox dele), por isso a
// razão da regra não se aplica aqui. Mesmo assim o canal fica num só sítio, como no motor principal: é fácil de auditar
// o que entra nas páginas, e o SandboxRulesTests recusa CallDevToolsProtocolMethodAsync, ExecuteScriptAsync e
// AddScriptToExecuteOnDocumentCreatedAsync em qualquer outro ficheiro.
public static class VideoPageScript
{
    // Um script que corre antes dos da página em cada documento novo (Page.addScriptToEvaluateOnNewDocument, pelo
    // DevTools do WebView2). Uma falha (o motor a fechar) não sai daqui: a página segue sem o script.
    public static async Task AddEarlyAsync(CoreWebView2 core, string source)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.addScriptToEvaluateOnNewDocument",
                JsonSerializer.Serialize(new { source }));
        }
        catch (Exception) { }
    }
}
