using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Eteris.Sync.Crypto;
using Eteris.Sync.Format;
using Eteris.Sync.Pairing;
using Eteris.Sync.Transport;

namespace Eteris.Sync.Engine;

// O que um aparelho guarda da sincronização (o anfitrião protege-o: DPAPI no Windows). Nunca vai para o transporte.
public sealed class SavedSync
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    // Segredo da conta (base64url). null = sem conta neste aparelho (nunca entrou, ou «Sair» com «apagar a chave»).
    [JsonPropertyName("secret")] public string? Secret { get; set; }
    [JsonPropertyName("state")] public SyncState? Engine { get; set; }
    // Como o perfil local estava no fim da última sincronização (ProfileSync). null = ainda não juntou.
    [JsonPropertyName("base")] public Dictionary<string, string>? Baseline { get; set; }
    // A sincronização está ligada neste aparelho (desliga-se com «Sair», ao apagar os dados ou se a conta desaparecer).
    [JsonPropertyName("on")] public bool Enabled { get; set; }
    // Estado de antes da 0.2.15 («código por guardar»): só se lê, para migrar para Kit (nunca mais se escreve true; um
    // Éteris antigo que abra o mesmo perfil não volta a mostrar o código sozinho).
    [JsonPropertyName("codePending")] public bool CodePending { get; set; }
    // O kit de recuperação deste segredo neste computador (guardado, adiado, recusado). Local: nunca vai para o
    // transporte nem passa para os outros computadores.
    [JsonPropertyName("kit")] public KitRecord? Kit { get; set; }
    [JsonPropertyName("last")] public long LastSuccessMs { get; set; }
    [JsonPropertyName("compactMs")] public long LastCompactMs { get; set; }
    [JsonPropertyName("compactSeq")] public long LastCompactSeq { get; set; }
    // Porque parou (conta apagada, aparelho removido): mostrado até se ligar outra vez.
    [JsonPropertyName("stopped")] public string? Stopped { get; set; }
    // Troca de chave a meio (a conta nova já está aqui; falta entregá-la aos outros e apagar a antiga).
    [JsonPropertyName("rotation")] public RotationPending? Rotation { get; set; }
    // Com que conta do transporte (a conta Google, como impressão digital) este aparelho sincroniza: com outra sessão,
    // a sincronização fica em pausa sem apagar nada (SyncAgent.AccountKey).
    [JsonPropertyName("account")] public string? Account { get; set; }
    // Quando este aparelho criou a conta (0 = não a criou, ou já passou o tempo de ver se outro a criou ao mesmo tempo).
    [JsonPropertyName("created")] public long CreatedMs { get; set; }
    // As coleções ligadas NESTE aparelho (tópico «O que passa entre PCs»; null = as de omissão, SyncGroups). Desligar uma
    // linha para de enviar e de aplicar essas coleções e não apaga nada, nem aqui nem na conta.
    [JsonPropertyName("collections")] public List<string>? Collections { get; set; }
    // As coleções que já fizeram a «primeira junção» com a conta (null = estado de antes destas linhas: com base, as
    // coleções que já existiam). Uma coleção ligada outra vez sai daqui e junta-se de novo sem perder nada.
    [JsonPropertyName("joined")] public List<string>? Joined { get; set; }
    // «Apagar os meus dados do Éteris» por acabar: a sincronização já está desligada aqui (gravado antes do primeiro
    // pedido) e falta apagar no transporte o que ainda lá está. Ao abrir, o anfitrião acaba; «Ligar» acaba primeiro.
    [JsonPropertyName("deleting")] public bool DeletePending { get; set; }
    // O nome deste computador escolhido no Éteris (local, protegido; nunca vai em claro para o transporte).
    [JsonPropertyName("name")] public string? DeviceName { get; set; }
    // Quando acabaram mal os últimos pedidos de aparelhos novos a que este aparelho respondeu (PairingFatigue: com
    // muitos seguidos, a barra de aprovação deixa de abrir sozinha). Só horas: sem sessão, nome nem código.
    [JsonPropertyName("badPairs")] public long[]? BadPairings { get; set; }
    // Este aparelho já teve a chave de uma conta (criou-a, entrou, perdeu a criação ao mesmo tempo, apagou os dados).
    // Marca local: nunca sai deste computador. Um aparelho com ela nunca pede a aprovação sozinho (SyncAgent.AutoPair).
    [JsonPropertyName("had")] public bool HadAccount { get; set; }
    // Entrou agora na conta (aprovação ou kit) sem nenhum estado de sincronização anterior: na primeira ronda completa
    // herda a escolha da conta (pagamentos e documentos), se houver.
    [JsonPropertyName("inherit")] public bool InheritPending { get; set; }
    // A oferta «receber também neste?» já foi respondida (ou este aparelho fez a escolha / herdou-a).
    [JsonPropertyName("offerDone")] public bool OfferAnswered { get; set; }
    // Última leitura do registo da conta (fim da última ronda completa), ou a escolha feita aqui e ainda por escrever.
    [JsonPropertyName("acctSecrets")] public bool AccountSecrets { get; set; }
    // Há segredos em jogo na conta (registos secretos vivos, ou a escolha ligada), visto no fim da última ronda
    // completa. null = este aparelho ainda não acabou uma ronda nesta conta: conta como «há».
    [JsonPropertyName("secretsSeen")] public bool? SecretsInAccount { get; set; }
    // A escolha da conta feita aqui e ainda por escrever no motor: "on", "off" ou null. A ronda seguinte escreve-a.
    [JsonPropertyName("choiceOut")] public string? AccountChoiceOut { get; set; }
}

// Como este aparelho entrou na conta NESTA execução do Éteris (só memória): criou-a, ou entrou nela (aprovação ou kit).
public enum SyncEntry { None, Created, Joined }

// O kit de recuperação do segredo guardado, neste computador. Os estados derivam-se (nunca se gravam como tais):
// guardado ⇔ SavedId == ForId; deixou de valer ⇔ SavedId de outro kit; por guardar ⇔ SavedId nulo.
public sealed class KitRecord
{
    // A impressão digital (AccountSecret.KitId) do segredo guardado: evita derivar a cada leitura do estado.
    [JsonPropertyName("for")] public string? ForId { get; set; }
    // A impressão digital do último kit que o utilizador guardou neste computador.
    [JsonPropertyName("saved")] public string? SavedId { get; set; }
    // Quando o segredo atual chegou a este computador.
    [JsonPropertyName("since")] public long SinceMs { get; set; }
    // «Mais tarde»: o lembrete não aparece na barra antes desta hora.
    [JsonPropertyName("snoozed")] public long SnoozedUntilMs { get; set; }
    // «Não quero: aceito que, sem este computador, a cópia na nuvem não se recupera».
    [JsonPropertyName("declined")] public bool Declined { get; set; }
}

public enum KitState { Saved, Pending, Stale }

// O kit no estado (sem segredo): a impressão digital, o estado e quantos computadores se conhecem na conta (sem rede;
// inclui este).
public sealed record KitInfo(string Id, KitState State, long SinceMs, long SnoozedUntilMs, bool Declined, int KnownDevices);

// O kit em si (o segredo em palavras): só SyncAgent.RevealKit o dá, e só a porta do anfitrião o pede.
public sealed record RecoveryKitSecret(string Words, string KitId, long SinceMs);

// Um estado que existe mas não se leu (ficheiro ocupado, proteção do Windows ou JSON que falhou) não é «não existe»: o
// agente recusa ligar, mudar as linhas, mudar o nome ou apagar enquanto o não voltar a ler (SyncAgent.StateUnreadableText),
// para não o substituir por um vazio.
public sealed class SyncStateUnreadableException(string message, Exception? inner = null) : Exception(message, inner);

public interface ISyncStore
{
    // null = não há estado. Lança SyncStateUnreadableException se existe e não se leu.
    SavedSync? Load();
    void Save(SavedSync saved);
    void Delete();
}

public enum SyncPhase
{
    Off,       // desligada neste aparelho
    Starting,  // a ver se a conta já existe
    Choose,    // a conta já existe: entrar por aprovação noutro aparelho ou pelo código de recuperação
    Pairing,   // à espera da aprovação noutro aparelho
    On,        // ligada
    Stopped,   // parou: a conta foi apagada ou este aparelho foi removido (diz porquê)
}

// Um pedido de um aparelho novo, pronto para a pessoa escrever aqui o código que ele mostra. O código de verificação
// nunca sai do agente (este computador pede-o, não o mostra). «SeenMs»: a hora DESTE computador em que respondeu.
public sealed record HostRequest(string Session, string Name, int AttemptsLeft, long SeenMs);

// Como acabou o pedido de aprovação deste aparelho: ninguém respondeu (e a renovação parou), um aparelho respondeu mas
// o prazo acabou, o outro recusou, ou outra falha.
public enum GuestEnd { None, StillWaiting, TimeUp, Refused, Failed }

// Este aparelho a entrar por aprovação noutro: o código a mostrar (quando há), e o resultado.
public sealed record GuestPairing(string? Code, bool Confirmed, string? Error)
{
    // Um aparelho ligado respondeu ao pedido (o código só aparece depois disto).
    public bool HostAnswered { get; init; }
    public GuestEnd End { get; init; }
}

public sealed record DeviceInfo(string Id, string Name, string Kind, long LastSyncMs, bool IsThis)
{
    // Recebe pagamentos e documentos (os dois grupos ligados). null = não se sabe (um Éteris antigo não o diz).
    public bool? PaymentsAndDocuments { get; init; }
}

public sealed record SyncStatus(SyncPhase Phase, bool Running, long LastSuccessMs, string? Error, string? Notice,
    GuestPairing? Pairing, IReadOnlyList<HostRequest> Requests, IReadOnlyList<DeviceInfo> Devices, bool NeedsUpdate, int PendingDevices)
{
    // O kit de recuperação deste computador, sem o segredo (null = sem chave aqui). As palavras só por RevealKit.
    public KitInfo? Kit { get; init; }

    // A autorização da Google expirou ou foi retirada: nada se resolve sozinho, é preciso entrar outra vez.
    public bool NeedsSignIn { get; init; }

    // Pedidos de aparelhos novos vistos numa ronda e ainda sem resposta: só se responde quando o utilizador abre o menu
    // ou a secção Sincronização (CheckRequestsAsync).
    public int WaitingRequests { get; init; }

    // A apagar os dados do Éteris no transporte (ou por acabar, se o Éteris fechou a meio): a sincronização já está
    // desligada neste aparelho.
    public bool Deleting { get; init; }

    // O apagar não acabou e porquê (com «Tentar de novo»): só enquanto Deleting. null = a correr, ou ainda não tentou.
    public string? DeleteError { get; init; }

    // Parada por a sessão ser de outra conta Google (a fase sozinha não o diz: Stopped tem outras causas).
    public bool OtherAccount { get; init; }

    // Este computador respondeu a um pedido e espera pelo computador novo (ainda sem código): a hora da resposta mais
    // recente dentro do prazo; 0 se não há nenhum.
    public long AnsweringSinceMs { get; init; }

    // Pedidos em série: vários pedidos acabaram mal há pouco e a barra de aprovação não abre sozinha (o aviso fica nas
    // Definições, com «Mostrar o pedido na barra»). Este aparelho continua a responder quando a pessoa procura.
    public bool PairingQuiet { get; init; }

    // A conta deste aparelho desapareceu do transporte e lá está outra (fase Choose com a chave guardada), ou parou
    // por a conta ter desaparecido (Stopped com a chave guardada): o alerta «conta substituída». Nunca pede sozinho.
    public bool ChainReplaced { get; init; }

    // A conta mudou de chave noutro aparelho e a mensagem que deixou a conta nova a este não é inequívoca (este
    // aparelho fixou mais chaves do que a de quem a escreveu): a sincronização está em pausa, sem perder nada, até se
    // escrever o identificador do kit novo (SyncAgent.ConfirmRekeyAsync) ou se desistir (SyncAgent.AbandonRekey).
    public bool RekeyPending { get; init; }

    // O estado guardado neste computador existe mas não se leu: nada o substitui por um vazio (SyncAgent.StateUnreadableText).
    public bool StateUnreadable { get; init; }

    // Fase Choose num aparelho sem nenhum estado desta conta (SyncAgent.MayAutoRequest): pode pedir a aprovação.
    public bool MayAutoPair { get; init; }

    // Este aparelho acabou de entrar na conta (por aprovação ou pelo código de recuperação) e a primeira ronda ainda não
    // acabou.
    public bool JoinedNow { get; init; }

    // A conta sincroniza pagamentos e documentos (a escolha da conta, 0.2.15).
    public bool AccountSecrets { get; init; }

    // Este aparelho tem cards, ibans e idDocs ligados.
    public bool SecretsHere { get; init; }

    // Este aparelho deve mostrar a oferta única «receber também neste?».
    public bool SecretsOffer { get; init; }

    // Criou a conta / entrou nela nesta execução do Éteris.
    public SyncEntry Entry { get; init; }

    // Herdou a escolha da conta nesta execução do Éteris (recebeu os pagamentos e os documentos sem pedir).
    public bool InheritedSecrets { get; init; }
}

// O aparelho na conta de sincronização: decide se cria a conta, entra (kit de recuperação ou aprovação), sincroniza, emparelha,
// remove aparelhos e apaga os dados. Uma operação de cada vez. Construir e ler o estado nunca faz pedidos: a rede só
// é usada quando o anfitrião chama RunAsync (o agendador), StartAsync (o utilizador entrou ou ligou) ou outra ação.
//
// «Sair» não espera pela ronda em curso: cada gravação confere primeiro que o estado ainda é o mesmo objeto e que a
// sincronização continua ligada (IsCurrent). Assim uma ronda que acaba depois de «Sair» (com «apagar a chave») não
// volta a gravar a chave que o utilizador pediu para apagar.
public sealed class SyncAgent
{
    // Saltos seguidos por contas que também já desapareceram (duas remoções enquanto este aparelho estava desligado).
    private const int MaxRekeyHops = 8;

    // Depois de criar a conta, durante quanto tempo se confere se outro computador criou outra ao mesmo tempo.
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(30);

    private readonly Func<ISyncTransport> _transportFactory;
    private readonly ILocalProfile _profile;
    private readonly ISyncStore _store;
    private readonly Func<long> _now;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HostFlow> _hosts = new(StringComparer.Ordinal);
    // Sessões a que este aparelho respondeu e que já acabaram (recusadas, 3 códigos errados, expiradas, acabadas mal do
    // outro lado, aprovadas). Quem tem o Drive pode apagar o x-host e o 1-host e deixar o 0-request: a mesma sessão não
    // volta a ser respondida (seriam três tentativas novas sem pedido novo). Só memória, como as tentativas. Guarda a
    // última vez que a sessão acabou ou voltou a aparecer como pedido; sai Lifetime + ClockSkew depois disso (o pedido
    // já não estava pendente; se for reescrito mais tarde, conta como um pedido novo, com os pedidos em série).
    private readonly Dictionary<string, long> _ended = new(StringComparer.Ordinal);
    private ISyncTransport? _transport;
    private SavedSync? _saved;
    private SyncEngine? _engine;
    private SyncPhase _phase;
    private bool _running;
    private string? _error;
    private string? _notice;
    private bool _needsUpdate;
    private bool _needsSignIn;
    private int _pendingDevices;
    private int _waiting;
    // Identificadores errados na pausa da troca de chave (ConfirmRekeyAsync). Só memória, como as tentativas dos códigos.
    private int _rekeyWrong;
    private GuestFlow? _guest;
    // Entrou agora na conta (aprovação ou código de recuperação): até ao fim da primeira ronda. Só memória.
    private bool _justJoined;
    // Porque é que o último «acabar de apagar» falhou (mostrado com «Tentar de novo» enquanto o apagar estiver por acabar).
    private string? _deleteError;
    private IReadOnlyList<DeviceInfo> _devices = [];
    // A conta tem registos secretos vivos (visto no fim da última ronda; o motor só se lê dentro de uma ronda).
    private bool _secretsInAccount;
    // E quais coleções secretas (só memória; nunca se muda o conjunto: troca-se por outro).
    private IReadOnlySet<string> _secretCollections = NoCollections;
    private static readonly IReadOnlySet<string> NoCollections = new HashSet<string>(StringComparer.Ordinal);
    // Como este aparelho entrou na conta nesta execução, e se herdou a escolha da conta: até o Éteris fechar (a barra
    // «Sincronizado.» só existe a seguir a uma entrada nesta abertura). Só memória.
    private SyncEntry _entry;
    private bool _inheritedNow;
    // Quem ligou a escolha da conta e quando (lido no fim da última ronda; informação, não prova; esquecido ao mudar de
    // conta, com _inheritedNow). Só memória.
    private string? _accountBy;
    private long _accountSince;
    private static readonly long MaxDateMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
    // Já se leram os estados dos aparelhos (e fixaram as chaves) com o motor atual: uma vez por sessão e conta, e de
    // novo quando aparece um aparelho desconhecido. Um aparelho que ainda não enviou nada só se conhece por aqui.
    private SyncEngine? _pinnedFor;

    public SyncAgent(Func<ISyncTransport> transport, ILocalProfile profile, ISyncStore store, string deviceName, Func<long> now,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _transportFactory = transport;
        _profile = profile;
        _store = store;
        _now = now;
        _delay = delay;
        _defaultName = deviceName;
        DeviceName = deviceName;
        try { Adopt(store.Load()); }
        catch (SyncStateUnreadableException)
        {
            // Existe e não se leu: sem estado em memória, parado, e nada o substitui até se voltar a ler (ReloadState).
            _saved = null;
            _unreadable = true;
            _phase = SyncPhase.Stopped;
            _notice = StateUnreadableText;
        }
    }

    private readonly string _defaultName;

    // O estado guardado não se leu (SyncStateUnreadableException): ver EnsureReadable.
    private bool _unreadable;

    // Fica com o estado lido: o registo do kit (migra um estado da 0.2.14 sem registo e corrige um que não corresponda ao
    // segredo; só em memória, vai para o disco na gravação seguinte; um segredo ilegível não impede o agente de nascer:
    // fica sem kit, a ronda dá o erro dela), o nome, a fase e o aviso. Chamar com _gate (ou no construtor).
    private void Adopt(SavedSync? saved)
    {
        _saved = saved;
        if (_saved?.Secret is { } kept)
        {
            try
            {
                using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(kept));
                if (_saved.Kit is null)
                {
                    var id = account.KitId;
                    _saved.Kit = new KitRecord { ForId = id, SavedId = _saved.CodePending ? null : id, SinceMs = _now() };
                    _saved.CodePending = false;
                }
                else KitFor(_saved, account);
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                _saved.Kit = null;
            }
        }
        // O nome escolhido aqui; senão o que o estado do motor já tem (ligado antes desta versão); senão o de omissão.
        DeviceName = DeviceNameText.Clean(_saved?.DeviceName) ?? DeviceNameText.Clean(_saved?.Engine?.Device.Name) ?? _defaultName;
        _phase = _saved is { Enabled: true } ? SyncPhase.On : _saved?.Stopped is not null ? SyncPhase.Stopped : SyncPhase.Off;
        _notice = _saved?.Stopped;
    }

    // Volta a ler o estado guardado quando da última vez não se leu. true = já se lê (ou nunca falhou): null conta como
    // «não há estado». false = continua por ler.
    public bool ReloadState()
    {
        lock (_gate)
        {
            if (!_unreadable) return true;
            SavedSync? loaded;
            try { loaded = _store.Load(); }
            catch (SyncStateUnreadableException) { return false; }
            _unreadable = false;
            Adopt(loaded);
        }
        Notify();
        return true;
    }

    // Os cinco sítios que criam um estado vazio quando não há nenhum (_saved ??= new SavedSync()) começam por aqui: com um
    // estado que existe e não se leu, não criam nada.
    private void EnsureReadable()
    {
        if (!ReloadState()) throw new SyncUserException(StateUnreadableText);
    }

    public const string StateUnreadableText =
        "O estado da sincronização deste computador não se conseguiu ler (o ficheiro está ocupado por outro programa, ou a proteção do Windows falhou). Nada foi apagado nem substituído. Feche o Éteris e abra-o outra vez, ou carregue em «Ligar outra vez». Se continuar, «Recomeçar sem o estado antigo» põe o ficheiro de lado e este computador volta a entrar na conta.";

    public string DeviceName { get; private set; }

    // Muda o nome deste computador. Local e imediato; os outros computadores veem-no depois da próxima ronda.
    public Task RenameDeviceAsync(string? name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clean = DeviceNameText.Clean(name) ?? throw new SyncUserException("Escreva um nome para este computador.");
        EnsureReadable();
        lock (_gate)
        {
            if (clean == DeviceName) return Task.CompletedTask;
            _saved ??= new SavedSync();
            _saved.DeviceName = clean;
            DeviceName = clean;
            // O motor só é de quem tem a ronda: o nome chega-lhe no passo 0 da ronda seguinte (ApplyChosenName).
            _store.Save(_saved);
        }
        Notify();
        return Task.CompletedTask;
    }

    // O nome escolhido neste computador vale sobre o que o estado do motor traz (mudado sem motor carregado, antes da
    // primeira ronda ou durante uma troca de chave; ou um motor refeito de um estado antigo). Chamar com _gate.
    private void ApplyChosenName(SyncEngine engine)
    {
        if (_saved?.DeviceName is { } chosen && engine.Device.Name != chosen) engine.Rename(chosen);
    }

    // Intervalo entre perguntas ao transporte enquanto se espera pelo outro aparelho no emparelhamento.
    public TimeSpan PairingPoll { get; init; } = TimeSpan.FromSeconds(3);

    // A conta do transporte com que se está agora (para o Google Drive, uma impressão digital da conta Google; null =
    // não se sabe, ou o transporte não tem contas). Só disco: nunca faz pedidos.
    public Func<string?>? AccountKey { get; init; }

    // Pedir a aprovação noutro computador sozinho, na ronda do gesto de entrar ou de ligar (StartAsync), quando o
    // transporte já tem uma conta e este aparelho nunca teve estado dela (MayAutoRequest). Desligado por omissão.
    public bool AutoPair { get; init; }

    // O ecrã do pedido está à vista neste aparelho (a interface diz). null = nunca: o pedido não se renova sozinho.
    public Func<bool>? PairingScreenVisible { get; init; }

    // Pode pedir a aprovação sozinho? Só um aparelho sem nenhum estado de sincronização: nem chave, nem impressão
    // digital da conta, nem a marca de já ter tido uma (no modo pasta de teste não há impressão digital). «Sair» com
    // «apagar a chave» apaga o estado inteiro: o aparelho volta a ser novo.
    internal static bool MayAutoRequest(SavedSync? saved)
        => saved is null || (saved.Secret is null && saved.Account is null && !saved.HadAccount);

    // A primeira ronda a seguir a uma entrada por aprovação acabou (true: correu bem). Essa ronda é o agente que a
    // corre, fora do agendador do anfitrião: com este sinal o anfitrião marca a tentativa seguinte quando ela falha,
    // em vez de esperar pela ronda periódica. Chamado de qualquer thread.
    public Action<bool>? RoundEnded { get; init; }

    // Registo de diagnóstico (nomes opacos do transporte, nunca dados): o que o apagar deixou de fora e porque falhou.
    public Action<string>? Log { get; init; }

    // Mudou alguma coisa no estado (para a interface se redesenhar). Chamado de qualquer thread.
    public event Action? Changed;

    public bool Enabled
    {
        get { lock (_gate) return _saved?.Enabled == true; }
    }

    private bool SignInNeeded
    {
        get { lock (_gate) return _needsSignIn; }
    }

    // Há uma conta guardada neste aparelho (para retomar sem código depois de «Sair» sem apagar a chave).
    public bool HasKey
    {
        get { lock (_gate) return _saved?.Secret is not null; }
    }

    // «Apagar os meus dados…» por acabar (o Éteris fechou a meio). Só disco.
    public bool DeletePending
    {
        get { lock (_gate) return _saved?.DeletePending == true; }
    }

    // As coleções ligadas neste aparelho (as de omissão até o utilizador escolher). Só disco.
    public IReadOnlySet<string> EnabledCollections
    {
        get
        {
            lock (_gate) return EnabledOf(_saved);
        }
    }

    private static IReadOnlySet<string> EnabledOf(SavedSync? saved)
        => saved?.Collections is { } chosen ? chosen.ToHashSet(StringComparer.Ordinal) : Model.SyncGroups.DefaultCollections;

    // Liga ou desliga coleções neste aparelho. Desligar não apaga nada; ligar outra vez faz a «primeira junção» só delas
    // (o que só existe aqui sobe; o que a conta tem vale). A confirmação do Windows para Pagamentos e Documentos é do
    // anfitrião, antes de chamar isto.
    public void SetCollections(IEnumerable<string> collections, bool on)
    {
        EnsureReadable();
        lock (_gate)
        {
            _saved ??= new SavedSync();
            Choose(_saved, collections, on);
            _store.Save(_saved);
        }
        Notify();
    }

    // Liga ou desliga coleções no estado (sem gravar). Não mexe na escolha da conta nem na oferta (desligar aqui não
    // muda a conta). Chamar dentro do _gate.
    private static void Choose(SavedSync saved, IEnumerable<string> collections, bool on)
    {
        var enabled = EnabledOf(saved).ToHashSet(StringComparer.Ordinal);
        var joined = JoinedOf(saved).ToHashSet(StringComparer.Ordinal);
        foreach (var collection in collections)
        {
            if (on) enabled.Add(collection);
            else enabled.Remove(collection);
            joined.Remove(collection);
        }
        saved.Collections = enabled.Order(StringComparer.Ordinal).ToList();
        saved.Joined = joined.Order(StringComparer.Ordinal).ToList();
    }

    // Há segredos em jogo neste aparelho (coleções secretas ligadas, registos secretos na conta, ou a conta com a
    // escolha de pagamentos e documentos ligada): aprovar um aparelho novo entrega-lhe também as palavras-passe, os
    // pagamentos e os documentos. Um aparelho numa conta que ainda não acabou uma ronda nela não sabe: conta como «há»
    // (quem aprovar passa pela porta do Windows sempre que possa haver segredos em jogo).
    public bool HasSecrets
    {
        get
        {
            lock (_gate)
                return EnabledOf(_saved).Any(Model.Collections.IsSecret) || _secretsInAccount
                    || (_saved is { Secret: not null } saved && saved.SecretsInAccount != false);
        }
    }

    // Uma troca de chave começou e ainda não acabou (acaba na ronda seguinte).
    public bool RotationPending
    {
        get { lock (_gate) return _saved?.Rotation is not null; }
    }

    // As coleções secretas com registos vivos na conta, vistas no fim da última ronda (memória; vazio antes dela e depois
    // de sair).
    public IReadOnlySet<string> SecretCollectionsInAccount
    {
        get { lock (_gate) return _secretCollections; }
    }

    // ---- escolha da conta: pagamentos e documentos (0.2.15) --------------------------------------------------------

    // O que o aparelho que herdou diz, na barra «Sincronizado.» e (com ponto final) na nota do estado.
    public const string InheritedText = "Recebeu favoritos, palavras-passe, cartões e documentos";

    // A conta sincroniza pagamentos e documentos (a última leitura, ou a escolha feita aqui). Só disco.
    public bool AccountSecrets
    {
        get { lock (_gate) return _saved is { Secret: not null, AccountSecrets: true }; }
    }

    // Este aparelho deve mostrar a oferta única «receber também neste?». Barato (só memória): serve o «wants» da barra.
    public bool SecretsOffer
    {
        get { lock (_gate) return OfferOf(_saved); }
    }

    // A conta tem a escolha ligada, este aparelho já estava (ou já esteve) noutra conta quando ela foi feita, ainda não
    // respondeu e falta-lhe algum dos dois grupos. Chamar dentro do _gate.
    private static bool OfferOf(SavedSync? saved)
        => saved is { Enabled: true, Secret: not null, AccountSecrets: true, OfferAnswered: false, InheritPending: false } s
            && !Model.SyncGroups.AccountCollections.All(EnabledOf(s).Contains);

    // O nome do aparelho que ligou a escolha (já limpo: é um nome recebido) e quando. null / 0 = não se sabe.
    public string? AccountSecretsBy
    {
        get { lock (_gate) return _accountBy; }
    }

    public long AccountSecretsSinceMs
    {
        get { lock (_gate) return _accountSince; }
    }

    // Grava a escolha da conta. Não usa o motor (só se usa dentro de uma ronda): fica por escrever e a ronda seguinte
    // escreve-a. Quem chama já passou pela porta do Windows (o anfitrião).
    public void SetAccountSecrets(bool on)
    {
        lock (_gate)
        {
            if (_saved is not { Enabled: true, Secret: not null } saved) throw new SyncUserException(NotOnText);
            saved.AccountChoiceOut = on ? "on" : "off";
            saved.AccountSecrets = on;
            saved.OfferAnswered = true;
            if (on) saved.SecretsInAccount = true;
            _store.Save(saved);
        }
        Notify();
    }

    // A resposta à oferta única: «Sim» liga aqui os dois grupos (o anfitrião já pediu o Windows Hello); «Não» fecha-a
    // para sempre neste computador (os interruptores continuam nas Definições).
    public void AnswerSecretsOffer(bool accept)
    {
        lock (_gate)
        {
            if (_saved is not { } saved) return;
            saved.OfferAnswered = true;
            if (accept) Choose(saved, Model.SyncGroups.AccountCollections, on: true);
            _store.Save(saved);
        }
        Notify();
    }

    private const string NotOnText = "A sincronização não está ligada neste computador.";

    // A escolha da conta por escrever (SetAccountSecrets) entra no motor no início da ronda, antes do passo do perfil:
    // segue no envio desta ronda; se a ronda falhar, fica na caixa de saída guardada. Chamar dentro do _gate.
    private void WriteAccountChoice(SavedSync saved, SyncEngine engine)
    {
        if (saved.AccountChoiceOut is not { } choice) return;
        engine.Put(Model.Collections.Settings, Model.Collections.AccountSecretsKey,
            new JsonObject { ["value"] = choice, ["since"] = _now(), ["by"] = engine.Device.Id });
        saved.AccountChoiceOut = null;
    }

    // Lê o registo da conta no fim do passo do perfil e, num aparelho que acabou de entrar sem estado anterior, herda a
    // escolha (liga pagamentos e documentos, sem os pôr nas já juntas: a ronda junta-os já a seguir). Valor desconhecido
    // ou ilegível = desligado. Devolve true se herdou agora. Chamar dentro do _gate; quem chama grava.
    private bool ReadAccountChoice(SavedSync saved, SyncEngine engine)
    {
        var record = engine.Get(Model.Collections.Settings, Model.Collections.AccountSecretsKey);
        var on = record is { Deleted: false } && record.Text("value") == "on";
        if (saved.AccountChoiceOut is null) saved.AccountSecrets = on;
        // «by» é o id de um aparelho: o nome é o deste (já limpo) ou um nome recebido, que se limpa antes de mostrar.
        _accountBy = record is { Deleted: false } && record.Text("by") is { } by
            ? by == engine.Device.Id ? DeviceNameText.Shown(engine.Device.Name)
            : engine.State.DeviceNames.TryGetValue(by, out var name) ? DeviceNameText.Shown(name) : null
            : null;
        // «since» é recebido: fora do intervalo das datas, ou mais de um dia no futuro, não se sabe quando (0).
        _accountSince = record is { Deleted: false, Data: { } data } && data["since"] is JsonValue since && since.TryGetValue<long>(out var ms)
            && ms > 0 && ms <= MaxDateMs && ms <= _now() + 86_400_000L ? ms : 0;
        saved.SecretsInAccount = _secretsInAccount || saved.AccountSecrets;
        if (!saved.InheritPending) return false;
        saved.InheritPending = false;
        if (!saved.AccountSecrets) return false;
        var enabled = EnabledOf(saved).ToHashSet(StringComparer.Ordinal);
        enabled.UnionWith(Model.SyncGroups.AccountCollections);
        saved.Collections = enabled.Order(StringComparer.Ordinal).ToList();
        saved.OfferAnswered = true;
        _inheritedNow = true;
        return true;
    }

    // Acrescenta uma frase ao aviso do estado (uma vez). Chamar dentro do _gate.
    private void AddNotice(string text)
    {
        if (_notice?.EndsWith(text, StringComparison.Ordinal) == true) return;
        _notice = string.IsNullOrEmpty(_notice) ? text : _notice + " " + text;
    }

    // Uma conta nova (criada ou entrada) começa sem nada lido da escolha da conta. Chamar dentro do _gate.
    private static void ResetAccountChoice(SavedSync saved)
    {
        saved.AccountSecrets = false;
        saved.SecretsInAccount = null;
        saved.AccountChoiceOut = null;
    }

    // As já juntadas (um estado de antes das linhas: com base, as coleções que existiam antes das secretas).
    private static IReadOnlyCollection<string> JoinedOf(SavedSync saved)
        => saved.Joined ?? (saved.Baseline is null ? [] : [.. Model.SyncGroups.LegacyCollections]);

    private static ProfileSyncOptions OptionsOf(SavedSync saved)
    {
        var enabled = EnabledOf(saved);
        var joined = JoinedOf(saved);
        return new ProfileSyncOptions { Enabled = enabled, Fresh = enabled.Where(c => !joined.Contains(c)).ToHashSet(StringComparer.Ordinal) };
    }

    public SyncStatus Status
    {
        get
        {
            lock (_gate)
            {
                var now = _now();
                var live = _hosts.Values.Where(h => now - h.Created < (long)PairingProtocol.Lifetime.TotalMilliseconds).ToList();
                // O mais antigo primeiro (e, com a mesma hora, pela sessão): a barra mostra um de cada vez, sempre o mesmo.
                var requests = live.Where(h => h.Code is not null).OrderBy(h => h.Created).ThenBy(h => h.Session, StringComparer.Ordinal)
                    .Select(h => new HostRequest(h.Session, DeviceNameText.Shown(h.Name), PairingCode.MaxWrong - h.Wrong, h.Created)).ToList();
                var answering = live.Where(h => h.Code is null).Select(h => h.Created).DefaultIfEmpty(0).Max();
                var pairing = _guest is null ? null : new GuestPairing(_guest.Code, _guest.Confirm.Task.IsCompleted, _guest.Error)
                {
                    HostAnswered = _guest.HostAnswered,
                    End = _guest.End,
                };
                // Este computador: pagamentos e documentos pelo que está ligado agora (não pela última leitura da lista).
                var here = Model.SyncGroups.AccountCollections.All(EnabledOf(_saved).Contains);
                var devices = _devices.Any(d => d.IsThis && d.PaymentsAndDocuments != here)
                    ? _devices.Select(d => d.IsThis ? d with { PaymentsAndDocuments = here } : d).ToList()
                    : _devices;
                return new SyncStatus(_phase, _running, _saved?.LastSuccessMs ?? 0, _error, _notice, pairing, requests, devices, _needsUpdate, _pendingDevices)
                {
                    Kit = KitInfoOf(_saved),
                    NeedsSignIn = _needsSignIn,
                    WaitingRequests = _waiting,
                    Deleting = _saved?.DeletePending == true,
                    DeleteError = _saved?.DeletePending == true ? _deleteError : null,
                    OtherAccount = _phase == SyncPhase.Stopped && _notice == OtherAccountText,
                    AnsweringSinceMs = answering,
                    PairingQuiet = PairingFatigue.Quiet(_saved?.BadPairings, now),
                    ChainReplaced = _saved?.Secret is not null
                        && (_phase == SyncPhase.Choose || (_phase == SyncPhase.Stopped && _saved.Stopped == GoneText)),
                    RekeyPending = RekeyHeld(_saved),
                    StateUnreadable = _unreadable,
                    MayAutoPair = _phase == SyncPhase.Choose && MayAutoRequest(_saved),
                    JoinedNow = _justJoined && _phase == SyncPhase.On,
                    AccountSecrets = _saved is { Secret: not null, AccountSecrets: true },
                    SecretsHere = here,
                    SecretsOffer = OfferOf(_saved),
                    Entry = _entry,
                    InheritedSecrets = _inheritedNow,
                };
            }
        }
    }

    // ---- ligar ------------------------------------------------------------------------------------------------------

    // O utilizador entrou (ou carregou em «Ligar»): liga a sincronização neste aparelho e faz a primeira ronda — retoma
    // a conta guardada, cria a conta se o transporte não tiver nenhuma, ou pede para escolher como entrar.
    // Se a sessão do transporte for de outra conta (outra conta Google), a sincronização fica em pausa sem apagar nada;
    // só com «switchAccount» (o utilizador carregou em «Ligar outra vez» depois de ler o aviso) passa para a conta nova.
    public async Task StartAsync(CancellationToken cancellationToken = default) => await StartAsync(switchAccount: false, cancellationToken).ConfigureAwait(false);

    public async Task StartAsync(bool switchAccount, CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        // «Apagar os meus dados…» por acabar (o Éteris fechou a meio): acaba primeiro; a conta nova começa do zero. Se não
        // acabar, diz porquê e como sair dali: tentar de novo, ou ligar sem acabar de apagar (AbandonCloudDeletion).
        if (DeletePending)
        {
            try { await FinishCloudDeletionAsync(cancellationToken).ConfigureAwait(false); }
            catch (SyncUserException e) { throw new SyncUserException(e.Message + " " + StartAnywayText); }
        }
        lock (_gate)
        {
            _saved ??= new SavedSync();
            _saved.Enabled = true;
            _saved.Stopped = null;
            if (switchAccount && OtherAccount(_saved))
            {
                // A conta guardada é de outra sessão: recomeça nesta (a chave antiga fica até haver uma conta nova).
                _engine = null;
                _saved.Engine = null;
                _saved.Baseline = null;
                _saved.Joined = null;
                _saved.Rotation = null;
                _saved.CreatedMs = 0;
                _saved.Account = AccountKey?.Invoke();
            }
            _notice = null;
            _error = null;
            _needsSignIn = false;
            _phase = SyncPhase.Starting;
            _store.Save(_saved);
        }
        Notify();
        // A ronda do gesto: a única em que o aparelho novo pode pedir a aprovação sozinho (AutoPair).
        await RunCoreAsync(force: true, gesture: true, cancellationToken).ConfigureAwait(false);
    }

    // Uma ronda: o que fizer falta (retomar, criar, sincronizar). Devolve false se falhou (o agendador espera mais).
    // «force» não é um gesto (o agendador e os testes também a pedem): nenhuma ronda daqui pede a aprovação sozinha.
    public Task<bool> RunAsync(bool force = false, CancellationToken cancellationToken = default)
        => RunCoreAsync(force, gesture: false, cancellationToken);

    private async Task<bool> RunCoreAsync(bool force, bool gesture, CancellationToken cancellationToken)
    {
        if (!Enabled) return true;
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunning(true);
        SavedSync? saved;
        lock (_gate) saved = _saved;
        try
        {
            if (saved is not { Enabled: true }) return true;
            if (OtherAccount(saved))
            {
                Stop(saved, OtherAccountText, StopKeeps.Everything);
                return true;
            }
            if (_engine is null)
            {
                if (saved is { Secret: { } secret, Engine: { } state })
                {
                    if (!await ResumeAsync(saved, secret, state, cancellationToken).ConfigureAwait(false)) return true;
                }
                else
                {
                    if (!force && _phase is SyncPhase.Choose or SyncPhase.Pairing) return true;
                    await SetUpAsync(saved, gesture, cancellationToken).ConfigureAwait(false);
                    if (_engine is null) return true;
                }
            }
            if (saved.Rotation is { } rotation) await FinishRotationAsync(saved, rotation, cancellationToken).ConfigureAwait(false);
            await CycleAsync(saved, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChainGoneException)
        {
            await ChainGoneAsync(saved!, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (IsTransient(e))
        {
            Fail(e);
            return false;
        }
        finally
        {
            // «Sair» a meio: o motor desta ronda já não é o deste aparelho.
            lock (_gate)
                if (!IsCurrent(saved))
                    _engine = null;
            EndRun();
        }
    }

    // Há alterações locais por enviar? (Sem rede: o agendador só sincroniza depois de uma alteração se houver.)
    public bool HasLocalChanges()
    {
        SavedSync? saved;
        lock (_gate) saved = _saved;
        if (saved is not { Enabled: true, Secret: not null }) return false;
        // A escolha da conta feita aqui e ainda por escrever (também depois de reabrir o Éteris).
        lock (_gate)
            if (saved.AccountChoiceOut is not null)
                return true;
        ProfileSyncOptions options;
        lock (_gate) options = OptionsOf(saved);
        return ProfileSync.CountLocalChanges(_profile, saved.Baseline, options) > 0;
    }

    private async Task<bool> ResumeAsync(SavedSync saved, string secret, SyncState state, CancellationToken cancellationToken)
    {
        using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(secret));
        try
        {
            // O motor fica com uma cópia: o estado guardado não muda enquanto ele trabalha (Snapshot).
            var engine = await SyncEngine.ResumeAsync(Transport(), account, SyncState.Parse(state.ToBytes()), _now, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
            {
                if (!IsCurrent(saved)) return false;
                _engine = engine;
                _phase = SyncPhase.On;
            }
            Notify();
            return true;
        }
        catch (WrongCodeException)
        {
            // O registo de chaves no transporte não abre com a chave guardada (alterado ou danificado): para, sem apagar
            // nada deste computador — a chave fica, para quando o registo voltar ao normal ou para o utilizador decidir.
            Stop(saved, WrongKeysText, StopKeeps.Everything);
            return false;
        }
    }

    private async Task SetUpAsync(SavedSync saved, bool gesture, CancellationToken cancellationToken)
    {
        var chains = await ChainsAsync(cancellationToken).ConfigureAwait(false);
        // A chave ficou neste aparelho («Sair» sem a apagar, ou parou sem a apagar) e a conta ainda existe: entra outra
        // vez sem código.
        if (saved.Secret is { } kept)
        {
            using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(kept));
            if (chains.Contains(account.ChainId))
            {
                try
                {
                    // Se um «apagar» (ou «Sair») chegou entretanto, a entrada não grava nada e a ronda acaba aqui.
                    await JoinCoreAsync(account, null, fresh: false, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (WrongCodeException) { } // o registo dessa conta não abre: escolher como entrar
            }
        }
        if (chains.Count == 0)
        {
            using var account = AccountSecret.Create();
            var engine = await SyncEngine.CreateAsync(Transport(), account, DeviceIdentity.Create(DeviceName), _now, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var snapshot = Snapshot(engine);
            lock (_gate)
            {
                if (!IsCurrent(saved)) return;
                _engine = engine;
                saved.Secret = Kdf.Base64Url(account.ToBytes());
                // Conta nova: kit novo, por guardar (mesmo que este computador tivesse o registo de outra conta). Nada
                // é pedido agora; o lembrete vem depois.
                saved.Kit = null;
                KitFor(saved, account);
                saved.HadAccount = true;
                saved.Engine = snapshot;
                saved.Baseline = null;
                saved.Joined = null;
                saved.CreatedMs = _now();
                saved.Account = AccountKey?.Invoke();
                // Conta nova: sem escolha da conta, nada a herdar nem a oferecer; «há segredos?» fica por saber até ao fim
                // da primeira ronda (conta como «há»).
                ResetAccountChoice(saved);
                saved.InheritPending = false;
                saved.OfferAnswered = false;
                _entry = SyncEntry.Created;
                _inheritedNow = false;
                _accountBy = null;
                _accountSince = 0;
                _phase = SyncPhase.On;
                _store.Save(saved);
            }
            return;
        }
        lock (_gate)
        {
            if (!IsCurrent(saved)) return;
            // Um pedido de aprovação que já estava a correr (ligar ou entrar outra vez a meio dele) continua à vista.
            _phase = _guest is { Error: null } ? SyncPhase.Pairing : SyncPhase.Choose;
        }
        Notify();
        // O pedido automático: um por gesto, e só num aparelho que nunca teve estado desta conta (um com a chave de uma
        // conta que desapareceu mostra o alerta «conta substituída» e não pede nada).
        if (AutoPair && gesture && MayAutoRequest(saved)) RequestPairing();
    }

    private async Task<HashSet<string>> ChainsAsync(CancellationToken cancellationToken)
    {
        var chains = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in await Transport().ListAsync(SyncPaths.Root, cancellationToken).ConfigureAwait(false))
            if (SyncPaths.TryParseKeys(path, out var chain)) chains.Add(chain);
        return chains;
    }

    // ---- sincronizar ------------------------------------------------------------------------------------------------

    private async Task CycleAsync(SavedSync saved, CancellationToken cancellationToken)
    {
        var engine = _engine!;
        // Atribuído sempre na primeira passagem do ciclo abaixo (ou a ronda sai por exceção ou por «return»).
        SyncReport report = null!;
        var inheritedHere = false;
        // Herdar a escolha da conta liga pagamentos e documentos a meio da ronda: o passo do perfil repete-se uma vez,
        // com as opções novas, para os cartões e os documentos chegarem já nesta ronda (antes de LastSuccessMs mudar).
        for (var pass = 0; pass < 2; pass++)
        {
            Dictionary<string, string> baseline;
            ProfileSyncOptions options;
            lock (_gate)
            {
                options = OptionsOf(saved);
                // O estado do aparelho diz aos outros que grupos recebe (lista de aparelhos).
                engine.DeviceGroups = Model.SyncGroups.GroupsOn(EnabledOf(saved));
                if (pass == 0)
                {
                    ApplyChosenName(engine);
                    WriteAccountChoice(saved, engine);
                }
            }
            try
            {
                (report, baseline) = await ProfileSync.RunAsync(engine, _profile, saved.Baseline, DeviceName, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // O que ficou na caixa de saída (e o que já se leu) fica guardado mesmo que a ronda falhe a meio.
                var staged = Snapshot(engine);
                lock (_gate)
                    if (IsCurrent(saved) && ReferenceEquals(_engine, engine))
                    {
                        saved.Engine = staged;
                        _store.Save(saved);
                    }
            }
            bool inherited;
            lock (_gate)
            {
                if (!IsCurrent(saved) || !ReferenceEquals(_engine, engine)) return;
                // As coleções que fizeram a ronda inteira já estão juntas (as ligadas entretanto ficam por juntar).
                var joined = JoinedOf(saved).ToHashSet(StringComparer.Ordinal);
                var enabledNow = EnabledOf(saved);
                joined.UnionWith(report.Joined.Where(c => options.IsEnabled(c) && enabledNow.Contains(c)));
                saved.Baseline = baseline;
                saved.Joined = joined.Order(StringComparer.Ordinal).ToList();
                _secretsInAccount = engine.State.Records.Values.Any(r => !r.Deleted && Model.Collections.IsSecret(r.Collection));
                _secretCollections = engine.State.Records.Values.Where(r => !r.Deleted && Model.Collections.IsSecret(r.Collection)).Select(r => r.Collection).ToHashSet(StringComparer.Ordinal);
                if (_profile is ILocalNotices notices && notices.TakeNotice() is { } notice) _notice = notice;
                inherited = ReadAccountChoice(saved, engine);
                inheritedHere |= inherited;
                // O aviso fica também na nota do estado (a barra «Sincronizado.» só existe a seguir a uma entrada).
                if (inheritedHere) AddNotice(InheritedText + ".");
                _store.Save(saved);
            }
            if (!inherited) break;
        }
        if (await LeaveDuplicateAsync(saved, engine, cancellationToken).ConfigureAwait(false)) return;
        await engine.PushAsync(cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(_pinnedFor, engine) || engine.HasUnpinnedPeers)
        {
            await RefreshDevicesCoreAsync(cancellationToken).ConfigureAwait(false);
            _pinnedFor = engine;
        }
        var now = _now();
        if (now - saved.LastCompactMs > (long)TimeSpan.FromDays(1).TotalMilliseconds && engine.State.NextSeq - 1 > saved.LastCompactSeq)
        {
            await engine.CompactAsync(cancellationToken).ConfigureAwait(false);
            var compacted = engine.State.NextSeq - 1;
            lock (_gate)
            {
                saved.LastCompactMs = now;
                saved.LastCompactSeq = compacted;
            }
        }
        // Numa ronda, os pedidos de aparelhos novos só se contam (para o menu dizer que há um): responder-lhes faz
        // pedidos durante minutos e só acontece quando o utilizador abre o menu ou a secção Sincronização. Se a
        // contagem falhar de passagem, a ronda — que já enviou e recebeu tudo — não conta como falhada (a sessão da
        // Google por renovar não passa calada: tem de ficar no estado).
        try { await AnswerRequestsCoreAsync(answer: false, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (IsTransient(e) && e is not Google.OAuthException { NeedsSignIn: true }) { }
        var done = Snapshot(engine);
        lock (_gate)
        {
            if (!IsCurrent(saved) || !ReferenceEquals(_engine, engine)) return;
            saved.Engine = done;
            saved.LastSuccessMs = now;
            if (saved.Account is null && AccountKey?.Invoke() is { } account) saved.Account = account;
            _store.Save(saved);
            _error = null;
            _needsSignIn = false;
            _needsUpdate = report.NeedsUpdate;
            _pendingDevices = report.Pending.Count;
            _phase = SyncPhase.On;
            _justJoined = false;
        }
    }

    // Dois computadores entraram pela primeira vez quase ao mesmo tempo, com o transporte vazio, e cada um criou a sua
    // conta. Enquanto este aparelho estiver sozinho na conta que criou (e só na primeira meia hora), confere: se houver
    // outra conta com o identificador menor, essa fica (o mesmo critério nos dois) e este aparelho sai da sua — apaga-a
    // do transporte e volta a «escolher como entrar», sem perder nada deste computador (a junção é sem perda).
    private async Task<bool> LeaveDuplicateAsync(SavedSync saved, SyncEngine engine, CancellationToken cancellationToken)
    {
        if (saved.CreatedMs == 0) return false;
        if (_now() - saved.CreatedMs > (long)DuplicateWindow.TotalMilliseconds)
        {
            lock (_gate)
                if (IsCurrent(saved))
                {
                    saved.CreatedMs = 0;
                    _store.Save(saved);
                }
            return false;
        }
        var files = await Transport().ListAsync(SyncPaths.Root, cancellationToken).ConfigureAwait(false);
        var mine = engine.ChainId;
        if (files.Any(f => SyncPaths.TryParseState(mine, f, out var device) && device != engine.Device.Id))
        {
            // Já há outro aparelho nesta conta: fica.
            lock (_gate)
                if (IsCurrent(saved))
                {
                    saved.CreatedMs = 0;
                    _store.Save(saved);
                }
            return false;
        }
        var other = files.Select(f => SyncPaths.TryParseKeys(f, out var chain) ? chain : null).OfType<string>()
            .Where(c => c != mine).Order(StringComparer.Ordinal).FirstOrDefault();
        if (other is null || string.CompareOrdinal(other, mine) > 0) return false;
        await engine.DeleteEverythingAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (!IsCurrent(saved)) return true;
            _engine = null;
            _devices = [];
            saved.HadAccount = true;
            saved.Secret = null;
            saved.Kit = null;
            saved.Engine = null;
            saved.Baseline = null;
            saved.Joined = null;
            saved.CodePending = false;
            saved.CreatedMs = 0;
            saved.Rotation = null;
            ResetAccountChoice(saved);
            saved.InheritPending = false;
            _entry = SyncEntry.None;
            _inheritedNow = false;
            _accountBy = null;
            _accountSince = 0;
            _store.Save(saved);
            _phase = SyncPhase.Choose;
            _notice = "Outro computador criou a conta de sincronização ao mesmo tempo que este. Junte este computador a essa conta (aprovação no outro computador ou o kit de recuperação dele): os dados deste computador juntam-se aos da conta, nada se perde.";
        }
        return true;
    }

    // A conta deste aparelho desapareceu: outro aparelho fez «nova cadeia» (e deixou-lhe a conta nova), removeu este
    // aparelho, ou os dados foram apagados. Procura a conta nova (FindRekeyAsync) e só a segue sozinho quando a
    // mensagem é inequívoca: quem a escreveu é a única chave fixada neste aparelho. Com mais chaves fixadas, a mensagem
    // pode ser de um aparelho removido (tem a chave dele fixada aqui e, enquanto tiver acesso ao Drive, consegue apagar
    // a mensagem verdadeira e deixar a sua): fica em pausa, sem perder nada, até o utilizador escrever o identificador
    // do kit novo (ConfirmRekeyAsync).
    private async Task ChainGoneAsync(SavedSync saved, CancellationToken cancellationToken)
    {
        SyncState? old;
        lock (_gate)
        {
            if (!IsCurrent(saved)) return;
            old = _engine?.State ?? saved.Engine;
            _engine = null;
        }
        if (old is not null)
        {
            try
            {
                var (found, consumed, unambiguous) = await FindRekeyAsync(Transport(), old, cancellationToken).ConfigureAwait(false);
                using var rekey = found;
                if (rekey is not null)
                {
                    if (rekey.Conflict) Stop(saved, ConflictText, StopKeeps.Secret);
                    else if (!unambiguous) Stop(saved, RekeyHeldText, StopKeeps.Everything);
                    else await AdoptRekeyAsync(saved, old, rekey, consumed, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            catch (ChainGoneException) { }
            catch (Exception e) when (IsTransient(e))
            {
                // Sem rede a meio: tenta outra vez na próxima ronda (o estado antigo continua guardado).
                Fail(e);
                return;
            }
        }
        Stop(saved, GoneText, StopKeeps.Secret);
    }

    // A conta nova deixada a este aparelho: segue as mensagens r/ em sequência (duas remoções seguidas enquanto este
    // aparelho estava desligado) até uma conta que exista. NUNCA entra numa conta: nada deste aparelho é escrito numa
    // conta por confirmar. «Consumed»: as mensagens lidas pelo caminho (saem do transporte depois de a conta nova ficar
    // guardada). «Unambiguous»: em todos os saltos, quem escreveu a mensagem era a única chave fixada neste aparelho.
    // Devolve Rekey nulo quando não há mensagem (ou os saltos se esgotaram), ou um Conflict. Quem recebe o resultado
    // faz-lhe Dispose.
    private static async Task<(RekeyResult? Rekey, List<string> Consumed, bool Unambiguous)> FindRekeyAsync(ISyncTransport transport,
        SyncState old, CancellationToken cancellationToken)
    {
        var chain = old.ChainId;
        var consumed = new List<string>();
        var unambiguous = true;
        for (var hop = 0; hop < MaxRekeyHops; hop++)
        {
            var rekey = await SyncEngine.ReceiveRekeyAsync(transport, old, chain, cancellationToken).ConfigureAwait(false);
            if (rekey is null || rekey.Conflict) return (rekey, consumed, unambiguous);
            var exists = false;
            try
            {
                consumed.AddRange(rekey.Paths);
                unambiguous &= SyncEngine.RekeyIsUnambiguous(old, rekey.From);
                exists = await transport.ReadAsync(SyncPaths.Keys(rekey.Secret!.ChainId), cancellationToken).ConfigureAwait(false) is not null;
                if (exists) return (rekey, consumed, unambiguous);
                // Esta conta nova também já foi substituída: a mensagem seguinte está dentro dela.
                chain = rekey.Secret.ChainId;
            }
            finally
            {
                if (!exists) rekey.Dispose();
            }
        }
        return (null, consumed, unambiguous);
    }

    // Entra na conta nova de uma mensagem r/ (inequívoca, ou confirmada pelo identificador do kit), guarda-a e faz uma
    // ronda. Uma ChainGoneException dessa ronda não para o aparelho: a conta nova já ficou guardada, e ou já tem outra
    // mensagem r/ para este aparelho (duas trocas seguidas, a segunda ainda por acabar, com o registo de chaves do meio
    // presente) ou desapareceu entretanto. Conta como uma ronda por acabar: a seguinte retoma na conta guardada e trata
    // disso pelo caminho normal (ChainGoneAsync, já com o estado novo, onde as chaves do removido não entraram).
    private async Task AdoptRekeyAsync(SavedSync saved, SyncState old, RekeyResult rekey, List<string> consumed, CancellationToken cancellationToken)
    {
        var transport = Transport();
        SyncEngine engine;
        try
        {
            engine = await SyncEngine.JoinAsync(transport, rekey.Secret!, old.Device, _now, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ChainGoneException)
        {
            // A conta mudou entre a leitura e a entrada: nada foi gravado, e a tentativa seguinte repete (passageira).
            throw new IOException("A conta mudou outra vez durante a troca de chave.");
        }
        engine.AdoptDeviceKeys(old, rekey.Devices);
        engine.RestageOwn(old);
        var rekeyed = Snapshot(engine);
        lock (_gate)
        {
            if (!IsCurrent(saved)) return;
            _engine = engine;
            saved.Secret = Kdf.Base64Url(rekey.Secret!.ToBytes());
            // Este computador recebeu a cadeia nova: o kit que tinha guardado deixou de valer.
            KitFor(saved, rekey.Secret!);
            saved.HadAccount = true;
            saved.Engine = rekeyed;
            saved.Rotation = null;
            saved.CreatedMs = 0;
            // Os dados deste computador não mudaram: a base continua a valer, e o que mudou desde a última
            // ronda segue para a conta nova; o que este aparelho tinha escrito volta a seguir com a hora
            // original (RestageOwn), para não ser desfeito pelo instantâneo de quem trocou a chave.
            saved.LastCompactMs = 0;
            saved.LastCompactSeq = 0;
            _store.Save(saved);
            // O nome que este aparelho já conhecia do remetente, não o que vem na mensagem (quem a escreve põe lá o
            // que quiser).
            _notice = DeviceNameText.Clean(old.DeviceNames.GetValueOrDefault(rekey.From)) is { } deNome
                ? $"A conta mudou de chave em «{deNome}» (um aparelho foi removido, ou há um kit de recuperação novo). Este computador continua ligado."
                : "A conta mudou de chave (um aparelho foi removido, ou há um kit de recuperação novo). Este computador continua ligado.";
        }
        // Só agora, com a conta nova guardada, as mensagens saem do transporte (melhor esforço).
        try { await SyncEngine.ForgetRekeysAsync(transport, consumed, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (IsTransient(e)) { }
        try { await CycleAsync(saved, cancellationToken).ConfigureAwait(false); }
        catch (ChainGoneException)
        {
            lock (_gate)
                if (IsCurrent(saved) && _phase == SyncPhase.Starting) _phase = SyncPhase.On;
        }
    }

    // A pausa da troca de chave (Status.RekeyPending): o utilizador escreveu o identificador do kit novo, lido nas
    // Definições do computador onde fez a alteração. O identificador deriva do segredo da conta nova e não está no
    // transporte: um aparelho removido não o conhece, e a conta que ele deixar tem outro. Certo, este aparelho entra
    // na conta nova; errado PairingCode.MaxWrong vezes, para de vez (junta-se outra vez pelo kit ou por aprovação).
    public async Task ConfirmRekeyAsync(string kitId, CancellationToken cancellationToken = default)
    {
        var typed = RecoveryCode.Normalize(kitId ?? string.Empty);
        if (typed.Length != 4) throw new SyncUserException(RekeyIdFormatText);
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunning(true);
        SavedSync? saved = null;
        // A memória já foi mexida para religar (o identificador estava certo). Antes disso a pausa está intacta — ou
        // deu lugar a outra paragem («Não tenho o identificador», «Sair») — e uma falha não tem nada a repor.
        var armed = false;
        try
        {
            SyncState old;
            lock (_gate)
            {
                saved = _saved;
                if (!RekeyHeld(saved)) throw new SyncUserException(NoRekeyText);
                old = saved!.Engine!;
            }
            var (found, consumed, _) = await FindRekeyAsync(Transport(), old, cancellationToken).ConfigureAwait(false);
            using var rekey = found;
            // «Desistir» (AbandonRekey) ou «Sair» enquanto se lia o transporte: já não há pausa para confirmar.
            lock (_gate)
                if (!RekeyHeld(saved)) throw new SyncUserException(NoRekeyText);
            if (rekey is null)
            {
                Stop(saved, GoneText, StopKeeps.Secret);
                throw new SyncUserException(GoneText);
            }
            if (rekey.Conflict)
            {
                Stop(saved, ConflictText, StopKeeps.Secret);
                throw new SyncUserException(ConflictText);
            }
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(rekey.Secret!.KitId), System.Text.Encoding.ASCII.GetBytes(typed)))
            {
                bool refused;
                lock (_gate)
                {
                    refused = ++_rekeyWrong >= PairingCode.MaxWrong;
                    if (refused) _rekeyWrong = 0;
                }
                if (!refused) throw new SyncUserException(RekeyWrongText);
                Stop(saved, RekeyRefusedText, StopKeeps.Secret);
                throw new SyncUserException(RekeyRefusedText);
            }
            lock (_gate)
            {
                if (!RekeyHeld(saved)) throw new SyncUserException(NoRekeyText);
                // Sem gravar: só AdoptRekeyAsync grava, já com a conta nova. Se o Éteris fechar antes, ao reabrir a
                // pausa continua.
                saved.Enabled = true;
                saved.Stopped = null;
                _phase = SyncPhase.Starting;
                _notice = null;
                _error = null;
                _rekeyWrong = 0;
                armed = true;
            }
            await AdoptRekeyAsync(saved, old, rekey, consumed, cancellationToken).ConfigureAwait(false);
        }
        catch (ChainGoneException)
        {
            // Só de antes de religar (a ronda de AdoptRekeyAsync já não a deixa sair): a conta desapareceu a meio da procura.
            Stop(saved!, GoneText, StopKeeps.Secret);
            throw new SyncUserException(GoneText);
        }
        catch (Exception e) when (IsTransient(e))
        {
            // Antes de religar: a pausa (ou a paragem que a substituiu) fica como está. Depois, com a conta nova ainda
            // por guardar: a pausa volta a ficar como estava. Já guardada: é uma ronda que falhou, e a seguinte repete.
            if (armed && !HoldAgain(saved!))
            {
                bool live;
                lock (_gate) live = _engine is not null;
                if (live) Fail(e);
            }
            throw new SyncUserException(Message(e));
        }
        catch (Exception) when (armed && HoldAgain(saved!))
        {
            // Uma falha que não é passageira (um cancelamento, um registo de chaves que não abre) depois de religar e
            // antes de a conta nova ficar guardada: a pausa volta, em vez de a memória ficar «a ligar» sem conta.
            throw;
        }
        finally
        {
            EndRun();
        }
        Notify();
    }

    // A confirmação religou a memória e falhou antes de a conta nova ficar guardada: repõe a pausa da troca de chave e
    // devolve true. Devolve false sem mexer em nada quando a conta nova já ficou guardada (há motor) ou quando o estado
    // já não é o desta confirmação («Sair» ou um «apagar» entretanto: o que eles deixaram fica).
    private bool HoldAgain(SavedSync saved)
    {
        lock (_gate)
            if (_engine is not null || !IsCurrent(saved)) return false;
        Stop(saved, RekeyHeldText, StopKeeps.Everything);
        return true;
    }

    // Desiste da pausa da troca de chave: passa a valer o alerta «conta substituída» (Status.ChainReplaced) e este
    // computador junta-se outra vez por aprovação ou pelo kit. Um ConfirmRekeyAsync a correr deixa de gravar (IsCurrent).
    public void AbandonRekey()
    {
        SavedSync? saved;
        lock (_gate)
        {
            saved = _saved;
            if (!RekeyHeld(saved)) return;
            _rekeyWrong = 0;
        }
        Stop(saved!, GoneText, StopKeeps.Secret);
    }

    // Chamado dentro do _gate: este aparelho está na pausa da troca de chave (com o estado e a base guardados).
    private bool RekeyHeld(SavedSync? saved)
        => saved is { Engine: not null } && ReferenceEquals(_saved, saved) && _phase == SyncPhase.Stopped && saved.Stopped == RekeyHeldText;

    private enum StopKeeps
    {
        Nothing,    // os dados foram apagados por este aparelho: a chave já não serve para nada
        Secret,     // a conta desapareceu: guarda a chave (outra sessão Google, ou para entrar outra vez sem código)
        Everything, // pausa (outra conta, registo de chaves que não abre): nada deste computador muda
    }

    private void Stop(SavedSync saved, string reason, StopKeeps keeps)
    {
        GuestFlow? guest;
        List<HostFlow> hosts;
        lock (_gate)
        {
            // Um «apagar» começou entretanto: o aviso dele fica (esta ronda já não é a deste aparelho).
            if (!ReferenceEquals(_saved, saved) || saved.DeletePending) return;
            // Com a sincronização parada, nenhum pedido de aprovação continua a sondar o transporte (nem o deste
            // aparelho, nem os dos computadores novos a que respondeu), como em «Sair».
            guest = _guest;
            _guest = null;
            hosts = _hosts.Values.ToList();
            _hosts.Clear();
            _waiting = 0;
            _engine = null;
            saved.Enabled = false;
            if (keeps != StopKeeps.Everything)
            {
                saved.Engine = null;
                saved.Baseline = null;
                saved.Joined = null;
                saved.Rotation = null;
                saved.CreatedMs = 0;
            }
            if (keeps == StopKeeps.Nothing)
            {
                saved.HadAccount = true;
                saved.Secret = null;
                saved.Kit = null;
                saved.CodePending = false;
                ResetAccountChoice(saved);
                saved.InheritPending = false;
            }
            saved.Stopped = reason;
            _store.Save(saved);
            _phase = SyncPhase.Stopped;
            _justJoined = false;
            _entry = SyncEntry.None;
            _inheritedNow = false;
            _accountBy = null;
            _accountSince = 0;
            _notice = reason;
            _devices = [];
        }
        guest?.Cancel.Cancel();
        foreach (var host in hosts) host.Cancel.Cancel();
        Notify();
    }

    private const string GoneText =
        "A sincronização parou: a conta de sincronização deste computador já não está neste Google Drive — os dados foram apagados, este computador foi removido noutro aparelho, ou a sessão é de outra conta Google. A chave ficou neste computador; nada daqui foi apagado.";

    private const string ConflictText =
        "A sincronização parou: dois aparelhos deixaram a este computador contas novas que não batem certo (um deles pode ter sido removido da conta). Para continuar, junte este computador outra vez à conta, com o kit de recuperação novo ou por aprovação noutro computador.";

    public const string RekeyHeldText =
        "A sincronização está em pausa: a conta mudou de chave noutro computador (um aparelho foi removido, ou há um kit de recuperação novo). Para este computador continuar, escreva aqui o identificador do kit novo: os 4 caracteres de «O seu kit atual termina em…», nas Definições › Sincronização do computador onde fez a alteração. Nada foi apagado.";

    private const string RekeyWrongText =
        "Esse identificador não é o da conta nova deixada a este computador. Confirme os 4 caracteres no outro computador e escreva-os outra vez.";

    private const string RekeyIdFormatText = "Escreva os 4 caracteres do identificador do kit.";

    private const string RekeyRefusedText =
        "A sincronização parou: a conta nova deixada a este computador não é a do identificador que escreveu (pode ter sido deixada por um aparelho removido). Junte este computador outra vez à conta, com o kit de recuperação novo ou por aprovação noutro computador. A chave ficou neste computador; nada daqui foi apagado.";

    private const string NoRekeyText = "Não há nenhuma troca de chave por confirmar.";

    private const string WrongKeysText =
        "A sincronização parou: o registo de chaves da conta no Google Drive não abre com a chave guardada neste computador (foi alterado ou danificou-se). A chave ficou neste computador; nada foi apagado.";

    private const string OtherAccountText =
        "A sincronização está em pausa: a sessão é de outra conta Google, diferente da que este computador usa para sincronizar. Nada foi apagado. Para continuar com a conta anterior, saia e entre com ela; para sincronizar com esta, carregue em «Ligar outra vez» (para voltar depois à anterior será preciso o kit de recuperação dela ou outro computador).";

    // ---- entrar num aparelho novo -----------------------------------------------------------------------------------

    public async Task JoinWithCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        AccountSecret account;
        try { account = AccountSecret.FromCode(code); }
        catch (FormatException e) { throw new SyncUserException(e.Message); }
        using (account)
        {
            await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
            SetRunning(true);
            try
            {
                // «Apagar…» ou «Sair» enquanto se entrava: nada foi gravado e não há ronda.
                if (!await JoinCoreAsync(account, null, fresh: true, cancellationToken).ConfigureAwait(false)) return;
                SavedSync saved;
                lock (_gate)
                {
                    saved = _saved!;
                    // Entrou com o kit: quem o escreveu tem-no.
                    if (IsCurrent(saved) && saved.Kit is { } kit)
                    {
                        kit.SavedId = kit.ForId;
                        _store.Save(saved);
                    }
                }
                await CycleAsync(saved, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WrongCodeException or ChainGoneException)
            {
                throw new SyncUserException("Este kit não abre a conta do Éteris neste Google Drive. Confirme as 24 palavras (ou o código de 14 grupos de 4).");
            }
            catch (Exception e) when (IsTransient(e))
            {
                Fail(e);
                throw new SyncUserException(Message(e));
            }
            finally
            {
                EndRun();
            }
        }
    }

    // Entra na conta e grava-a. Devolve false (sem gravar nada) se, enquanto se lia a conta, começou um «apagar» ou o
    // utilizador saiu: como nas rondas (IsCurrent), nada grava depois de BeginCloudDeletion ou de «Sair».
    // «fresh»: este aparelho entra agora na conta (aprovação ou código de recuperação), não volta com a chave guardada:
    // até ao fim da primeira ronda o estado diz JoinedNow.
    private async Task<bool> JoinCoreAsync(AccountSecret account, string? from, bool fresh, CancellationToken cancellationToken)
    {
        EnsureReadable();
        SavedSync? before;
        bool wasOn, hadBefore;
        lock (_gate)
        {
            before = _saved;
            wasOn = before?.Enabled == true;
            // Já teve estado de sincronização (chave, conta, ou a marca de já ter tido): lido AQUI, antes de o 2.º cadeado
            // gravar a chave nova e a marca no mesmo objeto. Só um aparelho sem estado herda a escolha da conta.
            hadBefore = !MayAutoRequest(before);
        }
        var engine = await SyncEngine.JoinAsync(Transport(), account, DeviceIdentity.Create(DeviceName), _now, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var snapshot = Snapshot(engine);
        lock (_gate)
        {
            if (!ReferenceEquals(_saved, before) || before is { DeletePending: true } || (wasOn && before is { Enabled: false })) return false;
            _engine = engine;
            _saved ??= new SavedSync();
            _saved.Enabled = true;
            _saved.Stopped = null;
            _saved.Secret = Kdf.Base64Url(account.ToBytes());
            // A mesma chave (reentrada): nada muda; outra conta: o kit é outro (deixou de valer, ou por guardar).
            KitFor(_saved, account);
            _saved.HadAccount = true;
            _saved.Engine = snapshot;
            _saved.Baseline = null; // primeira vez: junta sem perder nada (ProfileSync)
            _saved.Joined = null;
            _saved.CodePending = false;
            _saved.LastCompactMs = 0;
            _saved.LastCompactSeq = 0;
            _saved.Rotation = null;
            _saved.CreatedMs = 0;
            _saved.Account = AccountKey?.Invoke() ?? _saved.Account;
            // A escolha da conta lê-se outra vez no fim da primeira ronda. Ao voltar com a chave guardada, uma escolha
            // feita aqui e ainda por escrever continua por escrever (é a mesma conta).
            var pendingChoice = fresh ? null : _saved.AccountChoiceOut;
            ResetAccountChoice(_saved);
            _saved.AccountChoiceOut = pendingChoice;
            _saved.AccountSecrets = pendingChoice == "on";
            if (fresh)
            {
                // Computador novo: herda na primeira ronda. Já esteve numa conta (cadeia substituída e «Fui eu», kit novo
                // sem ter apagado o estado, removido e aprovado de novo): não herda; recebe a oferta única.
                _saved.InheritPending = !hadBefore;
                _saved.OfferAnswered = false;
            }
            _entry = fresh ? SyncEntry.Joined : SyncEntry.None;
            _inheritedNow = false;
            _accountBy = null;
            _accountSince = 0;
            _store.Save(_saved);
            _phase = SyncPhase.On;
            _guest = null;
            if (fresh) _justJoined = true;
            _notice = from is null ? null : $"Este computador entrou na conta, aprovado em «{DeviceNameText.Shown(from)}».";
        }
        // A interface desenha «entrou na conta» já, antes de a primeira ronda acabar.
        if (fresh) Notify();
        return true;
    }

    // «Aprovar noutro computador»: abre o pedido, espera que um aparelho ligado responda, mostra o código de
    // verificação e, depois de o utilizador confirmar aqui (ConfirmPairing) e lá, recebe a conta e junta os dados.
    // Enquanto nenhum aparelho responder, o pedido renova-se sozinho (sessão nova) só com o ecrã à vista e dentro dos
    // limites de PairingRenewal; depois de um responder, a confirmação tem o prazo da sessão (PairingProtocol.Lifetime).
    public void RequestPairing()
    {
        GuestFlow flow;
        var alive = false;
        lock (_gate)
        {
            if (_guest is { Error: null })
            {
                // Já há um pedido vivo: não se abre outro, mas fica à vista (uma ronda pode ter mudado a fase).
                alive = true;
                flow = _guest;
                if (_phase == SyncPhase.Choose) _phase = SyncPhase.Pairing;
            }
            else
            {
                _guest = flow = new GuestFlow { FirstRequestMs = _now() };
                _phase = SyncPhase.Pairing;
                _error = null;
            }
        }
        Notify();
        if (alive) return;
        _ = Task.Run(async () =>
        {
            string? session = null;
            var transport = Transport();
            try
            {
                using var guest = await WaitForHostAsync(transport, flow).ConfigureAwait(false);
                session = flow.Session;
                lock (_gate) flow.HostAnswered = true;
                Notify();
                var code = await guest.WaitForCodeAsync(flow.Cancel.Token).ConfigureAwait(false);
                lock (_gate) flow.Code = code;
                Notify();
                // À espera da confirmação do utilizador aqui; entretanto, o outro aparelho pode recusar, e a sessão acaba.
                while (!flow.Confirm.Task.IsCompleted)
                {
                    bool refused;
                    try { refused = await PairingRequests.IsRefusedAsync(transport, session!, flow.Cancel.Token).ConfigureAwait(false); }
                    // O transporte falhou de passagem: o código continua à vista e pergunta-se outra vez no intervalo seguinte.
                    catch (Exception e) when (PairingProtocol.IsTransportFailure(e, flow.Cancel.Token)) { refused = false; }
                    if (refused)
                        throw new PairingException("O outro aparelho recusou o pedido.") { Failure = PairingFailure.Refused };
                    if (_now() - flow.SessionStartMs > (long)PairingProtocol.Lifetime.TotalMilliseconds)
                        throw new PairingException("O emparelhamento expirou. Comece de novo.") { Failure = PairingFailure.TimedOut };
                    await Task.WhenAny(flow.Confirm.Task, (_delay ?? Task.Delay)(PairingPoll, flow.Cancel.Token)).ConfigureAwait(false);
                    flow.Cancel.Token.ThrowIfCancellationRequested();
                }
                guest.ConfirmCode();
                using var account = await guest.WaitForSecretAsync(flow.Cancel.Token).ConfigureAwait(false);
                // A conta chegou: a sessão acabou bem (daqui em diante nunca se escreve a recusa). Os ficheiros dela só
                // saem depois de entrar, em melhor esforço: uma falha a limpar não deita fora a conta que o outro
                // computador acabou de entregar (e de dizer «foi aprovado»).
                var delivered = session!;
                session = null;
                await _run.WaitAsync(flow.Cancel.Token).ConfigureAwait(false);
                SetRunning(true);
                bool joined = false, synced = false;
                try
                {
                    joined = await JoinDeliveredAsync(account, guest.HostName, flow.Cancel.Token).ConfigureAwait(false);
                    if (joined)
                    {
                        SavedSync saved;
                        lock (_gate) saved = _saved!;
                        await CycleAsync(saved, flow.Cancel.Token).ConfigureAwait(false);
                        synced = true;
                    }
                }
                // Já entrou (o pedido acabou e deixou de ser o deste aparelho): uma primeira ronda que falha é uma ronda
                // falhada como as outras — fica no estado (o cartão deixa de dizer «entrou na conta. A sincronizar…») e
                // o anfitrião marca a tentativa seguinte (RoundEnded), que trata também de uma conta que mudou de chave.
                catch (Exception e) when (joined && (IsTransient(e) || e is ChainGoneException))
                {
                    Fail(e);
                }
                finally
                {
                    EndRun();
                    if (joined && !flow.Cancel.IsCancellationRequested) EndedRound(synced);
                    // Não depois de «Sair» nem de «Apagar…» (o pedido parado): nenhum pedido novo ao transporte.
                    if (!flow.Cancel.IsCancellationRequested)
                    {
                        try { await PairingRequests.CleanUpAsync(transport, delivered, flow.Cancel.Token).ConfigureAwait(false); }
                        catch (Exception x) when (IsTransient(x) || x is OperationCanceledException) { }
                    }
                }
            }
            // O pedido foi parado («Sair», apagar, cancelar): nada a dizer. O limite de tempo do cliente HTTP também é uma
            // OperationCanceledException, mas com o pedido vivo: essa cai em baixo, como falha do transporte.
            catch (OperationCanceledException) when (flow.Cancel.IsCancellationRequested) { }
            catch (Exception e)
            {
                var transportFailure = PairingProtocol.IsTransportFailure(e, flow.Cancel.Token);
                var known = e is PairingException or WrongCodeException or ChainGoneException || IsTransient(e) || transportFailure;
                if (!known) Log?.Invoke($"O pedido de aprovação acabou com uma falha inesperada ({e.GetType().Name}).");
                lock (_gate)
                {
                    // Nunca a mensagem crua de uma exceção que ninguém esperava.
                    flow.Error = e is PairingException ? e.Message : known ? Message(e) : UnexpectedPairingText;
                    flow.End = e switch
                    {
                        // O prazo: antes de alguém responder (a renovação parou), ou depois (nunca renova).
                        PairingException { Failure: PairingFailure.TimedOut } => flow.HostAnswered ? GuestEnd.TimeUp : GuestEnd.StillWaiting,
                        PairingException { Failure: PairingFailure.Refused } => GuestEnd.Refused,
                        _ => GuestEnd.Failed,
                    };
                    // Só o pedido em curso mexe na fase: um que já foi cancelado ou trocado por outro não esconde o novo.
                    if (ReferenceEquals(_guest, flow) && _phase == SyncPhase.Pairing) _phase = SyncPhase.Choose;
                }
                // A sessão em curso (ainda não entregue) fica recusada: o outro aparelho deixa de esperar. Não quando a
                // causa é o transporte: a escrita falhava também, e uma recusa conta lá como um pedido mau.
                if (!transportFailure && (session ?? (flow.HostAnswered ? null : flow.Session)) is { } open)
                {
                    try { await PairingRequests.RefuseAsync(transport, open, _now, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception x) when (IsTransient(x)) { }
                }
            }
            Notify();
        });
    }

    // Nunca deixa sair uma exceção do anfitrião para a tarefa do pedido.
    private void EndedRound(bool ok)
    {
        try { RoundEnded?.Invoke(ok); }
        catch (Exception e) { Log?.Invoke($"O aviso do fim da ronda falhou ({e.GetType().Name})."); }
    }

    private const string UnexpectedPairingText = "Não foi possível juntar este computador (erro inesperado). Peça outra vez.";

    // A entrada com a conta que o outro computador acabou de entregar: ler o registo de chaves pode falhar de passagem,
    // e a conta só existe aqui em memória — repete-se como as escritas dos passos, em vez de a deitar fora.
    private async Task<bool> JoinDeliveredAsync(AccountSecret account, string? from, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await JoinCoreAsync(account, from, fresh: true, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (attempt < PairingProtocol.WriteAttempts && PairingProtocol.IsTransportFailure(e, cancellationToken))
            {
                await (_delay ?? Task.Delay)(PairingPoll, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Abre o pedido e espera que um aparelho ligado responda. Se ninguém responder no prazo e a renovação ainda valer
    // (PairingRenewal: ninguém respondeu, ecrã à vista, meia hora, duas vezes), a sessão velha fica recusada (um aparelho
    // que tenha respondido no último instante deixa de esperar) e abre-se outra no mesmo pedido, sem erro à vista.
    private async Task<PairingGuest> WaitForHostAsync(ISyncTransport transport, GuestFlow flow)
    {
        while (true)
        {
            flow.SessionStartMs = _now();
            var session = await PairingRequests.OpenAsync(transport, _now, flow.Cancel.Token, _delay, PairingPoll).ConfigureAwait(false);
            flow.Session = session;
            try
            {
                return await PairingRequests.WaitForHostAsync(transport, session, DeviceName, _now, _delay, PairingPoll, flow.Cancel.Token)
                    .ConfigureAwait(false);
            }
            catch (PairingException e) when (e.Failure == PairingFailure.TimedOut
                && PairingRenewal.MayRenew(hostAnswered: false, PairingScreenVisible?.Invoke() == true, flow.FirstRequestMs, _now(), flow.Renewals))
            {
                try { await PairingRequests.RefuseAsync(transport, session, _now, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception x) when (IsTransient(x)) { }
                lock (_gate) flow.Renewals++;
            }
        }
    }

    // O utilizador confirmou NESTE aparelho que o código é igual ao do outro.
    public void ConfirmPairing()
    {
        lock (_gate)
        {
            if (_guest?.Code is null) return;
            _guest.Confirm.TrySetResult();
        }
        Notify();
    }

    public void CancelPairing()
    {
        GuestFlow? flow;
        lock (_gate)
        {
            flow = _guest;
            _guest = null;
            if (_phase == SyncPhase.Pairing) _phase = SyncPhase.Choose;
        }
        if (flow is null) return;
        flow.Cancel.Cancel();
        if (flow.Session is { } session)
        {
            var transport = Transport();
            // O outro aparelho deixa de esperar (vê a recusa); os ficheiros saem na limpeza dos pedidos abandonados.
            _ = Task.Run(async () =>
            {
                try { await PairingRequests.RefuseAsync(transport, session, _now).ConfigureAwait(false); }
                catch (Exception e) when (IsTransient(e)) { }
            });
        }
        Notify();
    }

    // ---- aprovar um aparelho novo -----------------------------------------------------------------------------------

    // Pedidos de aparelhos novos, por ação do utilizador (abriu o menu ou a secção Sincronização, carregou em «Ver» na
    // barra, ou voltou à janela): responde a cada um e, quando o aparelho novo revela o código de verificação, o pedido
    // aparece em Status.Requests, à espera de que a pessoa escreva aqui esse código. Responder a um pedido
    // é acompanhá-lo (lê o Drive de 2 em 2 s até o aparelho novo responder, no máximo 10 minutos).
    public async Task CheckRequestsAsync(CancellationToken cancellationToken = default)
    {
        // Com a autorização da Google por renovar (NeedsSignIn) nada se procura: cada tentativa era mais um pedido que
        // não pode correr bem, e a pausa só acaba quando o utilizador entra outra vez.
        if (!Enabled || _engine is null || SignInNeeded) return;
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AnswerRequestsCoreAsync(answer: true, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (IsTransient(e)) { }
        finally { _run.Release(); }
        Notify();
    }

    private async Task AnswerRequestsCoreAsync(bool answer, CancellationToken cancellationToken)
    {
        if (_engine is null) return;
        var transport = Transport();
        var waiting = 0;
        foreach (var session in await PairingRequests.PendingAsync(transport, _now, cancellationToken).ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (_ended.ContainsKey(session))
                {
                    _ended[session] = _now();
                    continue;
                }
                if (_hosts.ContainsKey(session) || _guest?.Session == session) continue;
            }
            if (!answer)
            {
                waiting++;
                continue;
            }
            // O teto (PairingRequests.MaxAnswered): sem vaga, o pedido fica só contado e é respondido numa procura
            // seguinte, quando um dos que este aparelho acompanha acabar.
            int following;
            lock (_gate) following = _hosts.Values.Count(h => _now() - h.Created < (long)PairingProtocol.Lifetime.TotalMilliseconds);
            if (following >= PairingRequests.MaxAnswered)
            {
                waiting++;
                continue;
            }
            var host = await PairingHost.AnswerAsync(transport, session, _now, _delay, cancellationToken).ConfigureAwait(false);
            var flow = new HostFlow(session, host, _now());
            lock (_gate) _hosts[session] = flow;
            _ = Task.Run(async () =>
            {
                try
                {
                    var code = await host.WaitForGuestAsync(flow.Cancel.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        flow.Name = host.GuestName;
                        flow.Code = code;
                    }
                    Notify();
                    // O pedido está pronto, à espera de que a pessoa escreva o código. Continua a ser acompanhado, ao
                    // ritmo de sempre, até ser aprovado, recusado ou expirar: o computador novo pode desistir («Não», o
                    // tempo dele acabou, pediu outra vez) e esse pedido tem de sair da barra sem gesto nenhum — senão
                    // o código do pedido seguinte era comparado com o deste, e três erros contavam como um pedido mau.
                    await FollowReadyRequestAsync(transport, host, flow).ConfigureAwait(false);
                }
                // Qualquer exceção acaba aqui (nenhuma fica por observar com o fluxo «a responder» para sempre). As
                // falhas passageiras do transporte já se repetem dentro da espera; só chegam cá as que não passaram.
                catch (Exception e)
                {
                    // Ainda era deste aparelho (não saiu por «Sair», apagar, «Não fui eu» ou pelo prazo, que já tratam
                    // de si). A sessão fica nas acabadas: o 1-host já existe, por isso o pedido não volta a ser listado
                    // e nenhum fluxo novo a retomava.
                    //
                    // O pedido acabou do outro lado depois de este aparelho responder: recusa ou cancelamento escrito
                    // por quem pede (x-host), mensagens que não conferem (incluindo uma chave ou um ficheiro ilegível,
                    // FormatException/CryptographicException, e um compromisso dirigido a outro anfitrião), ou o prazo.
                    // Tudo isto quem escreve no Drive consegue provocar, por isso conta para os pedidos em série (dois
                    // aparelhos desta conta a responder ao mesmo pedido no mesmo instante custa, raramente, um); uma
                    // falha que ninguém esperava conta também (na dúvida, foi o que estava no Drive). Não contam: parar
                    // deste lado (cancelado) e um erro de rede, da sessão da Google ou da pasta.
                    var passes = e is OperationCanceledException || (IsTransient(e) && e is not (FormatException or CryptographicException));
                    bool mine, approving;
                    lock (_gate)
                    {
                        mine = _hosts.TryGetValue(session, out var current) && ReferenceEquals(current, flow);
                        approving = flow.Approving;
                        if (mine)
                        {
                            // Sair da lista e contar é um só passo (quem lê o estado nunca vê um sem o outro).
                            _hosts.Remove(session);
                            _ended[session] = _now();
                            if (!passes) CountBadPairing(flow);
                        }
                    }
                    // Uma aprovação ainda a meio (a entrega) usa a chave da sessão: quem tirou o fluxo trata dela.
                    if (mine || !approving) host.Dispose();
                }
                Notify();
            });
        }
        List<HostFlow> stale;
        lock (_gate)
        {
            _waiting = waiting;
            stale = _hosts.Values.Where(h => _now() - h.Created > (long)PairingProtocol.Lifetime.TotalMilliseconds).ToList();
            foreach (var flow in stale)
            {
                _hosts.Remove(flow.Session);
                _ended[flow.Session] = _now();
            }
            var forget = (long)(PairingProtocol.Lifetime + PairingProtocol.ClockSkew).TotalMilliseconds;
            foreach (var (session, last) in _ended.ToList())
                if (_now() - last > forget) _ended.Remove(session);
        }
        foreach (var flow in stale)
        {
            flow.Cancel.Cancel();
            // Passou o prazo sem aprovação depois de este aparelho responder: acabou mal.
            if (!flow.Approving) NoteBadPairing(flow);
        }
    }

    // Um pedido pronto continua a ser acompanhado: lê a recusa do outro lado (x-host) de PollInterval em PollInterval
    // (2 s) até o pedido acabar. Sai por exceção: o pedido cancelado do outro lado ou o prazo (PairingException: o fluxo
    // sai e conta), ou parado deste lado (cancelado: aprovado, «Não fui eu», três erros, «Sair»). Uma aprovação a meio
    // (porta do Windows ou entrega) fica com o pedido: a entrega confere a recusa antes de escrever. As falhas
    // passageiras do transporte não acabam com nada.
    private async Task FollowReadyRequestAsync(ISyncTransport transport, PairingHost host, HostFlow flow)
    {
        var lifetime = (long)PairingProtocol.Lifetime.TotalMilliseconds;
        while (true)
        {
            bool withdrawn;
            try { withdrawn = await PairingRequests.IsRefusedAsync(transport, flow.Session, flow.Cancel.Token).ConfigureAwait(false); }
            catch (Exception e) when (PairingProtocol.IsTransportFailure(e, flow.Cancel.Token)) { withdrawn = false; }
            bool approving;
            lock (_gate) approving = flow.Approving;
            if (!approving)
            {
                if (withdrawn) throw new PairingException("O outro aparelho cancelou o pedido.") { Failure = PairingFailure.Refused };
                if (_now() - flow.Created >= lifetime)
                    throw new PairingException("O emparelhamento expirou. Comece de novo.") { Failure = PairingFailure.TimedOut };
            }
            await (_delay ?? Task.Delay)(host.PollInterval, flow.Cancel.Token).ConfigureAwait(false);
        }
    }

    // Um pedido a que este aparelho respondeu acabou mal (PairingFatigue). Cada sessão conta uma vez. Grava (só horas).
    private void NoteBadPairing(HostFlow flow)
    {
        lock (_gate) CountBadPairing(flow);
        Notify();
    }

    // Chamar dentro do _gate; quem chama avisa (Notify) depois de o largar.
    private void CountBadPairing(HostFlow flow)
    {
        _ended[flow.Session] = _now();
        if (flow.CountedBad || _saved is not { } saved) return;
        flow.CountedBad = true;
        saved.BadPairings = PairingFatigue.Add(saved.BadPairings, _now());
        _store.Save(saved);
    }

    // «Mostrar o pedido na barra» (um gesto nas Definições, com a barra calada pelos pedidos em série): a contagem
    // recomeça e a barra volta a abrir sozinha.
    public void ResumePairingBar()
    {
        lock (_gate)
        {
            if (_saved is not { BadPairings: not null } saved) return;
            saved.BadPairings = null;
            _store.Save(saved);
        }
        Notify();
    }

    // A pessoa escreveu NESTE aparelho o código que o aparelho novo mostra («number matching»): este aparelho nunca o
    // mostra, compara-o em memória com o que calculou. Certo → a porta do Windows (gate, quando a conta sincroniza
    // segredos; o anfitrião passa-a sempre) e só depois a entrega da conta, cifrada só para o aparelho novo. Três
    // códigos errados recusam o pedido. «gate» é opcional só para os testes do agente.
    public async Task<PairingAnswer> ApproveWithCodeAsync(string session, string typedCode, Func<Task<bool>>? gate = null,
        CancellationToken cancellationToken = default)
    {
        HostFlow? flow;
        string secret;
        bool right, refuse = false;
        lock (_gate)
        {
            _hosts.TryGetValue(session, out flow);
            // Só com a sincronização ligada: parada ou em pausa, a chave guardada não se entrega a ninguém.
            if (flow?.Code is not { } expected || _saved is not { Enabled: true, Secret: { } saved }
                || _now() - flow.Created >= (long)PairingProtocol.Lifetime.TotalMilliseconds)
                return PairingAnswer.Gone;
            // Um 2.º Enter enquanto o 1.º ainda está na porta do Windows ou na rede não volta a entregar a conta.
            if (flow.Approving) return PairingAnswer.Busy;
            if (PairingCode.Normalize(typedCode) is null) return PairingAnswer.Incomplete;
            secret = saved;
            right = PairingCode.Matches(expected, typedCode);
            if (right) flow.Approving = true;
            else if (++flow.Wrong >= PairingCode.MaxWrong)
            {
                _hosts.Remove(session);
                refuse = true;
            }
        }
        if (!right && !refuse)
        {
            Notify();
            return PairingAnswer.WrongCode;
        }
        if (refuse)
        {
            // Como «Não fui eu»: o outro lado fica a saber (x-host) e o pedido não volta a ser listado.
            flow.Cancel.Cancel();
            try { await flow.Host.RefuseAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (IsTransient(e)) { }
            flow.Host.Dispose();
            NoteBadPairing(flow);
            Notify();
            return PairingAnswer.Refused;
        }
        bool passed;
        try { passed = gate is null || await gate().ConfigureAwait(false); }
        catch
        {
            lock (_gate) flow.Approving = false;
            throw;
        }
        lock (_gate)
        {
            if (!passed)
            {
                flow.Approving = false;
                return PairingAnswer.NotConfirmed;
            }
            // Enquanto a porta do Windows esperava, o pedido pode ter saído («Sair», apagar, expirou): nada se entrega.
            if (!_hosts.TryGetValue(session, out var still) || !ReferenceEquals(still, flow) || flow.Cancel.IsCancellationRequested)
                return PairingAnswer.Gone;
        }
        return await ApproveAsync(flow, secret, cancellationToken).ConfigureAwait(false) ? PairingAnswer.Approved : PairingAnswer.Gone;
    }

    private const string NotDeliveredText = "Não foi possível entregar a conta ao computador novo. Tente outra vez.";

    // A entrega (passo 5). Privada: nenhuma rota nem interface entrega a conta sem o código escrito. Devolve false (sem
    // entregar nada) se o computador novo desistiu entretanto: o pedido sai e conta como os outros que acabam mal.
    private async Task<bool> ApproveAsync(HostFlow flow, string secret, CancellationToken cancellationToken)
    {
        var session = flow.Session;
        try
        {
            using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(secret));
            await flow.Host.ApproveAsync(account, DeviceName, cancellationToken).ConfigureAwait(false);
        }
        catch (PairingException e) when (e.Failure == PairingFailure.Refused)
        {
            lock (_gate)
            {
                flow.Approving = false;
                if (_hosts.TryGetValue(session, out var current) && ReferenceEquals(current, flow)) _hosts.Remove(session);
                CountBadPairing(flow);
            }
            flow.Cancel.Cancel();
            flow.Host.Dispose();
            Notify();
            return false;
        }
        catch (Exception e) when (e is PairingException || IsTransient(e))
        {
            lock (_gate) flow.Approving = false;
            throw new SyncUserException(e is PairingException ? e.Message : Message(e));
        }
        catch (Exception e)
        {
            // Uma falha que ninguém esperava também devolve o pedido: senão ficava «ocupado» (e «Não fui eu» sem
            // efeito) até expirar. Nunca a mensagem crua.
            lock (_gate) flow.Approving = false;
            Log?.Invoke($"A entrega da conta falhou de forma inesperada ({e.GetType().Name}).");
            throw new SyncUserException(NotDeliveredText);
        }
        lock (_gate)
        {
            _hosts.Remove(session);
            _ended[session] = _now();
            _notice = $"«{DeviceNameText.Shown(flow.Name)}» foi aprovado: os dados juntam-se nesse computador.";
            // Uma aprovação repõe a contagem dos pedidos em série.
            if (_saved is { BadPairings: not null } saved)
            {
                saved.BadPairings = null;
                _store.Save(saved);
            }
        }
        // Aprovado: quem acompanhava o pedido para.
        flow.Cancel.Cancel();
        flow.Host.Dispose();
        Notify();
        return true;
    }

    // «Não fui eu» (ou «Recusar» nas Definições). Devolve se recusou: false quando o pedido já não existe ou quando uma
    // aprovação dele já vai a meio (BeginRefuse).
    public async Task<bool> RefuseAsync(string session, CancellationToken cancellationToken = default)
    {
        if (BeginRefuse(session) is not { } write) return false;
        await write(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // A parte imediata de «Não fui eu» (sem rede): tira o pedido e conta-o. Devolve a escrita da recusa por fazer (o
    // computador novo fica a saber), ou null se nada foi recusado — quem mostra «Recusado» só o diz neste caso.
    public Func<CancellationToken, Task>? BeginRefuse(string session)
    {
        HostFlow? flow;
        lock (_gate)
        {
            // Uma aprovação já passou o código (porta do Windows ou entrega): «Não fui eu» noutra janela não escreve
            // o x-host por cima de uma entrega nem conta um pedido aprovado. Quem desiste fá-lo na porta do Windows.
            if (_hosts.TryGetValue(session, out var approving) && approving.Approving) return null;
            _hosts.Remove(session, out flow);
        }
        if (flow is null) return null;
        flow.Cancel.Cancel();
        // «Não fui eu» (ou «Recusar» nas Definições): conta para os pedidos em série.
        NoteBadPairing(flow);
        return async cancellationToken =>
        {
            try { await flow.Host.RefuseAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (IsTransient(e) || e is OperationCanceledException) { }
            flow.Host.Dispose();
            Notify();
        };
    }

    // ---- aparelhos, remover, apagar, sair ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<DeviceInfo>> RefreshDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (_engine is null || SignInNeeded) return Status.Devices;
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RefreshDevicesCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (IsTransient(e)) { }
        finally { _run.Release(); }
        Notify();
        return Status.Devices;
    }

    private async Task RefreshDevicesCoreAsync(CancellationToken cancellationToken)
    {
        if (_engine is not { } engine) return;
        var states = await engine.DevicesAsync(cancellationToken).ConfigureAwait(false);
        // Pagamentos e documentos: deste aparelho, pelo que está ligado agora; dos outros, pelo estado que escreveram (um
        // Éteris antigo não o diz: null).
        bool here;
        lock (_gate) here = Model.SyncGroups.AccountCollections.All(EnabledOf(_saved).Contains);
        var list = states
            .Select(s => new DeviceInfo(s.Device, DeviceNameText.Shown(s.Name), s.Kind, s.LastSyncMs, s.Device == engine.Device.Id)
            {
                PaymentsAndDocuments = s.Device == engine.Device.Id ? here
                    : s.Groups is null ? null : Model.SyncGroups.AccountGroups.All(s.Groups.Contains),
            })
            .OrderByDescending(d => d.IsThis).ThenByDescending(d => d.LastSyncMs).ToList();
        if (!list.Any(d => d.IsThis))
            list.Insert(0, new DeviceInfo(engine.Device.Id, engine.Device.Name, engine.Device.Kind, engine.State.LastPullMs, true) { PaymentsAndDocuments = here });
        // Depois de uma troca de chave, os aparelhos que ficaram ainda não escreveram o seu estado na conta nova: entram
        // na lista pela chave fixada (à espera da chave nova), para continuarem à vista e receberem uma troca seguinte.
        foreach (var peer in engine.State.PeerKeys.Keys)
            if (!list.Any(d => d.Id == peer))
                list.Add(new DeviceInfo(peer, DeviceNameText.Shown(engine.State.DeviceNames.GetValueOrDefault(peer)), "pc", 0, false));
        var pinned = Snapshot(engine);
        lock (_gate)
        {
            if (!ReferenceEquals(_engine, engine)) return;
            _devices = list;
            if (_saved is { Enabled: true })
            {
                _saved.Engine = pinned; // chaves fixadas
                _store.Save(_saved);
            }
        }
    }

    // Remove um aparelho da conta: nova cadeia (kit de recuperação novo, por guardar; o antigo deixa de valer); os que ficam
    // recebem a conta nova — sozinhos só quando este aparelho é a única chave que fixaram; os outros ficam em pausa
    // até se escrever neles o identificador do kit novo (ConfirmRekeyAsync), porque o removido tem a chave fixada neles
    // e consegue deixar-lhes uma mensagem sua —, o removido deixa de conseguir abrir o que vier a seguir. Não retira a autorização
    // Google do removido (ainda pode apagar ou estragar os ficheiros no Drive): isso só o faz «Perdi um computador».
    // «shown»: os aparelhos que o utilizador viu na lista quando decidiu (por omissão, a última lista). Só esses, e só
    // com a chave fixada antes desta ação, recebem a conta nova.
    public Task RemoveDeviceAsync(string device, IReadOnlyCollection<string>? shown = null, CancellationToken cancellationToken = default)
        => RotateAsync(device, shown, cancellationToken);

    // «Gerar um kit novo»: nova cadeia sem tirar ninguém; o kit antigo deixa de valer.
    public Task NewRecoveryCodeAsync(CancellationToken cancellationToken = default) => RotateAsync(null, null, cancellationToken);

    private async Task RotateAsync(string? device, IReadOnlyCollection<string>? shown, CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunning(true);
        SavedSync? saved;
        lock (_gate) saved = _saved;
        try
        {
            if (_engine is null || saved is not { Enabled: true }) throw new SyncUserException("A sincronização não está ligada neste computador.");
            if (device == _engine.Device.Id) throw new SyncUserException("Para tirar este computador da conta, use «Sair».");
            if (saved.Rotation is { } unfinished) await FinishRotationAsync(saved, unfinished, cancellationToken).ConfigureAwait(false);
            var engine = _engine;
            // O que o utilizador viu, e as chaves fixadas ANTES desta ação: um aparelho que apareça agora não recebe nada.
            List<string> seen;
            string name;
            lock (_gate)
            {
                seen = (shown ?? _devices.Select(d => d.Id)).ToList();
                name = _devices.FirstOrDefault(d => d.Id == device)?.Name ?? "o aparelho";
            }
            var staying = engine.State.PeerKeys.Where(p => seen.Contains(p.Key) && p.Key != device && p.Key != engine.Device.Id)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

            // Primeiro uma ronda completa: o que os outros aparelhos enviaram e este ainda não leu entra no estado que
            // passa para a conta nova (a cadeia antiga, com esses lotes, vai ser apagada).
            await CycleAsync(saved, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(_engine, engine) || !IsCurrentLocked(saved)) throw new SyncUserException("A sincronização mudou entretanto. Tente outra vez.");
            var newcomers = engine.State.DeviceNames.Keys.Concat(engine.State.PeerKeys.Keys).Concat(engine.State.Read.Keys)
                .Where(d => d != engine.Device.Id && d != device && !seen.Contains(d)).Distinct(StringComparer.Ordinal)
                .Select(d => DeviceNameText.Clean(engine.State.DeviceNames.GetValueOrDefault(d)) is { } clean ? $"«{clean}»" : "um aparelho sem nome").ToList();

            var (rotated, secret, pending) = await engine.StartRotationAsync(device, staying, cancellationToken).ConfigureAwait(false);
            using (secret)
            {
                // O motor novo ainda é só desta ronda: leva o nome escolhido antes de se guardar a cópia.
                lock (_gate) ApplyChosenName(rotated);
                var snapshot = Snapshot(rotated);
                lock (_gate)
                {
                    if (!IsCurrent(saved)) throw new SyncUserException("A sincronização foi desligada entretanto.");
                    // Guardado ANTES de mexer na conta antiga: uma falha a seguir continua na ronda seguinte.
                    _engine = rotated;
                    saved.Secret = Kdf.Base64Url(secret.ToBytes());
                    saved.HadAccount = true;
                    saved.Engine = snapshot;
                    // Cadeia nova: kit novo; o guardado (se havia) deixou de valer.
                    KitFor(saved, secret);
                    saved.Rotation = pending;
                    saved.CreatedMs = 0;
                    saved.LastCompactMs = _now();
                    saved.LastCompactSeq = rotated.State.NextSeq - 1;
                    _store.Save(saved);
                    // Ninguém para avisar, ou um kit novo numa conta de dois: quem fica segue sozinho. Nos outros casos
                    // os que ficam podem ter mais chaves fixadas e pedem o identificador do kit (ConfirmRekeyAsync).
                    var alone = staying.Count == 0 || (device is null && staying.Count == 1);
                    var others = alone
                        ? "; os outros aparelhos continuam ligados sozinhos."
                        : ". Nos outros computadores, escreva o identificador deste kit (os 4 caracteres de «O seu kit atual termina em…») para continuarem ligados.";
                    var done = device is null
                        ? "A conta tem um kit de recuperação novo (o antigo deixou de valer)" + others
                        : $"«{name}» foi removido. A conta tem um kit de recuperação novo (o antigo deixou de valer)" + others;
                    // Os selos novos não servem de nada contra o que o aparelho removido já leu (desenho, secção 4.5).
                    if (device is not null && engine.State.Records.Values.Any(r => !r.Deleted && Model.Collections.IsSecret(r.Collection)))
                        done += " O aparelho removido já tinha as suas palavras-passe: mude as dos sites importantes.";
                    if (device is not null) done += " Reveja também as proteções (Escudo, «HTTPS primeiro») que esse aparelho possa ter mudado.";
                    _notice = newcomers.Count == 0 ? done
                        : done + $" Apareceu na conta {string.Join(", ", newcomers)}, que não estava na lista: não recebeu a chave nova. Se for seu, junte-o outra vez com o kit novo ou por aprovação.";
                }
                await FinishRotationCoreAsync(saved, secret, pending, rotated.State, cancellationToken).ConfigureAwait(false);
            }
            await rotated.PullAsync(cancellationToken).ConfigureAwait(false);
            await RefreshDevicesCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChainGoneException)
        {
            // Outro aparelho trocou a chave antes deste: recebe a conta nova (se houver) e pede para tentar outra vez.
            if (saved is not null) await ChainGoneAsync(saved, cancellationToken).ConfigureAwait(false);
            throw new SyncUserException("A conta mudou de chave noutro aparelho entretanto. Veja a lista outra vez e repita, se ainda for preciso.");
        }
        catch (Exception e) when (IsTransient(e))
        {
            Fail(e);
            throw new SyncUserException(Message(e));
        }
        finally
        {
            EndRun();
        }
        Notify();
    }

    // Acaba uma troca de chave guardada a meio (rede em baixo, Éteris fechado): entrega a conta nova aos aparelhos que
    // ficam e apaga a conta antiga.
    private async Task FinishRotationAsync(SavedSync saved, RotationPending rotation, CancellationToken cancellationToken)
    {
        if (saved.Secret is not { } secret || _engine is not { } engine) return;
        using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(secret));
        await FinishRotationCoreAsync(saved, account, rotation, engine.State, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishRotationCoreAsync(SavedSync saved, AccountSecret secret, RotationPending rotation, SyncState state, CancellationToken cancellationToken)
    {
        await SyncEngine.FinishRotationAsync(Transport(), state, secret, rotation, cancellationToken).ConfigureAwait(false);
        lock (_gate)
            if (IsCurrent(saved) && ReferenceEquals(saved.Rotation, rotation))
            {
                saved.Rotation = null;
                _store.Save(saved);
            }
    }

    // «Apagar os meus dados do Éteris»: apaga todos os ficheiros do Éteris no transporte (todas as contas, pedidos de
    // emparelhamento). Não sai da conta Google. A sincronização fica desligada neste aparelho; nos outros para.
    //
    // A ordem é a que protege o «apagar»: primeiro grava (já, sem esperar por uma ronda em curso) que a sincronização
    // está desligada e que falta apagar, e esquece a chave e o motor; só depois apaga. Assim, se o Éteris fechar a meio,
    // ao abrir continua desligada e nenhuma ronda volta a criar a conta ou a enviar (o anfitrião acaba de apagar). Uma
    // ronda que já estava a correr deixa de gravar (IsCurrent) e o apagar só começa depois dela (_run): o que ela tenha
    // escrito entretanto também sai.
    public async Task DeleteCloudDataAsync(CancellationToken cancellationToken = default)
    {
        BeginCloudDeletion();
        await FinishCloudDeletionAsync(cancellationToken).ConfigureAwait(false);
    }

    // A primeira metade (síncrona, só disco): o anfitrião chama-a na rota, antes de responder à página.
    public void BeginCloudDeletion()
    {
        EnsureReadable();
        GuestFlow? guest;
        List<HostFlow> hosts;
        lock (_gate)
        {
            var saved = _saved ??= new SavedSync();
            saved.Enabled = false;
            saved.DeletePending = true;
            saved.HadAccount = true;
            saved.Secret = null;
            saved.Kit = null;
            saved.CodePending = false;
            saved.Engine = null;
            saved.Baseline = null;
            saved.Joined = null;
            saved.Rotation = null;
            saved.CreatedMs = 0;
            saved.Stopped = DeletingText;
            ResetAccountChoice(saved);
            saved.InheritPending = false;
            _store.Save(saved);
            _entry = SyncEntry.None;
            _inheritedNow = false;
            _accountBy = null;
            _accountSince = 0;
            _deleteError = null;
            _engine = null;
            _pinnedFor = null;
            _phase = SyncPhase.Stopped;
            _notice = DeletingText;
            _error = null;
            _needsSignIn = false;
            _needsUpdate = false;
            _pendingDevices = 0;
            _waiting = 0;
            _devices = [];
            _secretsInAccount = false;
            _secretCollections = NoCollections;
            _justJoined = false;
            // Emparelhamentos a meio escrevem fora das rondas: param já (nada de pedidos novos depois do apagar).
            guest = _guest;
            _guest = null;
            hosts = _hosts.Values.ToList();
            _hosts.Clear();
        }
        guest?.Cancel.Cancel();
        foreach (var host in hosts) host.Cancel.Cancel();
        Notify();
    }

    // A segunda metade: apaga no transporte o que lá estiver (também depois de reabrir, se o Éteris fechou a meio).
    // Lista outra vez depois de apagar (outro aparelho a meio de uma ronda pode ter escrito entretanto), até 4 passagens.
    // Um nome que o transporte recusa (ArgumentException: não é do esquema nem uma cópia em conflito dentro da pasta do
    // Éteris) fica de fora, com registo, e não trava o resto. Qualquer outra falha — de rede ou inesperada — chega ao
    // utilizador (SyncUserException, e Status.DeleteError com «Tentar de novo») em vez de deixar o apagar preso.
    public async Task FinishCloudDeletionAsync(CancellationToken cancellationToken = default)
    {
        SavedSync? saved;
        lock (_gate) saved = _saved;
        if (saved is not { DeletePending: true }) return;
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunning(true);
        var left = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_saved, saved) || !saved.DeletePending)
                    return; // «Sair» com «apagar a chave», ou outro pedido já acabou
                _deleteError = null;
            }
            var transport = Transport();
            for (var pass = 0; pass < 4; pass++)
            {
                var files = (await transport.ListAsync(SyncPaths.Root, cancellationToken).ConfigureAwait(false)).Where(f => !left.Contains(f)).ToList();
                if (files.Count == 0) break;
                foreach (var path in files)
                {
                    try { await transport.DeleteAsync(path, cancellationToken).ConfigureAwait(false); }
                    catch (ArgumentException e)
                    {
                        left.Add(path);
                        Log?.Invoke($"Apagar do transporte: «{path}» ficou de fora ({e.Message}).");
                    }
                }
            }
            lock (_gate)
            {
                if (!ReferenceEquals(_saved, saved) || !saved.DeletePending) return;
                var done = left.Count == 0 ? DeletedText : DeletedText + LeftOutText(left.Count);
                saved.DeletePending = false;
                saved.Stopped = done;
                _store.Save(saved);
                _phase = SyncPhase.Stopped;
                _notice = done;
                _error = null;
                _deleteError = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // o Éteris a fechar: acaba ao abrir
        }
        catch (Exception e)
        {
            var text = NotDeletedText(e);
            Log?.Invoke($"Apagar do transporte: não acabou ({e.GetType().Name}: {e.Message}).");
            if (IsTransient(e)) Fail(e);
            lock (_gate)
                if (ReferenceEquals(_saved, saved) && saved.DeletePending)
                    _deleteError = text;
            throw new SyncUserException(text);
        }
        finally
        {
            EndRun();
        }
        Notify();
    }

    private const string DeletingText =
        "A apagar os dados do Éteris do Google Drive. A sincronização já está desligada neste computador; os dados deste computador ficam.";

    private const string DeletedText =
        "Os dados do Éteris foram apagados do Google Drive. A sincronização está desligada; os dados deste computador ficaram.";

    private static string NotDeletedText(Exception e)
    {
        var why = e switch
        {
            Google.OAuthException { NeedsSignIn: true } => "a sessão da Google acabou: entre outra vez com o Google",
            HttpRequestException or TaskCanceledException or TimeoutException or Google.OAuthException => "sem ligação ao Google Drive",
            IOException or UnauthorizedAccessException => "a pasta de sincronização não está acessível",
            _ => "erro inesperado: " + e.Message,
        };
        return $"Ainda não se conseguiu apagar tudo do Google Drive ({why}). A sincronização ficou desligada neste computador e nada voltou a ser enviado. Carregue em «Tentar de novo» (o Éteris também tenta quando abrir outra vez).";
    }

    private static string LeftOutText(int count)
        => count == 1
            ? " Ficou 1 ficheiro com um nome que o Éteris não reconhece (não foi apagado)."
            : $" Ficaram {count} ficheiros com nomes que o Éteris não reconhece (não foram apagados).";

    private const string StartAnywayText =
        "Para ligar já a sincronização, carregue em «Ligar sem acabar de apagar»: o que ficou no Drive fica lá e, se a conta antiga ainda lá estiver, este computador pede o kit de recuperação dela (ou volte a «Apagar…» mais tarde).";

    // «Ligar sem acabar de apagar»: o utilizador desiste de acabar o apagar que falhou (fica no transporte o que lá
    // estava) e liga a sincronização a seguir (StartAsync). Nunca fica preso num apagar que não acaba.
    public void AbandonCloudDeletion()
    {
        lock (_gate)
        {
            if (_saved is not { DeletePending: true } saved) return;
            saved.DeletePending = false;
            saved.Stopped = null;
            _store.Save(saved);
            _deleteError = null;
            _notice = null;
            _error = null;
            _phase = SyncPhase.Off;
        }
        Log?.Invoke("Apagar do transporte: o utilizador ligou sem acabar de apagar.");
        Notify();
    }

    // «Sair»: a sincronização para neste aparelho. Com forgetKey, a cópia local da conta sai também (para voltar a
    // entrar é preciso o kit de recuperação ou outro aparelho; o registo do kit sai com ela); sem ela, entrar outra vez
    // retoma sem kit — e o estado do kit (guardado, por guardar) fica como estava.
    // Não espera pela ronda em curso: ela confere IsCurrent antes de gravar (e o «apagar a chave» não volta atrás).
    public void SignOut(bool forgetKey)
    {
        GuestFlow? guest;
        List<HostFlow> hosts;
        var keyLeft = false;
        lock (_gate)
        {
            guest = _guest;
            _guest = null;
            hosts = _hosts.Values.ToList();
            _hosts.Clear();
            _engine = null;
            _transport = null;
            _devices = [];
            _error = null;
            _notice = null;
            _needsSignIn = false;
            _waiting = 0;
            _justJoined = false;
            _secretCollections = NoCollections;
            _entry = SyncEntry.None;
            _inheritedNow = false;
            _accountBy = null;
            _accountSince = 0;
            _phase = SyncPhase.Off;
            if (forgetKey)
            {
                if (_saved is not null) _saved.Enabled = false; // uma ronda em curso com este objeto já não grava
                _saved = null;
                _unreadable = false; // o estado deixou de existir (ou vai deixar: _store.Delete a seguir)
                // O disco não deixou apagar o estado: «Sair» acaba na mesma (em baixo) e só depois o diz.
                try { _store.Delete(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    keyLeft = true;
                    Log?.Invoke($"«Sair»: o estado guardado não se apagou ({e.GetType().Name}).");
                }
            }
            else if (_saved is not null)
            {
                _saved.Enabled = false;
                _store.Save(_saved);
            }
        }
        guest?.Cancel.Cancel();
        foreach (var host in hosts) host.Cancel.Cancel();
        Notify();
        if (keyLeft) throw new SyncUserException(KeyNotDeletedText);
    }

    private const string KeyNotDeletedText =
        "A sincronização parou neste computador, mas não se conseguiu apagar a chave do disco: ao abrir o Éteris outra vez, a sincronização pode voltar a ligar-se. Feche os outros programas que estejam a usar a pasta do Éteris e saia outra vez.";

    // ---- kit de recuperação -----------------------------------------------------------------------------------------

    // O kit (as 24 palavras do segredo guardado), ou null sem chave. NÃO pede confirmação nenhuma: o anfitrião só o
    // chama atrás da sua porta (a confirmação do Windows).
    public RecoveryKitSecret? RevealKit()
    {
        lock (_gate)
        {
            if (_saved is not { Secret: { } secret, Kit: { } kit }) return null;
            using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(secret));
            return new RecoveryKitSecret(account.ToWords(), account.KitId, kit.SinceMs);
        }
    }

    // O utilizador guardou o kit atual (ficheiro, ou «Já guardei o kit»). Com «forId», só se for esse o kit atual (o que
    // a página mostrou): uma cadeia nova entretanto não fica marcada como guardada sem ter sido vista.
    public void MarkKitSaved(string? forId = null)
    {
        lock (_gate)
        {
            if (forId is not null && _saved?.Kit?.ForId != forId) return;
        }
        ChangeKit(kit =>
        {
            if (forId is null || kit.ForId == forId) kit.SavedId = kit.ForId;
        });
    }

    // «Mais tarde»: o lembrete não aparece na barra antes de «untilMs».
    public void SnoozeKit(long untilMs) => ChangeKit(kit => kit.SnoozedUntilMs = untilMs);

    // «Não quero: aceito que, sem este computador, a cópia na nuvem não se recupera».
    public void DeclineKit() => ChangeKit(kit => kit.Declined = true);

    // Sem chave neste computador, nada.
    private void ChangeKit(Action<KitRecord> change)
    {
        lock (_gate)
        {
            if (_saved is not { Secret: not null, Kit: { } kit }) return;
            change(kit);
            _store.Save(_saved);
        }
        Notify();
    }

    // Acerta o registo do kit ao segredo guardado. Chamar dentro do _gate, logo depois de gravar o segredo: o mesmo
    // segredo não muda nada; outro começa por guardar — e, se havia um kit guardado, esse deixou de valer (Stale). Um
    // «Não quero» fica só se nunca se guardou nenhum kit (aceitou o risco); quem contava com um kit volta a ser lembrado.
    private void KitFor(SavedSync saved, AccountSecret account)
    {
        var id = account.KitId;
        if (saved.Kit is { } kit && kit.ForId == id) return;
        var hadSaved = saved.Kit?.SavedId is not null;
        saved.Kit = new KitRecord
        {
            ForId = id,
            SavedId = saved.Kit?.SavedId,
            SinceMs = _now(),
            SnoozedUntilMs = 0,
            Declined = !hadSaved && saved.Kit?.Declined == true,
        };
    }

    // O kit no estado, sem derivar nada (lê ForId). Chamar dentro do _gate.
    private KitInfo? KitInfoOf(SavedSync? saved)
    {
        if (saved is not { Secret: not null, Kit: { ForId: { } id } kit }) return null;
        var state = kit.SavedId is null ? KitState.Pending : kit.SavedId == id ? KitState.Saved : KitState.Stale;
        return new KitInfo(id, state, kit.SinceMs, kit.SnoozedUntilMs, kit.Declined, KnownDevices(saved));
    }

    // Quantos computadores há na conta, sem rede (inclui este): a lista da última leitura dos aparelhos; antes dela,
    // este mais as chaves fixadas dos outros (DeviceNames não serve: guarda os removidos). Na dúvida conta a menos —
    // o lembrete a mais é o lado seguro. Lê só a cópia guardada, nunca o motor (Status é lido de qualquer thread, a meio
    // de uma ronda). Chamar dentro do _gate.
    private int KnownDevices(SavedSync saved)
    {
        if (_devices.Count > 0) return _devices.Count;
        if (saved.Engine is not { } state) return 1;
        return 1 + state.PeerKeys.Keys.Count(k => k != state.Device.Id);
    }

    // Uma cópia do estado do motor, para guardar. O motor muda o seu estado no sítio durante a ronda, fora do _gate; o
    // que fica em SavedSync.Engine é lido por Status e serializado por cada gravação, de qualquer thread — por isso
    // nunca é esse objeto, e ninguém muda a cópia depois de guardada. Só quem tem a ronda (_run) chama isto, e FORA do
    // _gate (serializa o estado inteiro).
    private static SyncState Snapshot(SyncEngine engine) => SyncState.Parse(engine.State.ToBytes());

    // ---- auxiliares -------------------------------------------------------------------------------------------------

    // Chamado dentro do _gate: o estado desta operação ainda é o deste aparelho e a sincronização continua ligada.
    private bool IsCurrent(SavedSync? saved) => saved is { Enabled: true } && ReferenceEquals(_saved, saved);

    private bool IsCurrentLocked(SavedSync saved)
    {
        lock (_gate) return IsCurrent(saved);
    }

    // A sessão do transporte é de outra conta (só quando se sabem as duas).
    private bool OtherAccount(SavedSync saved) => saved.Account is { } mine && AccountKey?.Invoke() is { } now && now != mine;

    private ISyncTransport Transport()
    {
        lock (_gate) return _transport ??= _transportFactory();
    }

    private void SetRunning(bool running)
    {
        lock (_gate) _running = running;
        Notify();
    }

    private void Fail(Exception e)
    {
        lock (_gate)
        {
            _error = Message(e);
            if (e is Google.OAuthException { NeedsSignIn: true }) _needsSignIn = true;
            if (_phase == SyncPhase.Starting) _phase = SyncPhase.On;
        }
    }

    // Cada assinante por si: a exceção de um (a interface a redesenhar-se) não sai para a operação que avisou — que
    // ficava a meio, com o trinco das rondas por libertar ou «Sair» por acabar — nem cala os assinantes seguintes.
    private void Notify()
    {
        if (Changed is not { } changed) return;
        foreach (var subscriber in changed.GetInvocationList())
        {
            try { ((Action)subscriber)(); }
            catch (Exception e) { Log?.Invoke($"Um assinante de Changed falhou ({e.GetType().Name})."); }
        }
    }

    // O fim de uma operação com o trinco das rondas: o trinco sai sempre.
    private void EndRun()
    {
        try { SetRunning(false); }
        finally { _run.Release(); }
    }

    // Falhas que passam sozinhas (rede, sessão, pasta): ficam no estado e o agendador tenta outra vez mais tarde. A
    // autorização da Google retirada ou expirada (OAuthException.NeedsSignIn) também cai aqui, mas fica marcada em
    // Status.NeedsSignIn: o anfitrião para o agendador até o utilizador entrar outra vez.
    public static bool IsTransient(Exception e)
        => e is HttpRequestException or IOException or TaskCanceledException { CancellationToken.IsCancellationRequested: false } or TimeoutException
            // O limite de tempo do cliente HTTP: o token da exceção vem cancelado (é o do limite), com a TimeoutException dentro.
            or TaskCanceledException { InnerException: TimeoutException }
            or UnauthorizedAccessException or CryptographicException or FormatException or Google.OAuthException or UnsupportedFormatException;

    public static string Message(Exception e) => e switch
    {
        Google.OAuthException o => o.Message,
        HttpRequestException or OperationCanceledException or TimeoutException => "Sem ligação ao Google Drive. O Éteris tenta outra vez sozinho.",
        IOException or UnauthorizedAccessException => "A pasta de sincronização não está acessível. O Éteris tenta outra vez sozinho.",
        WrongCodeException w => w.Message,
        UnsupportedFormatException u => u.Message,
        _ => "Um ficheiro da sincronização não se leu (" + e.Message + "). O Éteris tenta outra vez mais tarde.",
    };

    private sealed class GuestFlow
    {
        public CancellationTokenSource Cancel { get; } = new();
        public TaskCompletionSource Confirm { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Session { get; set; }
        public string? Code { get; set; }
        public string? Error { get; set; }
        // Um aparelho ligado respondeu (escreveu o 1-host): daqui em diante o pedido não se renova.
        public bool HostAnswered { get; set; }
        public GuestEnd End { get; set; }
        // A hora do primeiro pedido da série (a janela de PairingRenewal) e a do início da sessão em curso (o prazo).
        public long FirstRequestMs { get; init; }
        public long SessionStartMs { get; set; }
        public int Renewals { get; set; }
    }

    private sealed class HostFlow(string session, PairingHost host, long created)
    {
        public string Session { get; } = session;
        public PairingHost Host { get; } = host;
        public long Created { get; } = created;
        public CancellationTokenSource Cancel { get; } = new();
        public string? Name { get; set; }
        // O código de verificação calculado aqui: só para comparar com o escrito, nunca para mostrar.
        public string? Code { get; set; }
        public bool Approving { get; set; }
        // Códigos errados escritos neste pedido. Só memória: o Éteris reabriu, a sessão morreu (não há tentativas novas).
        public int Wrong { get; set; }
        // Já contou para os pedidos em série (cada sessão conta uma vez).
        public bool CountedBad { get; set; }
    }
}

// Uma ação do utilizador não se pôde cumprir: a mensagem é para lhe mostrar.
public sealed class SyncUserException(string message) : Exception(message);
