using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eteris.Browser.Tests.Sync;

// Google falsa em memória: OAuth (token com PKCE, renovação, revogação) e a API do Drive v3 na pasta appDataFolder
// (list com páginas, get alt=media, delete, envio multipart/related lido dos bytes, resumable, PATCH media).
// Responde como a Google responde (JSON, códigos), valida o Bearer e deixa injetar falhas (401, 429, 503).
internal sealed class FakeGoogle : HttpMessageHandler
{
    private readonly Dictionary<string, (string Name, byte[] Data, long Modified)> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uploads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Challenge, string Redirect)> _codes = new(StringComparer.Ordinal);
    private readonly Queue<HttpStatusCode> _failures = new();
    private long _clock;
    private int _ids;

    public string ClientId { get; init; } = "123-test.apps.googleusercontent.com";
    public string ValidAccessToken { get; private set; } = "acesso-1";
    public string RefreshToken { get; } = "renovacao-1";
    public int PageSize { get; init; } = 3;
    public bool GrantScope { get; init; } = true;
    public List<string> Requests { get; } = [];
    public List<string> Revoked { get; } = [];
    public int Refreshes { get; private set; }
    // A conta devolvida pelo about.get (fictícia).
    public string? AccountName { get; set; } = "Ana Teste";
    public string? AccountEmail { get; set; } = "ana.teste@example.com";

    public IReadOnlyCollection<(string Name, byte[] Data)> Files
    {
        get { lock (_files) return _files.Values.Select(f => (f.Name, f.Data)).ToList(); }
    }

    public void ExpireAccessToken() => ValidAccessToken = "acesso-" + Guid.NewGuid().ToString("N")[..6];

    public void FailNext(HttpStatusCode status, int times = 1)
    {
        for (var i = 0; i < times; i++) _failures.Enqueue(status);
    }

    // O que o ecrã de consentimento faria: regista o código para o desafio PKCE e devolve-o.
    public string Authorize(string challenge, string redirect)
    {
        var code = "codigo-" + Guid.NewGuid().ToString("N")[..8];
        _codes[code] = (challenge, redirect);
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add($"{request.Method} {uri.GetLeftPart(UriPartial.Path)}");
        if (_failures.Count > 0 && uri.Host == "www.googleapis.com") return new HttpResponseMessage(_failures.Dequeue());

        if (uri.Host == "oauth2.googleapis.com") return await OAuthAsync(request, uri, cancellationToken);
        if (request.Headers.Authorization?.Parameter != ValidAccessToken)
            return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = new JsonObject { ["code"] = 401, ["message"] = "Invalid Credentials" } });

        var path = uri.AbsolutePath;
        var query = Query(uri);
        if (request.Method == HttpMethod.Get && path == "/drive/v3/about")
        {
            // Como a Google: «fields» é obrigatório e a resposta só leva o que se pediu.
            if (query.GetValueOrDefault("fields") != "user(displayName,emailAddress)")
                return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "fields obrigatório" });
            var user = new JsonObject();
            if (AccountName is not null) user["displayName"] = AccountName;
            if (AccountEmail is not null) user["emailAddress"] = AccountEmail;
            return Json(HttpStatusCode.OK, new JsonObject { ["user"] = user });
        }
        lock (_files)
        {
            if (request.Method == HttpMethod.Get && path == "/drive/v3/files")
            {
                if (query.GetValueOrDefault("spaces") != "appDataFolder") return Json(HttpStatusCode.Forbidden, new JsonObject { ["error"] = "só appDataFolder" });
                var all = _files.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
                var start = int.TryParse(query.GetValueOrDefault("pageToken"), out var token) ? token : 0;
                var size = Math.Min(PageSize, int.Parse(query.GetValueOrDefault("pageSize") ?? "100"));
                var page = all.Skip(start).Take(size).ToList();
                var body = new JsonObject
                {
                    ["files"] = new JsonArray(page.Select(f => (JsonNode)new JsonObject
                    {
                        ["id"] = f.Key, ["name"] = f.Value.Name, ["modifiedTime"] = $"2026-11-08T00:00:{f.Value.Modified % 60:D2}.{f.Value.Modified:D6}Z",
                    }).ToArray()),
                };
                if (start + size < all.Count) body["nextPageToken"] = (start + size).ToString();
                return Json(HttpStatusCode.OK, body);
            }
            if (path.StartsWith("/drive/v3/files/", StringComparison.Ordinal))
            {
                var id = path["/drive/v3/files/".Length..];
                if (!_files.TryGetValue(id, out var file)) return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "notFound" });
                if (request.Method == HttpMethod.Delete)
                {
                    _files.Remove(id);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (request.Method == HttpMethod.Get && query.GetValueOrDefault("alt") == "media")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Data) };
            }
        }
        if (path == "/upload/drive/v3/files" && request.Method == HttpMethod.Post)
        {
            var type = query.GetValueOrDefault("uploadType");
            if (type == "multipart")
            {
                var (metadata, data) = await ParseMultipartAsync(request.Content!, cancellationToken);
                return Created(metadata, data);
            }
            if (type == "resumable")
            {
                var metadata = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                var uploadId = Guid.NewGuid().ToString("N");
                lock (_files) _uploads[uploadId] = metadata.ToJsonString();
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri($"https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id={uploadId}");
                return response;
            }
        }
        if (path == "/upload/drive/v3/files" && request.Method == HttpMethod.Put && query.GetValueOrDefault("upload_id") is { } upload)
        {
            string? metadata;
            lock (_files) _uploads.Remove(upload, out metadata);
            if (metadata is null) return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "sessão de envio desconhecida" });
            return Created(JsonNode.Parse(metadata)!.AsObject(), await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        }
        if (path.StartsWith("/upload/drive/v3/files/", StringComparison.Ordinal) && request.Method == HttpMethod.Patch)
        {
            var id = path["/upload/drive/v3/files/".Length..];
            var data = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            lock (_files)
            {
                if (!_files.TryGetValue(id, out var file)) return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "notFound" });
                _files[id] = (file.Name, data, ++_clock);
            }
            return Json(HttpStatusCode.OK, new JsonObject { ["id"] = id });
        }
        return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "pedido desconhecido: " + request.Method + " " + uri });
    }

    private HttpResponseMessage Created(JsonObject metadata, byte[] data)
    {
        if (metadata["parents"] is not JsonArray parents || parents.Count != 1 || parents[0]!.GetValue<string>() != "appDataFolder")
            return Json(HttpStatusCode.Forbidden, new JsonObject { ["error"] = "só appDataFolder" });
        lock (_files)
        {
            var id = "f" + (++_ids).ToString("D4");
            _files[id] = (metadata["name"]!.GetValue<string>(), data, ++_clock);
            return Json(HttpStatusCode.OK, new JsonObject { ["id"] = id });
        }
    }

    private async Task<HttpResponseMessage> OAuthAsync(HttpRequestMessage request, Uri uri, CancellationToken cancellationToken)
    {
        var form = Query(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (uri.AbsolutePath == "/revoke")
        {
            Revoked.Add(form["token"]);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        if (form.GetValueOrDefault("client_id") != ClientId) return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = "invalid_client" });
        var scope = GrantScope ? "https://www.googleapis.com/auth/drive.appdata" : "";
        if (form.GetValueOrDefault("grant_type") == "authorization_code")
        {
            if (!_codes.Remove(form["code"], out var issued)) return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant" });
            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (challenge != issued.Challenge || form["redirect_uri"] != issued.Redirect)
                return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant" });
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["access_token"] = ValidAccessToken, ["refresh_token"] = RefreshToken, ["expires_in"] = 3599, ["scope"] = scope, ["token_type"] = "Bearer",
            });
        }
        if (form.GetValueOrDefault("grant_type") == "refresh_token")
        {
            if (form["refresh_token"] != RefreshToken || Revoked.Contains(RefreshToken)) return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant" });
            Refreshes++;
            ExpireAccessToken();
            return Json(HttpStatusCode.OK, new JsonObject { ["access_token"] = ValidAccessToken, ["expires_in"] = 3599, ["scope"] = scope });
        }
        return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "unsupported_grant_type" });
    }

    // multipart/related lido dos bytes, como o servidor da Google faria (não do objeto MultipartContent).
    private static async Task<(JsonObject Metadata, byte[] Data)> ParseMultipartAsync(HttpContent content, CancellationToken cancellationToken)
    {
        Assert.Equal("multipart/related", content.Headers.ContentType!.MediaType);
        var boundary = content.Headers.ContentType.Parameters.First(p => p.Name == "boundary").Value!.Trim('"');
        var raw = await content.ReadAsByteArrayAsync(cancellationToken);
        var text = Encoding.Latin1.GetString(raw);
        var parts = text.Split("--" + boundary).Skip(1).Where(p => !p.StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, parts.Count);
        static (string Headers, string Body) Split(string part)
        {
            var at = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return (part[..at], part[(at + 4)..^2]);
        }
        var (h1, b1) = Split(parts[0]);
        var (h2, b2) = Split(parts[1]);
        Assert.Contains("application/json", h1);
        Assert.Contains("application/octet-stream", h2);
        return (JsonNode.Parse(Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(b1)))!.AsObject(), Encoding.Latin1.GetBytes(b2));
    }

    private static Dictionary<string, string> Query(Uri uri) => Query(uri.Query.TrimStart('?'));

    private static Dictionary<string, string> Query(string query)
        => query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body)
        => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
