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

    // A marca é o atributo data-eteris-pausa (dataset.eterisPausa), o que a prova ao vivo procura.
    [Fact]
    public void TheMark_IsTheDataAttribute()
    {
        Assert.Contains("eterisPausa", MediaInfo.PauseMarkScript);
        Assert.Contains("eterisPausa", MediaInfo.ResumeMarkedScript);
    }
}
