using System.IO;
using CefSharp;

namespace Eteris.Browser.Browser;

// A loja de teste das provas ao vivo da vigia (ETERIS_TEST_SHOP_DIR, Infrastructure/TestSwitches.cs): os ficheiros de uma
// pasta respondem em https://loja-eteris.invalid/, dentro do próprio Éteris, como o auto-teste de privacidade
// (SelfTestSchemeHandlerFactory). Um nome .invalid nunca existe na Internet: nada vai à rede. Sem a variável (e sempre
// na versão instalada e no perfil normal) a fábrica nem se regista.
public sealed class TestShopSchemeHandlerFactory(string folder) : ISchemeHandlerFactory
{
    public const string Host = "loja-eteris.invalid";

    public static void Register(IRequestContext context)
    {
        if (Infrastructure.TestSwitches.ShopDir is not { } folder) return;
        context.RegisterSchemeHandlerFactory("https", Host, new TestShopSchemeHandlerFactory(folder));
    }

    // Uma exceção num tratador do CEF fecha o Éteris inteiro: aqui nunca passa.
    public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
    {
        try
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || !uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
                return NotFound();
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if (relative.Length == 0) relative = "index.html";
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            var file = Path.GetFullPath(Path.Combine(root, relative));
            // Só ficheiros dentro da pasta: «../» não sai dela.
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return NotFound();
            var mime = Cef.GetMimeType(Path.GetExtension(file).TrimStart('.')) is { Length: > 0 } known ? known : "application/octet-stream";
            var handler = new ResourceHandler(mime, new MemoryStream(File.ReadAllBytes(file)), autoDisposeStream: true);
            handler.Headers.Add("Cache-Control", "no-store");
            return handler;
        }
        catch (Exception) { return NotFound(); }
    }

    private static IResourceHandler NotFound() => ResourceHandler.ForErrorMessage("Não encontrado", System.Net.HttpStatusCode.NotFound);
}
