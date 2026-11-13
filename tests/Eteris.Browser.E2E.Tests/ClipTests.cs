namespace Eteris.Browser.E2E.Tests;

// O recorte ao vivo (ClipWindow): um pedaço da loja de teste numa janela pequena, sempre por cima, aberto ao arrancar
// (ETERIS_TEST_CLIP; o gesto do menu precisa do rato para apontar o pedaço). A loja é servida dentro do Éteris em
// https://loja-eteris.invalid/ (ETERIS_TEST_SHOP_DIR): nada vai à rede.
[Collection("E2E")]
public class ClipTests
{
    // Com a janela principal minimizada e o sinal de pouca memória ligado, o recorte continua vivo: o pedaço fica sozinho no
    // canto da janela, a página diz-se à vista e as animações continuam a correr. Fechar a janela principal fecha o recorte
    // e o Éteris sai (nada fica a correr).
    [Fact]
    public async Task Clip_StaysAliveWithWindowMinimized_AndClosesWithIt()
    {
        await using var eteris = await EterisProcess.StartAsync("about:blank", new Dictionary<string, string>
        {
            ["ETERIS_TEST_SHOP_DIR"] = ShopDir(),
            ["ETERIS_TEST_CLIP"] = "https://loja-eteris.invalid/loja-timers.html\t#preco",
            ["ETERIS_TEST_PRESSAO"] = "1",
        });
        await using var clip = await eteris.PageAsync(u => u.Contains("loja-eteris.invalid/loja-timers.html", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        eteris.MinimizeMainWindow();

        // O pedaço (com o preço posto por JavaScript) fica na origem da janela, e o título da loja escondido.
        await clip.WaitForAsync("""
            (() => {
              const el = document.querySelector('#preco');
              if (!el || !el.hasAttribute('data-eteris-recorte') || !el.textContent.includes('1.234,56')) return false;
              const r = el.getBoundingClientRect();
              return Math.abs(r.left) < 1 && Math.abs(r.top) < 1 && getComputedStyle(document.querySelector('h1')).visibility === 'hidden';
            })()
            """, TimeSpan.FromSeconds(30), "document.querySelector('#preco')?.outerHTML");

        // Vivo com a janela principal minimizada: à vista e com fotogramas a correr.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal("visible", await clip.EvaluateAsync<string>("document.visibilityState"));
        var frames = await clip.EvaluateAsync<int>("""
            new Promise(done => { let n = 0; const tick = () => { n++; }; const id = setInterval(() => requestAnimationFrame(tick), 16);
              setTimeout(() => { clearInterval(id); done(n); }, 1500); })
            """);
        Assert.True(frames > 10, $"só {frames} fotogramas em 1,5 s");

        Assert.True(await eteris.CloseMainWindowAsync(TimeSpan.FromSeconds(20)), "o Éteris continuou a correr depois de fechar a janela principal");
    }

    private static string ShopDir() => Path.Combine(EterisProcess.Root(), "tests", "Eteris.Browser.E2E.Tests", "Shop");
}
