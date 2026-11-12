namespace Eteris.Browser.E2E.Tests;

[CollectionDefinition("E2E", DisableParallelization = true)]
public sealed class E2ECollection;

// Primeira fatia dos testes que arrancam o Éteris (TCO-01): a 1.ª página abre, e o auto-teste de privacidade não tem
// nenhuma linha exposta. Um Éteris de cada vez (instância única por perfil, e as janelas disputam o ecrã).
[Collection("E2E")]
public class StartupTests
{
    [Fact]
    public async Task Startup_OpensTheFirstPage()
    {
        await using var eteris = await EterisProcess.StartAsync("eteris://privacy/");
        await using var page = await eteris.PageAsync(u => u.StartsWith("eteris://privacy", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
        await page.WaitForAsync("document.readyState === 'complete' && document.body.innerText.trim().length > 0", TimeSpan.FromSeconds(30));
        Assert.Equal("eteris:", await page.EvaluateAsync<string>("location.protocol"));
        Assert.False(eteris.HasExited);
    }

    private const string ExposedRows = "[...document.querySelectorAll('.row')].filter(r => r.querySelector('.mark.bad')).map(r => r.dataset.id).join(',')";

    // O auto-teste corre duas vezes na mesma aba, e cada exceção tem a sua razão:
    // - no 1.º documento de uma aba a linha «gpc» ainda sai exposta: o navigator.globalPrivacyControl não chega a esse
    //   documento (achado PRO-03 da auditoria; a correção é a Task 14 do plano 6, que tira esta exceção);
    // - a linha «canvas» só decide ao comparar entre recargas, e a própria página diz que a impressão digital borrada
    //   ainda está por fazer: depois de recarregar sai exposta até isso existir.
    // Tudo o resto, incluindo o «gpc» no 2.º documento, tem de estar protegido.
    [Fact]
    public async Task SelfTest_HasNoExposedLine()
    {
        await using var eteris = await EterisProcess.StartAsync("eteris://privacy/teste");
        await using var page = await eteris.PageAsync(u => u.StartsWith("https://autoteste-eteris.invalid/", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
        await page.WaitForAsync("document.body.classList.contains('done')", TimeSpan.FromSeconds(90), describe: "document.getElementById('status').textContent");
        Assert.Contains(await page.EvaluateAsync<string>(ExposedRows), new[] { "", "gpc" });
        Assert.True(await page.EvaluateAsync<int>("document.querySelectorAll('.mark.ok').length") >= 1);

        await page.EvaluateAsync<bool>("location.reload(), true");
        await page.WaitForAsync("document.readyState === 'complete' && document.body.classList.contains('done')", TimeSpan.FromSeconds(90),
                                describe: "document.getElementById('status').textContent");
        Assert.Contains(await page.EvaluateAsync<string>(ExposedRows), new[] { "", "canvas" });
    }
}
