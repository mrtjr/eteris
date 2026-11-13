namespace Eteris.Browser.Shields;

// MOT-01 (revisão final): os pedidos de certificado de cliente de uma janela. O Chromium só guarda a resposta por
// anfitrião:porta, por isso uma página que pede a N subdomínios trazia N diálogos modais em cadeia e a janela ficava
// inutilizável. Aqui: um diálogo de cada vez (os outros em fila); o pedido de uma página fora da vista espera na fila
// até ela aparecer (recusá-lo partia o site até fechar o Éteris: o motor guarda o «sem certificado» por servidor:porta);
// depois de RefusalLimit recusas seguidas na mesma página, os pedidos dela são recusados sem perguntar até o
// utilizador navegar nessa aba (ResetsRefusals, Navigated). Cada pedido acaba SEMPRE com uma resposta
// (null = não enviar): o callback do CEF nunca fica pendurado. Corre só na thread da janela.
public sealed class ClientCertificatePrompts
{
    public const int RefusalLimit = 3;

    // A navegação que repõe as recusas: a do utilizador (um gesto, a barra, um favorito; o recarregar da barra chama
    // Navigated à parte). Uma navegação da própria página (location.href, um 204, location.reload(), history.back())
    // não: senão uma página recusada três vezes voltava a abrir três diálogos a cada navegação, sem fim.
    public static bool ResetsRefusals(bool userGesture, bool isRedirect, CefSharp.TransitionType transition)
        => !isRedirect && (userGesture || RedirectChain.IsTyped(transition));

    public sealed class Request
    {
        internal Request(object page, string host, int port, IReadOnlyList<ClientCertificateRow> rows, string? from, Func<bool> isOnScreen)
        {
            Page = page;
            Host = host;
            Port = port;
            Rows = rows;
            From = from;
            IsOnScreen = isOnScreen;
        }

        public object Page { get; }
        public string Host { get; }
        public int Port { get; }
        public IReadOnlyList<ClientCertificateRow> Rows { get; }
        // De onde vem o pedido, para o diálogo (a aba, o site fixado).
        public string? From { get; }
        internal Func<bool> IsOnScreen { get; }
        internal TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly Task<string?> Refused = Task.FromResult<string?>(null);

    private readonly Queue<Request> _waiting = new();
    private readonly Dictionary<object, int> _refusals = new(ReferenceEqualityComparer.Instance);
    private bool _closed;

    // O pedido com o diálogo aberto (null: nenhum).
    public Request? Showing { get; private set; }

    public Task<string?> Ask(object page, string host, int port, IReadOnlyList<ClientCertificateRow> rows, string? from, Func<bool> isOnScreen)
    {
        if (_closed || OverLimit(page)) return Refused;
        var request = new Request(page, host, port, rows, from, isOnScreen);
        _waiting.Enqueue(request);
        return request.Answer.Task;
    }

    // O próximo pedido a mostrar: o primeiro, por ordem de chegada, com a página à vista (null: já há um diálogo aberto,
    // ou nenhum à vista). Os de páginas fora da vista ficam à espera; os de uma página no limite saem recusados.
    public Request? Next()
    {
        if (_closed || Showing is not null) return null;
        RefuseWaiting(r => OverLimit(r.Page));
        Request? next = null;
        var keep = new Queue<Request>();
        while (_waiting.TryDequeue(out var request))
        {
            if (next is null && SafeOnScreen(request.IsOnScreen)) next = request;
            else keep.Enqueue(request);
        }
        foreach (var request in keep) _waiting.Enqueue(request);
        Showing = next;
        return next;
    }

    // A resposta do diálogo (null: «Não enviar», Esc, fechar). Só uma recusa do utilizador conta para o limite.
    public void Answered(Request request, string? thumbprint)
    {
        if (!ReferenceEquals(request, Showing))
        {
            // Adiado (Postpone): continua na fila, à espera de voltar a aparecer.
            if (!_waiting.Contains(request)) request.Answer.TrySetResult(null);
            return;
        }
        Showing = null;
        if (thumbprint is null) _refusals[request.Page] = _refusals.GetValueOrDefault(request.Page) + 1;
        else _refusals.Remove(request.Page);
        request.Answer.TrySetResult(thumbprint);
        if (OverLimit(request.Page)) RefuseWaiting(r => ReferenceEquals(r.Page, request.Page));
    }

    // A janela privada trancou com o diálogo aberto (MainWindow.PrivateLock.cs): o pedido volta para a frente da fila, sem
    // resposta e sem contar como recusa (o motor guardava o «sem certificado» até reiniciar), e a resposta que o diálogo
    // fechado ainda dá não conta (Answered). Volta a aparecer pelo Next com a página outra vez à vista. False: nenhum aberto.
    public bool Postpone()
    {
        if (Showing is not { } request) return false;
        Showing = null;
        var rest = _waiting.ToList();
        _waiting.Clear();
        _waiting.Enqueue(request);
        foreach (var other in rest) _waiting.Enqueue(other);
        return true;
    }

    // O utilizador navegou nesta aba: volta a poder perguntar, e o que o documento anterior deixou à espera é recusado.
    public void Navigated(object page)
    {
        _refusals.Remove(page);
        RefuseWaiting(r => ReferenceEquals(r.Page, page));
    }

    // A aba fechou: o que deixou na fila é recusado e a contagem dela sai (nada fica preso à aba).
    public void Forget(object page)
    {
        _refusals.Remove(page);
        RefuseWaiting(r => ReferenceEquals(r.Page, page));
    }

    // A janela fechou: tudo o que esperava, o diálogo aberto e o que chegar depois respondem «não enviar».
    public void Close()
    {
        _closed = true;
        RefuseWaiting(_ => true);
        Showing?.Answer.TrySetResult(null);
        Showing = null;
        _refusals.Clear();
    }

    private bool OverLimit(object page) => _refusals.GetValueOrDefault(page) >= RefusalLimit;

    private static bool SafeOnScreen(Func<bool> isOnScreen)
    {
        try { return isOnScreen(); }
        catch (Exception) { return false; }
    }

    private void RefuseWaiting(Func<Request, bool> which)
    {
        var keep = new Queue<Request>();
        while (_waiting.TryDequeue(out var request))
        {
            if (which(request)) request.Answer.TrySetResult(null);
            else keep.Enqueue(request);
        }
        foreach (var request in keep) _waiting.Enqueue(request);
    }
}
