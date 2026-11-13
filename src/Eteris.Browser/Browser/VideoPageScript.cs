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
// AddScriptToExecuteOnDocumentCreatedAsync em qualquer outro ficheiro. Pelo mesmo canal passa a limpeza do
// armazenamento de uma origem no perfil do motor de vídeo (ClearOriginAsync), a única chamada DevTools que não é um script.
public static class VideoPageScript
{
    // Um script que corre antes dos da página em cada documento novo (Page.addScriptToEvaluateOnNewDocument, pelo
    // DevTools do WebView2). Uma falha (o motor a fechar) não sai daqui: a página segue sem o script.
    // Medido (docs/paridade.md §7, WebView2 158.0.4370.16): sem o Page.enable antes, o registo devolve um identificador mas
    // o script nunca corre nas páginas; com ele corre antes dos da própria página.
    // Devolve o identificador do registo (para RemoveEarlyAsync), ou nulo se falhou.
    public static async Task<string?> AddEarlyAsync(CoreWebView2 core, string source)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            return ScriptId(await core.CallDevToolsProtocolMethodAsync("Page.addScriptToEvaluateOnNewDocument",
                JsonSerializer.Serialize(new { source })));
        }
        catch (Exception) { return null; }
    }

    // Tira um script registado por AddEarlyAsync: os documentos seguintes já não o correm (o atual fica como está).
    public static async Task RemoveEarlyAsync(CoreWebView2 core, string identifier)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.removeScriptToEvaluateOnNewDocument",
                JsonSerializer.Serialize(new { identifier }));
        }
        catch (Exception) { }
    }

    // O identificador na resposta do Page.addScriptToEvaluateOnNewDocument ({"identifier":"1"}), ou nulo.
    public static string? ScriptId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("identifier", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // Apaga o armazenamento de uma origem no perfil do motor de vídeo (Storage.clearDataForOrigin, pelo DevTools do
    // WebView2): o «Triturar», o «Esquecer ao fechar» e os sites de salto. Devolve false se o motor recusou ou falhou
    // (a falha não sai daqui, como em AddEarlyAsync).
    public static async Task<bool> ClearOriginAsync(CoreWebView2 core, string origin)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin",
                JsonSerializer.Serialize(new { origin, storageTypes = "all" }));
            return true;
        }
        catch (Exception) { return false; }
    }

    // Um script na página do motor de vídeo (Runtime.evaluate pelo DevTools do WebView2), com ou sem gesto do utilizador,
    // que devolve um número: a Pausa (MainWindow.Pause.cs) pausa e marca o que toca, e o «Retomar o que tocava» toca-o
    // outra vez com o gesto do clique. Nulo se falhou (o motor a fechar, uma exceção da página).
    public static async Task<double?> EvaluateNumberAsync(CoreWebView2 core, string expression, bool userGesture = false)
    {
        try
        {
            return NumberResult(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression, returnByValue = true, userGesture })));
        }
        catch (Exception) { return null; }
    }

    // O número na resposta do Runtime.evaluate ({"result":{"type":"number","value":2}}), ou nulo.
    public static double? NumberResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                   && result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble() : null;
        }
        catch (JsonException) { return null; }
    }
}
