using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Tests.Sync;

namespace Eteris.Browser.Tests;

// 0.2.15 (T15): os textos públicos — a política de privacidade e o sítio (docs/site), o README e os documentos — dizem o
// que o código final faz, e não voltam a dizer o que deixou de ser verdade («Sair» que revoga, o código de recuperação
// mostrado uma única vez, as palavras-passe que não vão). Cada procura reduz os espaços e as quebras de linha a um espaço
// (as frases estão partidas por linhas nos ficheiros).
public class PublicTextsTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([GoogleSyncTests.FindRoot(), .. parts]), Encoding.UTF8);

    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    // A parte portuguesa e a inglesa de uma página do sítio (o espelho inglês começa em id="en").
    private static (string Pt, string En) Languages(string html)
    {
        var at = html.IndexOf("id=\"en\"", StringComparison.Ordinal);
        Assert.True(at > 0, "sem a parte inglesa");
        return (Flat(html[..at]), Flat(html[at..]));
    }

    private static string Policy() => Read("docs", "site", "privacidade.html");

    // Os ficheiros públicos de texto. Lista fechada.
    private static readonly string[][] SiteFiles = [["docs", "site", "privacidade.html"], ["docs", "site", "index.html"], ["docs", "site", "termos.html"]];
    private static readonly string[][] PublicFiles = [.. SiteFiles, ["README.md"], ["docs", "sincronizacao.md"], ["docs", "palavras-passe.md"]];

    private static string SyncDoc() => Flat(Read("docs", "sincronizacao.md"));

    private static IEnumerable<(string Name, string Text)> PublicTexts()
    {
        foreach (var file in PublicFiles) yield return (string.Join('/', file), Flat(Read(file)));
        yield return (nameof(PrivacyPage.SyncException), PrivacyPage.SyncException);
    }

    [Fact]
    public void PrivacyPolicy_SaysWhereYouCanSignIn_InBothLanguages()
    {
        var (pt, en) = Languages(Policy());
        Assert.Contains("Nada é pedido à Google enquanto não carregar em «Entrar com o Google» (no menu, nas boas-vindas ou nas Definições).", pt);
        Assert.Contains("Nothing is requested from Google until you click “Sign in with Google” (in the menu, on the welcome page or in Settings).", en);
        Assert.DoesNotContain("enquanto não a ligar e carregar em", pt);
    }

    [Fact]
    public void PrivacyPolicy_ListsWhatSyncs()
    {
        var (pt, en) = Languages(Policy());
        Assert.Contains("Favoritos, Espaços, definições, decisões do Escudo, notas, palavras-passe, contactos e viagens; pagamentos e documentos de identificação só se os ligar. O histórico, as abas abertas, cookies, cache e permissões dos sites não vão.", pt);
        Assert.Contains("Bookmarks, Spaces, settings, Shield decisions, notes, passwords, contact details and trips; payments and identity documents only if you turn them on. History, open tabs, cookies, cache and site permissions are not synced.", en);
        // A mesma lista que a página eteris://privacy/ mostra.
        Assert.Contains(PrivacyPage.SyncGoes, pt);
    }

    [Fact]
    public void PrivacyPolicy_DescribesTheRecoveryKit_NotACodeShownOnce()
    {
        var (pt, en) = Languages(Policy());
        Assert.Contains("Fica nos seus computadores e no seu kit de recuperação, que pode guardar como ficheiro ou imprimir e voltar a ver num computador ligado, com o Windows Hello", pt);
        Assert.Contains("It stays on your computers and in your recovery kit, which you can save as a file or print, and view again on a connected computer with Windows Hello", en);
        Assert.Contains("Os códigos de recuperação antigos continuam a servir.", pt);
        Assert.DoesNotContain("mostrado uma única vez", pt);
        Assert.DoesNotContain("recovery code shown once", en);
    }

    [Fact]
    public void PrivacyPolicy_SignOutDoesNotRevoke_RemovingAccessDoes()
    {
        var (pt, en) = Languages(Policy());
        Assert.Contains("apagados deste computador ao «Sair»", pt);
        Assert.Contains("Retirar o acesso do Éteris à conta Google (Definições › Sincronização) revoga a autorização na Google e desliga todos os seus computadores; também em myaccount.google.com/permissions", pt);
        Assert.Contains("deleted from this computer on “Sign out”", en);
        Assert.Contains("Removing Éteris's access to your Google account (Settings › Sync) revokes the authorisation at Google and disconnects all your computers; you can also do it at myaccount.google.com/permissions", en);
        foreach (var old in new[] { "revogados ao «Sair»", "revoga o acesso do Éteris na Google", "revoked on “Sign out”" })
        {
            Assert.DoesNotContain(old, pt);
            Assert.DoesNotContain(old, en);
        }
    }

    // O âmbito pedido à Google continua a ser só drive.appdata (a chave não vai para a Google).
    [Fact]
    public void PrivacyPolicy_StillAsksOnlyForTheAppFolder()
    {
        var policy = Policy();
        Assert.Contains("drive.appdata", policy);
        foreach (Match m in Regex.Matches(policy, @"googleapis\.com/auth/[A-Za-z0-9._-]+"))
            Assert.Equal("googleapis.com/auth/drive.appdata", m.Value);
    }

    // A data de uma política só o dono a muda (ao publicar): o conteúdo mudou na 0.2.15, a data não se toca aqui.
    [Fact]
    public void PrivacyPolicy_EffectiveDateIsUntouched()
        => Assert.Contains("Em vigor desde 8 de novembro de 2026", Policy());

    // «Sair deste computador» já não revoga (T12). As cadeias são as que os textos da 0.2.13/0.2.14 tinham.
    [Fact]
    public void NoPublicText_SaysSignOutRevokes()
    {
        string[] old =
        [
            "revogados ao «Sair»", "revoga o acesso do Éteris na Google", "revoga o acesso na Google", "apagados e revogados na Google",
            "revoga os tokens na Google", "revogar ao «Sair»", "sair (que retira a autorização na Google)", "revoked on “Sign out”",
            "revokes Éteris's access at Google", "revokes access at Google",
        ];
        foreach (var (name, text) in PublicTexts())
            foreach (var s in old)
                Assert.False(text.Contains(s, StringComparison.Ordinal), $"{name} ainda diz «{s}»");
        var doc = SyncDoc();
        Assert.Contains("«Sair deste computador»", doc);
        Assert.Contains("não revoga", doc);
    }

    // O kit de recuperação volta a ver-se (T5): nenhum texto diz que o código aparece uma vez e nunca mais.
    [Fact]
    public void NoPublicText_SaysTheRecoveryCodeNeverComesBack()
    {
        foreach (var (name, text) in PublicTexts())
            foreach (var s in new[] { "nunca mais aparece", "código de recuperação mostrado uma única vez", "recovery code shown once", "recovery code only you see" })
                Assert.False(text.Contains(s, StringComparison.Ordinal), $"{name} ainda diz «{s}»");
        // docs/sincronizacao.md diz «mostrado uma única vez» do código do perfil exportado (.eteris), que é verdade.
        foreach (var file in new[] { SiteFiles[0], SiteFiles[1], SiteFiles[2], ["README.md"] })
            Assert.DoesNotContain("mostrado uma única vez", Flat(Read(file)));
    }

    // R9 e R8: pagamentos e documentos são uma escolha da conta; e o limite de tokens de renovação da Google.
    [Fact]
    public void Docs_SayPaymentsAndDocumentsAreAnAccountChoice()
    {
        Assert.Contains("escolha da conta", Flat(Read("docs", "palavras-passe.md")));
        Assert.Contains("escolha da conta", SyncDoc());
        Assert.Contains("100 tokens de renovação", SyncDoc());
    }

    // R13: o tamanho do kit e o lembrete coincidem com as constantes (a T6 está no ramo: 24 palavras).
    [Fact]
    public void Docs_NameTheKitSize_AndItMatchesTheCode()
    {
        Assert.Equal(24, Eteris.Sync.Crypto.RecoveryCode.WordCount);
        Assert.Contains("24 palavras", Flat(Read("README.md")));
        Assert.Contains("24 palavras", SyncDoc());
        Assert.Equal(30, Eteris.Browser.Sync.RecoveryReminder.SnoozeDays);
        Assert.Contains("de 30 em 30 dias", SyncDoc());
        foreach (var (name, text) in PublicTexts())
            Assert.False(text.Contains("durante 30 dias", StringComparison.Ordinal), name);
    }

    [Fact]
    public void Readme_DescribesTheNewFlow()
    {
        var readme = Flat(Read("README.md"));
        foreach (var s in new[] { "kit de recuperação", "Sair deste computador", "Retirar o acesso" })
            Assert.Contains(s, readme);
        Assert.DoesNotContain("o mesmo código nos dois ecrãs", readme);
    }

    // R18: os momentos de rede dos documentos são os do teste de varrimento e os da exceção da página de privacidade.
    [Fact]
    public void NetworkMoments_AreTheOnesTheExceptionNames()
    {
        var doc = SyncDoc();
        Assert.Contains("NetworkMoments_ClosedListOfCallers", doc);
        var activation = File.Exists(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Sync", "ActivationLook.cs"));
        Assert.Equal(activation, PrivacyPage.SyncException.Contains("volta à janela do Éteris", StringComparison.Ordinal));
        Assert.Equal(activation, doc.Contains("ao voltar à janela do Éteris", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("menu do Éteris (⋯)", PrivacyPage.SyncException);
        Assert.Contains("secção Sincronização das Definições à vista", PrivacyPage.SyncException);
        if (GoogleSyncTests.Src("Eteris.Browser", "Browser", "settings.html").Contains("id=\"syncNow\"", StringComparison.Ordinal))
            Assert.Contains("sincronizar agora", PrivacyPage.SyncException);
    }

    // Os textos da interface que ainda diziam «código de recuperação» (a conta duplicada, a conta substituída, outra conta
    // Google, ligar sem acabar de apagar) passaram a «kit de recuperação» (0.2.15): o código antigo continua aceite, mas é
    // o kit que a pessoa tem. Só as cadeias entre aspas: os comentários podem falar do formato antigo.
    [Fact]
    public void UiTexts_SayTheKit_NotTheOldRecoveryCode()
    {
        foreach (Match m in Regex.Matches(GoogleSyncTests.Src("Eteris.Sync", "Engine", "SyncAgent.cs"), "\"[^\"\\n]*\""))
            Assert.DoesNotContain("código de recuperação", m.Value);
        foreach (Match m in Regex.Matches(GoogleSyncTests.Src("Eteris.Browser", "Browser", "settings.html"), "'[^'\\n]*'"))
            Assert.DoesNotContain("código de recuperação", m.Value);
    }

    // As palavras-passe sincronizam desde a 0.2.13: a lista do que não vai não as pode ter.
    [Fact]
    public void NoPublicText_SaysPasswordsAreNotSynced()
    {
        var index = Flat(Read("docs", "site", "index.html"));
        foreach (var s in new[] { "as senhas, os cookies", "as palavras-passe, os cookies", "open tabs, passwords, cookies" })
            Assert.DoesNotContain(s, index);
    }

    // Comparações só com o Chrome, o Firefox e o Brave.
    [Fact]
    public void PublicTexts_DoNotNameOtherBrowsersThanTheThree()
    {
        foreach (var file in new[] { SiteFiles[0], SiteFiles[1], SiteFiles[2], ["README.md"] })
            Assert.DoesNotMatch(@"\bEdge\b", Read(file));
    }

    private static readonly Regex Address = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+", RegexOptions.Compiled);

    // Domínios de exemplo (RFC 2606/6761) e o .test que os documentos já usam.
    private static bool IsExampleDomain(string address)
    {
        var domain = address[(address.IndexOf('@') + 1)..].ToLowerInvariant();
        return domain is "example.com" or "example.org" || domain.EndsWith(".test", StringComparison.Ordinal)
            || domain.EndsWith(".example", StringComparison.Ordinal) || domain.EndsWith(".invalid", StringComparison.Ordinal);
    }

    // Nenhum dado pessoal: guarda pela forma (endereços), não por uma lista de pessoas.
    [Fact]
    public void PublicTexts_CarryNoPersonalData()
    {
        string[][] files = [.. PublicFiles, ["CHANGELOG.md"]];
        foreach (var file in files)
            foreach (Match m in Address.Matches(Read(file)))
                Assert.True(IsExampleDomain(m.Value), $"{string.Join('/', file)} tem um endereço fora dos domínios de exemplo");
        // Em src/, tests/ e docs/: nenhum endereço num fornecedor de correio de pessoas.
        string[] providers = ["gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "yahoo.com", "icloud.com", "proton.me", "protonmail.com", "sapo.pt"];
        var root = GoogleSyncTests.FindRoot();
        foreach (var top in new[] { "src", "tests", "docs" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, top), "*", SearchOption.AllDirectories))
            {
                if (Path.GetExtension(file) is not (".cs" or ".html" or ".md" or ".json" or ".js")) continue;
                var relative = Path.GetRelativePath(root, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(d => d is "bin" or "obj" or "target" or "node_modules" or "TestResults")) continue;
                foreach (Match m in Address.Matches(File.ReadAllText(file)))
                {
                    var domain = m.Value[(m.Value.IndexOf('@') + 1)..].ToLowerInvariant();
                    Assert.False(providers.Contains(domain), $"{relative} tem um endereço num fornecedor de correio");
                }
            }
    }

    // R16: o Windows Hello nunca aparece como garantia incondicional. A frase da chave segue MayApproveAsync, e cada
    // documento longo diz uma vez o que «com o Windows Hello» quer dizer (e o caso da conta do Windows sem proteção).
    [Fact]
    public void WindowsHello_IsNeverAnUnconditionalGuarantee()
    {
        var choices = GoogleSyncTests.Src("Eteris.Browser", "Sync", "SyncChoices.cs");
        var start = choices.IndexOf("public static async Task<bool> MayApproveAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "MayApproveAsync não encontrado");
        var approve = choices[start..choices.IndexOf(";\n", start, StringComparison.Ordinal)];
        if (approve.Contains("!agent.HasSecrets ||", StringComparison.Ordinal))
        {
            Assert.Contains("quando há palavras-passe ou outros dados do cofre em jogo", PrivacyPage.SyncKey);
            Assert.DoesNotContain("ligado (com o Windows Hello) ou", PrivacyPage.SyncKey);
        }
        else Assert.Contains("(com o Windows Hello)", PrivacyPage.SyncKey);
        var (pt, en) = Languages(Policy());
        Assert.Single(Regex.Matches(pt, Regex.Escape("Onde se lê «com o Windows Hello», é a confirmação do Windows")));
        Assert.Single(Regex.Matches(en, Regex.Escape("it means the Windows confirmation")));
        foreach (var doc in new[] { SyncDoc(), Flat(Read("docs", "palavras-passe.md")) })
            Assert.Single(Regex.Matches(doc, Regex.Escape("Onde se lê «com o Windows Hello», é a confirmação do Windows")));
        foreach (var (name, text) in PublicTexts())
            Assert.False(text.Contains("sem janela de graça", StringComparison.Ordinal), name);
    }

    // R17: cada promessa diz o que fica de fora.
    [Fact]
    public void PublicTexts_SayWhatEachPromiseLeavesOut()
    {
        var policy = Flat(Policy());
        Assert.Contains("Os pedidos para juntar um computador novo são a exceção", policy);
        Assert.Contains("Requests to add a new computer are the exception", policy);
        Assert.Contains("o ficheiro fica onde o guardar", policy);
        // O que «Sair deste computador» deixa: a frase da própria interface (T12).
        Assert.Contains(Eteris.Browser.Sync.SyncExit.SignOutKeeps, policy);
        var doc = SyncDoc();
        Assert.Contains("0-request", doc);
        Assert.Contains(Eteris.Browser.Sync.SyncExit.SignOutKeeps, doc);
        Assert.Contains("Os pedidos para juntar um computador novo são a exceção", doc);
        Assert.Contains("o ficheiro fica onde o guardar", doc);
        if (GoogleSyncTests.Src("Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs").Contains("\"/sync-lost\"", StringComparison.Ordinal))
            Assert.Contains("O que já estava nesse computador continua lá", doc);
        // A49 (S22): o limite do código escrito.
        if (GoogleSyncTests.Src("Eteris.Sync", "Engine", "SyncAgent.cs").Contains("ApproveWithCodeAsync(", StringComparison.Ordinal))
            Assert.Contains("Escrever o código só protege se for o código que está a ver no seu outro computador", doc);
        foreach (var (name, text) in PublicTexts())
            Assert.False(text.Contains("nunca enviado", StringComparison.Ordinal), name);
    }
}
