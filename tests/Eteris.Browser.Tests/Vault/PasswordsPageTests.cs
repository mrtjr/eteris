using System.IO;
using System.Text;
using Eteris.Browser.Browser;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// B2 (docs/palavras-passe.md): eteris://passwords/ — a lista sem segredos, «Mostrar» e «Copiar» com o Windows Hello,
// editar, apagar com «Anular», «Nunca neste site», a verificação local, importar e exportar pelo processo do navegador.
public sealed class PasswordsPageTests : IDisposable
{
    private readonly VaultFixture _f = new();

    public void Dispose() => _f.Dispose();

    private const string Secret = "Segredo-De-Teste-7Q!";

    private string Seed(string origin = "https://github.com", string user = "ana@exemplo.pt", string password = Secret, string? note = "nota-secreta-xyz")
        => _f.Vault.SaveLogin(new LoginItem(string.Empty, origin, user, password, note, default, default, null, null, false, null));

    // Nenhum segredo no HTML inicial: nem a palavra-passe nem a nota; só metadados (site, utilizador) e o token.
    [Fact]
    public async Task Page_NoSecretInInitialHtml()
    {
        Seed();
        Seed("https://conta.exemplo.pt", "bruno", "Outra-Senha-9!", null);
        _f.Vault.AddNeverSave("https://nunca.exemplo.pt");
        var reply = await _f.Get("passwords", "/");
        Assert.Equal("text/html", reply.Mime);
        Assert.DoesNotContain(Secret, reply.Body);
        Assert.DoesNotContain("nota-secreta-xyz", reply.Body);
        Assert.DoesNotContain("Outra-Senha-9!", reply.Body);
        Assert.Contains("ana@exemplo.pt", reply.Body);
        Assert.Contains("github.com", reply.Body);
        Assert.Contains("nunca.exemplo.pt", reply.Body);
        Assert.Contains(InternalPages.Token, reply.Body);
        Assert.Equal(0, _f.Reauth.Prompts);
    }

    // A página é «Palavras-passe», com o estilo das Definições (tema claro incluído) e a CSP sem pedidos de rede.
    [Fact]
    public void Template_ThemedAndLockedDown()
    {
        var html = PasswordsPage.Template;
        Assert.Contains("<title>Palavras-passe</title>", html);
        Assert.Contains("html.light {", html);
        Assert.Contains("default-src 'none'", html);
        Assert.DoesNotContain("connect-src", html);
        // «Mudar palavra-passe» abre /.well-known/change-password só ao clicar, pelo processo do navegador: o endereço
        // nunca está na página (nem como ligação que se possa pré-carregar).
        Assert.DoesNotContain("well-known", html);
        Assert.Contains("id=\"verificacao\"", html);
        Assert.Contains("id=\"definicoes\"", html);
    }

    // As ações exigem o token desta sessão.
    [Theory]
    [InlineData("/reveal", "id=x")]
    [InlineData("/copy", "id=x&what=password")]
    [InlineData("/delete", "id=x")]
    [InlineData("/export", "")]
    [InlineData("/import", "")]
    [InlineData("/health", "")]
    [InlineData("/list", "")]
    [InlineData("/set", "k=pwOffer&v=false")]
    public async Task Actions_NeedTheToken(string path, string query)
    {
        var id = Seed();
        var reply = await _f.Get("passwords", path, query.Replace("id=x", "id=" + id), authorized: false);
        Assert.Equal("recusado", reply.Body);
        Assert.Equal(0, _f.Reauth.Prompts);
        Assert.Equal(0, _f.Host.ImportDialogs + _f.Host.ExportDialogs);
        Assert.Empty(_f.Host.Copied);
        Assert.NotNull(_f.Vault.GetLogin(id));
    }

    // Na janela privada a página não abre («Abra numa janela normal») e as rotas recusam.
    [Fact]
    public async Task PrivateWindow_Refused()
    {
        var id = Seed();
        var page = await _f.Get("passwords", "/", isPrivate: true);
        Assert.Contains("Abra numa janela normal", page.Body);
        Assert.DoesNotContain("ana@exemplo.pt", page.Body);
        Assert.Equal("recusado", (await _f.Get("passwords", "/reveal", "id=" + id, isPrivate: true)).Body);
        Assert.Equal("recusado", (await _f.Get("passwords", "/list", isPrivate: true)).Body);
        Assert.Equal(0, _f.Reauth.Prompts);
    }

    // «Mostrar» pede o Windows Hello e só então devolve a palavra-passe e a nota; recusado, nada.
    [Fact]
    public async Task Reveal_NeedsReauth()
    {
        var id = Seed();
        _f.Reauth.Outcome = ReauthOutcome.Canceled;
        var refused = VaultFixture.Json(await _f.Get("passwords", "/reveal", "id=" + id));
        Assert.False(refused["ok"]!.GetValue<bool>());
        Assert.DoesNotContain(Secret, refused.ToJsonString());
        Assert.Equal(1, _f.Reauth.Prompts);
        Assert.Equal(ReauthPolicy.Message(ReauthPurpose.Reveal), _f.Reauth.Messages[0]);

        _f.Reauth.Outcome = ReauthOutcome.Verified;
        var shown = VaultFixture.Json(await _f.Get("passwords", "/reveal", "id=" + id));
        Assert.True(shown["ok"]!.GetValue<bool>());
        Assert.Equal(Secret, shown["password"]!.GetValue<string>());
        Assert.Equal("nota-secreta-xyz", shown["note"]!.GetValue<string>());
        // Com o segredo à vista, as janelas saem das capturas até o esconder.
        Assert.Equal(1, _f.Host.Shown);
        await _f.Get("passwords", "/hide");
        Assert.Equal(1, _f.Host.Hidden);
        // Na janela de graça (5 min) não volta a pedir.
        await _f.Get("passwords", "/reveal", "id=" + id);
        Assert.Equal(2, _f.Reauth.Prompts);
    }

    // A aba saiu da frente enquanto o Windows perguntava: o pedido é cancelado e nada se mostra.
    [Fact]
    public async Task Reveal_CanceledWhenTabLeavesTheFront()
    {
        var id = Seed();
        _f.Host.Front.Cancel();
        var reply = VaultFixture.Json(await _f.Get("passwords", "/reveal", "id=" + id));
        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Equal(0, _f.Host.Shown);
    }

    // «Copiar» a palavra-passe pede o Hello e passa pela área de transferência segura (nunca pela página); o
    // utilizador copia-se sem pedir.
    [Fact]
    public async Task Copy_PasswordWithReauth_UsernameWithout()
    {
        var id = Seed();
        var user = VaultFixture.Json(await _f.Get("passwords", "/copy", $"id={id}&what=username"));
        Assert.True(user["ok"]!.GetValue<bool>());
        Assert.Equal(0, _f.Reauth.Prompts);
        Assert.Equal(["ana@exemplo.pt"], _f.Host.Copied);

        var password = VaultFixture.Json(await _f.Get("passwords", "/copy", $"id={id}&what=password"));
        Assert.True(password["ok"]!.GetValue<bool>());
        Assert.Equal(1, _f.Reauth.Prompts);
        Assert.Equal(Secret, _f.Host.Copied[1]);
        Assert.DoesNotContain(Secret, password.ToJsonString());
        Assert.Contains("60 s", password["message"]!.GetValue<string>());
    }

    // Adicionar não pede nada; editar um item existente pede (é ver e mudar a palavra-passe). A anterior vai para o
    // histórico de 1 versão.
    [Fact]
    public async Task Save_AddWithoutReauth_EditWithReauth()
    {
        var added = VaultFixture.Json(await _f.Post("passwords", "/save", new() { ["origin"] = "https://exemplo.pt/entrar", ["username"] = "carla", ["password"] = "Nova-1-Senha!", ["note"] = "" }));
        Assert.True(added["ok"]!.GetValue<bool>());
        Assert.Equal(0, _f.Reauth.Prompts);
        var id = added["id"]!.GetValue<string>();
        Assert.Equal("https://exemplo.pt", _f.Vault.GetLogin(id)!.Origin);

        _f.Reauth.Outcome = ReauthOutcome.Canceled;
        var refused = VaultFixture.Json(await _f.Post("passwords", "/save", new() { ["id"] = id, ["origin"] = "https://exemplo.pt", ["username"] = "carla", ["password"] = "Mudada-2!" }));
        Assert.False(refused["ok"]!.GetValue<bool>());
        Assert.Equal("Nova-1-Senha!", _f.Vault.GetLogin(id)!.Password);

        _f.Reauth.Outcome = ReauthOutcome.Verified;
        var edited = VaultFixture.Json(await _f.Post("passwords", "/save", new() { ["id"] = id, ["origin"] = "https://exemplo.pt", ["username"] = "carla", ["password"] = "Mudada-2!" }));
        Assert.True(edited["ok"]!.GetValue<bool>());
        Assert.Equal("Mudada-2!", _f.Vault.GetLogin(id)!.Password);
        Assert.Equal("Nova-1-Senha!", _f.Vault.PreviousPassword(id));
    }

    [Theory]
    [InlineData("ftp://exemplo.pt", "x", "Senha-1!", "Endereço")]
    [InlineData("", "x", "Senha-1!", "Endereço")]
    [InlineData("https://exemplo.pt", "x", "", "palavra-passe")]
    public async Task Save_Validates(string origin, string user, string password, string expected)
    {
        var reply = VaultFixture.Json(await _f.Post("passwords", "/save", new() { ["origin"] = origin, ["username"] = user, ["password"] = password }));
        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Contains(expected, reply["message"]!.GetValue<string>());
        Assert.Empty(_f.Vault.Logins());
    }

    // Apagar some logo da lista, com «Anular» durante 10 s; só depois se apaga no cofre. Anular devolve-o tal e qual.
    [Fact]
    public async Task Delete_WithUndo()
    {
        var id = Seed();
        var reply = VaultFixture.Json(await _f.Get("passwords", "/delete", "id=" + id));
        Assert.True(reply["ok"]!.GetValue<bool>());
        var undo = reply["undo"]!.GetValue<string>();
        Assert.Equal(VaultRoutes.UndoWindow, _f.Host.Scheduled.Single().Delay);
        Assert.DoesNotContain(id, (await _f.Get("passwords", "/list")).Body);
        Assert.DoesNotContain("ana@exemplo.pt", (await _f.Get("passwords", "/")).Body);
        Assert.NotNull(_f.Vault.GetLogin(id));

        Assert.True(VaultFixture.Json(await _f.Get("passwords", "/undo", "u=" + undo))["ok"]!.GetValue<bool>());
        Assert.Contains(id, (await _f.Get("passwords", "/list")).Body);
        _f.Host.RunScheduled();
        Assert.Equal(Secret, _f.Vault.GetLogin(id)!.Password);

        await _f.Get("passwords", "/delete", "id=" + id);
        _f.Host.RunScheduled();
        Assert.Null(_f.Vault.GetLogin(id));
        Assert.Contains(_f.Vault.Tombstones(VaultCollection.Logins), t => t.Id == id);
        Assert.False(VaultFixture.Json(await _f.Get("passwords", "/undo", "u=" + undo))["ok"]!.GetValue<bool>());
    }

    // Ao fechar o Éteris com um apagamento por anular, ele faz-se.
    [Fact]
    public async Task Delete_CommittedOnExit()
    {
        var id = Seed();
        await _f.Get("passwords", "/delete", "id=" + id);
        _f.Routes.CommitPending();
        Assert.Null(_f.Vault.GetLogin(id));
    }

    // «Sites onde nunca guardar»: lista na página e «Remover».
    [Fact]
    public async Task NeverSave_Remove()
    {
        _f.Vault.AddNeverSave("https://nunca.exemplo.pt");
        await _f.Get("passwords", "/never-remove", "origin=" + Uri.EscapeDataString("https://nunca.exemplo.pt"));
        Assert.False(_f.Vault.IsNeverSave("https://nunca.exemplo.pt"));
    }

    // «Mudar palavra-passe»: nenhum pedido até ao clique; ao clicar, o navegador abre /.well-known/change-password da
    // origem guardada (a página só diz qual item).
    [Fact]
    public async Task ChangePassword_OnlyOnClick_FromTheSavedOrigin()
    {
        var id = Seed("https://github.com:8443");
        await _f.Get("passwords", "/");
        Assert.Empty(_f.Host.Opened);
        await _f.Get("passwords", "/change", "id=" + id);
        Assert.Equal(["https://github.com:8443/.well-known/change-password"], _f.Host.Opened);
        Assert.Null(VaultRoutes.ChangePasswordUrl("android://abc@com.exemplo"));
    }

    // Verificação local: fracas e repetidas, só com os IDs; nenhuma palavra-passe sai.
    [Fact]
    public async Task Health_IdsOnly()
    {
        var weak = Seed("https://a.exemplo.pt", "a", "123456", null);
        var r1 = Seed("https://b.exemplo.pt", "b", "Repetida-Forte-42!", null);
        var r2 = Seed("https://c.exemplo.pt", "c", "Repetida-Forte-42!", null);
        var reply = await _f.Get("passwords", "/health");
        var json = VaultFixture.Json(reply);
        Assert.Equal([weak], json["weak"]!.AsArray().Select(n => n!.GetValue<string>()));
        // Os IDs são aleatórios: compara-se pela mesma ordem dos dois lados (antes, o teste falhava metade das vezes).
        Assert.Equal(new[] { r1, r2 }.Order(), json["reused"]!.AsArray().Single()!.AsArray().Select(n => n!.GetValue<string>()).Order());
        Assert.DoesNotContain("123456", reply.Body.Replace(weak, "").Replace(r1, "").Replace(r2, ""));
        Assert.DoesNotContain("Repetida-Forte-42!", reply.Body);
        Assert.Equal(0, _f.Reauth.Prompts);
    }

    // Importar: o diálogo nativo é do processo do navegador (o CSV nunca entra na página); sem conflitos aplica logo,
    // com conflitos pergunta «a do ficheiro» ou «a atual»; no fim oferece a Reciclagem.
    [Fact]
    public async Task Import_ThroughTheBrowserProcess()
    {
        var existing = Seed("https://github.com", "ana@exemplo.pt", "Antiga-1!", null);
        var csv = Path.Combine(_f.Root, "senhas.csv");
        File.WriteAllText(csv, "name,url,username,password,note\ngithub.com,https://github.com/,ana@exemplo.pt,Nova-2!,\nexemplo.pt,https://exemplo.pt/,bruno,Bruno-3!,\n,nada,x,y,\n", new UTF8Encoding(true));
        _f.Host.ImportPath = csv;
        var first = VaultFixture.Json(await _f.Get("passwords", "/import"));
        Assert.Equal(1, _f.Host.ImportDialogs);
        Assert.True(first["ok"]!.GetValue<bool>());
        Assert.Equal(1, first["conflicts"]!.GetValue<int>());
        Assert.DoesNotContain("Nova-2!", first.ToJsonString());
        Assert.DoesNotContain("Bruno-3!", first.ToJsonString());
        // Ainda nada aplicado enquanto pergunta.
        Assert.Single(_f.Vault.Logins());

        var done = VaultFixture.Json(await _f.Get("passwords", "/import-resolve", "choice=file"));
        Assert.True(done["ok"]!.GetValue<bool>());
        Assert.Contains("1 importada", done["message"]!.GetValue<string>());
        Assert.Contains("1 substituída", done["message"]!.GetValue<string>());
        Assert.Contains("1 inválida", done["message"]!.GetValue<string>());
        Assert.Equal("Nova-2!", _f.Vault.GetLogin(existing)!.Password);
        Assert.Equal("Antiga-1!", _f.Vault.PreviousPassword(existing));
        Assert.Equal(2, _f.Vault.Logins().Count);

        Assert.True(VaultFixture.Json(await _f.Get("passwords", "/import-recycle"))["ok"]!.GetValue<bool>());
        Assert.Equal([csv], _f.Host.Recycled);
        Assert.Equal(0, _f.Reauth.Prompts);
    }

    [Fact]
    public async Task Import_KeepCurrent_AndCanceledDialog()
    {
        var existing = Seed("https://github.com", "ana@exemplo.pt", "Antiga-1!", null);
        var csv = Path.Combine(_f.Root, "senhas.csv");
        File.WriteAllText(csv, "url,username,password\nhttps://github.com,ana@exemplo.pt,Nova-2!\n");
        _f.Host.ImportPath = null;
        Assert.False(VaultFixture.Json(await _f.Get("passwords", "/import"))["ok"]!.GetValue<bool>());
        _f.Host.ImportPath = csv;
        await _f.Get("passwords", "/import");
        var done = VaultFixture.Json(await _f.Get("passwords", "/import-resolve", "choice=current"));
        Assert.Contains("1 em conflito", done["message"]!.GetValue<string>());
        Assert.Equal("Antiga-1!", _f.Vault.GetLogin(existing)!.Password);
    }

    [Fact]
    public async Task Import_TooLarge_Refused()
    {
        var csv = Path.Combine(_f.Root, "grande.csv");
        File.WriteAllBytes(csv, new byte[PasswordCsv.MaxFileBytes + 1]);
        _f.Host.ImportPath = csv;
        var reply = VaultFixture.Json(await _f.Get("passwords", "/import"));
        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Contains("1000 KiB", reply["message"]!.GetValue<string>());
    }

    // Exportar pede sempre o Windows Hello (sem janela de graça), depois o diálogo nativo; avisa numa pasta da nuvem.
    [Fact]
    public async Task Export_AlwaysReauth_ThenNativeDialog_CloudWarning()
    {
        Seed();
        var path = Path.Combine(_f.Root, "export.csv");
        _f.Host.ExportPath = path;
        _f.Reauth.Outcome = ReauthOutcome.Canceled;
        Assert.False(VaultFixture.Json(await _f.Get("passwords", "/export"))["ok"]!.GetValue<bool>());
        Assert.Equal(0, _f.Host.ExportDialogs);
        Assert.False(File.Exists(path));

        _f.Reauth.Outcome = ReauthOutcome.Verified;
        var done = VaultFixture.Json(await _f.Get("passwords", "/export"));
        Assert.True(done["ok"]!.GetValue<bool>());
        Assert.Contains("em claro", done["message"]!.GetValue<string>());
        Assert.StartsWith("name,url,username,password,note", File.ReadAllText(path));
        Assert.Contains(Secret, File.ReadAllText(path));
        Assert.DoesNotContain(Secret, done.ToJsonString());
        // Sem janela de graça: pede outra vez.
        await _f.Get("passwords", "/export");
        Assert.Equal(3, _f.Reauth.Prompts);
        Assert.Equal(ReauthPolicy.Message(ReauthPurpose.Export), _f.Reauth.Messages[^1]);

        var cloud = Path.Combine(_f.Root, "OneDrive", "senhas.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(cloud)!);
        _f.Host.ExportPath = cloud;
        _f.Host.ConfirmAnswer = false;
        var refused = VaultFixture.Json(await _f.Get("passwords", "/export"));
        Assert.False(refused["ok"]!.GetValue<bool>());
        Assert.Contains("OneDrive", _f.Host.Confirms.Single());
        Assert.False(File.Exists(cloud));
    }

    // «Apagar todas as palavras-passe…» pede o Hello e apaga só as palavras-passe e os «nunca» (com lápides).
    [Fact]
    public async Task EraseAll_OnlyPasswords_WithReauth()
    {
        var id = Seed();
        _f.Vault.AddNeverSave("https://nunca.exemplo.pt");
        var card = _f.Vault.Save(new CardItem(string.Empty, "Ana", "4111 1111 1111 1111", 8, 2029, null, default, null));
        _f.Reauth.Outcome = ReauthOutcome.Canceled;
        Assert.False(VaultFixture.Json(await _f.Get("passwords", "/erase-all"))["ok"]!.GetValue<bool>());
        Assert.NotNull(_f.Vault.GetLogin(id));
        _f.Reauth.Outcome = ReauthOutcome.Verified;
        Assert.True(VaultFixture.Json(await _f.Get("passwords", "/erase-all"))["ok"]!.GetValue<bool>());
        Assert.Empty(_f.Vault.Logins());
        Assert.Empty(_f.Vault.NeverSaveSites());
        Assert.NotNull(_f.Vault.GetCard(card));
        Assert.Equal(ReauthPolicy.Message(ReauthPurpose.EraseVault), _f.Reauth.Messages[^1]);
    }

    // Os interruptores da página: só os das palavras-passe.
    [Fact]
    public async Task Set_OnlyPasswordSwitches()
    {
        Assert.Equal("ok", (await _f.Get("passwords", "/set", "k=pwOffer&v=false")).Body);
        Assert.False(_f.Host.Current.PasswordOffer);
        Assert.Equal("recusado", (await _f.Get("passwords", "/set", "k=engine&v=brave")).Body);
        Assert.Equal("recusado", (await _f.Get("passwords", "/set", "k=payAskHello&v=false")).Body);
    }

    // A lista: agrupada por site no cliente, com o site em Unicode, o domínio registável e «Ligação não segura» para http.
    [Fact]
    public async Task List_Metadata()
    {
        Seed("https://xn--bcher-kva.example", "u1", "Senha-Forte-1!", null);
        Seed("http://intranet.exemplo.pt", "u2", "Senha-Forte-2!", null);
        var json = VaultFixture.Json(await _f.Get("passwords", "/list"));
        var logins = json["logins"]!.AsArray();
        var idn = logins.Single(l => l!["username"]!.GetValue<string>() == "u1")!;
        Assert.Equal("bücher.example", idn["host"]!.GetValue<string>());
        Assert.Equal("xn--bcher-kva.example", idn["site"]!.GetValue<string>());
        var http = logins.Single(l => l!["username"]!.GetValue<string>() == "u2")!;
        Assert.True(http["insecure"]!.GetValue<bool>());
        Assert.Equal("exemplo.pt", http["site"]!.GetValue<string>());
    }

    // Guardas no código: as páginas do cofre só por eteris://, rotas registadas, recurso embutido, nunca na privada.
    [Fact]
    public void Source_RoutesAndResources()
    {
        var src = Path.Combine(Root(), "src", "Eteris.Browser");
        var factory = File.ReadAllText(Path.Combine(src, "Browser", "EterisSchemeHandlerFactory.cs"));
        Assert.Contains("Host: \"passwords\" or \"autofill\"", factory);
        Assert.Contains("<EmbeddedResource Include=\"Browser\\passwords.html\" />", File.ReadAllText(Path.Combine(src, "Eteris.Browser.csproj")));
        // Os diálogos de importar e exportar são nativos e do processo do navegador.
        var host = File.ReadAllText(Path.Combine(src, "MainWindow.Vault.cs"));
        Assert.Contains("Microsoft.Win32.OpenFileDialog", host);
        Assert.Contains("Microsoft.Win32.SaveFileDialog", host);
        Assert.Contains("CancelVaultRequests();", File.ReadAllText(Path.Combine(src, "MainWindow.xaml.cs")));
        Assert.Contains("Vault.VaultRoutes.CommitPendingOnExit();", File.ReadAllText(Path.Combine(src, "App.xaml.cs")));
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
