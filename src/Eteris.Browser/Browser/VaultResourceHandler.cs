using System.IO;
using System.Text;
using CefSharp;

namespace Eteris.Browser.Browser;

// Resposta assíncrona das páginas do cofre: a reautenticação com o Windows e os diálogos de ficheiro demoram o que o
// utilizador demorar, por isso a resposta segue quando a rota acaba (fora da thread de E/S do CEF).
internal sealed class VaultResourceHandler(Func<Task<Vault.VaultReply>> work) : ResourceHandler
{
    public override CefReturnValue ProcessRequestAsync(IRequest request, ICallback callback)
    {
        _ = Task.Run(async () =>
        {
            using (callback)
            {
                Vault.VaultReply reply;
                try { reply = await work().ConfigureAwait(false); }
                catch (Exception) { reply = Vault.VaultReply.Json(new { ok = false, message = "Não foi possível fazer o pedido." }); }
                var body = reply.Mime == "text/html" ? InternalPages.Themed(reply.Body, InternalPages.LightTheme) : reply.Body;
                MimeType = reply.Mime;
                Charset = "utf-8";
                Stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
                AutoDisposeStream = true;
                if (!callback.IsDisposed) callback.Continue();
            }
        });
        return CefReturnValue.ContinueAsync;
    }
}
