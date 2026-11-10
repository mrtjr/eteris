using System.IO;
using System.Net.Http;
using Eteris.Sync.Google;

namespace Eteris.Browser.Sync;

// Onde está o início de sessão na Google (0.2.15). Idle = nada a mostrar; Waiting = à espera do navegador do sistema;
// TimedOut, Refused, DriveDenied, NoNetwork = acabou mal (com «Tentar outra vez»); Done = há tokens e conta.
internal enum SignInStage { Idle, Waiting, TimedOut, Refused, DriveDenied, NoNetwork, Done }

// AtMs: quando entrou neste estado (é por ele que o «some sozinha» limpa só o estado a que pertence). Outcome: o que a
// página de fim mostrou (T2), quando já se sabe.
internal sealed record SignInProgress(SignInStage Stage, string? ManualUrl = null, SignInPageKind? Outcome = null, long AtMs = 0)
{
    public static readonly SignInProgress None = new(SignInStage.Idle);

    public bool Ended => Stage is not (SignInStage.Idle or SignInStage.Waiting);
}

// A máquina de estados do início de sessão, separada do GoogleSync (estático, com o disco e o navegador do sistema)
// para se poder testar. Só memória: nada daqui vai para o disco nem para registos — em especial o e-mail da reentrada.
internal sealed class SignInTracker(Func<long> now)
{
    private readonly Lock _gate = new();
    private SignInProgress _current = SignInProgress.None;
    private string? _lastHint;

    // Fora do cadeado; de qualquer thread.
    public event Action? Changed;

    public SignInProgress Current
    {
        get { lock (_gate) return _current; }
    }

    // O e-mail da reentrada, para «Tentar outra vez». Só existe em Waiting e nos estados de erro: quem já entrou, quem
    // cancelou ou quem deixou o erro desaparecer não precisa dele, e não fica em memória.
    public string? LastHint
    {
        get { lock (_gate) return _lastHint; }
    }

    public void Begin(string? loginHint) => Set(() =>
    {
        _current = new SignInProgress(SignInStage.Waiting, AtMs: now());
        _lastHint = loginHint;
        return true;
    });

    // Não havia outro navegador: o endereço tem de ser copiado à mão. Só faz sentido enquanto se espera.
    public void ShowManualUrl(string url) => Set(() =>
    {
        if (_current.Stage != SignInStage.Waiting) return false;
        _current = _current with { ManualUrl = url };
        return true;
    });

    public void SignedIn() => Set(() =>
    {
        _current = new SignInProgress(SignInStage.Done, AtMs: now());
        _lastHint = null;
        return true;
    });

    // A página de fim já foi escolhida. Não muda o AtMs: a barra conta o «Sincronizado.» a partir da entrada.
    public void Finished(SignInPageKind outcome) => Set(() =>
    {
        if (_current.Stage != SignInStage.Done) return false;
        _current = _current with { Outcome = outcome };
        return true;
    });

    // Um cancelamento leva a Idle a partir de qualquer estado (também de Done: a barra nunca diz «Sessão iniciada»
    // depois de um «Cancelar», que desfaz a sessão).
    public void Failed(Exception e) => Set(() =>
    {
        var stage = StageFor(e);
        if (stage == SignInStage.Idle) return GoIdle();
        _current = new SignInProgress(stage, AtMs: now());
        return true;
    });

    // O «some sozinha» (ou «Fechar») de um estado terminal: só limpa se o estado ainda for o mesmo (mesmo AtMs).
    public void Clear(long atMs) => Set(() => _current.Ended && _current.AtMs == atMs && GoIdle());

    // O fim da tarefa da entrada: se ainda estiver à espera (a entrada acabou sem dizer como — por exemplo um «Cancelar»
    // que chegou entre a troca do código e o trabalho depois dos tokens), não fica presa em «À espera…» sem entrada
    // nenhuma a correr. Os outros estados ficam como estão.
    public void Settle() => Set(() => _current.Stage == SignInStage.Waiting && GoIdle());

    // Sair da conta (ou retirar o acesso): esquece tudo, de qualquer estado.
    public void Reset() => Set(GoIdle);

    public static SignInStage StageFor(Exception e) => e switch
    {
        OAuthException { Failure: OAuthFailure.TimedOut } => SignInStage.TimedOut,
        OAuthException { Failure: OAuthFailure.MissingScope } => SignInStage.DriveDenied,
        OAuthException => SignInStage.Refused,
        OperationCanceledException => SignInStage.Idle,
        HttpRequestException or IOException or System.Net.Sockets.SocketException => SignInStage.NoNetwork,
        _ => SignInStage.Refused,
    };

    // Dentro do cadeado.
    private bool GoIdle()
    {
        var changed = _current != SignInProgress.None || _lastHint is not null;
        _current = SignInProgress.None;
        _lastHint = null;
        return changed;
    }

    private void Set(Func<bool> change)
    {
        bool changed;
        lock (_gate) changed = change();
        if (changed) Changed?.Invoke();
    }
}
