namespace Eteris.Browser.Sync;

// O aviso de sessão expirada (0.2.15): a Google pediu para entrar outra vez. «Uma vez por abertura»: uma instância por
// processo (SyncService). Só memória — ao abrir o Éteris começa calado e só pergunta quando uma ronda o descobrir.
// Seguro entre threads: o evento do agente chega de qualquer uma.
internal sealed class SessionExpiredPrompt
{
    private readonly Lock _gate = new();
    private bool _asked;
    private bool _pending;

    // Chamado a cada mudança de estado. Devolve true só na primeira vez que needsSignIn é verdadeiro nesta abertura.
    // Observe(false) nunca muda nada: a barra esconde-se pelo estado real, não por aqui.
    public bool Observe(bool needsSignIn)
    {
        if (!needsSignIn) return false;
        lock (_gate)
        {
            if (_asked) return false;
            _asked = true;
            _pending = true;
            return true;
        }
    }

    // Há um aviso por mostrar ou à vista (ainda não respondido).
    public bool Pending
    {
        get { lock (_gate) return _pending; }
    }

    // «Agora não» ou «Entrar»: não volta nesta abertura. Sem aviso feito não faz nada (pode chamar-se sempre, por
    // exemplo ao sair), para não calar uma expiração que ainda venha a acontecer.
    public void Dismiss()
    {
        lock (_gate) _pending = false;
    }
}
