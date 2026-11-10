using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Sync;
using Eteris.Browser.Tests.Vault;
using Eteris.Browser.Vault;
using Eteris.Sync.Crypto;
using Eteris.Sync.Engine;

namespace Eteris.Browser.Tests.Sync;

// O kit de recuperação (0.2.15, T5): o documento, para onde se grava, a porta do Windows e o kit aberto num separador.
// Nada aqui toca na área de transferência real, no Windows Hello real, na rede nem no perfil do dono.
public class RecoveryKitTests
{
    // O vetor público «e» da T6.
    private const string Phrase = "abacate afivelar inativo afivelar donzela achatar barulho beber albergue exalar cuidado baioneta abotoar joia dever informal argola peneira aditivo celebrar ouvido amarelo gritaria toalha";
    private static readonly DateTime Day = new(2026, 11, 9);

    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine([GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", .. parts])).Replace("\r\n", "\n");

    private static int Count(string text, string what) => Regex.Matches(text, Regex.Escape(what)).Count;

    // ---- o documento ------------------------------------------------------------------------------------------------

    [Fact]
    public void Html_HasTheWordsTheIdAndTheWarnings()
    {
        var html = RecoveryKit.Html(Phrase, "K7QF", Day);
        foreach (var word in Phrase.Split(' ')) Assert.Contains(word, html);
        Assert.Contains("K7QF", html);
        Assert.Contains("9 de novembro de 2026", html);
        Assert.Contains("Conta Google:", html);
        Assert.Contains("Não fotografe com o telemóvel", html);
        Assert.Contains("guarde-o como guardaria uma chave de casa", html);
        Assert.Contains("O kit sozinho não abre nada", html);
        Assert.Contains(RecoveryKit.KeepOffThisComputer, html);
        Assert.Contains("lang=\"pt-PT\"", html);
        Assert.Contains("data-eteris-kit=\"" + Phrase + "\"", html);
        Assert.Equal("data-eteris-kit", RecoveryCode.KitAttribute);
    }

    [Fact]
    public void Html_HasNoEmail_NoScript_NoExternalRequests()
    {
        var html = RecoveryKit.Html(Phrase, "K7QF", Day);
        foreach (var bad in new[] { "@", "<script", "http:", "https:", "<img", "<link", "<iframe", "src=", "href=", "url(" })
            Assert.DoesNotContain(bad, html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch("([0-9A-Z]{4}-){13}[0-9A-Z]{4}", html);
        // Revisão final (I-07): aberto num navegador com tradução automática, as palavras não vão para o tradutor.
        Assert.Contains("<html lang=\"pt-PT\" translate=\"no\">", html);
        Assert.Contains("<meta name=\"google\" content=\"notranslate\">", html);
    }

    [Fact]
    public void Html_CanBeReadBack()
    {
        using var s = AccountSecret.Create();
        var html = RecoveryKit.Html(s.ToWords(), s.KitId, DateTime.Today);
        Assert.True(RecoveryCode.TryReadKit(html, out var read));
        using var back = AccountSecret.FromBytes(read);
        Assert.Equal(s.ChainId, back.ChainId);
    }

    [Fact]
    public void FileName_HasTheDate_AndIsAValidName()
    {
        var name = RecoveryKit.FileName(Day);
        Assert.Equal("Kit de recuperação do Éteris 2026-11-09.html", name);
        Assert.True(name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    // ---- para onde vai o ficheiro -------------------------------------------------------------------------------------

    private static readonly string[] Roots = [@"C:\Users\teste\Downloads", @"C:\Users\teste\Documents", @"C:\Users\teste\Desktop"];

    private static string? Env(string name) => name == "OneDrive" ? @"C:\Users\teste\OneDrive" : null;

    private static KitFolder FolderOf(string path, DriveKind drive = DriveKind.Fixed, IReadOnlyList<string>? roots = null)
        => RecoveryKit.Folder(path, Env, roots ?? Roots, _ => drive);

    [Theory]
    [InlineData(@"C:\Users\teste\OneDrive\Documentos\kit.html", "OneDrive")]
    [InlineData(@"c:\users\TESTE\onedrive\documentos\kit.html", "OneDrive")]
    [InlineData(@"G:\O meu disco\kit.html", "Google Drive")]
    [InlineData(@"G:\My Drive\a\kit.html", "Google Drive")]
    [InlineData(@"C:\Users\teste\Dropbox\kit.html", "Dropbox")]
    public void Folder_DetectsTheCloudProviders(string path, string provider)
    {
        Assert.Equal(new KitFolder(KitFolderKind.Cloud, provider), FolderOf(path));
        // Uma pasta conhecida redirecionada para a nuvem: a nuvem ganha.
        Assert.Equal(new KitFolder(KitFolderKind.Cloud, "OneDrive"),
            FolderOf(@"C:\Users\teste\OneDrive\Documentos\kit.html", roots: [@"C:\Users\teste\OneDrive\Documentos"]));
    }

    [Fact]
    public void Folder_OnlyKnownLocalFoldersAreLocal()
    {
        Assert.Equal(KitFolderKind.Local, FolderOf(@"C:\Users\teste\Downloads\kit.html").Kind);
        Assert.Equal(KitFolderKind.Unknown, FolderOf(@"C:\Users\teste\Downloads2\kit.html").Kind);
        Assert.Equal(KitFolderKind.Unknown, FolderOf(@"C:\Users\teste\OneDriveX\kit.html").Kind);
        Assert.Equal(KitFolderKind.Unknown, FolderOf(@"D:\Coisas\kit.html", DriveKind.Fixed).Kind);
        Assert.Equal(KitFolderKind.Local, FolderOf(@"E:\kit.html", DriveKind.Removable).Kind);
        Assert.Equal(KitFolderKind.Unknown, FolderOf(@"Z:\kit.html", DriveKind.Network).Kind);
        Assert.Equal(KitFolderKind.Unknown, FolderOf(@"\\servidor\partilha\kit.html", DriveKind.Removable).Kind);
        Assert.Equal("Esta pasta vai para a nuvem de OneDrive. Quem tiver essa conta fica com o kit.",
            RecoveryKit.FolderWarning(new KitFolder(KitFolderKind.Cloud, "OneDrive")));
        Assert.Equal(RecoveryKit.UnknownFolderWarning, RecoveryKit.FolderWarning(new KitFolder(KitFolderKind.Unknown)));
        Assert.Equal("O Éteris não consegue confirmar que esta pasta fica só neste computador. Se for copiada para a nuvem, quem tiver essa conta fica com o kit.",
            RecoveryKit.UnknownFolderWarning);
    }

    private static readonly RecoveryKitSecret Kit = new(Phrase, "K7QF", 0);

    [Fact]
    public void Save_OutsideALocalFolder_AsksFirst_AndWritesNothing()
    {
        var writes = new List<(string Path, string Text)>();
        void Write(string path, string text) => writes.Add((path, text));
        foreach (var folder in new[] { new KitFolder(KitFolderKind.Cloud, "OneDrive"), new KitFolder(KitFolderKind.Unknown) })
        {
            Assert.Equal(KitSaveResult.NeedsConfirm, RecoveryKit.Save(Kit, @"C:\x\kit.html", false, folder, Write, Day));
            Assert.Empty(writes);
            Assert.Equal(KitSaveResult.Saved, RecoveryKit.Save(Kit, @"C:\x\kit.html", true, folder, Write, Day));
            var (path, text) = Assert.Single(writes);
            Assert.Equal(@"C:\x\kit.html", path);
            Assert.Equal(RecoveryKit.Html(Phrase, "K7QF", Day), text);
            writes.Clear();
        }
        Assert.Equal(KitSaveResult.Saved, RecoveryKit.Save(Kit, @"C:\x\kit.html", false, new KitFolder(KitFolderKind.Local), Write, Day));
        Assert.Single(writes);
    }

    [Fact]
    public void Save_Cancelled_WritesNothing_AndAnIoErrorIsFailed()
    {
        var writes = 0;
        Assert.Equal(KitSaveResult.Cancelled, RecoveryKit.Save(Kit, null, true, new KitFolder(KitFolderKind.Local), (_, _) => writes++, Day));
        Assert.Equal(0, writes);
        Assert.Equal(KitSaveResult.Failed, RecoveryKit.Save(Kit, @"C:\x\kit.html", true, new KitFolder(KitFolderKind.Local), (_, _) => throw new IOException("cheio"), Day));
        Assert.Equal(KitSaveResult.Failed, RecoveryKit.Save(Kit, @"C:\x\kit.html", true, new KitFolder(KitFolderKind.Local), (_, _) => throw new UnauthorizedAccessException(), Day));
    }

    // ---- a porta do Windows e o kit aberto --------------------------------------------------------------------------

    private long _now = 1_762_560_000_000;

    private sealed class Reveals(RecoveryKitSecret? kit)
    {
        public RecoveryKitSecret? Kit { get; set; } = kit;
        public int Calls { get; private set; }

        public RecoveryKitSecret? Reveal()
        {
            Calls++;
            return Kit;
        }
    }

    private sealed class Confirms(bool answer)
    {
        public List<ReauthPurpose> Asked { get; } = [];

        public Task<bool> Confirm(ReauthPurpose purpose)
        {
            Asked.Add(purpose);
            return Task.FromResult(answer);
        }
    }

    private sealed class Hold : IDisposable
    {
        public int Disposed { get; private set; }

        public void Dispose() => Disposed++;
    }

    [Fact]
    public async Task KitGate_WithoutWindowsConfirmation_RevealsNothing()
    {
        var reveals = new Reveals(Kit);
        var confirms = new Confirms(false);
        var unlock = new KitUnlock(() => _now);
        var shown = await KitGate.ShowAsync(reveals.Reveal, unlock, 1, false, confirms.Confirm, () => null);
        Assert.Equal(KitShowStep.Refused, shown.Step);
        Assert.Null(shown.Kit);
        Assert.Equal(0, reveals.Calls);
        Assert.Equal([ReauthPurpose.ShowRecoveryKit], confirms.Asked);
        Assert.False(unlock.IsOpen(1));
    }

    [Fact]
    public async Task KitGate_UnprotectedAccount_AdviceFirst_ThenAck()
    {
        var reveals = new Reveals(Kit);
        var unlock = new KitUnlock(() => _now);
        var first = await KitGate.ShowAsync(reveals.Reveal, unlock, 1, false, new Confirms(true).Confirm, () => ReauthGate.ProtectAccountAdvice);
        Assert.Equal(KitShowStep.AdviceFirst, first.Step);
        Assert.Equal(ReauthGate.ProtectAccountAdvice, first.Advice);
        Assert.Null(first.Kit);
        Assert.Equal(0, reveals.Calls);
        Assert.False(unlock.IsOpen(1));

        var acked = await KitGate.ShowAsync(reveals.Reveal, unlock, 1, true, new Confirms(true).Confirm, () => ReauthGate.ProtectAccountAdvice);
        Assert.Equal(KitShowStep.Reveal, acked.Step);
        Assert.Equal(Phrase, acked.Kit!.Words);
        Assert.Equal(ReauthGate.ProtectAccountAdvice, acked.Advice);
        Assert.True(unlock.IsOpen(1));

        var protectedAccount = await KitGate.ShowAsync(reveals.Reveal, new KitUnlock(() => _now), 1, false, new Confirms(true).Confirm, () => null);
        Assert.Equal(KitShowStep.Reveal, protectedAccount.Step);
        Assert.Null(protectedAccount.Advice);
    }

    [Fact]
    public async Task KitGate_Opened_OnlyInTheSameTab_AndForTheSameKit()
    {
        var reveals = new Reveals(Kit);
        var unlock = new KitUnlock(() => _now);
        Assert.Null(KitGate.Opened(reveals.Reveal, unlock, 1));
        Assert.Equal(0, reveals.Calls);

        var confirms = new Confirms(true);
        await KitGate.ShowAsync(reveals.Reveal, unlock, 1, false, confirms.Confirm, () => null);
        Assert.Equal(Kit, KitGate.Opened(reveals.Reveal, unlock, 1));
        Assert.Null(KitGate.Opened(reveals.Reveal, unlock, 2));
        // O kit aberto não dispensa a confirmação de «Ver».
        await KitGate.ShowAsync(reveals.Reveal, unlock, 1, false, confirms.Confirm, () => null);
        Assert.Equal(2, confirms.Asked.Count);

        // Cadeia nova: o kit aberto é de outra impressão digital — fecha.
        reveals.Kit = new RecoveryKitSecret("outras palavras", "ZZZZ", 0);
        Assert.Null(KitGate.Opened(reveals.Reveal, unlock, 1));
        Assert.False(unlock.IsOpen(1));
    }

    [Fact]
    public void KitUnlock_ExpiresClosesAndReleasesTheHold()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), KitUnlock.Limit);
        var unlock = new KitUnlock(() => _now);
        var hold = new Hold();
        unlock.Open(1, "K7QF", hold);
        _now += (long)KitUnlock.Limit.TotalMilliseconds - 1;
        Assert.True(unlock.IsOpen(1));
        _now += 1;
        Assert.False(unlock.IsOpen(1));
        Assert.Equal(1, hold.Disposed);
        unlock.Close();
        unlock.Close();
        Assert.Equal(1, hold.Disposed);

        var closed = new Hold();
        unlock.Open(1, "K7QF", closed);
        unlock.Close();
        Assert.Equal(1, closed.Disposed);

        var previous = new Hold();
        unlock.Open(1, "K7QF", previous);
        unlock.Open(2, "K7QF", new Hold());
        Assert.Equal(1, previous.Disposed);
        Assert.False(unlock.IsOpen(1));
        Assert.True(unlock.IsOpen(2));

        unlock.SetPendingPath(@"C:\x\kit.html");
        Assert.Equal(@"C:\x\kit.html", unlock.TakePendingPath());
        Assert.Null(unlock.TakePendingPath());
        unlock.SetPendingPath(@"C:\x\kit.html");
        unlock.Close();
        Assert.Null(unlock.TakePendingPath());
        // Com o kit fechado, nenhum destino fica à espera.
        unlock.SetPendingPath(@"C:\x\kit.html");
        Assert.Null(unlock.TakePendingPath());
    }

    [Fact]
    public void KitUnlock_ClosesWhenTheTabNavigates()
    {
        var unlock = new KitUnlock(() => _now);
        var hold = new Hold();
        unlock.Open(1, "K7QF", hold);
        unlock.TabNavigated(2);
        Assert.True(unlock.IsOpen(1));
        unlock.TabNavigated(1);
        Assert.False(unlock.IsOpen(1));
        Assert.Equal(1, hold.Disposed);
    }

    [Fact]
    public void Copy_UsesTheSecureClipboard()
    {
        var clip = new SecureClipboardTests.FakeClipboard();
        var scheduler = new SecureClipboardTests.FakeScheduler();
        Assert.True(new SecureClipboard(clip, scheduler.Schedule).Copy(Phrase));
        Assert.Equal(Phrase, clip.Text);
        Assert.Equal(SecureClipboard.ExclusionFormats.Select(f => f.Format), clip.Extra.Select(f => f.Format));
        Assert.Equal(3, clip.Extra.Count);
        Assert.All(clip.Extra, f => Assert.Equal(new byte[4], f.Data));
        var (delay, run) = Assert.Single(scheduler.Scheduled);
        Assert.Equal(TimeSpan.FromSeconds(60), delay);
        // O utilizador copiou outra coisa entretanto: nada é limpo.
        clip.Sequence++;
        run();
        Assert.Equal(0, clip.Clears);
        // Ainda é a nossa cópia: limpa.
        Assert.True(new SecureClipboard(clip, scheduler.Schedule).Copy(Phrase));
        scheduler.Scheduled[^1].Run();
        Assert.Equal(1, clip.Clears);
    }

    // ---- as ligações no navegador (fonte) ---------------------------------------------------------------------------

    [Fact]
    public void KitRoutes_ArePostOnly_AndBehindTheGate()
    {
        var factory = Source("Browser", "EterisSchemeHandlerFactory.cs");
        foreach (var route in new[] { "show", "hide", "copy", "save", "save-anyway", "print", "saved", "later", "decline", "from-file", "complete", "open" })
            Assert.Contains($"case \"/sync-kit-{route}\"", factory);
        Assert.Equal(12, Count(factory, "case \"/sync-kit-"));
        // «Ainda está aberto?» só pergunta: não abre, não fecha, não lê o kit.
        var open = factory[factory.IndexOf("case \"/sync-kit-open\"", StringComparison.Ordinal)..];
        open = open[..open.IndexOf("case \"", 10, StringComparison.Ordinal)];
        Assert.Contains("OpenKit.IsOpen(browserId)", open);
        Assert.DoesNotContain("RevealKit", open);
        Assert.DoesNotContain("OpenKit.Open(", open);
        Assert.DoesNotContain("OpenKit.Close(", open);
        Assert.DoesNotContain("/sync-code-saved", factory);
        Assert.DoesNotContain("AcknowledgeCode", factory);
        // Cada rota do kit recusa o que não for POST; fora do kit, só a aprovação nas Definições (T8K,
        // «/sync-request-code») tem a mesma guarda.
        Assert.Contains("case \"/sync-request-code\"", factory);
        Assert.Equal(Count(factory, "case \"/sync-kit-") + 1,
            Count(factory, "\"POST\", StringComparison.OrdinalIgnoreCase)) return Text(\"recusado\");"));
        Assert.Contains("VaultFrontToken().Register(", factory);
        Assert.Contains("\"Cache-Control\", \"no-store\"", factory);
        Assert.Contains("SyncChoices.NotConfirmed(\"o kit não foi mostrado\")", factory);
        // Revisão final (I-07): as duas rotas do ficheiro marcam o kit que gravaram, nunca «o que houver».
        Assert.True(Count(factory, "MarkKitSaved(kit.KitId)") >= 2);
        Assert.Equal(0, Count(factory, "MarkKitSaved()"));
        // «Já guardei o kit» só marca o kit que a página mostrou (o id vai no corpo; sem id não marca nada).
        Assert.Contains("MarkKitSaved(kitId)", factory);
        Assert.Contains("SyncForm.Parse(request).GetValueOrDefault(\"id\") is { Length: > 0 } kitId", factory);
        Assert.Equal(2, Count(factory, "SyncChoices.MayRenewKeyAsync(agent, purpose => ConfirmWithWindows(browserId, purpose))"));
        Assert.Contains("Headers[\"Cache-Control\"] = \"no-store\";", Source("Browser", "VaultResourceHandler.cs"));
    }

    [Fact]
    public void RevealKit_HasAClosedListOfCallers()
    {
        var root = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var code = File.ReadAllText(file);
            if (Path.GetFileName(file) == "EterisSchemeHandlerFactory.cs")
            {
                Assert.Equal(2, Count(code, ".RevealKit"));
                Assert.Contains("KitGate.ShowAsync(agent.RevealKit,", code);
                Assert.Contains("KitGate.Opened(agent.RevealKit,", code);
            }
            else Assert.DoesNotContain(".RevealKit", code);
        }
        var gate = Source("Sync", "KitGate.cs");
        var show = gate[gate.IndexOf("public static async Task<KitShown> ShowAsync(", StringComparison.Ordinal)..];
        Assert.True(show.IndexOf("SyncChoices.MayRevealKitAsync(confirm)", StringComparison.Ordinal)
            < show.IndexOf("reveal()", StringComparison.Ordinal));
        var window = Source("MainWindow.xaml.cs");
        Assert.Contains("KitTabNavigated(e.Browser.Identifier)", window);
        Assert.DoesNotContain("SyncService", window);
        Assert.Contains("OpenKit.TabNavigated(browserId)", Source("MainWindow.Sync.cs"));
        var service = Source("Sync", "SyncService.cs");
        var signOut = service[service.IndexOf("public static void SignOut(bool forgetKey)", StringComparison.Ordinal)..];
        Assert.Contains("OpenKit.Close()", signOut[..signOut.IndexOf("\n    }", StringComparison.Ordinal)]);
    }

    [Fact]
    public void KitSaveDialog_StartsInDownloads()
    {
        var vault = Source("MainWindow.Vault.cs");
        var start = vault.IndexOf("Task<string?> PickKitSaveAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = vault[start..vault.IndexOf("}).Task;", start, StringComparison.Ordinal)];
        Assert.Contains("InitialDirectory = Downloads.DownloadPaths.DownloadsFolder()", body);
        // Revisão final (I-07): o kit não fica nos «Recentes» do Windows (nem ao guardar nem ao abrir).
        Assert.Contains("AddToRecent = false,", body);
        var open = vault[vault.IndexOf("Task<string?> PickKitOpenAsync(", StringComparison.Ordinal)..];
        Assert.Contains("AddToRecent = false,", open[..open.IndexOf("}).Task;", StringComparison.Ordinal)]);
        Assert.DoesNotContain("MyDocuments", body);
        Assert.DoesNotContain("PickKit", Source("Vault", "VaultRoutes.cs"));
    }

    // ---- a página e o ecrã de entrada (T5d) ---------------------------------------------------------------------------

    private static string Page() => Source("Browser", "settings.html");

    private static int At(string text, string what)
    {
        var at = text.IndexOf(what, StringComparison.Ordinal);
        Assert.True(at >= 0, "não encontrei " + what);
        return at;
    }

    [Fact]
    public void Settings_HasTheKitSection_AndNoLongerCopiesFromThePage()
    {
        var page = Page();
        foreach (var text in new[]
                 {
                     "id=\"syncKitItem\"", "id=\"syncKitShow\"", "id=\"syncKitSave\"", "id=\"syncKitPrint\"", "id=\"syncKitCopy\"",
                     "id=\"syncKitSaved\"", "id=\"syncKitNew\"", "O seu kit atual termina em",
                     "O kit sozinho não abre nada: também é preciso entrar na sua conta Google. E a Google sozinha também não.",
                     "Não fotografe com o telemóvel", RecoveryKit.KeepOffThisComputer, RecoveryKit.UnprotectedNote, RecoveryKit.PrintNote,
                     "Guardar noutro sítio", "Guardar mesmo assim", "sync-kit-show", "sync-kit-save-anyway", "sync-kit-print",
                     "visibilitychange", "document.hidden", "sync-kit-open", "KIT_CHECK = 2000", "setInterval(",
                 })
            Assert.Contains(text, page);
        Assert.True(At(page, "id=\"syncKitAdvice\"") < At(page, RecoveryKit.UnprotectedNote));
        Assert.True(At(page, RecoveryKit.UnprotectedNote) < At(page, "id=\"syncKitWords\""));
        Assert.True(At(page, "id=\"syncKitPrint\"") < At(page, RecoveryKit.PrintNote));
        Assert.True(At(page, RecoveryKit.PrintNote) < At(page, "id=\"syncKitCopy\""));
        foreach (var text in new[]
                 {
                     "execCommand('copy')", "syncCodeItem", "syncCodeSaved", "syncCodeCopy", "syncNewCodeItem", "syncNewCode'", "sync-code-saved",
                     "!s.code", "lastState.sync.code", ">Já guardei<", "Código de recuperação — guarde-o agora", "Gerar um código novo",
                 })
            Assert.DoesNotContain(text, page);
        // Os textos do lembrete que a página repete são os mesmos da barra.
        Assert.Contains(RecoveryReminder.StaleText, page);
        Assert.Contains(RecoveryReminder.Decline, page);
        // «Já guardei o kit» é do kit que foi copiado ou impresso, e só dele (cadeia nova: o botão sai).
        Assert.Contains("const kept = (id = kitShownId) => {\n      kitKeptId = id;", page);
        Assert.Contains("kept(printId)", page);
        Assert.Contains("if (!kit || kit.id !== kitKeptId) kitKeptId = null;", page);
        Assert.Contains("$('syncKitSaved').hidden = !(kitKeptId === kit.id && kit.state !== 'saved')", page);
        Assert.Contains("post('sync-kit-saved', { id: kitKeptId }", page);
        Assert.DoesNotContain("kitKept = true", page);
        // O documento de impressão (com as palavras) sai com as palavras.
        Assert.Contains("if (kitPrintFrame) { kitPrintFrame.remove(); kitPrintFrame = null; }", page);
        Assert.Equal("Qualquer pessoa com acesso a este computador pode ver o kit.", RecoveryKit.UnprotectedNote);
        Assert.Equal("«Imprimir para PDF» cria um ficheiro com o kit: trate-o como o ficheiro do kit. Numa impressora partilhada, vá buscar a folha logo.",
            RecoveryKit.PrintNote);
        Assert.Equal("Não deixe este ficheiro neste computador: passe-o para uma pen ou imprima-o e apague-o daqui.", RecoveryKit.KeepOffThisComputer);
    }

    [Fact]
    public void Settings_SignOutGuard_UsesTheKit()
    {
        var page = Page();
        Assert.Contains("lastState.sync.kit.state !== 'saved'", page);
        Assert.Contains("O kit de recuperação ainda não foi guardado.", page);
        Assert.DoesNotContain("codePending", page);
    }

    [Fact]
    public void Completion_OnlyFromFourLetters()
    {
        Assert.Equal(["abacate"], RecoveryKit.Completion("abac"));
        Assert.Equal(["abacate"], RecoveryKit.Completion("ABÁC"));
        foreach (var typed in new[] { "aba", null, "", "ab1c", new string('a', 17) })
            Assert.Empty(RecoveryKit.Completion(typed));
    }

    [Fact]
    public void EntryScreen_OpensTheKitFile_AndCompletesWords()
    {
        var page = Page();
        foreach (var text in new[] { "id=\"syncKitOpenFile\"", "Abrir o ficheiro do kit…", "sync-kit-from-file", "id=\"syncJoinHint\"", "sync-kit-complete" })
            Assert.Contains(text, page);
        var factory = Source("Browser", "EterisSchemeHandlerFactory.cs");
        Assert.Contains("RecoveryKit.TryReadFile(", factory);
        Assert.Contains("RecoveryKit.Completion(", factory);
        Assert.Contains("PickKitOpenAsync(", factory);
    }

    [Fact]
    public void KitFile_TooBigOrNotAKit_IsRefused_AndAKitIsRead()
    {
        var reads = 0;
        Assert.False(RecoveryKit.TryReadFile("k.html", _ => RecoveryKit.MaxFileBytes + 1, _ => { reads++; return Phrase; }, out _));
        Assert.Equal(0, reads);
        Assert.False(RecoveryKit.TryReadFile("k.html", _ => 10, _ => "uma página qualquer", out _));
        Assert.False(RecoveryKit.TryReadFile("k.html", _ => 10, _ => throw new IOException("ocupado"), out _));
        Assert.False(RecoveryKit.TryReadFile("k.html", _ => throw new UnauthorizedAccessException(), _ => Phrase, out _));
        using var s = AccountSecret.Create();
        var html = RecoveryKit.Html(s.ToWords(), s.KitId, Day);
        Assert.True(RecoveryKit.TryReadFile("k.html", _ => html.Length, _ => html, out var secret));
        using var back = AccountSecret.FromBytes(secret);
        Assert.Equal(s.ChainId, back.ChainId);
    }

    // O ecrã de entrada fala do kit (a T5d é a dona destes textos) e G8 é a frase do plano.
    [Fact]
    public void EntryScreen_SaysKit_AndG8IsThePlanText()
    {
        var page = Page();
        foreach (var text in new[] { "Kit de recuperação (24 palavras ou o código antigo)", "Usar o kit de recuperação", "ou use o kit de recuperação" })
            Assert.Contains(text, page);
        foreach (var text in new[] { "Código de recuperação (14 grupos de 4)", "Usar o código de recuperação", "Escreva o código de recuperação." })
            Assert.DoesNotContain(text, page);
        // (A nota syncLostLink saiu na T12c: o caminho sem kit nem outro computador é «Começar do zero…», em SyncExitTests.)
        foreach (var text in new[] { "id=\"syncKitHelp\"", "id=\"syncKitPaste\"", "id=\"syncJoinButton\"", "method=\"post\"" })
            Assert.Contains(text, page);
        Assert.Equal("Para entrar sem o outro computador, use o seu kit de recuperação (24 palavras ou o código de 56 caracteres).", GuestPairingModel.UseKit);
        // Na página, «kit de recuperação» vai a negrito: as duas metades da frase, antes e depois.
        Assert.Contains("Para entrar sem o outro computador, use o seu <b>kit de recuperação</b> (24 palavras ou o código de 56 caracteres).", page);
        Assert.Contains("Escreva ou cole o kit de recuperação.", page);
        Assert.Contains("Escreva ou cole o kit de recuperação.", Source("Browser", "EterisSchemeHandlerFactory.cs"));
    }
}
