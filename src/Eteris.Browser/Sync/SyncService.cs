using System.IO;
using Eteris.Browser.Infrastructure;
using Eteris.Sync.Engine;
using Eteris.Sync.Format;
using Eteris.Sync.Pairing;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Sync;

// A sincronização do Éteris no navegador: o aparelho na conta (SyncAgent da biblioteca) com o perfil do Éteris
// (BrowserProfile), o estado guardado com DPAPI (<perfil>\sync\sync.state) e o agendador (SyncSchedule).
//
// Transporte: a pasta oculta da aplicação no Google Drive (GoogleSync, depois de «Entrar com o Google»). Só para os
// testes ao vivo, ETERIS_TEST_SYNC_FOLDER=<pasta> troca o Drive por uma pasta local (nenhum pedido de rede); a versão
// instalada ignora-a (TestFolder).
//
// Quando há rede (só com a sincronização ligada): 30 s depois de a janela estar pronta (ou 10 s depois de uma
// alteração, se for antes), 10 s depois de uma alteração local (e só se houver alguma coisa por enviar), de 10 em 10
// minutos, ao entrar e em «Sincronizar agora»; ao abrir o menu do Éteris (⋯), com a secção Sincronização à vista, ao
// carregar em «Ver» na barra de aprovação (ações do utilizador) e ao voltar à janela do Éteris (depois da 1.ª ronda
// desta abertura, no máximo uma vez a cada 5 minutos, ou logo se uma ronda já tiver encontrado um pedido:
// WindowActivated, ActivationLook), procura pedidos de aparelhos novos e responde-lhes. Construir o serviço e ler o
// estado nunca faz pedidos: o arranque sem sincronização ligada não fala com a Google. Se a autorização da Google
// tiver sido retirada ou tiver expirado, o agendador para até o utilizador entrar outra vez (não «tenta outra vez
// sozinho»).
internal static class SyncService
{
    private static readonly Lock Gate = new();
    private static readonly SyncSchedule Schedule = new();
    private static SyncAgent? _agent;
    private static System.Threading.Timer? _timer;
    private static bool _busy;
    private static bool _hooked;
    private static long _lastLook;
    // Já se fez (ou tentou) uma ronda nesta abertura do Éteris: até lá, voltar à janela não é um momento de rede. Só
    // memória.
    private static volatile bool _firstRoundDone;
    private static readonly ActivationLook Activation = new();

    // O estado mudou (para a linha da conta no menu e as Definições). Chamado de qualquer thread.
    public static event Action? Changed;

    public static string StatePath => Path.Combine(AppPaths.Root, "sync", "sync.state");

    // Só nos testes ao vivo, e nunca na versão instalada (Velopack: há um Update.exe na pasta de cima).
    public static string? TestFolder => TestFolderFor(Environment.GetEnvironmentVariable("ETERIS_TEST_SYNC_FOLDER"), TestSwitches.IsInstalled);

    public static string? TestFolderFor(string? variable, bool installed)
        => string.IsNullOrWhiteSpace(variable) || installed ? null : Path.GetFullPath(variable.Trim());

    public static bool FolderMode => TestFolder is not null;

    // Há com que sincronizar: o identificador da aplicação Google (google-oauth.json) ou a pasta de teste.
    public static bool Available => FolderMode || GoogleSync.Config is not null;

    // Ligado à conta (Google com sessão; na pasta de teste, ligada ou parada — até «Sair»). Só disco.
    public static bool Connected => FolderMode ? Agent()?.Status.Phase is not (null or SyncPhase.Off) : GoogleSync.SignedIn;

    public static bool Enabled => Available && Connected && Agent()?.Enabled == true;

    // O valor inicial do nome deste computador. O nome em vigor (o que o utilizador escolheu) é Agent()?.DeviceName.
    public static string DeviceName => DeviceNameFor(Environment.MachineName,
        FolderMode ? Path.GetFileName(AppPaths.Root.TrimEnd('\\', '/')) : null);

    // «Éteris em {nome do Windows}»; no modo pasta de teste, com o nome do perfil entre parênteses (dois perfis no mesmo
    // computador distinguem-se). Sempre limpo e com 60 caracteres no máximo.
    internal static string DeviceNameFor(string? windowsName, string? testProfile)
    {
        var name = DeviceNameText.Default(windowsName);
        return testProfile is null ? name : DeviceNameText.Clean($"{name} ({testProfile})") ?? name;
    }

    public const string WhatSyncs =
        "Favoritos, Espaços (nome, cor e emoji; as abas ficam em cada computador), as definições que viajam (tema, motor de pesquisa, proteções, nova aba, leitura…), as decisões do Escudo por site (desligado, modo estrito, elementos escondidos, bloqueios por site, exceções ao HTTPS primeiro), as notas e, com uma segunda cifra própria, as palavras-passe, as informações de contacto e as viagens (pagamentos e documentos de identificação só se os ligar).";

    public const string WhatStays =
        "Não sincroniza: histórico, abas abertas, cookies, transferências, chaves de acesso (ficam no Windows Hello ou na chave de segurança), o histórico das palavras-passe e o que só vale neste computador (Mica, barra lateral, esconder das capturas, «Pedir o Windows Hello…»).";

    public static SyncAgent? Agent()
    {
        if (!Available) return null;
        lock (Gate)
        {
            if (_agent is not null) return _agent;
            var store = new DpapiSyncStore(StatePath, new Dpapi("sync-state"));
            // O perfil inteiro: o resto do Éteris (BrowserProfile) e o cofre (Vault/VaultSyncProfile, só as coleções ligadas
            // neste computador; sem nenhuma ligada, ou sem cofre neste perfil, nem o abre).
            var profile = new Vault.ProfileWithVault(new BrowserProfile(), new Vault.VaultSyncProfile(() => Vault.VaultService.Profile),
                () => _agent?.EnabledCollections);
            _agent = new SyncAgent(Transport, profile, store, DeviceName, Now)
            {
                AccountKey = AccountKey,
                // Só nomes opacos do transporte e o tipo da falha (nunca dados): para o diagnóstico com um depurador.
                Log = m => System.Diagnostics.Debug.WriteLine("[sincronização] " + m),
            };
            // Observa primeiro e avisa depois: as janelas refrescam as barras a partir de Changed e têm de já encontrar o
            // aviso pendente (o agendador para quando é preciso entrar, por isso pode não haver outra mudança).
            SyncAgent agent = _agent;
            _agent.Changed += () =>
            {
                var first = !FolderMode && Expired.Observe(agent.Status.NeedsSignIn);
                Changed?.Invoke();
                if (first) NeedsSignIn?.Invoke();
            };
            return _agent;
        }
    }

    // ---- aviso de sessão expirada (0.2.15) --------------------------------------------------------------------------

    // Uma vez por abertura do Éteris; só memória (nada em disco, nenhum pedido novo: reage às rondas que já se fazem).
    private static readonly SessionExpiredPrompt Expired = new();

    // A sincronização descobriu que é preciso entrar outra vez (uma vez por abertura). Qualquer thread. Não é um
    // estado: «é preciso entrar» é SyncStatus.NeedsSignIn e «a barra deve estar à vista» é SignInPrompt.
    public static event Action? NeedsSignIn;

    // A barra «entre outra vez» deve estar à vista: ainda não respondida e o estado continua a pedi-lo. Barato e sem
    // exceções: lê o campo (nunca cria o agente) e, sem aviso pendente, não lê mais nada.
    public static bool SignInPrompt
    {
        get { return Expired.Pending && !FolderMode && _agent?.Status.NeedsSignIn == true; }
    }

    // «Entrar», «Agora não» (e, na T12, sair): o aviso não volta nesta abertura; todas as janelas o escondem.
    public static void DismissSignInPrompt()
    {
        Expired.Dismiss();
        Changed?.Invoke();
    }

    private static ISyncTransport Transport()
    {
        if (TestFolder is { } folder) return new FolderTransport(folder);
        return GoogleSync.DriveTransport() ?? throw new Eteris.Sync.Google.OAuthException("Sem sessão iniciada na Google.");
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // A conta Google desta sessão, como impressão digital (o e-mail não vai para o estado da sincronização). Só disco.
    private static string? AccountKey() => FolderMode ? null : AccountFingerprint(GoogleSync.Account?.Email);

    public static string? AccountFingerprint(string? email)
        => string.IsNullOrWhiteSpace(email) ? null
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("eteris-account|" + email.Trim().ToLowerInvariant()))[..16]);

    // ---- arranque e agendador ---------------------------------------------------------------------------------------

    // Depois da 1.ª página (App.xaml.cs, FirstPaint): sem pedidos. Com a sincronização ligada, marca a primeira ronda
    // para daqui a 30 s; sem ela, só fica à escuta das alterações (que não fazem nada enquanto estiver desligada).
    public static void AfterFirstPaint()
    {
        Hook();
        // «Apagar os meus dados…» que o fecho do Éteris interrompeu: a sincronização já está desligada (ficou gravado
        // antes do primeiro pedido); acaba de apagar, sem ronda nenhuma.
        if (Agent()?.DeletePending == true)
        {
            Act("delete", a => a.FinishCloudDeletionAsync());
            return;
        }
        if (!Enabled) return;
        lock (Gate) Schedule.Startup(Now());
        Arm();
    }

    private static void Hook()
    {
        lock (Gate)
        {
            if (_hooked) return;
            _hooked = true;
        }
        Data.Settings.Changed += _ => Touch();
        Data.AppData.SyncableChanged += Touch;
        Data.NotesStore.Saved += Touch;
        Vault.VaultService.Changed += Touch;
    }

    // Uma alteração local (favorito, definição, Escudo, Espaço, notas): sincroniza 10 s depois da última.
    public static void Touch()
    {
        if (!Enabled || Agent()?.Status.NeedsSignIn == true) return;
        lock (Gate) Schedule.LocalChange(Now());
        Arm();
    }

    // «Sincronizar agora».
    public static void SyncNow()
    {
        if (!Enabled) return;
        lock (Gate) Schedule.Now(Now());
        Arm();
    }

    private static void Arm()
    {
        lock (Gate)
        {
            _timer ??= new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
            if (Schedule.NextMs is not { } next)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
            _timer.Change(Math.Max(0, next - Now()), Timeout.Infinite);
        }
    }

    private static void Tick()
    {
        SyncSchedule.Action action;
        lock (Gate)
        {
            if (_busy) return;
            action = Enabled ? Schedule.Due(Now()) : SyncSchedule.Action.None;
            if (action == SyncSchedule.Action.None)
            {
                if (!Enabled) Schedule.Stop();
            }
            else _busy = true;
        }
        if (action == SyncSchedule.Action.None)
        {
            Arm();
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var agent = Agent();
                if (agent is null) return;
                if (action == SyncSchedule.Action.LocalCheck && !agent.HasLocalChanges())
                {
                    lock (Gate) Schedule.Checked();
                    return;
                }
                var ok = await agent.RunAsync().ConfigureAwait(false);
                _firstRoundDone = true;
                lock (Gate)
                {
                    Schedule.Done(Now(), ok);
                    // A autorização da Google foi retirada ou expirou: nada se resolve sozinho, para até entrar outra vez.
                    if (agent.Status.NeedsSignIn) Schedule.Stop();
                }
            }
            catch (Exception)
            {
                // Uma ronda que falhou também conta como a 1.ª (corra bem ou mal).
                _firstRoundDone = true;
                lock (Gate) Schedule.Done(Now(), ok: false);
            }
            finally
            {
                lock (Gate) _busy = false;
                Arm();
            }
        });
    }

    // ---- ações do utilizador ----------------------------------------------------------------------------------------

    // Entrou na Google (GoogleSync) ou carregou em «Ligar a sincronização» / «Ligar outra vez»: liga e faz a primeira
    // ronda já. «switchAccount» só no botão (o utilizador leu o aviso de que a sessão é de outra conta Google).
    // «abandonDeletion»: «Ligar sem acabar de apagar», depois de um apagar do Drive que não acabou (o que ficou fica lá).
    public static void Start(bool switchAccount = false, bool abandonDeletion = false) => _ = Task.Run(() => StartAsync(switchAccount, abandonDeletion));

    // O mesmo, para quem espera pela primeira ronda (a página de fim da entrada pela Google). Não lança. Não há nenhum
    // «await» antes do StartAsync do agente: quando esta tarefa é devolvida, o agente já está em «Starting» (ou mais à
    // frente) e quem lê a fase não vê a da ronda anterior.
    internal static async Task StartAsync(bool switchAccount = false, bool abandonDeletion = false)
    {
        if (Agent() is not { } agent) return;
        Hook();
        // Uma ação nova: a mensagem da anterior (por exemplo, um apagar que falhou) sai da página.
        lock (Gate) _message = null;
        if (abandonDeletion) agent.AbandonCloudDeletion();
        bool ok;
        try
        {
            await agent.StartAsync(switchAccount).ConfigureAwait(false);
            ok = agent.Status.Error is null;
        }
        catch (SyncUserException e)
        {
            // Um apagar por acabar que ainda não acabou: a sincronização continua desligada, e diz porquê (e que pode
            // tentar de novo ou ligar sem acabar de apagar).
            ok = false;
            Say("delete", e.Message);
        }
        catch (Exception e)
        {
            ok = false;
            if (agent.DeletePending) Say("delete", "Não foi possível acabar de apagar (" + e.Message + ").");
        }
        // A ronda do gesto («Ligar», entrar) acabou: a partir daqui voltar à janela pode procurar pedidos.
        _firstRoundDone = true;
        lock (Gate)
        {
            Schedule.Now(Now());
            Schedule.Done(Now(), ok);
            if (agent.Status.NeedsSignIn || !agent.Enabled) Schedule.Stop();
        }
        Arm();
    }

    // «Apagar os meus dados do Éteris no Google Drive»: grava já (antes de responder à página e de qualquer pedido) que a
    // sincronização está desligada e que falta apagar, para o agendador, e só depois apaga em segundo plano. Se o
    // Éteris fechar a meio, ao abrir continua desligada e o apagar acaba (AfterFirstPaint).
    public static void DeleteCloud()
    {
        if (Agent() is not { } agent) return;
        Hook();
        agent.BeginCloudDeletion();
        lock (Gate) Schedule.Stop();
        Arm();
        Act("delete", a => a.FinishCloudDeletionAsync());
    }

    // «Tentar de novo» depois de um apagar do Drive que não acabou (já decidido: sem outra confirmação).
    public static void RetryDeleteCloud()
    {
        if (Agent() is not { DeletePending: true }) return;
        Act("delete", a => a.FinishCloudDeletionAsync());
    }

    // «Sair»: a sincronização para neste computador (a revogação na Google é do GoogleSync).
    public static void SignOut(bool forgetKey)
    {
        Agent()?.SignOut(forgetKey);
        lock (Gate) Schedule.Stop();
        Arm();
    }

    // O utilizador abriu o menu do Éteris (⋯) ou tem a secção Sincronização à vista: procura pedidos de aparelhos novos
    // e responde-lhes (no máximo uma vez a cada 5 s) e, com «devices», relê a lista de aparelhos.
    public static void Look(bool devices = false)
    {
        if (!Enabled || Agent() is not { } agent) return;
        lock (Gate)
        {
            if (Now() - _lastLook < 5_000 && !devices) return;
            _lastLook = Now();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (devices) await agent.RefreshDevicesAsync().ConfigureAwait(false);
                await agent.CheckRequestsAsync().ConfigureAwait(false);
            }
            catch (Exception) { }
        });
    }

    // ---- barra de aprovação (0.2.15) -------------------------------------------------------------------------------

    // O destino de «Retirar o acesso do Éteris» nas barras. Um só sítio (a T12 muda-o para o item próprio).
    internal const string RevokeAnchor = "#sincronizacao";

    // O que a barra de aprovação mostra agora. Lê o campo (nunca cria o agente; sem agente, nada) e só memória: é
    // chamado pelo «wants» da candidata, a cada RefreshBars.
    internal static PairingBarView PairingBarState()
    {
        var agent = _agent;
        return PairingBarModel.From(agent?.Status, Now(), PairingBarModel.DefaultReceives);
    }

    // A pessoa escreveu na barra o código que o computador novo mostra. «confirm» é a porta do Windows da janela que tem
    // a barra: o agente só a chama depois do código certo, e só entrega a conta depois dela (quando há segredos em
    // jogo). Não apanha SyncUserException: a barra mostra a mensagem.
    public static Task<PairingAnswer> ApprovePairingAsync(string session, string typed, Func<Vault.ReauthPurpose, Task<bool>> confirm)
    {
        if (Agent() is not { } agent) return Task.FromResult(PairingAnswer.Gone);
        return agent.ApproveWithCodeAsync(session, typed, () => SyncChoices.MayApproveAsync(agent, confirm));
    }

    // «Não fui eu»: recusa o pedido (o computador novo fica a saber).
    public static void RefusePairing(string session) => Act("approve", a => a.RefuseAsync(session));

    // «Mostrar o pedido na barra» (Definições, com a barra calada pelos pedidos em série): um gesto que repõe a
    // contagem. Sem rede; lê o campo do agente (não o cria).
    public static void ResumePairingBar()
    {
        _agent?.ResumePairingBar();
    }

    // A janela do Éteris veio para a frente. Não é um gesto de rede antes da 1.ª ronda; depois, no máximo uma vez a
    // cada 5 minutos, ou logo se a última ronda contou um pedido.
    public static void WindowActivated(bool privateWindow)
    {
        // Antes da 1.ª ronda (inclui o arranque, antes da 1.ª página) e em janelas privadas: nada. Só memória — não
        // cria o agente nem lê o disco.
        if (privateWindow || !_firstRoundDone) return;
        // O campo, nunca a função que cria o agente.
        if (_agent is not { } agent || !Enabled) return;
        var status = agent.Status;
        // Com a barra calada (pedidos em série), um pedido contado já não dá direito a procurar logo: 5 minutos.
        var counted = status.PairingQuiet ? 0 : status.WaitingRequests;
        if (Activation.TryPass(Now(), counted, firstRoundDone: true)) Look();
    }

    // ---- o computador novo: cartão do pedido e alerta «conta substituída» (0.2.15, T7) -------------------------------

    // O que as barras do computador novo mostram agora. Lê o campo (nunca cria o agente; sem agente, nada) e só memória:
    // é chamado pelos «wants» das candidatas, a cada RefreshBars.
    internal static GuestPairingView GuestPairingState() => GuestPairingModel.From(_agent?.Status);

    private static volatile bool _replacedDismissed;
    private static volatile bool _guestCardOpen = true;

    // «Não fui eu» no alerta: a barra do alerta não volta até o Éteris fechar (o alerta continua nas Definições).
    internal static bool ReplacedAlertDismissed => _replacedDismissed;

    // Só memória: sem rede, sem agente.
    internal static void DismissReplacedAlert()
    {
        _replacedDismissed = true;
        Changed?.Invoke();
    }

    // O cartão do computador novo está aberto (por omissão) ou recolhido («Fechar»). Partilhado por todas as janelas,
    // só memória. «Ver código», «Foi eu», «Pedir outra vez» e um pedido novo voltam a abri-lo.
    internal static bool GuestCardOpen => _guestCardOpen;

    internal static void SetGuestCard(bool open)
    {
        if (_guestCardOpen == open) return;
        _guestCardOpen = open;
        Changed?.Invoke();
    }

    // «Foi eu — juntar este computador» (alerta de conta substituída): junta este computador à conta que está AGORA no
    // Drive. Com segredos ligados aqui, o Windows confirma primeiro (sempre). true = o pedido foi lançado; false = não
    // foi confirmado, ou já não se aplica. Não apanha as exceções da confirmação: quem chama trata.
    public static async Task<bool> JoinAfterReplacedAsync(Func<Vault.ReauthPurpose, Task<bool>> confirm)
    {
        if (Agent() is not { } agent || !agent.Status.ChainReplaced) return false;
        if (!await SyncChoices.MayJoinReplacedAsync(agent, confirm).ConfigureAwait(false))
        {
            Say("replaced", SyncChoices.NotConfirmed("este computador não foi juntado à conta"));
            return false;
        }
        lock (Gate) if (_message?.Area == "replaced") _message = null;
        SetGuestCard(true);
        // Parada por a conta ter desaparecido: a ronda do gesto («Ligar outra vez») vê a conta que lá está agora.
        if (agent.Status.Phase == SyncPhase.Stopped) await StartAsync().ConfigureAwait(false);
        if (agent.Status.Phase == SyncPhase.Choose) agent.RequestPairing();
        return true;
    }

    // «Pedir outra vez» (cartão) e a rota /sync-pair (Definições). Com a conta substituída não pede: o único caminho é
    // JoinAfterReplacedAsync, com a confirmação do Windows.
    public static void AskPairingAgain()
    {
        if (Agent() is not { } agent || agent.Status.ChainReplaced) return;
        if (agent.Status.Phase != SyncPhase.Choose) return;
        SetGuestCard(true);
        agent.RequestPairing();
    }

    private static (string Area, string Text)? _message;

    // A mensagem da última ação que falhou (código errado, pedido expirado…), com a parte da página onde aparece.
    public static object? Message
    {
        get { lock (Gate) return _message is { } m ? new { area = m.Area, text = m.Text } : null; }
    }

    // Uma mensagem para uma parte da página sem ação na rede (por exemplo «não foi confirmado com o Windows»).
    public static void Say(string area, string? text)
    {
        lock (Gate) _message = text is null ? null : (area, text);
        Changed?.Invoke();
    }

    // Uma ação do utilizador nas Definições, em segundo plano (as rotas da página não esperam pela rede).
    public static void Act(string area, Func<SyncAgent, Task> action)
    {
        if (Agent() is null) return;
        Hook();
        lock (Gate) _message = null;
        _ = Task.Run(async () =>
        {
            var error = await RunAsync(action).ConfigureAwait(false);
            lock (Gate) _message = error is null ? null : (area, error);
            Changed?.Invoke();
        });
    }

    // Uma ação que pode falhar com uma mensagem para o utilizador (código errado, pedido expirado…).
    public static async Task<string?> RunAsync(Func<SyncAgent, Task> action)
    {
        if (Agent() is not { } agent) return "A sincronização não está disponível nesta compilação.";
        try
        {
            await action(agent).ConfigureAwait(false);
            if (agent.Enabled)
            {
                lock (Gate) Schedule.Done(Now(), agent.Status.Error is null);
                Arm();
            }
            return null;
        }
        catch (SyncUserException e) { return e.Message; }
        // Uma falha inesperada também chega à página (antes perdia-se e a página ficava à espera para sempre).
        catch (Exception e) { return "Não foi possível concluir (" + e.Message + ")."; }
    }

    // ---- estado para a interface ------------------------------------------------------------------------------------

    public static SyncStatus? Status => Enabled || Agent()?.Status.Phase is SyncPhase.Stopped ? Agent()?.Status : null;

    // «há 2 min», «há 1 h», e depois de um dia a data: «07/11 às 14:05».
    public static string Ago(long ms, long nowMs)
    {
        var seconds = Math.Max(0, (nowMs - ms) / 1000);
        if (seconds < 60) return "agora mesmo";
        if (seconds < 3600) return $"há {seconds / 60} min";
        if (seconds < 86_400) return $"há {seconds / 3600} h";
        var when = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        return when.ToString("dd/MM 'às' HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    // O estado numa linha (menu da conta).
    public static string Summary(SyncStatus? status, long nowMs)
    {
        if (status is null) return "Sincronização desligada";
        if (status.Deleting) return status.DeleteError is null ? "A apagar os dados do Drive…" : "Por apagar do Drive: veja Definições › Sincronização";
        // Um pedido pronto: responde-se na barra da janela, escrevendo o código que o computador novo mostra (este
        // computador nunca o mostra).
        if (status.Requests.Count > 0) return "Um computador novo pede para entrar";
        if (status.NeedsSignIn) return "Sincronização em pausa: entre outra vez";
        if (status.WaitingRequests > 0 && status.Phase == SyncPhase.On) return "Um computador novo pede para entrar";
        return status.Phase switch
        {
            SyncPhase.Stopped => "Sincronização parada",
            SyncPhase.Choose => "Falta juntar este computador à conta",
            SyncPhase.Pairing => status.Pairing?.Code is { } code ? $"Confirme o código {code}" : "À espera da aprovação noutro computador",
            SyncPhase.Starting => "A ligar…",
            _ when status.RecoveryCode is not null => "Guarde o código de recuperação",
            _ when status.Running => "A sincronizar…",
            _ when status.Error is not null => "Por sincronizar: o Éteris tenta outra vez sozinho",
            _ when status.NeedsUpdate => "Atualize o Éteris para sincronizar tudo",
            _ when status.LastSuccessMs > 0 => "Sincronizado " + Ago(status.LastSuccessMs, nowMs),
            _ => "Sincronização ligada",
        };
    }

    // «none», «replaced», «waiting», «code», «joined», «timeUp», «stillWaiting», «refused», «failed».
    internal static string GuestKind(GuestPairingKind kind)
    {
        var name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static object StatusData(SyncStatus? status, long nowMs) => status is null
        ? new { phase = "off" }
        : new
        {
            phase = status.Phase.ToString().ToLowerInvariant(),
            running = status.Running,
            last = status.LastSuccessMs,
            lastText = status.LastSuccessMs > 0 ? "Sincronizado " + Ago(status.LastSuccessMs, nowMs) + "." : null,
            error = status.Error,
            needsSignIn = status.NeedsSignIn,
            notice = status.Notice,
            code = status.RecoveryCode,
            pairing = status.Pairing is { } p ? new { code = p.Code, confirmed = p.Confirmed, error = p.Error } : null,
            // Sem o código de verificação: a página não o mostra (a aprovação é na barra da janela, escrevendo-o). Sem o
            // nome: quem pede escolhe-o e a página não o usa.
            requests = status.Requests.Select(r => new { id = r.Session }),
            // Pedidos em série: a barra não abre sozinha e as Definições mostram o aviso.
            pairingQuiet = status.PairingQuiet,
            // A conta na nuvem foi apagada ou substituída (alerta), e se este computador pode pedir a aprovação sozinho.
            chainReplaced = status.ChainReplaced,
            mayAutoPair = status.MayAutoPair,
            // O que o computador novo mostra (o cartão e o alerta): a página decide só por isto.
            guest = GuestKind(GuestPairingModel.From(status).Kind),
            devices = status.Devices.Select(d => new
            {
                id = d.Id, name = d.Name, me = d.IsThis,
                last = d.LastSyncMs > 0 ? "sincronizou " + Ago(d.LastSyncMs, nowMs) : "ainda não sincronizou",
            }),
            update = status.NeedsUpdate,
            deleting = status.Deleting,
            deleteError = status.DeleteError,
            summary = Summary(status, nowMs),
            // Há segredos em jogo (aprovar um aparelho novo também lhos entrega e pede o Windows Hello, na barra).
            secrets = Agent()?.HasSecrets == true,
            // O nome em vigor deste computador (para as Definições; só vem do agente local, nunca de outro aparelho).
            deviceName = Agent()?.DeviceName,
        };
}

// O estado da sincronização num ficheiro do perfil, cifrado com DPAPI do utilizador do Windows (outra «entropia» que
// os tokens). Tem o segredo da conta: nunca sai deste computador.
internal sealed class DpapiSyncStore(string path, Eteris.Sync.Google.ISecretProtector protector) : ISyncStore
{
    public SavedSync? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Json.Parse<SavedSync>(protector.Unprotect(File.ReadAllBytes(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public void Save(SavedSync saved)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        // Até ao disco antes de trocar: «desligada, a apagar» tem de valer mesmo que o Éteris (ou o Windows) feche logo a
        // seguir.
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(protector.Protect(Json.Bytes(saved)));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }
}
