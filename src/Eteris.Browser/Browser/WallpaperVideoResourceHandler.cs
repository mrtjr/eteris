using System.IO;
using System.Net;
using System.Net.Http.Headers;
using CefSharp;
using CefSharp.Callback;

namespace Eteris.Browser.Browser;

// O leitor pede intervalos do WebM. No CEF, Skip precede GetResponseHeaders.
// A posição é aplicada uma única vez ao gerar os cabeçalhos, e Read fica limitado ao intervalo.
internal sealed class WallpaperVideoResourceHandler : ResourceHandler, IResourceHandler
{
    private readonly Stream _video;
    private readonly long _size;
    private readonly long _first;
    private readonly long _end;
    private readonly HttpStatusCode _status;
    private readonly bool _invalidClosedRange;
    private byte[]? _buffer;

    public WallpaperVideoResourceHandler(Stream video, string? range)
        : base("video/webm", video, autoDisposeStream: true)
    {
        _video = video;
        _size = video.Length;
        _end = _size;
        _status = HttpStatusCode.OK;

        if (!RangeHeaderValue.TryParse(range, out var header)
            || !header.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || header.Ranges.Count != 1)
            return;

        var bytes = header.Ranges.Single();
        var first = bytes.From ?? _size - Math.Min(bytes.To!.Value, _size);
        var last = bytes.From.HasValue ? bytes.To ?? _size - 1 : _size - 1;
        if (first >= _size || last >= _size)
        {
            _end = 0;
            _status = HttpStatusCode.RequestedRangeNotSatisfiable;
            // O CEF fixa previamente o tamanho dos intervalos fechados pedidos. Rejeita
            // limites fora do ficheiro sem enviar um corpo com comprimento incompatível.
            _invalidClosedRange = bytes.From.HasValue && bytes.To.HasValue;
            return;
        }

        _first = first;
        _end = last + 1;
        _status = HttpStatusCode.PartialContent;
    }

    bool IResourceHandler.Skip(long bytesToSkip, out long bytesSkipped, IResourceSkipCallback callback)
    {
        callback.Dispose();
        // A posição do stream será definida em GetResponseHeaders. Não aplicar o salto duas vezes.
        bytesSkipped = bytesToSkip >= 0 ? bytesToSkip : (long)CefErrorCode.RequestRangeNotSatisfiable;
        return bytesSkipped >= 0;
    }

    void IResourceHandler.GetResponseHeaders(IResponse response, out long responseLength, out string redirectUrl)
    {
        redirectUrl = string.Empty;
        responseLength = _end - _first;
        if (_invalidClosedRange)
        {
            response.ErrorCode = CefErrorCode.RequestRangeNotSatisfiable;
            return;
        }

        response.MimeType = MimeType;
        response.StatusCode = (int)_status;
        response.SetHeaderByName("Accept-Ranges", "bytes", overwrite: true);
        if (_status == HttpStatusCode.PartialContent)
            response.SetHeaderByName("Content-Range", FormattableString.Invariant($"bytes {_first}-{_end - 1}/{_size}"), overwrite: true);
        else if (_status == HttpStatusCode.RequestedRangeNotSatisfiable)
            response.SetHeaderByName("Content-Range", FormattableString.Invariant($"bytes */{_size}"), overwrite: true);
        _video.Position = _first;
    }

    bool IResourceHandler.Read(Stream dataOut, out int bytesRead, IResourceReadCallback callback)
    {
        callback.Dispose();
        var remaining = _end - _video.Position;
        bytesRead = 0;
        if (remaining <= 0) return false;

        _buffer ??= new byte[64 * 1024];
        var count = (int)Math.Min(Math.Min(dataOut.Length, _buffer.Length), remaining);
        bytesRead = _video.Read(_buffer, 0, count);
        if (bytesRead == 0) return false;
        dataOut.Write(_buffer, 0, bytesRead);
        return true;
    }
}

