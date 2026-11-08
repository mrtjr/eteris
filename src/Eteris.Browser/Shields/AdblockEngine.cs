using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Eteris.Browser.Shields;

// Motor do Brave (adblock-rust) com as listas do uBlock Origin Lite, via eteris_shields.dll.
// O SafeHandle mantém o motor vivo enquanto houver chamadas em curso, mesmo depois de Dispose.
public sealed partial class AdblockEngine : IRequestBlocker, IDisposable
{
    private const string Library = "eteris_shields";
    private readonly ShieldsHandle _handle;

    private AdblockEngine(ShieldsHandle handle) => _handle = handle;

    public static AdblockEngine Create(string rules, string? scriptletResources = null, string trustedRules = "")
    {
        var handle = shields_new(rules, trustedRules, scriptletResources);
        if (handle.IsInvalid) throw new InvalidOperationException("O motor do escudo não conseguiu carregar as listas.");
        return new AdblockEngine(handle);
    }

    // O mesmo motor, com o adblock-rust a ler as listas dos ficheiros (o texto, ~8 M carateres, nunca passa pelo .NET)
    // e a usar o motor já compilado em «cachePath» quando existe e é válido (senão compila e grava-o lá).
    // «fromCache»: se não foi preciso compilar.
    public static AdblockEngine CreateFromFiles(IEnumerable<string> rulePaths, IEnumerable<string> trustedPaths,
        string? resourcesPath, string? cachePath, out bool fromCache)
    {
        var handle = shields_new_from_files(string.Join('\n', rulePaths), string.Join('\n', trustedPaths), resourcesPath, cachePath, out var cached);
        if (handle.IsInvalid) throw new InvalidOperationException("O motor do escudo não conseguiu carregar as listas.");
        fromCache = cached == 1;
        return new AdblockEngine(handle);
    }

    public bool ShouldBlock(string requestUrl, string? pageUrl, string resourceType)
    {
        if (_handle.IsClosed) return false;
        try { return shields_check(_handle, requestUrl, pageUrl ?? requestUrl, resourceType) == 1; }
        catch (ObjectDisposedException) { return false; }
    }

    public string UrlCss(string url)
    {
        if (_handle.IsClosed) return string.Empty;
        try { return TakeString(shields_url_css(_handle, url)); }
        catch (ObjectDisposedException) { return string.Empty; }
    }

    public string GenericCss(string url, IEnumerable<string> classes, IEnumerable<string> ids)
    {
        if (_handle.IsClosed) return string.Empty;
        try { return TakeString(shields_generic_css(_handle, url, string.Join(' ', classes), string.Join(' ', ids))); }
        catch (ObjectDisposedException) { return string.Empty; }
    }

    // JavaScript dos scriptlets do uBlock (##+js(...)) para a página; vazio se não houver.
    public string Scriptlets(string url)
    {
        if (_handle.IsClosed) return string.Empty;
        try { return TakeString(shields_scriptlets(_handle, url)); }
        catch (ObjectDisposedException) { return string.Empty; }
    }

    public void Dispose() => _handle.Dispose();

    private static string TakeString(nint value)
    {
        if (value == 0) return string.Empty;
        try { return Marshal.PtrToStringUTF8(value) ?? string.Empty; }
        finally { shields_string_free(value); }
    }

    private sealed class ShieldsHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle()
        {
            shields_free(handle);
            return true;
        }
    }

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial ShieldsHandle shields_new(string rules, string trustedRules, string? resources);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial ShieldsHandle shields_new_from_files(string rulePaths, string trustedPaths, string? resourcesPath,
        string? cachePath, out int fromCache);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint shields_scriptlets(ShieldsHandle handle, string url);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shields_check(ShieldsHandle handle, string url, string sourceUrl, string resourceType);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint shields_url_css(ShieldsHandle handle, string url);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint shields_generic_css(ShieldsHandle handle, string url, string classes, string ids);

    [LibraryImport(Library)]
    private static partial void shields_string_free(nint value);

    [LibraryImport(Library)]
    private static partial void shields_free(nint handle);
}
