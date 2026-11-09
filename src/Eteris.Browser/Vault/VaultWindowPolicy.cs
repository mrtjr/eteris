namespace Eteris.Browser.Vault;

// O que o cofre faz numa janela (desenho, secção 3.11).
public enum VaultAction
{
    // Preencher quando o utilizador escolhe (lista ou menu de contexto).
    FillOnRequest,
    // Gravar a «última utilização» de um item.
    RecordUse,
    // Guardar candidatos de envio e oferecer guardar (bolha, ícone de chave).
    OfferSave,
    // O gerador de palavras-passe.
    Generate,
    // eteris://passwords e eteris://autofill.
    ManagementPages,
}

// A janela privada (docs/palavras-passe.md, C7): lê o cofre para preencher a pedido, como o Chrome e o Firefox, mas não
// deixa rasto nem guarda nada — sem «última utilização», sem candidatos nem bolha, sem gerador (D-3: uma palavra-passe
// criada numa janela privada guarda-se à mão depois), e as páginas de gestão abrem numa janela normal.
public static class VaultWindowPolicy
{
    public static bool Allows(VaultAction action, bool isPrivate) => !isPrivate || action == VaultAction.FillOnRequest;
}
