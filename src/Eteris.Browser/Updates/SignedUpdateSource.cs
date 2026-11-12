using System.IO;
using System.Security.Cryptography;
using System.Text;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Eteris.Browser.Updates;

// Fonte de atualizações que só entrega ao Velopack o que um índice assinado confirma (INF-02, CAD-01; docs/atualizacoes.md).
// Envolve a fonte de sempre (a GithubSource) em vez de a substituir: o Velopack continua a saber de que release vem cada
// pacote, e daqui só passam os ativos dela cujo FileName consta do releases.<canal>.json assinado com o mesmo PackageId,
// Version, Type e SHA256; os outros campos (SHA1, Size, notas) passam a ser os do índice assinado. Depois de cada
// descarregamento, o SHA256 do ficheiro volta a ser conferido contra o índice assinado.
// «fetch» traz um ficheiro da última release publicada pelo nome (o índice e o .sig). Sem nenhuma chave pública nada se
// pede à rede e nenhuma atualização é aceite: é o estado até o dono pôr as chaves dele em update-keys.txt.
public sealed class SignedUpdateSource(IUpdateSource inner, Func<string, CancellationToken, Task<byte[]>> fetch,
    IReadOnlyList<string> publicKeys) : IUpdateSource
{
    // FileName → entrada do último índice assinado verificado; nulo até haver um.
    private volatile IReadOnlyDictionary<string, VelopackAsset>? _signed;

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        if (publicKeys.Count == 0)
            throw new UpdateSignatureException("sem chave pública das atualizações (update-keys.txt): nenhuma atualização é aceite");
        var name = $"releases.{(string.IsNullOrEmpty(channel) ? "win" : channel)}.json";
        var index = await fetch(name, CancellationToken.None).ConfigureAwait(false);
        var signature = Encoding.ASCII.GetString(await fetch(name + UpdateSignature.SignatureSuffix, CancellationToken.None).ConfigureAwait(false)).Trim();
        if (!UpdateSignature.Verify(index, signature, publicKeys))
            throw new UpdateSignatureException($"o {name} da última release não tem uma assinatura válida para as chaves embutidas");
        var signed = SignedEntries(index);
        _signed = signed;

        var feed = await inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
        var accepted = new List<VelopackAsset>();
        foreach (var asset in feed.Assets ?? [])
        {
            if (asset?.FileName is not { } file || !signed.TryGetValue(file, out var entry) || !Matches(asset, entry)) continue;
            asset.SHA1 = entry.SHA1;
            asset.Size = entry.Size;
            asset.NotesMarkdown = entry.NotesMarkdown;
            asset.NotesHTML = entry.NotesHTML;
            accepted.Add(asset);
        }
        // Os mesmos objetos do feed interno: o descarregamento dele sabe de que release vem cada um.
        return new VelopackAssetFeed { Assets = [.. accepted] };
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress,
        CancellationToken cancelToken)
    {
        var signed = _signed ?? throw new UpdateSignatureException("nenhum índice assinado verificado antes do descarregamento");
        if (releaseEntry.FileName is not { } file || !signed.TryGetValue(file, out var entry) || !Matches(releaseEntry, entry))
            throw new UpdateSignatureException($"o pacote {releaseEntry.FileName} não consta do índice assinado");
        await inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken).ConfigureAwait(false);
        string actual;
        await using (var stream = File.OpenRead(localFile))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancelToken).ConfigureAwait(false));
        if (string.Equals(actual, entry.SHA256, StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(localFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        throw new UpdateSignatureException($"o pacote {file} descarregado não tem o SHA256 do índice assinado");
    }

    // As entradas do índice assinado com um SHA256 de 64 carateres hexadecimais (uma só com SHA1 não serve).
    private static Dictionary<string, VelopackAsset> SignedEntries(byte[] index)
    {
        var json = Encoding.UTF8.GetString(index).TrimStart('﻿');
        var entries = new Dictionary<string, VelopackAsset>(StringComparer.Ordinal);
        foreach (var entry in VelopackAssetFeed.FromJson(json).Assets ?? [])
        {
            if (entry?.FileName is not { Length: > 0 } file || !IsSha256(entry.SHA256)) continue;
            entries.TryAdd(file, entry);
        }
        return entries;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool Matches(VelopackAsset asset, VelopackAsset signed)
        => string.Equals(asset.PackageId, signed.PackageId, StringComparison.Ordinal)
           && Equals(asset.Version, signed.Version)
           && asset.Type == signed.Type
           && string.Equals(asset.SHA256, signed.SHA256, StringComparison.OrdinalIgnoreCase);
}
