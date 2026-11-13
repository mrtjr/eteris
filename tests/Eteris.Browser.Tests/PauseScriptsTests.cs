using Eteris.Browser.Browser;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Os scripts da Pausa (Tabs/MediaInfo.cs), corridos no Node sem DOM: um document.querySelectorAll de imitação devolve
// elementos { paused, pause(), play(), dataset }. A Pausa pausa e marca só o que estava a tocar; o «Retomar o que tocava»
// toca só o marcado e tira a marca. Uma marca que ficou de uma Pausa anterior (o «Retomar» ignorado) nunca toca.
public class PauseScriptsTests
{
    private const string Setup = """
        const calls = [];
        function media(name, paused, mark) {
          const m = { name, paused, dataset: {} };
          if (mark) m.dataset.eterisPausa = '1';
          m.pause = function () { calls.push('pause:' + name); this.paused = true; };
          m.play = function () { calls.push('play:' + name); this.paused = false; return Promise.resolve(); };
          return m;
        }
        const all = [media('a', false), media('c', true), media('velha', true, true)];
        globalThis.document = { querySelectorAll: sel => { calls.push('q:' + sel); return all; } };
        function state() { return all.map(m => m.name + '=' + (m.paused ? 'parado' : 'toca') + (m.dataset.eterisPausa ? '+marca' : '')).join(' '); }
        """;

    private static string Run(string body) => NodeRunner.Run(Setup + "\n" + body).Trim();

    [Fact]
    public void PauseMark_PausesAndMarksOnlyWhatWasPlaying()
    {
        var output = Run("const n = " + MediaInfo.PauseMarkScript + ";\nconsole.log(n + ' | ' + state() + ' | ' + calls.join(','));");
        Assert.Equal("1 | a=parado+marca c=parado velha=parado | q:video,audio,pause:a", output);
    }

    [Fact]
    public void ResumeMarked_PlaysOnlyTheMarkedAndRemovesTheMark()
    {
        var output = Run("""
            const n = PAUSE;
            calls.length = 0;
            const r = RESUME;
            console.log(n + ' ' + r + ' | ' + state() + ' | ' + calls.join(','));
            """.Replace("PAUSE", MediaInfo.PauseMarkScript).Replace("RESUME", MediaInfo.ResumeMarkedScript));
        Assert.Equal("1 1 | a=toca c=parado velha=parado | q:video,audio,play:a", output);
    }

    // Sem nada marcado, o «Retomar» não toca nada (e devolve 0).
    [Fact]
    public void ResumeMarked_WithoutMarks_PlaysNothing()
    {
        var output = Run("all.forEach(m => delete m.dataset.eterisPausa);\nconst r = " + MediaInfo.ResumeMarkedScript + ";\nconsole.log(r + ' | ' + calls.join(','));");
        Assert.Equal("0 | q:video,audio", output);
    }

    // Uma página que rebenta o querySelectorAll não rebenta o Éteris: devolve 0.
    [Fact]
    public void Scripts_SurviveAPageThatThrows()
    {
        var output = Run("document.querySelectorAll = () => { throw new Error('x'); };\nconsole.log((" + MediaInfo.PauseMarkScript + ") + ' ' + (" + MediaInfo.ResumeMarkedScript + "));");
        Assert.Equal("0 0", output);
    }

    // Um vídeo em Picture-in-Picture: a janela flutuante do Chromium (sempre por cima) ficava sobre o ecrã calmo. A Pausa sai
    // dela (sem a voltar a abrir no regresso: isso pedia um gesto do utilizador); sem PiP não chama nada; e uma página
    // onde sair falha não rebenta o script.
    [Fact]
    public void PauseMark_LeavesPictureInPicture()
    {
        var output = Run("""
            document.pictureInPictureElement = all[0];
            document.exitPictureInPicture = function () { calls.push('sair-pip'); return Promise.resolve(); };
            const n = PAUSE;
            console.log(n + ' | ' + calls.join(','));
            """.Replace("PAUSE", MediaInfo.PauseMarkScript));
        Assert.Equal("1 | q:video,audio,pause:a,sair-pip", output);
        Assert.Equal("1 | q:video,audio,pause:a", Run("const n = " + MediaInfo.PauseMarkScript + ";\nconsole.log(n + ' | ' + calls.join(','));"));
        var failing = Run("""
            document.pictureInPictureElement = all[0];
            document.exitPictureInPicture = function () { return Promise.reject(new Error('x')); };
            const n = PAUSE;
            setTimeout(() => console.log(n + ' | ' + state()), 0);
            """.Replace("PAUSE", MediaInfo.PauseMarkScript));
        Assert.Equal("1 | a=parado+marca c=parado velha=parado", failing);
        var throwing = Run("document.pictureInPictureElement = all[0];\ndocument.exitPictureInPicture = function () { throw new Error('x'); };\nconsole.log(" + MediaInfo.PauseMarkScript + ");");
        Assert.Equal("1", throwing);
    }

    // A marca é o atributo data-eteris-pausa (dataset.eterisPausa), o que a prova ao vivo procura.
    [Fact]
    public void TheMark_IsTheDataAttribute()
    {
        Assert.Contains("eterisPausa", MediaInfo.PauseMarkScript);
        Assert.Contains("eterisPausa", MediaInfo.ResumeMarkedScript);
    }

    // No motor de vídeo os scripts passam pelo canal único (Browser/VideoPageScript.cs, Runtime.evaluate pelo DevTools do
    // WebView2): da resposta só se lê um número; o resto (erro, texto, exceção da página) dá nulo.
    [Theory]
    [InlineData("{\"result\":{\"type\":\"number\",\"value\":2,\"description\":\"2\"}}", 2.0)]
    [InlineData("{\"result\":{\"type\":\"number\",\"value\":0}}", 0.0)]
    [InlineData("{\"result\":{\"type\":\"string\",\"value\":\"2\"}}", null)]
    [InlineData("{\"result\":{\"type\":\"object\",\"subtype\":\"error\"},\"exceptionDetails\":{}}", null)]
    [InlineData("não é JSON", null)]
    [InlineData("[]", null)]
    public void VideoEngine_ReadsOnlyANumber(string json, double? expected)
        => Assert.Equal(expected, VideoPageScript.NumberResult(json));
}
