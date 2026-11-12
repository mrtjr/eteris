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
                // O computador novo pede a aprovação sozinho na ronda do gesto de entrar (só sem nenhum estado desta
                // conta). Depende da aprovação com o código escrito no outro computador (T8): sai com ela.
                AutoPair = true,
                // O pedido do computador novo só se renova com o ecrã dele à vista (cartão ou Definições, há menos de 15 s).
                PairingScreenVisible = () => Now() - Interlocked.Read(ref _pairingSeen) < 15_000,
                // A primeira ronda a seguir a uma aprovação corre no agente, fora do agendador: quando acaba, o agendador
                // acerta-se como depois de «Ligar» (nova tentativa em 30 s se falhou, o período de sempre se correu bem).
                RoundEnded = ok =>
                {
                    lock (Gate)
                    {
                        Schedule.Now(Now());
                        Schedule.Done(Now(), ok);
                        if (PausedAfterRound()) Schedule.Stop();
                    }
                    Arm();
                },
                // Só nomes opacos do transporte e o tipo da falha (nunca dados): para o diagnóstico com um depurador.
                // Textos fixos e o tipo da falha: o registo local (SyncLog) e, para um depurador, a saída de depuração.
                Log = m =>
                {
                    System.Diagnostics.Debug.WriteLine("[sincronização] " + m);
                    SyncLog.Write(m);
                },
            };
            // Observa primeiro e avisa depois: as janelas refrescam as barras a partir de Changed e têm de já encontrar o
            // aviso pendente (o agendador para quando é preciso entrar, por isso pode não haver outra mudança).
            SyncAgent agent = _agent;
            _agent.Changed += () =>
            {
                var first = !FolderMode && Expired.Observe(Guarded(() => agent.Status.NeedsSignIn, false));
                Changed?.Invoke();
                if (first) NeedsSignIn?.Invoke();
            };
            return _agent;
        }
    }

    // Rede de segurança: ler o estado para a interface (barras, menu, agendador) ou gravar uma resposta de uma barra
    // nunca deixa sair uma exceção para a thread da IU nem para quem avisou a mudança (sem tratador global, fechava o
    // Éteris). Falhou: o valor de «sem estado», e segue.
    internal static T Guarded<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine("[sincronização] o estado não se leu: " + e.GetType().Name);
            return fallback;
        }
    }

    // Depois de uma ronda (StartAsync, Tick e RoundEnded, sempre com Gate): a autorização da Google retirada ou expirada
    // (NeedsSignIn) ou a sincronização desligada param o agendador — nada se resolve sozinho até entrar outra vez ou
    // voltar a ligar. O agente, uma vez criado, é sempre o mesmo (_agent).
    private static bool PausedAfterRound() => Guarded(() => _agent is { } a && (a.Status.NeedsSignIn || !a.Enabled), false);

    internal static void Guarded(Action act)
    {
        try { act(); }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine("[sincronização] a ação não se cumpriu: " + e.GetType().Name);
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
        get { return Expired.Pending && !FolderMode && Guarded(() => _agent?.Status.NeedsSignIn == true, false); }
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
        // Chamado na thread de quem mudou uma definição ou um favorito: nunca lança.
        if (!Guarded(() => Enabled && Agent()?.Status.NeedsSignIn != true, false)) return;
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
                    if (PausedAfterRound()) Schedule.Stop();
                }
            }
            catch (Exception e)
            {
                SyncLog.Write($"O agendador falhou ({e.GetType().Name}).");
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
        // O gesto de entrar ou de ligar pode criar um pedido novo (computador novo): o cartão volta a abrir-se.
        SetGuestCard(true);
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
            if (PausedAfterRound()) Schedule.Stop();
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
        try { agent.BeginCloudDeletion(); }
        catch (SyncUserException e)
        {
            // Com o estado por ler, nada se apaga: a mensagem diz porquê (e a página oferece pôr o ficheiro de lado).
            Say("delete", e.Message);
            return;
        }
        lock (Gate) Schedule.Stop();
        Arm();
        Act("delete", a => a.FinishCloudDeletionAsync());
    }

    // «Recomeçar sem o estado antigo»: com o estado da sincronização por ler (StateUnreadable), põe o ficheiro de lado
    // (sync.state.ilegivel, que sai com «apagar a chave») e este computador fica como um computador novo, desligado.
    // false = não havia nada a pôr de lado, ou não foi possível (a mensagem fica na área «signout»).
    internal static bool SetAsideState()
    {
        if (Agent() is not { Status.StateUnreadable: true } agent) return false;
        try { File.Move(StatePath, StatePath + DpapiSyncStore.AsideSuffix, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say("signout", "Não foi possível pôr o ficheiro de lado. Feche os outros programas que o estejam a usar e tente outra vez.");
            return false;
        }
        agent.ReloadState();
        return true;
    }

    // «Tentar de novo» depois de um apagar do Drive que não acabou (já decidido: sem outra confirmação).
    public static void RetryDeleteCloud()
    {
        if (Agent() is not { DeletePending: true }) return;
        Act("delete", a => a.FinishCloudDeletionAsync());
    }

    // «Sair»: a sincronização para neste computador. Nada é revogado na Google (só «Retirar o acesso», RevokeAccess);
    // os tokens deste computador saem pelo GoogleSync.SignOut. Também a parte local das outras saídas.
    public static void SignOut(bool forgetKey)
    {
        // O kit aberto fecha-se já (as janelas voltam às capturas; copiar, guardar e imprimir deixam de valer).
        OpenKit.Close();
        // Quem sai não fica com a barra de «sessão expirada» à vista.
        DismissSignInPrompt();
        // A chave que não se conseguiu apagar do disco (caso raro: SecretFile): a sincronização parou na mesma, o resto
        // de «Sair» segue (os tokens da Google saem) e a mensagem fica para a página (área «signout»).
        try { Agent()?.SignOut(forgetKey); }
        catch (SyncUserException e) { Say("signout", e.Message); }
        // O registo local sai com a chave (não fica nada deste computador depois de «apagar a chave»).
        if (forgetKey) SyncLog.Delete();
        lock (Gate) Schedule.Stop();
        Arm();
    }

    // «Sair deste computador», inteiro: a sincronização para aqui e os tokens e a conta guardada deste computador saem
    // (na pasta de teste não há Google). O mesmo para a rota das Definições e para o «Sair» do menu ⋯. Se gravar o estado
    // falhar (ficheiro preso, disco cheio), os tokens saem na mesma e a exceção segue para quem chamou.
    public static void SignOutHere(bool forgetKey)
    {
        try { SignOut(forgetKey); }
        finally { if (!FolderMode) GoogleSync.SignOut(); }
    }

    // Há uma chave da sincronização guardada neste computador (para o aviso do «Sair» do menu).
    public static bool HasKey => Agent()?.HasKey == true;

    // ---- saídas da conta (0.2.15, T12): «Retirar o acesso», «Perdi um computador», «Começar do zero…» ---------------

    private static volatile string? _exitBusy;

    // A saída em curso («revoke», «lost», «startover») ou null: a página desativa os três botões e pergunta o estado amiúde.
    public static string? ExitBusy => _exitBusy;

    // Corre uma saída em segundo plano. Não usa Act (que apaga uma mensagem de sucesso posta dentro da ação): a mensagem
    // final, de sucesso ou não, é a que a saída devolve, tal e qual. Uma de cada vez.
    private static void Exit(string area, Func<Task<string>> run)
    {
        lock (Gate)
        {
            if (_exitBusy is not null) return;
            _exitBusy = area;
            _message = null;
        }
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            string message;
            try { message = await run().ConfigureAwait(false); }
            catch (Exception e) { message = "Não foi possível concluir (" + e.Message + ")."; }
            // A mensagem e o fim chegam juntos à página (nunca «acabou» sem a mensagem).
            lock (Gate)
            {
                _message = (area, message);
                _exitBusy = null;
            }
            Changed?.Invoke();
        });
    }

    // A revogação na Google, com um limite de 30 s.
    private static async Task<Eteris.Sync.Google.RevokeOutcome> RevokeWithLimitAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        return await GoogleSync.RevokeAccessAsync(limit.Token).ConfigureAwait(false);
    }

    // «Retirar o acesso do Éteris à conta Google» (já confirmado na página). Não existe na pasta de teste.
    public static void RevokeAccess()
    {
        if (FolderMode) return;
        Exit("revoke", async () =>
        {
            var (_, message) = await SyncExit.RevokeAsync(RevokeWithLimitAsync, () => SignOut(forgetKey: false), CancellationToken.None).ConfigureAwait(false);
            return message;
        });
    }

    // «Perdi um computador / foi roubado» (o Windows já confirmou): remove o aparelho e, só com a troca de chave acabada,
    // retira o acesso na Google. Na pasta de teste, só remove.
    public static void LostDevice(string device, IReadOnlyCollection<string>? shown)
    {
        if (Agent() is not { } agent) return;
        Hook();
        Exit("lost", async () =>
        {
            var result = await SyncExit.LostDeviceAsync(agent, device, shown, FolderMode ? null : RevokeWithLimitAsync,
                () => SignOut(forgetKey: false), CancellationToken.None).ConfigureAwait(false);
            // Sem a revogação, a sincronização continua aqui: o agendador segue como depois de uma ação (RunAsync).
            AfterExit(agent);
            return result.Message;
        });
    }

    // «Começar do zero…» (a palavra e o Windows já confirmados): apaga a conta antiga da nuvem e cria uma nova a partir
    // deste computador. Só antes de entrar (SyncExit confere a fase).
    public static void StartOver()
    {
        if (Agent() is not { } agent) return;
        Hook();
        Exit("startover", async () =>
        {
            var (_, message) = await SyncExit.StartOverAsync(agent, CancellationToken.None).ConfigureAwait(false);
            AfterExit(agent);
            return message;
        });
    }

    // Acerta o agendador depois de uma saída que deixou a sincronização ligada aqui (com ela parada, não se arma).
    private static void AfterExit(SyncAgent agent)
    {
        if (!agent.Enabled) return;
        lock (Gate)
        {
            Schedule.Done(Now(), agent.Status.Error is null);
            if (PausedAfterRound()) Schedule.Stop();
        }
        Arm();
    }

    // O utilizador abriu o menu do Éteris (⋯) ou tem a secção Sincronização à vista: procura pedidos de aparelhos novos
    // e responde-lhes (no máximo uma vez a cada 5 s) e, com «devices», relê a lista de aparelhos.
    public static void Look(bool devices = false)
    {
        if (!Enabled || Agent() is not { } agent) return;
        // Em pausa até entrar outra vez (NeedsSignIn), como Touch: nenhum pedido ao Drive nem ao endpoint de tokens.
        if (Guarded(() => agent.Status.NeedsSignIn, true)) return;
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

    // O destino de «Retirar o acesso do Éteris» nas barras: o item próprio nas Definições (a página leva-lhe o foco).
    // Um só sítio.
    internal const string RevokeAnchor = "#syncRevokeItem";

    // O que a barra de aprovação mostra agora. Lê o campo (nunca cria o agente; sem agente, nada) e só memória: é
    // chamado pelo «wants» da candidata, a cada RefreshBars.
    internal static PairingBarView PairingBarState()
    {
        var agent = _agent;
        // Com a conta a sincronizar pagamentos e documentos, o computador novo recebe também cartões e documentos.
        return PairingBarModel.From(Guarded(() => agent?.Status, null), Now(), SyncChoices.Receives(_agent?.AccountSecrets == true));
    }

    // ---- pagamentos e documentos: a oferta única (0.2.15, T10) ------------------------------------------------------

    // «Fechar» na barra da oferta: escondida até à abertura seguinte do Éteris (a oferta continua nas Definições). Só
    // memória, partilhado por todas as janelas.
    private static volatile bool _offerClosed;

    // A barra da oferta deve estar à vista. Lê o campo (nunca cria o agente): barato, sem E/S nem rede.
    public static bool SecretsOfferPrompt
    {
        get { return !_offerClosed && Guarded(() => _agent?.SecretsOffer == true, false); }
    }

    public static void CloseSecretsOffer()
    {
        _offerClosed = true;
        Changed?.Invoke();
    }

    // A resposta à oferta «receber também neste?» (barra e Definições). «Sim» pede o Windows Hello (confirm). Devolve a
    // mensagem de erro, ou null.
    public static async Task<string?> AnswerSecretsOfferAsync(bool accept, Func<Vault.ReauthPurpose, Task<bool>> confirm)
    {
        if (Agent() is not { } agent) return null;
        var error = await SyncChoices.AnswerOfferAsync(agent, accept, confirm).ConfigureAwait(false);
        Say("offer", error);
        if (error is null && accept) SyncNow();
        return error;
    }

    // A pessoa escreveu na barra o código que o computador novo mostra. «confirm» é a porta do Windows da janela que tem
    // a barra: o agente só a chama depois do código certo, e só entrega a conta depois dela (quando há segredos em
    // jogo). Não apanha SyncUserException: a barra mostra a mensagem.
    public static Task<PairingAnswer> ApprovePairingAsync(string session, string typed, Func<Vault.ReauthPurpose, Task<bool>> confirm)
    {
        if (Agent() is not { } agent) return Task.FromResult(PairingAnswer.Gone);
        return agent.ApproveWithCodeAsync(session, typed, () => SyncChoices.MayApproveAsync(agent, confirm));
    }

    // Definições › Sincronização, só com o teclado (T8K): o mesmo caminho da barra (ApprovePairingAsync, com a porta do
    // Windows da janela da aba). Só quando a barra o deixaria (ligada e não calada pelos pedidos em série). A resposta
    // vai para a área «approve» da página, com os textos da barra.
    public static async Task ApprovePairingFromPageAsync(string session, string typed, Func<Vault.ReauthPurpose, Task<bool>> confirm)
    {
        if (!PairingBarModel.MayApproveHere(_agent?.Status))
        {
            Say("approve", PairingBarModel.GoneNote);
            return;
        }
        try
        {
            var answer = await ApprovePairingAsync(session, typed, confirm).ConfigureAwait(false);
            var left = _agent?.Status.Requests.FirstOrDefault(r => r.Session == session)?.AttemptsLeft ?? 0;
            Say("approve", PairingBarModel.PageNote(answer, left));
        }
        catch (SyncUserException e)
        {
            // Não se entregou (sem rede, o pedido expirou entretanto…): o pedido continua e diz-se porquê.
            Say("approve", e.Message);
        }
        catch (OperationCanceledException)
        {
            Say("approve", PairingBarModel.PageNote(PairingAnswer.NotConfirmed, 0));
        }
        catch (Exception)
        {
            // Nunca a mensagem crua de uma exceção inesperada na página.
            Say("approve", SyncChoices.CouldNotFinish);
        }
    }

    // «Não fui eu»: recusa o pedido (o computador novo fica a saber). Devolve se recusou — com uma aprovação desse
    // pedido já a meio (porta do Windows, entrega) nada é recusado e a barra não diz «Recusado». A decisão é imediata
    // (o agente tira o pedido já, sem rede); a escrita da recusa segue em segundo plano.
    public static bool RefusePairing(string session)
    {
        if (Agent() is not { } agent) return false;
        if (Guarded(() => agent.BeginRefuse(session), null) is not { } write) return false;
        Act("approve", _ => write(CancellationToken.None));
        return true;
    }

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
        if (_agent is not { } agent) return;
        // Na thread da IU, em cada ativação da janela: sem estado, nada.
        if (Guarded(() => Enabled ? agent.Status : null, null) is not { } status) return;
        // Em pausa até entrar outra vez (NeedsSignIn): voltar à janela não é um momento de rede.
        if (status.NeedsSignIn) return;
        // Com a barra calada (pedidos em série), um pedido contado já não dá direito a procurar logo: 5 minutos.
        var counted = status.PairingQuiet ? 0 : status.WaitingRequests;
        if (Activation.TryPass(Now(), counted, firstRoundDone: true)) Look();
    }

    // ---- o computador novo: cartão do pedido e alerta «conta substituída» (0.2.15, T7) -------------------------------

    // O que as barras do computador novo mostram agora. Lê o campo (nunca cria o agente; sem agente, nada) e só memória:
    // é chamado pelos «wants» das candidatas, a cada RefreshBars.
    internal static GuestPairingView GuestPairingState() => GuestPairingModel.From(Guarded(() => _agent?.Status, null));

    private static volatile bool _replacedDismissed;
    private static volatile bool _guestCardOpen = true;
    private static long _pairingSeen;

    // O ecrã do pedido do computador novo está à vista (o cartão em «à espera», de 5 em 5 s; a secção Sincronização das
    // Definições, em cada /sync-look). Só memória, sem rede: o agente lê-o para renovar o pedido (PairingScreenVisible).
    public static void PairingScreenSeen()
    {
        Interlocked.Exchange(ref _pairingSeen, Now());
    }

    // «Não fui eu» no alerta: a barra do alerta não volta até o Éteris fechar (o alerta continua nas Definições).
    internal static bool ReplacedAlertDismissed => _replacedDismissed;

    // Só memória: sem rede, sem agente.
    internal static void DismissReplacedAlert()
    {
        _replacedDismissed = true;
        Changed?.Invoke();
    }

    // O cartão do computador novo está aberto (por omissão) ou recolhido («Fechar»). Partilhado por todas as janelas,
    // só memória. «Ver código», «Fui eu», «Pedir outra vez» e um pedido novo voltam a abri-lo.
    internal static bool GuestCardOpen => _guestCardOpen;

    internal static void SetGuestCard(bool open)
    {
        if (_guestCardOpen == open) return;
        _guestCardOpen = open;
        Changed?.Invoke();
    }

    // «Fui eu — juntar este computador» (alerta de conta substituída): junta este computador à conta que está AGORA no
    // Drive. Com segredos ligados aqui, o Windows confirma primeiro (sempre). true = o pedido foi lançado; false = não
    // foi confirmado, ou já não se aplica. Não apanha as exceções da confirmação: quem chama trata.
    public static async Task<bool> JoinAfterReplacedAsync(Func<Vault.ReauthPurpose, Task<bool>> confirm)
    {
        if (Agent() is not { } agent || !agent.Status.ChainReplaced) return false;
        if (!await SyncChoices.MayJoinReplacedAsync(agent, confirm).ConfigureAwait(false))
        {
            Say("replaced", SyncChoices.NotConfirmed("este computador não entrou na conta"));
            return false;
        }
        lock (Gate) if (_message?.Area == "replaced") _message = null;
        SetGuestCard(true);
        // Parada por a conta ter desaparecido: a ronda do gesto («Ligar outra vez») vê a conta que lá está agora.
        if (agent.Status.Phase == SyncPhase.Stopped) await StartAsync().ConfigureAwait(false);
        if (agent.Status.Phase == SyncPhase.Choose) agent.RequestPairing();
        return true;
    }

    // Entrar com o kit (colado ou do ficheiro) quando a conta foi substituída manda para a conta que está agora no Drive o
    // mesmo que «Fui eu»: com segredos ligados aqui, o Windows confirma primeiro (sempre). Fora desse estado, nada a pedir.
    public static async Task<bool> MayJoinWithKitAsync(Func<Vault.ReauthPurpose, Task<bool>> confirm)
    {
        if (Agent() is not { } agent || !agent.Status.ChainReplaced) return true;
        if (await SyncChoices.MayJoinReplacedAsync(agent, confirm).ConfigureAwait(false)) return true;
        Say("replaced", SyncChoices.NotConfirmed("este computador não entrou na conta"));
        return false;
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

    // ---- lembrete do kit de recuperação (0.2.15, T5) ----------------------------------------------------------------

    // Quando este processo começou a usar a sincronização: um segredo que já cá estava antes é «a abertura seguinte».
    private static readonly long SessionStartMs = Now();

    // O kit aberto pela confirmação do Windows (um só, num separador, até 5 minutos): Sync.KitGate.
    internal static readonly KitUnlock OpenKit = new(Now);
    // A barra do lembrete já foi respondida nesta abertura (qualquer dos três botões). Sob Gate; só memória.
    private static bool _kitAnswered;

    // A barra do lembrete do kit, ou null. Barata (contrato das candidatas da T4): lê só o agente que já existe (o
    // campo), nunca o cria, e não faz E/S. Vale também na pasta de teste.
    public static KitReminderView? KitBar(bool isPrivate)
    {
        SyncAgent? agent;
        lock (Gate)
        {
            agent = _agent;
            if (agent is null || _kitAnswered || isPrivate) return null;
        }
        if (Guarded(() => agent.Status, null) is not { } status) return null;
        var view = RecoveryReminder.Decide(new KitReminderInput(status.Kit, status.Phase == SyncPhase.On, isPrivate, SessionStartMs, false), Now());
        return view.Bar ? view : null;
    }

    // Respondida (qualquer botão): não volta nesta abertura; todas as janelas a escondem.
    public static void KitBarAnswered()
    {
        lock (Gate) _kitAnswered = true;
        Changed?.Invoke();
    }

    // «Mais tarde»: 30 dias (RecoveryReminder.SnoozeDays).
    public static void SnoozeKit()
    {
        // Grava no disco, na thread da IU: se o disco falhar, a barra fecha na mesma (o lembrete volta noutra abertura).
        Guarded(() => _agent?.SnoozeKit(RecoveryReminder.SnoozeUntil(Now())));
        KitBarAnswered();
    }

    // «Não quero: aceito que…»: acaba com o lembrete (barra e menu), também depois de reiniciar.
    public static void DeclineKit()
    {
        Guarded(() => _agent?.DeclineKit());
        KitBarAnswered();
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

    // Uma ação do utilizador nas Definições, em segundo plano (as rotas da página não esperam pela rede). A mensagem
    // final de uma saída (SyncExit.IsExitArea) não sai por uma ação que acabe bem (SyncExit.AfterAction).
    public static void Act(string area, Func<SyncAgent, Task> action)
    {
        if (Agent() is null) return;
        Hook();
        lock (Gate) _message = SyncExit.AfterAction(_message, area, null);
        _ = Task.Run(async () =>
        {
            var error = await RunAsync(action).ConfigureAwait(false);
            lock (Gate) _message = SyncExit.AfterAction(_message, area, error);
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
                lock (Gate)
                {
                    Schedule.Done(Now(), agent.Status.Error is null);
                    if (PausedAfterRound()) Schedule.Stop();
                }
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
        // A conta mudou de chave noutro computador e este espera pelo identificador do kit novo (SyncAgent.ConfirmRekeyAsync).
        if (status.RekeyPending) return "Sincronização em pausa: confirme a troca de chave";
        return status.Phase switch
        {
            SyncPhase.Stopped => "Sincronização parada",
            SyncPhase.Choose => "Falta juntar este computador à conta",
            // Este computador mostra o código; o outro escreve-o (PROPOSTA P1).
            SyncPhase.Pairing => status.Pairing?.Code is { } code ? $"Escreva o código {code} no outro computador" : "À espera da aprovação noutro computador",
            SyncPhase.Starting => "A ligar…",
            _ when RecoveryReminder.MenuLine(status.Kit) => RecoveryReminder.MenuText,
            _ when status.Running => "A sincronizar…",
            _ when status.Error is not null => "Por sincronizar: o Éteris tenta outra vez sozinho",
            _ when status.NeedsUpdate => "Atualize o Éteris para sincronizar tudo",
            _ when status.LastSuccessMs > 0 => "Sincronizado " + Ago(status.LastSuccessMs, nowMs),
            _ => "Sincronização ligada",
        };
    }

    // A linha da frente em Definições › Sincronização, com sessão. Null: não há nada a dizer aqui (sem sincronização
    // ligada, ou há um aviso próprio a mostrar em vez disto). Um computador à espera de aprovação não está «Ligada».
    public static string? Headline(SyncStatus? status, long nowMs)
    {
        if (status is null || status.Phase is not (SyncPhase.On or SyncPhase.Starting) || status.NeedsSignIn || status.Deleting) return null;
        // O erro e «atualize o Éteris» aparecem no sítio de sempre, fora de «Mais opções».
        if (!string.IsNullOrEmpty(status.Error) || status.NeedsUpdate) return null;
        if (status.Phase == SyncPhase.Starting) return "A ligar…";
        if (status.Running) return "A sincronizar…";
        if (status.LastSuccessMs > 0) return "Ligada · última sincronização " + Ago(status.LastSuccessMs, nowMs);
        return "Ligada · a primeira sincronização está a caminho";
    }

    // «none», «replaced», «waiting», «code», «joined», «timeUp», «stillWaiting», «refused», «failed».
    internal static string GuestKind(GuestPairingKind kind) => Camel(kind);

    // O nome de um valor como a página o lê («stillWaiting», «timeUp»…).
    private static string Camel(Enum value)
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    // O que ficou à espera numa ronda «bem sucedida»: alterações de aparelhos que este computador ainda não conseguiu ler e
    // itens do cofre que ficaram de fora. null = nada à espera.
    public static string? WaitingText(SyncStatus status)
    {
        var parts = new List<string>();
        if (status.PendingDevices > 0)
            parts.Add($"Há alterações de {status.PendingDevices} {(status.PendingDevices == 1 ? "aparelho" : "aparelhos")} que este computador ainda não conseguiu ler; o Éteris tenta outra vez na próxima ronda.");
        if (status.SecretErrors == 1) parts.Add("1 item do cofre ficou de fora desta ronda (não abriu ou não se pôde cifrar).");
        else if (status.SecretErrors > 1) parts.Add($"{status.SecretErrors} itens do cofre ficaram de fora desta ronda (não abriram ou não se puderam cifrar).");
        return parts.Count == 0 ? null : string.Join(' ', parts);
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
            pendingDevices = status.PendingDevices,
            secretErrors = status.SecretErrors,
            notice = new[] { status.Notice, WaitingText(status) }.Where(t => !string.IsNullOrEmpty(t)) is var texts && texts.Any() ? string.Join(' ', texts) : null,
            // O kit de recuperação sem o segredo (as palavras só pela rota do kit, depois da confirmação do Windows):
            // a impressão digital, «saved», «pending» ou «stale», a recusa e quantos computadores se conhecem.
            kit = status.Kit is { } k
                ? new { id = k.Id, state = k.State.ToString().ToLowerInvariant(), declined = k.Declined, devices = k.KnownDevices }
                : null,
            pairing = status.Pairing is { } p
                ? new { code = p.Code, confirmed = p.Confirmed, error = p.Error, hostAnswered = p.HostAnswered, end = Camel(p.End) }
                : null,
            // Sem o código de verificação: a página não o mostra (aprova-se na barra ou nas Definições, escrevendo-o).
            // O nome só passa limpo e cortado, como na barra (quem pede escolhe-o depois de conhecer o código).
            requests = status.Requests.Select(r => new { id = r.Session, nameLine = PairingBarModel.NameLine(r.Name) }),
            // Os textos de cada pedido nas Definições: os da barra (T8K).
            requestTitle = PairingBarModel.Title(SyncChoices.Receives(status.AccountSecrets)) + ".",
            requestAsk = PairingBarModel.Ask,
            requestBox = PairingBarModel.CodeBoxName,
            // Pedidos em série: a barra não abre sozinha e as Definições mostram o aviso.
            pairingQuiet = status.PairingQuiet,
            // A conta na nuvem foi apagada ou substituída (alerta), e se este computador pode pedir a aprovação sozinho.
            chainReplaced = status.ChainReplaced,
            // A pausa da troca de chave: a página mostra a caixa do identificador do kit novo em vez de «Ligar outra vez».
            rekeyPending = status.RekeyPending,
            // O estado guardado não se leu: a página oferece «Recomeçar sem o estado antigo».
            stateUnreadable = status.StateUnreadable,
            mayAutoPair = status.MayAutoPair,
            // O que o computador novo mostra (o cartão e o alerta): a página decide só por isto.
            guest = GuestKind(GuestPairingModel.From(status).Kind),
            devices = status.Devices.Select(d => new
            {
                id = d.Id, name = d.Name, me = d.IsThis,
                last = d.LastSyncMs > 0 ? "sincronizou " + Ago(d.LastSyncMs, nowMs) : "ainda não sincronizou",
                // Recebe pagamentos e documentos (null: um Éteris antigo, que não o diz).
                receivesSecrets = d.PaymentsAndDocuments,
            }),
            // A conta sincroniza pagamentos e documentos, e a oferta única «receber também neste?».
            accountSecrets = status.AccountSecrets,
            offer = status.SecretsOffer,
            update = status.NeedsUpdate,
            deleting = status.Deleting,
            deleteError = status.DeleteError,
            summary = Summary(status, nowMs),
            // A linha de estado à frente nas Definições (null: esconde-se e fala o item próprio).
            headline = Headline(status, nowMs),
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
            // Um «apagar» que ficou por fazer (o ficheiro estava preso por outro processo): acaba agora; enquanto não
            // acabar, não há estado — a chave que a pessoa mandou apagar nunca volta a ser lida (SecretFile).
            if (!SecretFile.MayRead(path) || !File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return bytes.Length == 0 ? null : Json.Parse<SavedSync>(protector.Unprotect(bytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            // Existe e não se leu: não é «não existe» (SyncStateUnreadableException): o agente não o substitui por um vazio.
            throw new SyncStateUnreadableException("O estado da sincronização não se leu.", e);
        }
    }

    // O sufixo do ficheiro que «Recomeçar sem o estado antigo» põe de lado (SyncService.SetAsideState).
    internal const string AsideSuffix = ".ilegivel";

    public void Save(SavedSync saved)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SecretFile.BeforeWrite(path);
        var temp = path + ".tmp";
        // Até ao disco antes de trocar: «desligada, a apagar» tem de valer mesmo que o Éteris (ou o Windows) feche logo a
        // seguir.
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(protector.Protect(Json.Bytes(saved)));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
        SecretFile.AfterWrite(path);
    }

    // «Sair» com «apagar a chave»: o ficheiro e o temporário saem. Se o Windows não deixar apagar agora (preso por outro
    // processo, sem permissão), fica marcado para apagar e esvaziado no que der: a abertura seguinte não retoma com a
    // chave que a pessoa mandou apagar. Só lança (IOException) se nem isso foi possível.
    public void Delete()
    {
        SecretFile.Delete(path);
        // O ficheiro posto de lado pode ter a chave: sai com «apagar a chave» (melhor esforço).
        try { File.Delete(path + AsideSuffix); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
