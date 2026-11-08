using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eteris.Sync.Transport;

namespace Eteris.Sync.Google;

// Transporte pela pasta oculta da aplicação no Google Drive do utilizador (appDataFolder, âmbito drive.appdata):
// só o Éteris a vê, o utilizador não a vê na interface do Drive e nenhuma outra aplicação lhe chega. Tudo o que lá
// fica são os ficheiros cifrados do SealedFile com nomes opacos (SyncPaths); a Google vê tamanhos (arredondados a
// 1 KiB) e horas, nunca conteúdo.
//
// API REST v3 com JSON: files.list (spaces=appDataFolder), files.get?alt=media, files.delete; envio multipart até
// 5 MiB e «resumable» acima disso; substituição (estado do aparelho) por PATCH uploadType=media.
// 401 → renova o token uma vez e repete; 429/5xx → espera e repete (até 4 tentativas).
public sealed class GoogleDriveTransport(HttpClient http, Func<bool, CancellationToken, Task<string>> accessToken,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : ISyncTransport
{
    public const int MultipartLimit = 5 * 1024 * 1024;
    private const int MaxAttempts = 4;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Name => "Google Drive (pasta oculta da aplicação)";

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        await RefreshIndexAsync(cancellationToken).ConfigureAwait(false);
        lock (_ids) return _ids.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var id = await IdOfAsync(path, cancellationToken).ConfigureAwait(false);
        if (id is null) return null;
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{GoogleEndpoints.Files}/{Uri.EscapeDataString(id)}?alt=media"),
            cancellationToken, allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            lock (_ids) _ids.Remove(path);
            return null;
        }
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default)
    {
        var existing = await IdOfAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"{GoogleEndpoints.Upload}/{Uri.EscapeDataString(existing)}?uploadType=media")
            {
                Content = Octets(data),
            }, cancellationToken).ConfigureAwait(false);
            return;
        }
        var metadata = JsonSerializer.Serialize(new FileMetadata(path, ["appDataFolder"]));
        string id;
        if (data.Length <= MultipartLimit)
        {
            using var response = await SendAsync(() =>
            {
                var multipart = new MultipartContent("related", "eteris-" + Guid.NewGuid().ToString("N"));
                multipart.Add(new StringContent(metadata, Encoding.UTF8, "application/json"));
                multipart.Add(Octets(data));
                return new HttpRequestMessage(HttpMethod.Post, $"{GoogleEndpoints.Upload}?uploadType=multipart&fields=id") { Content = multipart };
            }, cancellationToken).ConfigureAwait(false);
            id = await ReadIdAsync(response, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Envio «resumable»: primeiro os metadados (a Google devolve o endereço da sessão), depois os bytes.
            Uri session;
            using (var start = await SendAsync(() =>
                   {
                       var request = new HttpRequestMessage(HttpMethod.Post, $"{GoogleEndpoints.Upload}?uploadType=resumable&fields=id")
                       {
                           Content = new StringContent(metadata, Encoding.UTF8, "application/json"),
                       };
                       request.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
                       request.Headers.Add("X-Upload-Content-Length", data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                       return request;
                   }, cancellationToken).ConfigureAwait(false))
            {
                session = start.Headers.Location ?? throw new HttpRequestException("A Google não devolveu a sessão de envio.");
            }
            if (!GoogleEndpoints.IsAllowedRequest(session)) throw new HttpRequestException("Sessão de envio fora da Google: recusada.");
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, session) { Content = Octets(data) }, cancellationToken)
                .ConfigureAwait(false);
            id = await ReadIdAsync(response, cancellationToken).ConfigureAwait(false);
        }
        lock (_ids) _ids[path] = id;
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var id = await IdOfAsync(path, cancellationToken).ConfigureAwait(false);
        if (id is null) return;
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"{GoogleEndpoints.Files}/{Uri.EscapeDataString(id)}"),
            cancellationToken, allowNotFound: true).ConfigureAwait(false);
        lock (_ids) _ids.Remove(path);
    }

    // Um nome que não está no índice pode ter sido criado por outro aparelho desde a última listagem: lista de novo.
    private async Task<string?> IdOfAsync(string path, CancellationToken cancellationToken)
    {
        lock (_ids)
            if (_ids.TryGetValue(path, out var id)) return id;
        await RefreshIndexAsync(cancellationToken).ConfigureAwait(false);
        lock (_ids) return _ids.GetValueOrDefault(path);
    }

    // Lista a pasta inteira (os nomes são opacos e poucos: centenas, não milhares) e guarda nome → ID do Drive.
    private async Task RefreshIndexAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var found = new Dictionary<string, (string Id, string Modified)>(StringComparer.Ordinal);
            string? page = null;
            do
            {
                var url = $"{GoogleEndpoints.Files}?spaces=appDataFolder&pageSize=1000&fields=nextPageToken,files(id,name,modifiedTime)"
                          + (page is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(page));
                using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
                var list = JsonSerializer.Deserialize<FileList>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
                           ?? throw new HttpRequestException("Lista do Drive ilegível.");
                foreach (var file in list.Files ?? [])
                {
                    if (file.Name is null || file.Id is null) continue;
                    // O Drive aceita dois ficheiros com o mesmo nome: fica o mais recente.
                    if (!found.TryGetValue(file.Name, out var other) || string.CompareOrdinal(file.ModifiedTime, other.Modified) > 0)
                        found[file.Name] = (file.Id, file.ModifiedTime ?? string.Empty);
                }
                page = list.NextPageToken;
            } while (page is not null);
            lock (_ids)
            {
                _ids.Clear();
                foreach (var (name, (id, _)) in found) _ids[name] = id;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        var refreshed = false;
        for (var attempt = 1; ; attempt++)
        {
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(false, cancellationToken).ConfigureAwait(false));
            var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)) return response;
            var status = (int)response.StatusCode;
            response.Dispose();
            if (status == 401 && !refreshed)
            {
                refreshed = true;
                await accessToken(true, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if ((status == 429 || status >= 500) && attempt < MaxAttempts)
            {
                await _delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
                continue;
            }
            throw new HttpRequestException($"Google Drive respondeu {status}.", null, (HttpStatusCode)status);
        }
    }

    private static ByteArrayContent Octets(byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private static async Task<string> ReadIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var file = JsonSerializer.Deserialize<DriveFile>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return file?.Id ?? throw new HttpRequestException("O Drive não devolveu o ID do ficheiro.");
    }

    private sealed record FileMetadata([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("parents")] string[] Parents);

    private sealed record DriveFile(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("modifiedTime")] string? ModifiedTime);

    private sealed record FileList(
        [property: JsonPropertyName("files")] List<DriveFile>? Files,
        [property: JsonPropertyName("nextPageToken")] string? NextPageToken);
}
