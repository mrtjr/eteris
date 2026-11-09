using System.IO;
using System.Text.Json;
using Eteris.Browser.Data;

namespace Eteris.Browser.Vault;

// Um pedido a eteris://passwords/… ou eteris://autofill/…: o caminho, a consulta, o formulário (POST, nunca no
// endereço), se trazia o token desta sessão, se veio da janela privada e o browser (aba) que o fez.
public sealed record VaultRequest(
    string Host, string Path, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Form,
    bool Authorized, bool IsPrivate, int BrowserId)
{
    public string? Q(string name) => Query.TryGetValue(name, out var value) ? value : null;

    public string? F(string name) => Form.TryGetValue(name, out var value) ? value : null;

    public override string ToString() => $"VaultRequest {Host}{Path}";
}

// A resposta: HTML (a página) ou texto/JSON lido pela página num iframe da mesma origem (a CSP não deixa fetch).
public sealed record VaultReply(string Body, string Mime)
{
    public static VaultReply Html(string html) => new(html, "text/html");

    public static VaultReply Json(object value) => new(JsonSerializer.Serialize(value), "text/plain");

    public static VaultReply Text(string text) => new(text, "text/plain");

    public static readonly VaultReply Refused = new("recusado", "text/plain");

    public static readonly VaultReply NotFound = new("Página interna não encontrada", "text/plain");

    public override string ToString() => $"VaultReply {Mime} {Body.Length}";
}

// O que as páginas do cofre pedem ao processo do navegador (MainWindow.Vault.cs): a janela dona da reautenticação, os
// diálogos de ficheiro nativos, a área de transferência segura, o esconder das capturas, abrir uma aba, as definições.
public interface IVaultHost
{
    Task<nint> WindowAsync(int browserId);

    // Cancelado quando a aba que fez o pedido sai da frente (ou a janela fecha): a reautenticação desiste.
    CancellationToken WhileInFront(int browserId);

    Task<string?> PickImportFileAsync(int browserId);

    Task<string?> PickExportFileAsync(int browserId, string suggestedName);

    Task<bool> ConfirmAsync(int browserId, string title, string message);

    bool Recycle(string path);

    bool CopySecret(string text);

    IDisposable ShowSecret();

    void OpenTab(int browserId, string url);

    IReadOnlyDictionary<string, string> Icons(IEnumerable<string> urls);

    UserSettings Settings { get; }

    bool SetSetting(string key, string value);

    IDisposable Schedule(TimeSpan delay, Action run);

    // A sincronização está ligada neste computador com a linha «Palavras-passe».
    bool SyncsPasswords => false;
}

// As rotas das páginas do cofre (docs/palavras-passe.md, B2–B3). Regras: nenhum segredo no HTML inicial; mostrar,
// copiar e editar um segredo pedem a reautenticação (com a janela de graça); exportar pede sempre; importar e exportar
// passam por diálogos nativos no processo do navegador (o CSV nunca entra na página); apagar tem «Anular»; tudo exige o
// token desta sessão; a janela privada não abre estas páginas.
public sealed partial class VaultRoutes(VaultService vault, ReauthGate gate, IVaultHost host)
{
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);

    public const string PrivateMessage = "Abra numa janela normal";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, PendingDelete> _pending = new(StringComparer.Ordinal);
    private PendingImport? _import;
    private string? _lastImportFile;
    // Os segredos à vista (cada «Mostrar» ou formulário de edição tem a sua retenção): o /hide liberta só a sua, para a
    // resposta atrasada de um «Mostrar» não libertar a de outro. Sem nenhuma, as janelas voltam às capturas.
    private readonly Dictionary<string, IDisposable> _onScreen = new(StringComparer.Ordinal);

    private sealed record PendingDelete(VaultCollection Collection, string Id, IDisposable Timer);

    private sealed record PendingImport(ImportPlan Plan, string File);

    // As do perfil em uso, criadas na primeira vez que uma destas páginas é pedida (o arranque não abre o cofre).
    private static Lazy<VaultRoutes>? s_profile;

    public static void UseProfileHost(Func<IVaultHost> host)
        => s_profile ??= new(() => new VaultRoutes(VaultService.Profile, ReauthGate.Shared, host()));

    public static VaultRoutes? Profile => s_profile?.Value;

    // Ao fechar o Éteris: os apagamentos ainda por anular fazem-se (só se as páginas chegaram a abrir o cofre).
    public static void CommitPendingOnExit()
    {
        if (s_profile is { IsValueCreated: true } routes) routes.Value.CommitPending();
    }

    public async Task<VaultReply> HandleAsync(VaultRequest r)
    {
        try
        {
            return r.Host switch
            {
                "passwords" => await PasswordsAsync(r).ConfigureAwait(false),
                "autofill" => await AutofillAsync(r).ConfigureAwait(false),
                _ => VaultReply.NotFound,
            };
        }
        catch (VaultUnavailableException e)
        {
            return r.Path == "/" ? VaultReply.Html(PrivatePage(e.Message)) : VaultReply.Json(new { ok = false, message = e.Message });
        }
    }

    public static string PrivatePage(string message)
        => "<!doctype html><html lang=\"pt\"><head><meta charset=\"utf-8\"><meta name=\"color-scheme\" content=\"dark\"><title>Palavras-passe</title>"
           + "<style>body{font-family:\"Segoe UI\",system-ui,sans-serif;background:#0b0d13;color:#e9ebf2;display:grid;place-items:center;min-height:90vh;margin:0}"
           + "html.light body{background:#f3f4f9;color:#1b1f2a}p{color:#a3a9b8;max-width:440px;text-align:center;line-height:1.5}</style></head>"
           + "<body><main><h1 style=\"font-weight:400\">" + System.Net.WebUtility.HtmlEncode(message) + "</h1>"
           + "<p>As palavras-passe e o preenchimento automático abrem-se numa janela normal: a janela privada não lê nem mexe no cofre fora do preenchimento que pedir.</p></main></body></html>";

    // ---- Palavras-passe ----

    private async Task<VaultReply> PasswordsAsync(VaultRequest r)
    {
        if (r.IsPrivate) return r.Path == "/" ? VaultReply.Html(PrivatePage(PrivateMessage)) : VaultReply.Refused;
        if (r.Path == "/") return VaultReply.Html(Browser.PasswordsPage.Render(PasswordsData()));
        if (!r.Authorized) return VaultReply.Refused;
        switch (r.Path)
        {
            case "/list":
                return VaultReply.Json(PasswordsData());
            case "/reveal" when r.Q("id") is { } id:
            {
                if (!await AskAsync(r, ReauthPurpose.Reveal).ConfigureAwait(false)) return NotConfirmed();
                if (vault.GetLogin(id) is not { } login) return Gone();
                var hold = HoldSecretOnScreen();
                // A anterior (histórico local de 1 versão: mudada aqui, ou substituída por outro computador) vem junto.
                return VaultReply.Json(new { ok = true, password = login.Password, note = login.Note ?? string.Empty, previous = vault.PreviousPassword(id) ?? string.Empty, hold });
            }
            case "/hide":
                ReleaseSecretOnScreen(r.Q("h"));
                return VaultReply.Json(new { ok = true });
            case "/keep" when r.Q("h") is { } keep:
                return VaultReply.Json(new { ok = KeepSecretOnScreen(keep) });
            case "/copy" when r.Q("id") is { } id:
            {
                var password = r.Q("what") != "username";
                if (password && !await AskAsync(r, ReauthPurpose.Reveal).ConfigureAwait(false)) return NotConfirmed();
                if (vault.GetLogin(id) is not { } login) return Gone();
                var ok = host.CopySecret(password ? login.Password : login.Username);
                return VaultReply.Json(new
                {
                    ok,
                    message = !ok ? "A área de transferência está ocupada por outro programa. Tente outra vez."
                        : password ? "Palavra-passe copiada. A área de transferência é limpa daqui a 60 s e não fica no histórico."
                        : "Utilizador copiado.",
                });
            }
            case "/save":
                return await SaveLoginAsync(r).ConfigureAwait(false);
            case "/delete" when r.Q("id") is { } id:
                return Delete(VaultCollection.Logins, id);
            case "/undo":
                return VaultReply.Json(new { ok = Undo(r.Q("u")) });
            case "/never-remove" when r.Q("origin") is { } origin:
                vault.RemoveNeverSave(origin);
                return VaultReply.Json(new { ok = true });
            case "/change" when r.Q("id") is { } id:
            {
                if (vault.Logins().FirstOrDefault(l => l.Id == id) is not { } login || ChangePasswordUrl(login.Origin) is not { } url) return Gone();
                host.OpenTab(r.BrowserId, url);
                return VaultReply.Json(new { ok = true });
            }
            case "/health":
            {
                var report = PasswordHealth.Check(VisibleLogins().Select(l => (l.Id, vault.GetLogin(l.Id)?.Password ?? string.Empty)));
                return VaultReply.Json(new { ok = true, weak = report.Weak, reused = report.Reused, summary = report.Summary, count = report.Checked });
            }
            case "/import":
                return await ImportAsync(r).ConfigureAwait(false);
            case "/import-resolve":
                return ResolveImport(r.Q("choice") == "file" ? ConflictChoice.UseFile : ConflictChoice.KeepCurrent);
            case "/import-recycle":
            {
                string? file;
                lock (_lock) (file, _lastImportFile) = (_lastImportFile, null);
                var ok = file is not null && File.Exists(file) && host.Recycle(file);
                return VaultReply.Json(new { ok, message = ok ? "O ficheiro foi para a Reciclagem." : "O ficheiro já não está lá." });
            }
            case "/export":
                return await ExportAsync(r).ConfigureAwait(false);
            case "/erase-all":
            {
                if (!await AskAsync(r, ReauthPurpose.ErasePasswords).ConfigureAwait(false)) return NotConfirmed();
                var count = 0;
                foreach (var login in vault.Logins()) count += vault.Delete(VaultCollection.Logins, login.Id) ? 1 : 0;
                foreach (var entry in vault.List(VaultCollection.LoginsNever)) vault.Delete(VaultCollection.LoginsNever, entry.Id);
                return VaultReply.Json(new { ok = true, message = count == 1 ? "1 palavra-passe apagada." : $"{count} palavras-passe apagadas." });
            }
            case "/set" when r.Q("k") is { } key && r.Q("v") is { } value:
            {
                if (key is not ("pwOffer" or "pwSuggest" or "pwAskHello")) return VaultReply.Refused;
                // Desligar «Pedir o Windows Hello ao preencher palavras-passe» também pede o Windows Hello (senão quem tem
                // o PC desbloqueado anulava a proteção com um clique).
                if (Data.UserSettings.NeedsReauth(key, value) && !await AskAsync(r, ReauthPurpose.ChangePasswordSetting).ConfigureAwait(false))
                    return VaultReply.Refused;
                return VaultReply.Text(host.SetSetting(key, value) ? "ok" : "recusado");
            }
            default:
                return VaultReply.Refused;
        }
    }

    // A página só recebe metadados: site, utilizador, datas. A palavra-passe e a nota vêm só em /reveal.
    private object PasswordsData()
    {
        var logins = VisibleLogins();
        var icons = host.Icons(logins.Where(l => !Origins.IsAndroid(l.Origin)).Select(l => l.Origin + "/"));
        var settings = host.Settings;
        return new
        {
            token = Browser.InternalPages.Token,
            logins = logins.Select(l => LoginJson(l, icons)),
            never = vault.NeverSaveSites().Select(n => new { origin = n.Origin, host = Origins.DisplayHost(n.Origin) ?? n.Origin }).OrderBy(n => n.host, StringComparer.Ordinal),
            pwOffer = settings.PasswordOffer,
            pwSuggest = settings.PasswordSuggestions,
            pwAskHello = settings.PasswordAskHello,
            advice = gate.Advice,
            // A linha «Palavras-passe» da sincronização está ligada aqui: apagar apaga também nos outros computadores.
            synced = host.SyncsPasswords,
        };
    }

    private static object LoginJson(LoginEntry l, IReadOnlyDictionary<string, string> icons)
    {
        var android = Origins.IsAndroid(l.Origin);
        var url = l.Origin + "/";
        var key = android ? null : HistoryStore.SiteIconKey(url);
        return new
        {
            id = l.Id,
            origin = l.Origin,
            host = android ? l.Origin : Origins.DisplayHost(l.Origin) ?? l.Origin,
            site = Origins.Site(l.Origin) ?? l.Origin,
            username = l.Username,
            changed = Day(l.Meta, "changed"),
            created = Day(l.Meta, "created"),
            used = Day(l.Meta, "used"),
            generated = l.Meta["generated"]?.GetValue<bool>() ?? false,
            android,
            insecure = l.Origin.StartsWith("http://", StringComparison.Ordinal) && !Origins.IsSecure(l.Origin),
            letter = android ? "A" : Tabs.SiteBadge.Letter(url) ?? "?",
            color = Tabs.SiteBadge.Palette[Tabs.SiteBadge.ColorIndex(url)],
            icon = key is not null && icons.TryGetValue(key, out var icon) ? icon : null,
        };
    }

    private static string? Day(System.Text.Json.Nodes.JsonObject meta, string name)
        => meta[name] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s)
           && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) : null;

    private List<LoginEntry> VisibleLogins()
    {
        var hidden = Hidden(VaultCollection.Logins);
        return vault.Logins().Where(l => !hidden.Contains(l.Id)).ToList();
    }

    // /.well-known/change-password da origem guardada (RFC 8615, como o Chrome e o Safari): só ao clicar.
    public static string? ChangePasswordUrl(string origin)
        => Origins.Normalize(origin) is { } normal && !Origins.IsAndroid(normal) ? normal + "/.well-known/change-password" : null;

    private async Task<VaultReply> SaveLoginAsync(VaultRequest r)
    {
        var id = r.F("id");
        var origin = Origins.Normalize(r.F("origin"));
        var username = (r.F("username") ?? string.Empty).Trim();
        var password = r.F("password") ?? string.Empty;
        var note = r.F("note");
        if (origin is null || Origins.IsAndroid(origin) && string.IsNullOrEmpty(id))
            return Invalid("Endereço inválido: escreva o endereço do site (https://…).");
        if (password.Length == 0) return Invalid("Escreva a palavra-passe.");
        if (password.Length > PasswordCsv.MaxPassword || username.Length > PasswordCsv.MaxUsername || (note?.Length ?? 0) > PasswordCsv.MaxNote)
            return Invalid("Texto demasiado comprido (até 1000 carateres).");
        LoginItem? old = null;
        if (!string.IsNullOrEmpty(id))
        {
            if (!await AskAsync(r, ReauthPurpose.Reveal).ConfigureAwait(false)) return NotConfirmed();
            old = vault.GetLogin(id);
            if (old is null) return Gone();
        }
        var saved = vault.SaveLogin(new LoginItem(old?.Id ?? string.Empty, origin, username, password, string.IsNullOrEmpty(note) ? null : note,
            old?.Created ?? default, old?.Changed ?? default, old?.Used, old?.FormAction, old?.Generated ?? false, old?.Realm));
        return VaultReply.Json(new { ok = true, id = saved, message = old is null ? "Palavra-passe guardada." : "Alterações guardadas." });
    }

    // ---- Importar e exportar ----

    private async Task<VaultReply> ImportAsync(VaultRequest r)
    {
        if (await host.PickImportFileAsync(r.BrowserId).ConfigureAwait(false) is not { } file) return VaultReply.Json(new { ok = false, message = "" });
        byte[] bytes;
        try
        {
            if (new FileInfo(file).Length > PasswordCsv.MaxFileBytes) return FileProblem(CsvFileError.TooLarge);
            bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Invalid("Não foi possível ler o ficheiro.");
        }
        var read = PasswordCsv.Read(bytes);
        Array.Clear(bytes);
        if (read.FileError is { } error) return FileProblem(error);
        var plan = PasswordCsv.Plan(read, vault);
        lock (_lock) _lastImportFile = file;
        if (plan.Conflicts.Count == 0) return Applied(PasswordCsv.Apply(plan, vault, _ => ConflictChoice.KeepCurrent), file);
        lock (_lock) _import = new PendingImport(plan, file);
        return VaultReply.Json(new
        {
            ok = true,
            ask = true,
            conflicts = plan.Conflicts.Count,
            adding = plan.Add.Count,
            message = (plan.Conflicts.Count == 1 ? "1 palavra-passe do ficheiro é diferente da guardada" : $"{plan.Conflicts.Count} palavras-passe do ficheiro são diferentes das guardadas")
                      + " (mesmo site e utilizador). Qual fica?",
        });
    }

    private VaultReply ResolveImport(ConflictChoice choice)
    {
        PendingImport? pending;
        lock (_lock) (pending, _import) = (_import, null);
        return pending is null ? Invalid("Não há nenhuma importação à espera.") : Applied(PasswordCsv.Apply(pending.Plan, vault, _ => choice), pending.File);
    }

    private static VaultReply Applied(ImportSummary summary, string file)
        => VaultReply.Json(new { ok = true, message = summary.Text + ".", file = Path.GetFileName(file), recycle = true });

    private static VaultReply FileProblem(CsvFileError error) => Invalid(error switch
    {
        CsvFileError.TooLarge => "O ficheiro tem mais de 1000 KiB (o limite do Chrome): divida-o ou exporte só uma parte.",
        CsvFileError.TooManyRows => $"O ficheiro tem mais de {PasswordCsv.MaxRows} linhas (o limite do Chrome).",
        _ => "Não é um ficheiro de palavras-passe: faltam as colunas do endereço, do utilizador ou da palavra-passe.",
    });

    private async Task<VaultReply> ExportAsync(VaultRequest r)
    {
        if (!await AskAsync(r, ReauthPurpose.Export).ConfigureAwait(false)) return NotConfirmed();
        if (await host.PickExportFileAsync(r.BrowserId, "Palavras-passe do Éteris.csv").ConfigureAwait(false) is not { } file)
            return VaultReply.Json(new { ok = false, message = "" });
        if (PasswordCsv.CloudFolder(file, Environment.GetEnvironmentVariable) is { } cloud
            && !await host.ConfirmAsync(r.BrowserId, "Pasta na nuvem",
                $"Esta pasta é do {cloud}: o ficheiro, com todas as palavras-passe em claro, vai ser enviado para lá. Gravar na mesma?").ConfigureAwait(false))
            return VaultReply.Json(new { ok = false, message = "Nada foi exportado." });
        var logins = VisibleLogins().Select(l => vault.GetLogin(l.Id)).OfType<LoginItem>().ToList();
        try { await File.WriteAllTextAsync(file, PasswordCsv.Write(logins), new System.Text.UTF8Encoding(false)).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Invalid("Não foi possível gravar o ficheiro.");
        }
        return VaultReply.Json(new
        {
            ok = true,
            message = (logins.Count == 1 ? "1 palavra-passe exportada" : $"{logins.Count} palavras-passe exportadas") + $" para {Path.GetFileName(file)}. "
                      + "O ficheiro tem as palavras-passe em claro: apague-o quando já não precisar.",
        });
    }

    // ---- Comum ----

    private async Task<bool> AskAsync(VaultRequest r, ReauthPurpose purpose)
    {
        var window = await host.WindowAsync(r.BrowserId).ConfigureAwait(false);
        return await gate.RequestAsync(window, purpose, host.WhileInFront(r.BrowserId)).ConfigureAwait(false);
    }

    private string HoldSecretOnScreen()
    {
        var id = VaultIds.New();
        lock (_lock) _onScreen[id] = host.ShowSecret();
        return id;
    }

    // id null: todas (a página saiu).
    private void ReleaseSecretOnScreen(string? id)
    {
        List<IDisposable> release;
        lock (_lock)
        {
            if (id is null)
            {
                release = [.. _onScreen.Values];
                _onScreen.Clear();
            }
            else release = _onScreen.Remove(id, out var one) ? [one] : [];
        }
        foreach (var hold in release) hold.Dispose();
    }

    // Um formulário de edição continua aberto com o segredo: a retenção recomeça (sem largar a anterior antes de a nova
    // estar posta, para as janelas não piscarem para as capturas).
    private bool KeepSecretOnScreen(string id)
    {
        IDisposable old;
        lock (_lock)
        {
            if (!_onScreen.TryGetValue(id, out old!)) return false;
            _onScreen[id] = host.ShowSecret();
        }
        old.Dispose();
        return true;
    }

    // Apagar: some já das listas e só se apaga no cofre ao fim de 10 s (ou ao fechar o Éteris), para «Anular» não
    // precisar de guardar nenhum segredo decifrado em memória.
    private VaultReply Delete(VaultCollection collection, string id)
    {
        if (vault.List(collection).All(e => e.Id != id)) return Gone();
        var token = VaultIds.New();
        lock (_lock)
        {
            if (_pending.Values.Any(p => p.Collection == collection && p.Id == id)) return VaultReply.Json(new { ok = true });
            var timer = host.Schedule(UndoWindow, () => Commit(token));
            _pending[token] = new PendingDelete(collection, id, timer);
        }
        return VaultReply.Json(new { ok = true, undo = token });
    }

    private bool Undo(string? token)
    {
        if (token is null) return false;
        lock (_lock)
        {
            if (!_pending.Remove(token, out var pending)) return false;
            pending.Timer.Dispose();
            return true;
        }
    }

    private void Commit(string token)
    {
        PendingDelete? pending;
        lock (_lock)
        {
            if (!_pending.Remove(token, out pending)) return;
        }
        try { vault.Delete(pending.Collection, pending.Id); }
        catch (VaultUnavailableException) { }
    }

    // «Apagar dados de navegação» › «Palavras-passe e preenchimento automático»: o cofre inteiro (as cinco áreas), depois
    // da reautenticação. Os apagamentos à espera de «Anular» deixam de ter o que anular.
    // A confirmação do Windows pedida por outra página interna (Definições › Sincronização: ligar Pagamentos ou
    // Documentos, aprovar um aparelho novo, exportar o perfil com segredos), com a janela dessa aba como dona.
    public async Task<bool> ConfirmAsync(int browserId, ReauthPurpose purpose)
    {
        var window = await host.WindowAsync(browserId).ConfigureAwait(false);
        return await gate.RequestAsync(window, purpose, host.WhileInFront(browserId)).ConfigureAwait(false);
    }

    public async Task<bool> EraseVaultAsync(int browserId)
    {
        var window = await host.WindowAsync(browserId).ConfigureAwait(false);
        if (!await gate.RequestAsync(window, ReauthPurpose.EraseVault, host.WhileInFront(browserId)).ConfigureAwait(false)) return false;
        lock (_lock)
        {
            foreach (var pending in _pending.Values) pending.Timer.Dispose();
            _pending.Clear();
            _import = null;
        }
        vault.EraseAll();
        return true;
    }

    public void CommitPending()
    {
        List<string> tokens;
        lock (_lock) tokens = _pending.Keys.ToList();
        foreach (var token in tokens) Commit(token);
    }

    private HashSet<string> Hidden(VaultCollection collection)
    {
        lock (_lock) return _pending.Values.Where(p => p.Collection == collection).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
    }

    private static VaultReply NotConfirmed() => VaultReply.Json(new { ok = false, message = "Não foi confirmado com o Windows." });

    private static VaultReply Gone() => VaultReply.Json(new { ok = false, message = "Este item já não existe." });

    private static VaultReply Invalid(string message) => VaultReply.Json(new { ok = false, message });
}
