using Eteris.Sync.Format;
using Eteris.Sync.Transport;

namespace Eteris.Sync.Pairing;

// Emparelhamento pedido pelo aparelho NOVO, como no Chrome, no Firefox e no Brave: o computador que entra mostra um
// código e o pedido aparece numa barra da janela do que já está ligado, onde a pessoa ESCREVE esse código («number
// matching», 0.2.15): o aparelho ligado nunca mostra o código, pede-o.
//
//   0 convidado → transporte  pedido: só a hora (nada do utilizador, nem o nome do aparelho)
//   um aparelho ligado vê o pedido e responde como anfitrião na mesma sessão (PairingHost.AnswerAsync): passo 1
//   1–5 o protocolo de sempre (Pairing.cs): compromisso, nonces, o nome do convidado cifrado, o código de verificação
//       calculado nos dois lados (mostrado só no convidado e escrito no anfitrião), confirmação no anfitrião E no
//       convidado, e só então o segredo, cifrado para o convidado
//
// A sessão é do convidado (80 bits do gerador do sistema). Quem garante as chaves é o código de verificação, como com o
// código curto. Um pedido que ninguém fez (escrito por quem controla o transporte) aparece no aparelho ligado sem nada
// para copiar: o código só existe no ecrã do convidado, e três códigos errados recusam o pedido. A hora do pedido só
// se aceita até PairingProtocol.ClockSkew à frente do relógio de quem responde. Expira em 10 minutos; os ficheiros
// saem no fim.
public static class PairingRequests
{
    // Quantos pedidos um aparelho ligado acompanha ao mesmo tempo (respondidos e ainda por acabar). O código de
    // verificação deriva de valores que estão em claro no transporte, e quem pede só revela o seu nonce depois de ver o
    // do anfitrião: quem escreve no transporte podia abrir N pedidos e deixar seguir só o que desse o código que o
    // computador novo verdadeiro mostra (N em 1 000 000). Com o teto, cada pedido a mais só é respondido quando outro
    // acaba — e um pedido falso só acaba mal, o que conta para os pedidos em série (PairingFatigue). O teto também
    // limita as sondagens de 10 minutos em curso.
    public const int MaxAnswered = 3;

    // Aparelho novo: abre o pedido e devolve a sessão. A escrita repete-se como as dos outros passos
    // (PairingProtocol.WriteAsync).
    public static async Task<string> OpenAsync(ISyncTransport transport, Func<long> now, CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? pollInterval = null)
    {
        var session = PairingProtocol.NewSession();
        await PairingProtocol.WriteAsync(transport, SyncPaths.Pairing(session) + PairingProtocol.Request, Json.Bytes(new PairingMessage { CreatedMs = now() }),
            delay ?? Task.Delay, pollInterval ?? TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        return session;
    }

    // Aparelho ligado: pedidos por responder (sem anfitrião nem recusa) e dentro do prazo. Os expirados há muito saem.
    public static async Task<IReadOnlyList<string>> PendingAsync(ISyncTransport transport, Func<long> now, CancellationToken cancellationToken = default)
    {
        var steps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var path in await transport.ListAsync(SyncPaths.Root + "pair/", cancellationToken).ConfigureAwait(false))
        {
            var parts = path[(SyncPaths.Root.Length + "pair/".Length)..].Split('/');
            if (parts.Length != 2 || !SyncPaths.IsValidSegment(parts[0])) continue;
            if (!steps.TryGetValue(parts[0], out var set)) steps[parts[0]] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(parts[1]);
        }
        var pending = new List<string>();
        var lifetime = (long)PairingProtocol.Lifetime.TotalMilliseconds;
        var skew = (long)PairingProtocol.ClockSkew.TotalMilliseconds;
        foreach (var (session, set) in steps)
        {
            if (!set.Contains(PairingProtocol.Request)) continue;
            long created;
            try
            {
                var bytes = await transport.ReadAsync(SyncPaths.Pairing(session) + PairingProtocol.Request, cancellationToken).ConfigureAwait(false);
                if (bytes is null) continue;
                created = Json.Parse<PairingMessage>(bytes).CreatedMs;
            }
            catch (FormatException) { continue; }
            var age = now() - created;
            if (age > 2 * lifetime)
            {
                // Abandonado (o aparelho novo fechou a meio): limpa.
                await CleanUpAsync(transport, session, cancellationToken).ConfigureAwait(false);
                continue;
            }
            // Do futuro só até ClockSkew: quem escreve o pedido escolhe a hora.
            if (age > lifetime || age < -skew) continue;
            if (set.Contains(PairingProtocol.HostKey) || set.Contains(PairingProtocol.Refused)) continue;
            pending.Add(session);
        }
        return pending;
    }

    // Aparelho novo: espera que um aparelho ligado responda ao pedido e entra no protocolo (passo 2).
    public static async Task<PairingGuest> WaitForHostAsync(ISyncTransport transport, string session, string guestName, Func<long> now,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var wait = delay ?? Task.Delay;
        var deadline = now() + (long)PairingProtocol.Lifetime.TotalMilliseconds;
        var path = SyncPaths.Pairing(session);
        while (true)
        {
            byte[]? answer = null;
            try
            {
                if (await transport.ReadAsync(path + PairingProtocol.Refused, cancellationToken).ConfigureAwait(false) is not null)
                    throw new PairingException("O outro aparelho recusou o pedido.") { Failure = PairingFailure.Refused };
                answer = await transport.ReadAsync(path + PairingProtocol.HostKey, cancellationToken).ConfigureAwait(false);
            }
            // O transporte falhou de passagem: a espera continua (PairingProtocol.IsTransportFailure).
            catch (Exception e) when (PairingProtocol.IsTransportFailure(e, cancellationToken)) { }
            if (answer is not null)
                return await PairingGuest.JoinAsync(transport, session, null, answer, guestName, now, delay, cancellationToken).ConfigureAwait(false);
            if (now() > deadline) throw new PairingException("Nenhum aparelho respondeu em 10 minutos. Comece de novo.") { Failure = PairingFailure.TimedOut };
            await wait(pollInterval ?? TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<bool> IsRefusedAsync(ISyncTransport transport, string session, CancellationToken cancellationToken = default)
        => await transport.ReadAsync(SyncPaths.Pairing(session) + PairingProtocol.Refused, cancellationToken).ConfigureAwait(false) is not null;

    // Recusa um pedido sem responder ao protocolo (o utilizador não reconhece o pedido).
    public static Task RefuseAsync(ISyncTransport transport, string session, Func<long> now, CancellationToken cancellationToken = default)
        => transport.WriteAsync(SyncPaths.Pairing(session) + PairingProtocol.Refused, Json.Bytes(new PairingMessage { CreatedMs = now() }), cancellationToken);

    public static async Task CleanUpAsync(ISyncTransport transport, string session, CancellationToken cancellationToken = default)
    {
        foreach (var file in await transport.ListAsync(SyncPaths.Pairing(session), cancellationToken).ConfigureAwait(false))
            await transport.DeleteAsync(file, cancellationToken).ConfigureAwait(false);
    }
}
