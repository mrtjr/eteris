using CefSharp;
using CefSharp.DevTools;

namespace Eteris.Browser.Shields;

// Scriptlets do uBlock registados via DevTools (Page.addScriptToEvaluateOnNewDocument): o Chromium corre-os
// antes de qualquer script da página, que é quando os anti-bloqueadores se instalam.
public sealed class ScriptletInjector(Func<string, bool> isProtected, Func<string, string?>? extra = null) : IDisposable
{
    private readonly Lock _gate = new();
    private DevToolsClient? _client;
    private string? _identifier;
    private Task _queue = Task.CompletedTask;

    // Chamado no início de cada navegação do frame principal (incluindo redirecionamentos).
    // A tarefa termina quando o registo está feito: o pedido da página espera por ela.
    public Task Prepare(IBrowser browser, string url)
    {
        var scriptlets = Uri.TryCreate(url, UriKind.Absolute, out var page) && isProtected(page.Host)
            && ShieldEngine.Cosmetic is { } engine
            ? ScriptletScripts.Wrap(engine.Scriptlets(url), url)
            : null;
        var added = extra?.Invoke(url);
        var script = scriptlets is null ? added : added is null ? scriptlets : scriptlets + "\n;\n" + added;

        lock (_gate)
        {
            if (script is null && _identifier is null && _client is null) return Task.CompletedTask;
            if (_client is null)
            {
                try { _client = browser.GetDevToolsClient(); }
                catch (Exception) { return Task.CompletedTask; }
                // O Chromium só corre os scripts registados se o domínio Page estiver ativo na sessão.
                var created = _client;
                _queue = _queue.ContinueWith(_ => created.Page.EnableAsync()).Unwrap();
            }
            var client = _client;
            _queue = _queue.ContinueWith(_ => Replace(client, script)).Unwrap();
            return _queue;
        }
    }

    private async Task Replace(DevToolsClient client, string? script)
    {
        try
        {
            string? previous;
            lock (_gate) { previous = _identifier; _identifier = null; }
            if (previous is not null)
            {
                // Numa navegação que muda de processo o Chromium já descartou o registo: não é erro.
                try { await client.Page.RemoveScriptToEvaluateOnNewDocumentAsync(previous); }
                catch (DevToolsClientException) { }
            }
            if (script is null) return;
            var response = await client.Page.AddScriptToEvaluateOnNewDocumentAsync(script);
            lock (_gate) _identifier = response.Identifier;
        }
        catch (Exception)
        {
            // O separador pode ter fechado entretanto; a página simplesmente segue sem scriptlets.
        }
    }

    public void Dispose()
    {
        lock (_gate) _client?.Dispose();
    }
}
