using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

// Data/HistoryVisit.cs: o que entra no histórico global no fim de um carregamento. Valores medidos ao vivo com um mp4
// aberto diretamente: o CEF dá FrameLoadEnd com -3 depois do commit; o WebView2 dá IsSuccess = false, 200 e
// ConnectionAborted depois de ContentLoading.
public class HistoryVisitTests
{
    [Theory]
    [InlineData(200, false, true)]
    [InlineData(304, false, true)]
    [InlineData(404, true, false)]   // página de erro do site
    [InlineData(500, true, false)]
    [InlineData(0, true, false)]     // sem resposta (falha de rede, página interna)
    [InlineData(-3, true, true)]     // ficheiro de média aberto diretamente: abortado depois do commit
    [InlineData(-3, false, false)]   // cancelado antes de haver documento (transferência, navegação substituída)
    [InlineData(-2, true, false)]    // outro erro de rede
    public void MainEngine(int status, bool committed, bool visit)
        => Assert.Equal(visit, HistoryVisit.FromMainEngine(status, committed));

    [Theory]
    [InlineData(true, 200, false, true, true)]
    [InlineData(true, 404, false, true, false)]
    [InlineData(false, 200, true, true, true)]    // ficheiro de média aberto diretamente (ConnectionAborted)
    [InlineData(false, 200, true, false, false)]  // abortado sem documento
    [InlineData(false, 200, false, true, false)]  // outro erro depois do commit
    [InlineData(false, 0, true, true, false)]     // abortado sem resposta
    [InlineData(false, 500, true, true, false)]
    public void VideoEngine(bool success, int status, bool aborted, bool committed, bool visit)
        => Assert.Equal(visit, HistoryVisit.FromVideoEngine(success, status, aborted, committed));
}
