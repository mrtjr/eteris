using Eteris.Sync.Secrets;

namespace Eteris.Sync.Engine;

// O perfil local com coleções secretas (o cofre do navegador: Vault/VaultSyncProfile). Collect()/Read() dão, de cada
// item secreto, só a chave LOCAL (opaca, HMAC com uma chave deste computador) e a impressão {"h": HMAC(conteúdo)}, sem
// decifrar nada; o conteúdo só se pede (ReadSecret) quando a impressão mudou — só os itens alterados se decifram.
public interface ISecretSource
{
    // O conteúdo de um item (chave natural e campos com os nomes de Secrets/SecretSchemas), decifrado agora. null = já
    // não existe (ou não se lê).
    SecretItem? ReadSecret(string collection, string localKey);

    // A chave local de um item com esta chave natural: a mesma que Collect devolve para ele.
    string LocalKey(string collection, string naturalKey);

    // A impressão de um conteúdo: a mesma que Collect devolve para um item com este conteúdo (só os campos conhecidos
    // contam, SecretSchemas.Canonical). Iguais = nada a aplicar nem a enviar.
    string Fingerprint(string collection, SecretItem item);
}

// Um perfil que tem avisos para o estado da sincronização (por exemplo, «a palavra-passe de … mudou noutro computador;
// a anterior ficou no histórico»). Lido no fim de cada ronda.
public interface ILocalNotices
{
    string? TakeNotice();
}
