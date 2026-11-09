namespace Eteris.Browser.Vault;

// eteris://autofill/ (B3).
public sealed partial class VaultRoutes
{
    private Task<VaultReply> AutofillAsync(VaultRequest r) => Task.FromResult(VaultReply.NotFound);
}
