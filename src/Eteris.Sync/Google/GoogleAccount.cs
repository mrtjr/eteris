using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eteris.Sync.Google;

// Com que conta se entrou: nome e e-mail, para o menu e as Definições mostrarem a conta como o Chrome, o Firefox e o
// Brave. Vem do Drive (about.get com fields=user(displayName,emailAddress)), que aceita o âmbito drive.appdata já
// concedido: nenhum âmbito novo, nenhum servidor novo (www.googleapis.com já é o do Drive), nenhuma foto.
public sealed record GoogleAccount(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("email")] string? Email)
{
    // A letra do avatar: a primeira letra ou algarismo do nome (ou do e-mail), em maiúscula — um nome como «- Ana»
    // dá «A», não «-»; «?» quando não há nenhuma.
    public string Initial
    {
        get
        {
            foreach (var source in new[] { Name, Email })
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                foreach (var rune in source.EnumerateRunes())
                    if (System.Text.Rune.IsLetterOrDigit(rune)) return System.Text.Rune.ToUpperInvariant(rune).ToString();
            }
            return "?";
        }
    }

    // «Nome (e-mail)», só o que houver.
    public string Label => (Name?.Trim(), Email?.Trim()) switch
    {
        ({ Length: > 0 } n, { Length: > 0 } e) => $"{n} ({e})",
        ({ Length: > 0 } n, _) => n,
        (_, { Length: > 0 } e) => e,
        _ => string.Empty,
    };

    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Email);
}

// Guardada no perfil, cifrada como os tokens (no Windows: DPAPI do utilizador), e apagada ao «Sair».
public sealed class GoogleAccountStore(string path, ISecretProtector protector)
{
    public string Path => path;

    public GoogleAccount? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var account = JsonSerializer.Deserialize<GoogleAccount>(protector.Unprotect(File.ReadAllBytes(path)));
            return account is null || account.IsEmpty ? null : account;
        }
        catch (Exception e) when (e is IOException or JsonException or CryptographicException or UnauthorizedAccessException) { return null; }
    }

    public void Save(GoogleAccount account)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(account)));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

// Quem pede a conta e quando. Regras (docs/sincronizacao.md, «A conta no menu»):
//  - nunca sozinho: só RequestIfMissing (chamado por uma ação do utilizador: abrir o menu, abrir a secção
//    Sincronização) ou AfterSignIn (logo a seguir a um início de sessão bem-sucedido);
//  - só com sessão e sem conta guardada; um pedido de cada vez;
//  - no máximo uma tentativa falhada por sessão com a Google: depois de falhar, só volta a tentar depois de sair e
//    entrar outra vez (ou de reabrir o Éteris);
//  - «Sair» (Forget) cancela o pedido em curso e muda a geração: uma resposta que chegue depois já não grava a conta
//    nem conta como tentativa falhada da sessão seguinte.
// Construir isto e ler a conta guardada não faz pedido nenhum (o arranque não fala com a Google). O pedido corre no
// conjunto de fios (Task.Run): quem o lança (o menu, no fio da interface) não espera pela rede nem pelo proxy.
public sealed class GoogleAccountService(HttpClient http, Func<bool, CancellationToken, Task<string>> accessToken, GoogleAccountStore store)
{
    private readonly Lock _gate = new();
    private GoogleAccount? _account;
    private bool _loaded;
    private bool _failed;
    private Task? _pending;
    private long _generation;
    private CancellationTokenSource _cancel = new();

    // Chamado (de qualquer thread) quando a conta muda: obtida, ou apagada ao sair.
    public event Action<GoogleAccount?>? Changed;

    public GoogleAccount? Current
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    _account = store.Load();
                    _loaded = true;
                }
                return _account;
            }
        }
    }

    public bool Pending
    {
        get { lock (_gate) return _pending is { IsCompleted: false }; }
    }

    public bool Failed
    {
        get { lock (_gate) return _failed; }
    }

    // Uma ação do utilizador pediu para ver a conta. Devolve a tarefa do pedido, ou null quando não pede nada (sem
    // sessão, conta já guardada, pedido em curso ou uma tentativa já falhada nesta sessão).
    public Task? RequestIfMissing(bool signedIn)
    {
        if (!signedIn || Current is not null) return null;
        lock (_gate)
        {
            if (_failed || _pending is { IsCompleted: false }) return null;
            return _pending = Start();
        }
    }

    // Logo a seguir a entrar: a conta pode ser outra, por isso esquece a antiga e pede sempre.
    public Task AfterSignIn()
    {
        Forget(notify: false);
        lock (_gate) return _pending = Start();
    }

    // «Sair»: apaga a conta deste aparelho, cancela o pedido em curso e volta a permitir uma tentativa na próxima sessão.
    public void Forget() => Forget(notify: true);

    private void Forget(bool notify)
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            _generation++;
            old = _cancel;
            _cancel = new CancellationTokenSource();
            _pending = null;
            _account = null;
            _loaded = true;
            _failed = false;
            try { store.Delete(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        old.Cancel();
        if (notify) Changed?.Invoke(null);
    }

    // Chamado dentro do _gate: o pedido leva a geração e o cancelamento desta sessão.
    private Task Start()
    {
        var generation = _generation;
        var cancellation = _cancel.Token;
        return Task.Run(() => FetchAndStoreAsync(generation, cancellation));
    }

    private async Task FetchAndStoreAsync(long generation, CancellationToken cancellationToken)
    {
        GoogleAccount? account = null;
        // Qualquer falha (rede, Google, renovação do token que não se consegue gravar — DPAPI ou ficheiro bloqueado,
        // cancelamento ao sair) conta como a tentativa falhada desta sessão.
        try { account = await FetchAsync(http, accessToken, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or IOException or OAuthException or JsonException
                                      or OperationCanceledException or InvalidOperationException
                                      or CryptographicException or UnauthorizedAccessException) { }
        lock (_gate)
        {
            // Saiu-se (ou entrou-se outra vez) enquanto o pedido estava na rede: a resposta já não é desta sessão.
            if (generation != _generation) return;
            if (account is null || account.IsEmpty)
            {
                _failed = true;
                return;
            }
            try { store.Save(account); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { }
            _account = account;
            _loaded = true;
        }
        Changed?.Invoke(account);
    }

    // GET https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress) — só estes dois campos
    // (o «fields» é obrigatório nesta chamada). 401 → renova o token uma vez e repete. Devolve null se a Google
    // responder outra coisa.
    public static async Task<GoogleAccount?> FetchAsync(HttpClient http, Func<bool, CancellationToken, Task<string>> accessToken, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await accessToken(attempt > 0, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, GoogleEndpoints.AboutUser);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var about = JsonSerializer.Deserialize<AboutResponse>(body);
            return about?.User is { } user ? new GoogleAccount(Clean(user.DisplayName), Clean(user.EmailAddress)) : null;
        }
        return null;
    }

    // Texto vindo da rede que vai para a interface: sem caracteres de controlo, sem espaços a mais, com limite.
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var chars = text.Where(c => !char.IsControl(c)).ToArray();
        var clean = new string(chars).Trim();
        return clean.Length == 0 ? null : clean.Length > 200 ? clean[..200] : clean;
    }

    private sealed record AboutResponse([property: JsonPropertyName("user")] AboutUser? User);

    private sealed record AboutUser(
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("emailAddress")] string? EmailAddress);
}
