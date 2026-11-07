using CefSharp;
using CefSharp.DevTools;

namespace Eteris.Browser.Shields;

// Scriptlets do uBlock registados via DevTools (Page.addScriptToEvaluateOnNewDocument): o Chromium corre-os
// antes de qualquer script da página, que é quando os anti-bloqueadores se instalam.
// onSession corre uma vez, quando a sessão DevTools nasce (antes do primeiro registo): por exemplo, a vista móvel
// de um site fixado (Emulation.setUserAgentOverride), que tem de valer antes de o primeiro documento nascer.
public sealed class ScriptletInjector(Func<string, bool> isProtected, Func<string, string?>? extra = null,
    Func<DevToolsClient, Task>? onSession = null) : IDisposable
{
    private readonly Lock _gate = new();
    private DevToolsClient? _client;
    private string? _identifier;
    // O último script pedido (registado ou na fila); null = nenhum.
    private string? _wanted;
    private Task _queue = Task.CompletedTask;

    // Chamado no início de cada navegação do frame principal (incluindo redirecionamentos).
    // A tarefa termina quando o registo está feito: o pedido da página espera por ela.
    public Task Prepare(IBrowser browser, string url)
    {
        var scriptlets = Uri.TryCreate(url, UriKind.Absolute, out var page) && isProtected(page.Host)
            && ShieldEngine.Cosmetic is { } engine
            ? ScriptletScripts.Wrap(engine.Scriptlets(url), url)
            : null;
        var script = PrivacySignals.Compose(scriptlets, extra?.Invoke(url));
        var signals = PrivacySignals.ScriptFor(url) is not null;

        lock (_gate)
        {
            if (script is null && !signals && _identifier is null && _client is null && onSession is null) return Task.CompletedTask;
            if (_client is null)
            {
                try { _client = browser.GetDevToolsClient(); }
                catch (Exception) { return Task.CompletedTask; }
                // O Chromium só corre os scripts registados se o domínio Page estiver ativo na sessão.
                var created = _client;
                _queue = _queue.ContinueWith(_ => created.Page.EnableAsync()).Unwrap();
                if (onSession is not null) _queue = _queue.ContinueWith(_ => RunSession(created)).Unwrap();
                // O GPC visível ao JavaScript (navigator.globalPrivacyControl) vale em todas as páginas, com ou sem
                // escudo: registado uma vez por sessão e nunca retirado, não depende da corrida de cada navegação.
                _queue = _queue.ContinueWith(_ => Register(created, PrivacySignals.GpcScript)).Unwrap();
            }
            // Mesmo script que o já registado (recarregar, outra página do mesmo site): fica como está. Retirar e
            // voltar a registar durante uma navegação só é atendido depois de o documento novo nascer (o Chromium
            // guarda os comandos até lá), e o documento ficava sem scripts — numa janela privada, sempre.
            if (!NeedsReplace(_wanted, script)) return _queue;
            _wanted = script;
            var client = _client;
            _queue = _queue.ContinueWith(_ => Replace(client, script)).Unwrap();
            return _queue;
        }
    }

    // Decisão pura (testada): um registo novo só quando o script muda.
    public static bool NeedsReplace(string? wanted, string? script) => !string.Equals(script, wanted, StringComparison.Ordinal);

    private async Task RunSession(DevToolsClient client)
    {
        try { await onSession!(client); }
        catch (Exception) { }
    }

    private static async Task Register(DevToolsClient client, string script)
    {
        try { await client.Page.AddScriptToEvaluateOnNewDocumentAsync(script); }
        catch (Exception) { }
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
            // Sem registo: a próxima navegação volta a tentar.
            lock (_gate) _wanted = null;
            // O separador pode ter fechado entretanto; a página simplesmente segue sem scriptlets.
        }
    }

    public void Dispose()
    {
        lock (_gate) _client?.Dispose();
    }
}
