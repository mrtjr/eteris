using CefSharp;
using CefSharp.DevTools;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Shields;

// Scripts do Éteris que correm antes dos da página, registados pelo DevTools (Page.addScriptToEvaluateOnNewDocument):
// scriptlets do uBlock (os anti-bloqueadores instalam-se logo no início), regras cosméticas precoces, modo escuro e os
// scripts comuns do Éteris (deteção de vídeo, último clique, Alt+clique).
//
// Porquê assim (medido, docs/motor.md, F2): quando o Chromium começa uma navegação (DidStartNavigation), a sessão
// DevTools deixa de entregar mensagens ao renderer até ao fim dessa navegação. Um registo feito a meio só chega depois
// do commit — tarde para o documento — e quem esperava por ele (antes, o pedido da página) esperava sempre ~1 s. Com a
// aba parada, o mesmo registo leva 1–5 ms. Por isso:
//  - o script base (igual em todos os sites) regista-se uma vez, quando a sessão nasce (o browser acabou de nascer);
//  - o GPC visível ao JavaScript (navigator.globalPrivacyControl, PrivacySignals) também: vale em todas as páginas,
//    com ou sem escudo, registado à parte e nunca retirado;
//  - os scripts de cada site vão protegidos pelo hostname da página de topo e acumulam-se (HostScriptRegistry): nunca
//    é preciso tirá-los a meio de uma navegação. Registam-se com a aba parada: antes de cada navegação pedida pelo
//    Éteris, depois de cada commit (os links do mesmo site já os encontram) e ao passar o rato num link;
//  - os mais antigos só saem com a aba parada (Tidy, no fim do carregamento).
// Limite medido: antes da 1.ª navegação de um browser ainda não há renderer, e o DevTools só responde depois do commit
// dela (esperar 300 ms antes de navegar não adiantou nada e atrasava a aba nova). O 1.º documento de cada aba nasce
// por isso sem os scripts precoces, como antes; a partir do 2.º, todos os têm.
// onSession corre uma vez, quando a sessão DevTools nasce: por exemplo, a vista móvel de um site fixado
// (Emulation.setUserAgentOverride).
// hostExtra(url) dá o resto do script do site e hostKey(url) o que o decide (por exemplo, o modo escuro ligado).
public sealed class ScriptletInjector(Func<string, bool> isProtected, Func<string, string?>? hostExtra = null,
    string? baseScript = null, Func<DevToolsClient, Task>? onSession = null, Func<string, string>? hostKey = null) : IDisposable
{
    public const int MaxHosts = 24;

    private readonly Lock _gate = new();
    private readonly HostScriptRegistry _hosts = new(MaxHosts);
    private DevToolsClient? _client;
    private Task _queue = Task.CompletedTask;
    private bool _disposed;

    // A sessão DevTools (Page.enable, onSession) e o script base. Chamar logo que o browser nasce.
    public Task Start(IBrowser browser)
    {
        lock (_gate) return EnsureClient(browser) ? _queue : Task.CompletedTask;
    }

    // Os scripts do site de url (nenhum, se o site não precisa). A tarefa termina quando o registo está feito.
    public Task EnsureHost(IBrowser browser, string? url)
    {
        if (HostScriptRegistry.HostOf(url) is not { } page) return Task.CompletedTask;
        var host = page.IdnHost;
        var key = KeyFor(url!, page);
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_hosts.IsCurrent(host, key)) return _hosts.Pending(host);
        }
        var script = HostScriptRegistry.Guard(host, ScriptFor(url!, page));
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_hosts.Plan(host, key, script) is not { } change) return _hosts.Pending(host);
            if (!EnsureClient(browser)) return Task.CompletedTask;
            var client = _client!;
            _queue = _queue.ContinueWith(_ => Apply(client, change)).Unwrap();
            _hosts.SetPending(host, _queue);
            return _queue;
        }
    }

    // Como EnsureHost, mas sem recalcular um site já conhecido (ao passar o rato num link: pode ser muitas vezes).
    public Task EnsureHostIfMissing(IBrowser browser, string? url)
    {
        if (HostScriptRegistry.HostOf(url) is not { } page) return Task.CompletedTask;
        lock (_gate)
        {
            if (_hosts.Contains(page.IdnHost)) return _hosts.Pending(page.IdnHost);
        }
        return EnsureHost(browser, url);
    }

    // Com a aba parada: tira os registos mais antigos além de MaxHosts (nunca o do site à vista).
    public void Tidy(string? currentUrl)
    {
        lock (_gate)
        {
            if (_disposed || _client is not { } client) return;
            foreach (var id in _hosts.Evict(HostScriptRegistry.HostOf(currentUrl)?.IdnHost))
                _queue = _queue.ContinueWith(_ => Remove(client, id)).Unwrap();
        }
    }

    // O que decide o script do site: o escudo ligado nele, as listas carregadas (uma atualização troca o motor) e o resto.
    private string KeyFor(string url, Uri page)
        => $"{isProtected(page.Host)}|{(ShieldEngine.Cosmetic is { } engine ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(engine) : 0)}|{hostKey?.Invoke(url)}";

    private string? ScriptFor(string url, Uri page)
    {
        var scriptlets = isProtected(page.Host) && ShieldEngine.Cosmetic is { } engine
            ? ScriptletScripts.Wrap(engine.Scriptlets(url), url)
            : null;
        return PrivacySignals.Compose(scriptlets, hostExtra?.Invoke(url));
    }

    // Decisão pura (testada; HostScriptRegistry.Plan): um registo novo só quando o script muda. Retirar e voltar a
    // registar durante uma navegação só é atendido depois de o documento novo nascer, e a página ficava sem scripts.
    public static bool NeedsReplace(string? wanted, string? script) => !string.Equals(script, wanted, StringComparison.Ordinal);

    // Dentro do _gate.
    private bool EnsureClient(IBrowser browser)
    {
        if (_disposed) return false;
        if (_client is not null) return true;
        try { _client = browser.GetDevToolsClient(); }
        catch (Exception) { return false; }
        // O Chromium só corre os scripts registados se o domínio Page estiver ativo na sessão.
        var created = _client;
        _queue = _queue.ContinueWith(_ => Quietly(() => DevToolsCalls.Run(() => created.Page.EnableAsync()))).Unwrap();
        // Dicas do cliente de alta entropia reduzidas no motor (página, frames, workers dedicados e cabeçalhos). Antes do
        // onSession: a vista móvel de um site fixado troca-as pelas dela.
        _queue = _queue.ContinueWith(_ => Quietly(() => ReducedClientHints.Apply(created))).Unwrap();
        if (onSession is not null) _queue = _queue.ContinueWith(_ => Quietly(() => onSession(created))).Unwrap();
        // O GPC visível ao JavaScript vale em todas as páginas, com ou sem escudo: registado uma vez por sessão e nunca
        // retirado, não depende do script de cada site nem da corrida de cada navegação. A lista de dispositivos sem
        // permissão (PrivacySignals.DevicesScript) vai no mesmo registo.
        _queue = _queue.ContinueWith(_ => Quietly(() => DevToolsCalls.Run(() => created.Page.AddScriptToEvaluateOnNewDocumentAsync(PrivacySignals.SessionScript)))).Unwrap();
        if (!string.IsNullOrEmpty(baseScript))
            _queue = _queue.ContinueWith(_ => Quietly(() => DevToolsCalls.Run(() => created.Page.AddScriptToEvaluateOnNewDocumentAsync(baseScript)))).Unwrap();
        return true;
    }

    // O separador pode ter fechado entretanto: a página segue sem o script.
    private static async Task Quietly(Func<Task> call)
    {
        try { await call(); }
        catch (Exception) { }
    }

    private async Task Apply(DevToolsClient client, HostScriptRegistry.Change change)
    {
        if (change.OldId is not null) await Remove(client, change.OldId);
        if (change.Script is null) return;
        try
        {
            var response = await DevToolsCalls.Run(() => client.Page.AddScriptToEvaluateOnNewDocumentAsync(change.Script));
            string? stale;
            lock (_gate) stale = _hosts.Registered(change.Host, change.Script, response.Identifier);
            if (stale is not null) await Remove(client, stale);
        }
        catch (Exception) { }
    }

    private static async Task Remove(DevToolsClient client, string id)
    {
        try { await DevToolsCalls.Run(() => client.Page.RemoveScriptToEvaluateOnNewDocumentAsync(id)); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _client?.Dispose();
        }
    }
}
