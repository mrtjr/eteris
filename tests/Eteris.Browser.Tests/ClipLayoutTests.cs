using System.IO;
using System.Text.Json;
using System.Windows;
using Eteris.Browser.Watching;

namespace Eteris.Browser.Tests;

// O recorte ao vivo: o tamanho da janela pequena a partir do pedaço escolhido, onde ela aparece e quantas podem abrir.
public class ClipLayoutTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void Size_KeepsSmallElements_AboveMinimum() => Assert.Equal(new Size(200, 120), ClipLayout.Size(80, 40, WorkArea));

    [Fact]
    public void Size_ShrinksLargeElements_KeepingAspect()
    {
        var s = ClipLayout.Size(1800, 900, WorkArea);
        Assert.Equal(960, s.Width);
        Assert.Equal(480, s.Height);
    }

    [Fact]
    public void Size_KeepsMediumElements() => Assert.Equal(new Size(320, 180), ClipLayout.Size(320, 180, WorkArea));

    [Fact]
    public void Size_TallElements_ShrinkByHeight()
    {
        var s = ClipLayout.Size(400, 2080, WorkArea);
        Assert.Equal(520, s.Height);
        // 400 × 520/2080 = 100, abaixo do mínimo: fica nos 200.
        Assert.Equal(200, s.Width);
    }

    // O retângulo vem da página: um valor sem sentido dá o tamanho mínimo, nunca uma janela gigante ou com NaN.
    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.NaN)]
    [InlineData(-50, 100)]
    [InlineData(0, 0)]
    [InlineData(double.PositiveInfinity, 100)]
    public void Size_NonsenseFromThePage_GivesTheMinimum(double w, double h)
        => Assert.Equal(new Size(200, 120), ClipLayout.Size(w, h, WorkArea));

    [Fact]
    public void Size_HugeElement_StaysWithinHalfTheWorkArea()
    {
        var s = ClipLayout.Size(1e12, 1e12, WorkArea);
        Assert.True(s.Width <= 960 && s.Height <= 520);
    }

    [Fact]
    public void Place_StacksFromBottomRight()
    {
        var wa = new Rect(0, 0, 1920, 1040);
        Assert.Equal(new Point(1920 - 16 - 300, 1040 - 16 - 200), ClipLayout.Place(new Size(300, 200), wa, 0));
        Assert.Equal(new Point(1920 - 16 - 300 - 24, 1040 - 16 - 200 - 24), ClipLayout.Place(new Size(300, 200), wa, 1));
    }

    [Fact]
    public void Place_FollowsTheWorkAreaOrigin()
        => Assert.Equal(new Point(1920 + 1280 - 16 - 300, 40 + 1000 - 16 - 200), ClipLayout.Place(new Size(300, 200), new Rect(1920, 40, 1280, 1000), 0));

    // O lugar de um recorte novo é o 1.º livre: fechado o 1.º de dois, o novo não cai em cima do 2.º.
    [Fact]
    public void FreeSlot_IsTheLowestUnused()
    {
        Assert.Equal(0, ClipLayout.FreeSlot([]));
        Assert.Equal(2, ClipLayout.FreeSlot([0, 1]));
        Assert.Equal(0, ClipLayout.FreeSlot([1]));
        Assert.Equal(1, ClipLayout.FreeSlot([2, 0]));
    }

    [Fact]
    public void AtMostThreeClips()
    {
        Assert.Equal(3, ClipLayout.MaxOpen);
        Assert.True(ClipLayout.CanOpen(2));
        Assert.False(ClipLayout.CanOpen(3));
    }

    // O seletor entra como texto JSON: aspas e «</script>» não saem da sua string.
    [Fact]
    public void Isolate_SerializesTheSelector()
    {
        const string selector = "a[title=\"x'y\"] </script>";
        Assert.Contains(JsonSerializer.Serialize(selector), ClipScripts.Isolate(selector));
    }

    // O script na página (Node, com um DOM de brincar): o pedaço vai para a origem da janela com um translate no html e
    // o script devolve o tamanho dele; um pedaço com 0×0 (a página esconde-o nesta largura) desfaz tudo e devolve null —
    // a janela mostra a página e di-lo, em vez de ficar em branco.
    private static string RunIsolate(double left, double top, double width, double height)
    {
        var stub = $$"""
            const styles = [];
            const node = () => ({ style: {}, textContent: '', isConnected: true, remove() { this.isConnected = false; } });
            const el = { isConnected: true, parentElement: null, setAttribute() {}, removeAttribute() {},
                         getBoundingClientRect: () => ({ left: {{left}}, top: {{top}}, width: {{width}}, height: {{height}} }) };
            globalThis.window = globalThis;
            globalThis.scrollX = 0; globalThis.scrollY = 0;
            globalThis.addEventListener = () => {}; globalThis.removeEventListener = () => {};
            globalThis.ResizeObserver = class { observe() {} disconnect() {} };
            globalThis.document = {
              querySelector: () => el, querySelectorAll: () => [],
              documentElement: {}, head: { append(...nodes) { styles.push(...nodes); } },
              createElement: node,
            };
            (
            """ + ClipScripts.Isolate("#preco") + """
            ).then(v => { console.log(JSON.stringify({ result: v, styles: styles.filter(s => s.isConnected).map(s => s.textContent) })); process.exit(0); });
            """;
        return NodeRunner.Run(stub).Trim();
    }

    [Fact]
    public void Isolate_MovesThePieceToTheOrigin()
    {
        using var output = JsonDocument.Parse(RunIsolate(100, 50, 300, 40));
        Assert.Equal("""{"w":300,"h":40}""", output.RootElement.GetProperty("result").GetString());
        var styles = output.RootElement.GetProperty("styles").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains(styles, s => s!.Contains("translate(-100px, -50px)", StringComparison.Ordinal));
        Assert.Contains(styles, s => s!.Contains("visibility: hidden", StringComparison.Ordinal));
    }

    [Fact]
    public void Isolate_ZeroSizePiece_ShowsThePage()
    {
        using var output = JsonDocument.Parse(RunIsolate(0, 0, 0, 0));
        Assert.Equal(JsonValueKind.Null, output.RootElement.GetProperty("result").ValueKind);
        Assert.Empty(output.RootElement.GetProperty("styles").EnumerateArray());
    }

    // «Esquecer ao fechar»: um recorte aberto conta como página aberta do site (senão o site era esquecido com o recorte
    // ainda a usar a sessão e a gravar cookies novos), e fechar o recorte volta a verificar o site.
    [Fact]
    public void ForgetOnClose_CountsOpenClips()
    {
        var src = Path.Combine(FindRoot(), "src", "Eteris.Browser");
        var forget = File.ReadAllText(Path.Combine(src, "MainWindow.Forget.cs"));
        Assert.Matches(@"AddressesOf\(MainWindow window\)[\s\S]*?\.Concat\(window\._clips\.Select\(c => c\.Address\)\)", forget);
        var watch = File.ReadAllText(Path.Combine(src, "MainWindow.Watch.cs"));
        Assert.Matches(@"clip\.Closed \+= \(_, _\) =>\s*\{\s*_clips\.Remove\(clip\);\s*if \(!_forgetOnCloseDone\) ScheduleForgetFor\(\[clip\.Address\]\);", watch);
    }

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    // O seletor de pedaços devolve também o retângulo do pedaço (px CSS), lido à parte do seletor e do texto.
    [Fact]
    public void PickSize_ReadsTheRect()
        => Assert.Equal(new Size(312.5, 48), WatchReadingParser.PickSize("""{"selector":"#a","text":"x","rect":{"x":10,"y":20,"w":312.5,"h":48}}"""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nada")]
    [InlineData("""{"selector":"#a","text":"x"}""")]
    [InlineData("""{"selector":"#a","rect":{"w":"10","h":5}}""")]
    [InlineData("""{"selector":"#a","rect":7}""")]
    public void PickSize_WithoutARect_IsNull(string? json) => Assert.Null(WatchReadingParser.PickSize(json));
}
