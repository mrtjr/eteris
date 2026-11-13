namespace Eteris.Browser.E2E.Tests;

// A vigia lê um preço posto por JavaScript (temporizadores encadeados, requestAnimationFrame) com a janela principal
// minimizada: o motor da vigia corre numa janela própria fora do ecrã, com o tamanho de uma janela normal. A loja de teste
// (Shop/) é servida dentro do Éteris em https://loja-eteris.invalid/ (ETERIS_TEST_SHOP_DIR): nada vai à rede.
// O resultado lê-se em eteris://vigias/ (data-last-prices no <body>), que chega com a Task 7 do plano da etapa A.
[Collection("E2E")]
public class WatchTests
{
    [Theory]
    [InlineData("loja-timers.html", "1234.56")]
    [InlineData("loja-raf.html", "999.90")]
    public async Task Watch_ReadsJsPrice_WithWindowMinimized(string page, string expected)
    {
        await using var eteris = await EterisProcess.StartAsync("about:blank", new Dictionary<string, string>
        {
            ["ETERIS_TEST_SHOP_DIR"] = ShopDir(),
            ["ETERIS_TEST_WATCH"] = $"https://loja-eteris.invalid/{page}\t#preco",
            ["ETERIS_TEST_WATCH_SECONDS"] = "20",
            ["ETERIS_TEST_PRESSAO"] = "1",
        });
        // Uma página da janela principal, nunca a do motor da vigia (que carrega a loja).
        await using var vigias = await eteris.PageAsync(u => !u.Contains("loja-eteris.invalid", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        eteris.MinimizeMainWindow();
        await vigias.NavigateAsync("eteris://vigias/");
        await vigias.WaitForAsync($"document.body?.dataset.lastPrices?.includes('{expected}')", TimeSpan.FromSeconds(90),
            "document.body?.dataset.lastPrices ?? location.href");
    }

    private static string ShopDir() => Path.Combine(EterisProcess.Root(), "tests", "Eteris.Browser.E2E.Tests", "Shop");
}
