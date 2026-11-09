using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CefSharp;
using CefSharp.DevTools;
using CefSharp.DevTools.Page;
using CefSharp.DevTools.Runtime;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Vault;

public enum VaultMessageType { Ready, Focus, Input, Blur, Scroll, Submit, FormGone, Generate, Context }

// Posição de um campo em píxeis CSS, relativa à área visível da página de topo.
public sealed record VaultRect(double X, double Y, double Width, double Height);

// Uma mensagem do script do cofre, já validada. Só o «submit» traz segredos (o que o utilizador escreveu e enviou); o
// ToString nunca os mostra.
public sealed record VaultMessage(VaultMessageType Type)
{
    public int Version { get; init; }
    public string? Field { get; init; }
    public string? Form { get; init; }
    public string? Kind { get; init; }
    public string? FormKind { get; init; }
    public VaultRect? Rect { get; init; }
    public bool Visible { get; init; }
    public string? Prefix { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? NewPassword { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public string? Pattern { get; init; }

    public override string ToString() => $"VaultMessage {Type} {Field} {Form} {Kind} {FormKind}";
}

// O esquema das mensagens do script (desenho, secção 3.4): JSON, no máximo 16 KiB, propriedades conhecidas e com o tipo
// certo. O que não encaixa é descartado — a página partilha o DOM com o script e pode ter mexido no que ele lê.
public static class VaultMessages
{
    public const int MaxBytes = 16 * 1024;
    public const int MaxPrefix = 256;
    public const int MaxSecret = 4096;
    public const int MaxPattern = 512;

    // Os tipos de campo que o classificador do script conhece (cofre.js, Classifier.KINDS).
    public static readonly IReadOnlyList<string> FieldKinds =
    [
        "username", "email", "password", "new-password", "one-time-code",
        "name", "given-name", "family-name", "organization", "street-address", "address-line1", "address-line2", "address-line3",
        "address-number", "address-complement", "neighborhood", "city", "region", "postal-code", "country", "tel",
        "cc-name", "cc-number", "cc-exp", "cc-exp-month", "cc-exp-year", "cc-csc", "cc-type",
        "iban", "tax-id", "id-number", "plate",
    ];

    public static readonly IReadOnlyList<string> FormKinds = ["login", "signup", "change", "address", "payment", "other"];

    private static readonly HashSet<string> s_fieldKinds = [.. FieldKinds];
    private static readonly HashSet<string> s_formKinds = [.. FormKinds];
    private static readonly Regex s_id = new("^[a-z][a-z0-9]{0,15}$", RegexOptions.CultureInvariant);

    private sealed record Shape(VaultMessageType Type, string[] Required, string[] Optional);

    private static readonly Dictionary<string, Shape> s_shapes = new(StringComparer.Ordinal)
    {
        ["ready"] = new(VaultMessageType.Ready, ["v"], []),
        ["focus"] = new(VaultMessageType.Focus, ["field", "form", "kind", "rect", "visible"], ["formKind", "prefix"]),
        ["input"] = new(VaultMessageType.Input, ["field"], ["prefix"]),
        ["blur"] = new(VaultMessageType.Blur, ["field"], []),
        ["scroll"] = new(VaultMessageType.Scroll, ["field"], ["rect"]),
        ["submit"] = new(VaultMessageType.Submit, ["form", "formKind"], ["username", "password", "newPassword"]),
        ["formGone"] = new(VaultMessageType.FormGone, ["form"], []),
        ["generate"] = new(VaultMessageType.Generate, ["field", "form"], ["minLength", "maxLength", "pattern"]),
        ["context"] = new(VaultMessageType.Context, ["field", "kind"], ["form", "rect"]),
    };

    public static VaultMessage? Parse(string? payload)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxBytes || Encoding.UTF8.GetByteCount(payload) > MaxBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
            return Read(document.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private static VaultMessage? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String || !s_shapes.TryGetValue(t.GetString()!, out var shape)) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "t") continue;
            if (!seen.Add(property.Name)) return null;
            if (!shape.Required.Contains(property.Name) && !shape.Optional.Contains(property.Name)) return null;
        }
        if (shape.Required.Any(name => !seen.Contains(name))) return null;

        var message = new VaultMessage(shape.Type);
        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "t":
                    break;
                case "v":
                    if (Integer(value, 1, 100) is not { } version) return null;
                    message = message with { Version = version };
                    break;
                case "field":
                    if (Id(value) is not { } field) return null;
                    message = message with { Field = field };
                    break;
                case "form":
                    if (Id(value) is not { } form) return null;
                    message = message with { Form = form };
                    break;
                case "kind":
                    if (value.ValueKind != JsonValueKind.String || !s_fieldKinds.Contains(value.GetString()!)) return null;
                    message = message with { Kind = value.GetString() };
                    break;
                case "formKind":
                    if (value.ValueKind != JsonValueKind.String || !s_formKinds.Contains(value.GetString()!)) return null;
                    message = message with { FormKind = value.GetString() };
                    break;
                case "rect":
                    if (Rect(value) is not { } rect) return null;
                    message = message with { Rect = rect };
                    break;
                case "visible":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                    message = message with { Visible = value.GetBoolean() };
                    break;
                case "prefix":
                    if (Text(value, MaxPrefix) is not { } prefix) return null;
                    message = message with { Prefix = prefix };
                    break;
                case "username":
                    if (Text(value, MaxSecret) is not { } username) return null;
                    message = message with { Username = username };
                    break;
                case "password":
                    if (Text(value, MaxSecret) is not { } password) return null;
                    message = message with { Password = password };
                    break;
                case "newPassword":
                    if (Text(value, MaxSecret) is not { } newPassword) return null;
                    message = message with { NewPassword = newPassword };
                    break;
                case "minLength":
                    if (Integer(value, 0, MaxSecret) is not { } min) return null;
                    message = message with { MinLength = min };
                    break;
                case "maxLength":
                    if (Integer(value, 0, MaxSecret) is not { } max) return null;
                    message = message with { MaxLength = max };
                    break;
                case "pattern":
                    if (Text(value, MaxPattern) is not { } pattern) return null;
                    message = message with { Pattern = pattern };
                    break;
                default:
                    return null;
            }
        }
        return message;
    }

    private static string? Id(JsonElement value)
        => value.ValueKind == JsonValueKind.String && s_id.IsMatch(value.GetString()!) ? value.GetString() : null;

    private static string? Text(JsonElement value, int max)
        => value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= max ? text : null;

    private static int? Integer(JsonElement value, int min, int max)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max ? number : null;

    private static VaultRect? Rect(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4) return null;
        var numbers = new double[4];
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number) || !double.IsFinite(number) || Math.Abs(number) > 1_000_000) return null;
            numbers[i++] = number;
        }
        return numbers[2] < 0 || numbers[3] < 0 ? null : new VaultRect(numbers[0], numbers[1], numbers[2], numbers[3]);
    }
}

// Um contexto do mundo isolado do cofre: o documento (frame + loader) e a origem que o processo do navegador lhe deu.
public sealed record VaultContext(int Id, string FrameId, string LoaderId, string Origin, bool IsMain);

// contextId → documento e origem. Cada documento (frame + loader) prepara-se uma vez; quando o frame navega, os
// contextos dos documentos que saíram esquecem-se (e um «preencher» atrasado não chega a outro documento).
public sealed class VaultContexts
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _claims = new(StringComparer.Ordinal);
    private readonly Dictionary<int, VaultContext> _contexts = [];

    public int Count
    {
        get { lock (_gate) return _contexts.Count; }
    }

    // true se este documento ainda não foi preparado (e passa a estar a sê-lo).
    public bool TryClaim(string frameId, string loaderId)
    {
        lock (_gate)
        {
            if (_claims.TryGetValue(frameId, out var claimed) && claimed == loaderId) return false;
            _claims[frameId] = loaderId;
            return true;
        }
    }

    // A preparação falhou: pode tentar-se outra vez.
    public void Release(string frameId, string loaderId)
    {
        lock (_gate)
            if (_claims.TryGetValue(frameId, out var claimed) && claimed == loaderId) _claims.Remove(frameId);
    }

    // false se o documento já saiu entretanto ou se o contexto já era conhecido.
    public bool Add(VaultContext context)
    {
        lock (_gate)
        {
            if (!_claims.TryGetValue(context.FrameId, out var claimed) || claimed != context.LoaderId) return false;
            return _contexts.TryAdd(context.Id, context);
        }
    }

    public VaultContext? Get(int id)
    {
        lock (_gate) return _contexts.GetValueOrDefault(id);
    }

    public IReadOnlyList<VaultContext> All()
    {
        lock (_gate) return [.. _contexts.Values];
    }

    // O frame passou a mostrar o documento loaderId. O de topo leva consigo todos os outros frames.
    public void FrameNavigated(string frameId, string loaderId, bool isMain)
    {
        lock (_gate)
        {
            foreach (var (id, context) in _contexts.ToList())
                if ((isMain || context.FrameId == frameId) && !(context.FrameId == frameId && context.LoaderId == loaderId)) _contexts.Remove(id);
            foreach (var (frame, loader) in _claims.ToList())
                if ((isMain || frame == frameId) && !(frame == frameId && loader == loaderId)) _claims.Remove(frame);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _contexts.Clear();
            _claims.Clear();
        }
    }
}

// O canal entre o navegador e o script do cofre numa aba (desenho, secção 3.4; docs/palavras-passe.md, C1).
//
// Por documento http(s) do processo da página — o de topo e os iframes da mesma origem —, uma sessão DevTools própria:
//  1. Page.createIsolatedWorld(frameId, "eteris-cofre") → o contexto de um mundo isolado: a página partilha o DOM com
//     ele, mas não vê as variáveis, os ouvintes nem a consola dele;
//  2. Runtime.addBinding(nome aleatório, executionContextId) → a função só existe nesse mundo;
//  3. Runtime.evaluate(cofre.js, contextId).
// Sem Runtime.enable (as páginas detetam-no): o Runtime.bindingCalled não depende dele (V8, v8-runtime-agent-impl.cc).
// A origem de cada contexto é a do frame de topo no processo do navegador (CEF), confirmada outra vez ao preencher.
// Os eventos chegam na thread do CEF; onMessage e onNavigated correm lá (quem os recebe passa para a da janela).
public sealed class VaultChannel : IDisposable
{
    public const string WorldName = "eteris-cofre";

    // O que o navegador pode pedir ao script.
    // probe: a página nova tem outra vez um início de sessão? (erro de login depois de um envio; C4)
    private static readonly HashSet<string> s_methods = new(StringComparer.Ordinal) { "fill", "rect", "forget", "probe" };

    private readonly IBrowser _browser;
    private readonly Action<VaultContext, VaultMessage> _onMessage;
    private readonly Action? _onNavigated;
    private readonly Func<bool> _offers;
    private readonly VaultContexts _contexts = new();
    private readonly string _binding = NewBindingName();
    private readonly Lock _gate = new();
    private DevToolsClient? _client;
    private Task _started = Task.CompletedTask;
    private bool _disposed;

    // offers: «Oferecer para guardar palavras-passe» ligado e fora da janela privada, lido a cada documento preparado.
    public VaultChannel(IBrowser browser, Action<VaultContext, VaultMessage> onMessage, Action? onNavigated = null, Func<bool>? offers = null)
    {
        _browser = browser;
        _onMessage = onMessage;
        _onNavigated = onNavigated;
        _offers = offers ?? (() => false);
    }

    public static string NewBindingName() => "e" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    // O documento é http(s) e tem a origem que o navegador conhece para a página de topo (o de topo, ou um iframe da mesma
    // origem). A origem devolvida é a do navegador; null se o script não deve entrar.
    public static string? EligibleOrigin(string? frameUrl, string? browserMainUrl)
    {
        if (Origins.Normalize(frameUrl) is not { } frame || Origins.IsAndroid(frame)) return null;
        if (Origins.Normalize(browserMainUrl) is not { } main) return null;
        return frame == main ? main : null;
    }

    public static string CallExpression(string method, object? argument)
    {
        if (!s_methods.Contains(method)) throw new ArgumentException("Método do cofre desconhecido.", nameof(method));
        // O JsonSerializer escapa as aspas, o < e o > e tudo o que não é ASCII: nada do argumento fecha a expressão.
        var json = JsonSerializer.Serialize(argument);
        return $"(Object.prototype.hasOwnProperty.call(globalThis, \"__cofre\") ? globalThis.__cofre.{method}({json}) : null)";
    }

    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    // Abre a sessão (uma vez) e prepara os documentos que já existem. Chamado no commit e no fim de cada carregamento do
    // frame de topo: o 1.º documento de uma aba nasce antes de a sessão poder receber o Page.frameNavigated dele.
    public void Refresh()
    {
        DevToolsClient client;
        lock (_gate)
        {
            if (_disposed) return;
            if (_client is null && !Start()) return;
            client = _client!;
        }
        _ = RefreshAsync(client);
    }

    // Dentro do _gate.
    private bool Start()
    {
        try { _client = _browser.GetDevToolsClient(); }
        catch (Exception) { return false; }
        _client.Page.FrameNavigated += OnFrameNavigated;
        _client.Runtime.BindingCalled += OnBindingCalled;
        var client = _client;
        _started = Quietly(() => DevToolsCalls.Run(() => client.Page.EnableAsync()));
        return true;
    }

    private async Task RefreshAsync(DevToolsClient client)
    {
        await _started.ConfigureAwait(false);
        FrameTree? tree;
        try { tree = (await DevToolsCalls.Run(() => client.Page.GetFrameTreeAsync()).ConfigureAwait(false)).FrameTree; }
        catch (Exception) { return; }
        if (tree is null) return;
        var pending = new Stack<(FrameTree Node, bool IsMain)>();
        pending.Push((tree, true));
        while (pending.Count > 0)
        {
            var (node, isMain) = pending.Pop();
            if (node.Frame is { } frame) await SetUpAsync(client, frame, isMain).ConfigureAwait(false);
            foreach (var child in node.ChildFrames ?? []) pending.Push((child, false));
        }
    }

    private void OnFrameNavigated(object? sender, FrameNavigatedEventArgs e)
    {
        if (e.Frame is not { } frame) return;
        var isMain = string.IsNullOrEmpty(frame.ParentId);
        _contexts.FrameNavigated(frame.Id, frame.LoaderId, isMain);
        if (isMain) _onNavigated?.Invoke();
        DevToolsClient? client;
        lock (_gate) client = _disposed ? null : _client;
        if (client is not null) _ = SetUpAsync(client, frame, isMain);
    }

    private async Task SetUpAsync(DevToolsClient client, Frame frame, bool isMain)
    {
        if (string.IsNullOrEmpty(frame.Id) || string.IsNullOrEmpty(frame.LoaderId)) return;
        if (EligibleOrigin(frame.Url, SafeFrame.MainUrl(_browser)) is not { } origin) return;
        if (!_contexts.TryClaim(frame.Id, frame.LoaderId)) return;
        try
        {
            var world = await DevToolsCalls.Run(() => client.Page.CreateIsolatedWorldAsync(frame.Id, WorldName, grantUniveralAccess: false)).ConfigureAwait(false);
            var contextId = world.ExecutionContextId;
            if (!_contexts.Add(new VaultContext(contextId, frame.Id, frame.LoaderId, origin, isMain))) return;
            await DevToolsCalls.Run(() => client.Runtime.AddBindingAsync(_binding, executionContextId: contextId)).ConfigureAwait(false);
            await DevToolsCalls.Run(() => client.Runtime.EvaluateAsync(VaultScripts.Cofre(_binding, _offers()), contextId: contextId, silent: true)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // O documento saiu a meio, ou a sessão fechou: fica para a próxima vez que o navegador o pedir.
            _contexts.Release(frame.Id, frame.LoaderId);
        }
    }

    private void OnBindingCalled(object? sender, BindingCalledEventArgs e)
    {
        if (!string.Equals(e.Name, _binding, StringComparison.Ordinal)) return;
        if (_contexts.Get(e.ExecutionContextId) is not { } context) return;
        if (VaultMessages.Parse(e.Payload) is not { } message) return;
        _onMessage(context, message);
    }

    // O documento de topo que o navegador mostra agora (null: ainda não preparado, ou não é http(s)).
    public VaultContext? MainContext()
    {
        var main = Origins.Normalize(SafeFrame.MainUrl(_browser));
        return _contexts.All().Where(c => c.IsMain && c.Origin == main).OrderByDescending(c => c.Id).FirstOrDefault();
    }

    // O contexto continua a ser o documento que o navegador mostra, com a mesma origem.
    public bool IsCurrent(VaultContext context)
        => _contexts.Get(context.Id) is not null && EligibleOrigin(context.Origin, SafeFrame.MainUrl(_browser)) == context.Origin;

    // Pede ao script do contexto (nunca a outro): method é fill, rect ou forget; o argumento vai serializado.
    public async Task<ScriptResult> CallAsync(VaultContext context, string method, object? argument, TimeSpan? timeout = null)
    {
        var expression = CallExpression(method, argument);
        DevToolsClient? client;
        lock (_gate) client = _disposed ? null : _client;
        if (client is null || !IsCurrent(context)) return ScriptResult.Failed;
        try
        {
            var call = DevToolsCalls.Run(() => client.Runtime.EvaluateAsync(expression, contextId: context.Id, returnByValue: true, silent: true));
            if (await Task.WhenAny(call, Task.Delay(timeout ?? TimeSpan.FromSeconds(5))).ConfigureAwait(false) != call)
                return new ScriptResult(false, null, "tempo esgotado");
            var response = await call.ConfigureAwait(false);
            if (response.ExceptionDetails is not null) return ScriptResult.Failed;
            return new ScriptResult(true, PageScript.ValueOf(response.Result?.Value));
        }
        catch (Exception)
        {
            return ScriptResult.Failed;
        }
    }

    private static async Task Quietly(Func<Task> call)
    {
        try { await call().ConfigureAwait(false); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        DevToolsClient? client;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            client = _client;
            _client = null;
        }
        _contexts.Clear();
        if (client is null) return;
        try
        {
            client.Page.FrameNavigated -= OnFrameNavigated;
            client.Runtime.BindingCalled -= OnBindingCalled;
            client.Dispose();
        }
        catch (Exception) { }
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"VaultChannel {_contexts.Count}");
}
