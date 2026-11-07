using System.Collections.Specialized;
using System.IO;
using System.Net;
using CefSharp;
using CefSharp.Callback;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

// O fundo «Ao vivo» da nova aba é um WebM embutido servido por intervalos (Range). Um erro de uma unidade no
// Content-Range deixa o vídeo preto sem nenhum erro visível: estes testes fixam os cabeçalhos e o corpo de cada caso.
public class WallpaperVideoResourceHandlerTests
{
    private const int Size = 1000;
    private static readonly byte[] Video = Enumerable.Range(0, Size).Select(i => (byte)(i % 251)).ToArray();

    private static (IResourceHandler Handler, FakeResponse Response, long Length) Serve(string? range)
    {
        IResourceHandler handler = new WallpaperVideoResourceHandler(new MemoryStream(Video), range);
        var response = new FakeResponse();
        handler.GetResponseHeaders(response, out var length, out var redirect);
        Assert.Equal(string.Empty, redirect);
        return (handler, response, length);
    }

    // Lê em pedaços pequenos, como o CEF: o buffer de saída tem tamanho fixo e Read é chamado até devolver false.
    private static byte[] ReadAll(IResourceHandler handler, int chunk = 128)
    {
        using var body = new MemoryStream();
        for (var i = 0; i < Size; i++)
        {
            var buffer = new byte[chunk];
            var callback = new FakeReadCallback();
            var more = handler.Read(new MemoryStream(buffer), out var read, callback);
            Assert.True(callback.IsDisposed);
            if (!more)
            {
                Assert.Equal(0, read);
                return body.ToArray();
            }
            Assert.InRange(read, 1, chunk);
            body.Write(buffer, 0, read);
        }
        throw new InvalidOperationException("Read nunca terminou");
    }

    [Fact]
    public void NoRange_ServesTheWholeFileAs200()
    {
        var (handler, response, length) = Serve(null);
        Assert.Equal((int)HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/webm", response.MimeType);
        Assert.Equal("bytes", response.GetHeaderByName("Accept-Ranges"));
        Assert.Empty(response.GetHeaderByName("Content-Range"));
        Assert.Equal(Size, length);
        Assert.Equal(Video, ReadAll(handler));
    }

    [Fact]
    public void ClosedRange_Serves206AndReadStopsAtTheEndOfTheRange()
    {
        var (handler, response, length) = Serve("bytes=0-99");
        Assert.Equal((int)HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 0-99/1000", response.GetHeaderByName("Content-Range"));
        Assert.Equal(100, length);
        Assert.Equal(Video[..100], ReadAll(handler));
    }

    [Fact]
    public void OpenRange_ServesUntilTheEnd()
    {
        var (handler, response, length) = Serve("bytes=100-");
        Assert.Equal((int)HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 100-999/1000", response.GetHeaderByName("Content-Range"));
        Assert.Equal(900, length);
        Assert.Equal(Video[100..], ReadAll(handler));
    }

    [Fact]
    public void SuffixRange_ServesTheLastBytes()
    {
        var (handler, response, length) = Serve("bytes=-50");
        Assert.Equal((int)HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 950-999/1000", response.GetHeaderByName("Content-Range"));
        Assert.Equal(50, length);
        Assert.Equal(Video[950..], ReadAll(handler));
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=1500-")]
    [InlineData("bytes=-0")]
    public void RangeBeyondTheEnd_Is416WithTheFileSizeAndNoBody(string range)
    {
        var (handler, response, length) = Serve(range);
        Assert.Equal((int)HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */1000", response.GetHeaderByName("Content-Range"));
        Assert.Equal(CefErrorCode.None, response.ErrorCode);
        Assert.Equal(0, length);
        Assert.Empty(ReadAll(handler));
    }

    // O CEF fixa previamente o tamanho de um intervalo fechado: um limite fora do ficheiro é recusado com o código de
    // erro, sem cabeçalhos nem corpo (ver o comentário no construtor do handler).
    [Fact]
    public void ClosedRangeBeyondTheEnd_IsRefusedWithTheRangeErrorCode()
    {
        var (handler, response, length) = Serve("bytes=5-999999");
        Assert.Equal(CefErrorCode.RequestRangeNotSatisfiable, response.ErrorCode);
        Assert.Equal(0, response.StatusCode);
        Assert.Empty(response.Headers);
        Assert.Equal(0, length);
        Assert.Empty(ReadAll(handler));
    }

    [Theory]
    [InlineData("bits=0-99")]
    [InlineData("bytes=0-9,20-29")]
    [InlineData("lixo")]
    [InlineData("")]
    public void UnsupportedRange_FallsBackToTheWholeFile(string range)
    {
        var (handler, response, length) = Serve(range);
        Assert.Equal((int)HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.GetHeaderByName("Content-Range"));
        Assert.Equal(Size, length);
        Assert.Equal(Video, ReadAll(handler));
    }

    // No CEF, Skip chega antes de GetResponseHeaders; a posição só é aplicada uma vez, ao gerar os cabeçalhos.
    [Fact]
    public void Skip_AcknowledgesTheCountWithoutMovingTheStreamTwice()
    {
        IResourceHandler handler = new WallpaperVideoResourceHandler(new MemoryStream(Video), "bytes=100-");
        var callback = new FakeSkipCallback();
        Assert.True(handler.Skip(100, out var skipped, callback));
        Assert.Equal(100, skipped);
        Assert.True(callback.IsDisposed);

        handler.GetResponseHeaders(new FakeResponse(), out _, out _);
        Assert.Equal(Video[100..], ReadAll(handler));
    }

    [Fact]
    public void Skip_RefusesANegativeCount()
    {
        IResourceHandler handler = new WallpaperVideoResourceHandler(new MemoryStream(Video), null);
        Assert.False(handler.Skip(-1, out var skipped, new FakeSkipCallback()));
        Assert.True(skipped < 0);
    }

    // Guarda só o que o handler escreve; GetHeaderByName devolve "" quando não há cabeçalho, como no CefSharp.
    private sealed class FakeResponse : IResponse
    {
        public NameValueCollection Headers { get; set; } = [];
        public string Charset { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
        public bool IsReadOnly { get; set; }
        public CefErrorCode ErrorCode { get; set; }
        public int StatusCode { get; set; }
        public string StatusText { get; set; } = string.Empty;

        public string GetHeaderByName(string name) => Headers[name] ?? string.Empty;

        public void SetHeaderByName(string name, string value, bool overwrite)
        {
            if (overwrite) Headers.Set(name, value);
            else Headers.Add(name, value);
        }

        public void Dispose() { }
    }

    private sealed class FakeReadCallback : IResourceReadCallback
    {
        public bool IsDisposed { get; private set; }
        public void Continue(int bytesRead) { }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeSkipCallback : IResourceSkipCallback
    {
        public bool IsDisposed { get; private set; }
        public void Continue(long bytesSkipped) { }
        public void Dispose() => IsDisposed = true;
    }
}
