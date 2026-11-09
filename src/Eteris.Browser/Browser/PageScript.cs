using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CefSharp;
using CefSharp.DevTools;

namespace Eteris.Browser.Browser;

// Resultado de um script corrido na página: Result é texto, verdadeiro/falso, número (double) ou null.
public sealed record ScriptResult(bool Success, object? Result, string? Message = null)
{
    public static readonly ScriptResult Failed = new(false, null);
}

// JavaScript na página principal pelo DevTools (Runtime.evaluate), do lado do navegador.
//
// O EvaluateScriptAsync do CefSharp corre através do processo filho do CefSharp, que é .NET e por isso não pode
// correr dentro do sandbox do Chromium (docs/sandbox.md, passo A). O DevTools fala com o motor diretamente e funciona
// com qualquer processo filho — o do CefSharp ou o nativo.
public static class PageScript
{
    // Uma sessão de DevTools por página, reutilizada (o centro de multimédia pergunta de 2 em 2 segundos).
    private static readonly ConditionalWeakTable<IWebBrowser, DevToolsClient> Sessions = new();

    // userGesture: conta como ação do utilizador (tocar vídeo, sair do ecrã inteiro...). Só para ações pedidas pelo
    // utilizador — nunca em consultas automáticas, senão a página ganharia ações falsas (pop-ups, reprodução automática).
    // asyncBody: o script é o corpo de uma função async (pode usar await e return); espera-se pela promessa.
    public static async Task<ScriptResult> EvaluateAsync(IWebBrowser browser, string script, TimeSpan? timeout = null,
        bool userGesture = false, bool asyncBody = false)
    {
        if (browser.IsDisposed || !browser.IsBrowserInitialized) return ScriptResult.Failed;
        // Braço «puro» das medições de desempenho (só com ETERIS_TEST_PURO): nenhuma sessão DevTools.
        if (Infrastructure.PerfDiagnostics.Without(Infrastructure.PerfLayers.DevTools)) return ScriptResult.Failed;
        try
        {
            var client = Sessions.GetValue(browser, b => b.GetDevToolsClient());
            var call = DevToolsCalls.Run(() => client.Runtime.EvaluateAsync(Expression(script, asyncBody), returnByValue: true,
                awaitPromise: asyncBody, userGesture: userGesture));
            var limit = timeout ?? TimeSpan.FromSeconds(10);
            if (await Task.WhenAny(call, Task.Delay(limit)) != call) return new ScriptResult(false, null, "tempo esgotado");
            var response = await call;
            if (response.ExceptionDetails is { } error) return new ScriptResult(false, null, error.Exception?.Description ?? error.Text);
            return new ScriptResult(true, ValueOf(response.Result?.Value));
        }
        catch (Exception exception)
        {
            return new ScriptResult(false, null, exception.Message);
        }
    }

    // A aba fechou ou adormeceu: a sessão de DevTools dessa página é descartada (a tabela fraca não chega: o
    // observador nativo do CefSharp mantém o cliente vivo até ao fim do processo).
    public static void Release(IWebBrowser browser)
    {
        if (!Sessions.TryGetValue(browser, out var client)) return;
        Sessions.Remove(browser);
        try { client.Dispose(); } catch (Exception) { }
    }

    // Corre e não espera pela resposta.
    public static void Run(IWebBrowser browser, string script, bool userGesture = false)
        => _ = EvaluateAsync(browser, script, userGesture: userGesture);

    public static string Expression(string script, bool asyncBody)
        => asyncBody ? "(async () => {\n" + script + "\n})()" : script;

    // O DevTools devolve o valor como JSON; os chamadores esperam texto, verdadeiro/falso ou número.
    public static object? ValueOf(object? value) => value switch
    {
        null => null,
        JsonElement json => json.ValueKind switch
        {
            JsonValueKind.String => json.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => json.GetDouble(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => json.GetRawText(),
        },
        string or bool or double => value,
        IConvertible number when value is int or long or float or decimal => number.ToDouble(CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
