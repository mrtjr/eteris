using System.Text.Json;

namespace Eteris.Browser.Browser;

// Auto-teste de privacidade (eteris://privacy/teste): mede, numa página web de verdade, o que um site vê do Éteris.
// A página e o «outro site» são servidos pelo próprio Éteris (SelfTestSchemeHandlerFactory, dois nomes .invalid que
// nunca existem na Internet): nada sai do computador. As medições correm pelo DevTools (PageScript) depois de a página
// carregar e o veredicto (✔/✘/○) é decidido aqui, com o que a página viu e o que o «servidor» recebeu.
public static class SelfTest
{
    public const string Url = "eteris://privacy/teste";
    public const string Site = "autoteste-eteris.invalid";
    public const string ThirdParty = "terceiro-eteris.invalid";
    public const string PageUrl = "https://" + Site + "/";
    public const string CookieName = "eteris_teste";

    public static bool IsTestPage(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == Site && uri.AbsolutePath == "/";

    public static bool IsTestHost(string? host) => host is Site or ThirdParty;

    // O que o «servidor» recebeu: o pedido da página («main», o último) e, por execução, os pedidos que ela fez
    // («<run>/<k>»). Só as execuções mais recentes ficam — nunca se apaga a que está a correr.
    public const int KeepRuns = 8;
    private static readonly Lock RunsGate = new();
    private static readonly Dictionary<string, (long Order, Dictionary<string, IReadOnlyDictionary<string, string>> Keys)> Runs = new(StringComparer.Ordinal);
    private static IReadOnlyDictionary<string, string>? _main;
    private static long _order;

    public static void Record(string key, IReadOnlyDictionary<string, string> headers)
    {
        if (key == "main")
        {
            Volatile.Write(ref _main, headers);
            return;
        }
        var slash = key.IndexOf('/');
        var run = slash < 0 ? string.Empty : key[..slash];
        var k = slash < 0 ? key : key[(slash + 1)..];
        lock (RunsGate)
        {
            if (!Runs.TryGetValue(run, out var entry))
            {
                entry = (++_order, new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal));
                Runs[run] = entry;
                // As execuções mais antigas saem; a nova (a que está a correr) fica sempre.
                while (Runs.Count > KeepRuns)
                    Runs.Remove(Runs.Where(r => r.Key != run).MinBy(r => r.Value.Order).Key);
            }
            // Uma execução só tem umas poucas medições: um pedido repetido substitui, nunca acumula.
            if (entry.Keys.Count < 16 || entry.Keys.ContainsKey(k)) entry.Keys[k] = headers;
        }
    }

    public static IReadOnlyDictionary<string, string>? Got(string key)
    {
        if (key == "main") return Volatile.Read(ref _main);
        var slash = key.IndexOf('/');
        var run = slash < 0 ? string.Empty : key[..slash];
        var k = slash < 0 ? key : key[(slash + 1)..];
        lock (RunsGate) return Runs.TryGetValue(run, out var entry) && entry.Keys.TryGetValue(k, out var h) ? h : null;
    }

    // Quem pode falar com os nomes do teste. Os nomes .invalid não existem na Internet: se respondessem a qualquer
    // página, um fetch() a eles bastava para reconhecer o Éteris. Servem só a página de teste (os pedidos e iframes
    // dela, «Repetir o teste», recarregar) e a navegação que a abre: o salto de eteris://privacy/teste, um endereço
    // escrito ou reposto pelo próprio Éteris (Explicit) ou o histórico. Tudo o resto falha como um nome inexistente.
    public static bool Serves(string? mainFrameUrl, string requestUrl, bool mainFrameNavigation, bool explicitOrHistory)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request) || request.Scheme != Uri.UriSchemeHttps || !IsTestHost(request.Host))
            return false;
        if (Uri.TryCreate(mainFrameUrl, UriKind.Absolute, out var main) && main.Scheme == Uri.UriSchemeHttps && main.Host == Site)
            return true;
        if (!mainFrameNavigation || !IsTestPage(requestUrl)) return false;
        return explicitOrHistory || string.Equals(mainFrameUrl, Url, StringComparison.OrdinalIgnoreCase);
    }

    public sealed record Check(string Id, string Title, bool? Passed, string Detail);

    // RtcApi: a página tem RTCPeerConnection (só se pergunta se existe — ver MeasureScript).
    public sealed record Measured(bool Gpc, IReadOnlyList<string> Languages, bool AdApis, bool LanReached, bool RtcApi,
        string Canvas, string? CanvasBefore, string Run);

    public static Measured? Parse(object? result)
    {
        if (result is not string json) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new Measured(
                r.GetProperty("gpc").GetBoolean(),
                r.GetProperty("languages").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList(),
                r.GetProperty("adApis").GetBoolean(),
                r.GetProperty("lanReached").GetBoolean(),
                r.TryGetProperty("rtc", out var rtc) && rtc.ValueKind == JsonValueKind.True,
                r.GetProperty("canvas").GetString() ?? string.Empty,
                r.TryGetProperty("canvasBefore", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
                r.GetProperty("run").GetString() ?? string.Empty);
        }
        catch (Exception) { return null; }
    }

    private static string? Header(IReadOnlyDictionary<string, string>? headers, string name)
        => headers?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool HasTestCookie(IReadOnlyDictionary<string, string>? headers)
        => Header(headers, "Cookie") is { } cookie && cookie.Contains(CookieName + "=", StringComparison.Ordinal);

    // O veredicto de cada linha. lanBlocked: o Éteris travou o pedido da página para 127.0.0.1 (registo da aba).
    // webRtcPolicy: a política de IP do WebRTC em vigor no perfil desta aba (null = não foi possível lê-la).
    public static IReadOnlyList<Check> Grade(Measured m, IReadOnlyDictionary<string, string>? main, IReadOnlyDictionary<string, string>? firstPartyEcho,
        IReadOnlyDictionary<string, string>? thirdPartyEcho, IReadOnlyDictionary<string, string>? referrerEcho, bool lanBlocked, string? webRtcPolicy)
    {
        var checks = new List<Check>();

        var header = Header(main, "Sec-GPC") == "1";
        checks.Add(new("gpc", "Pede para não ser seguido (GPC)", m.Gpc && header,
            m.Gpc && header ? "A página lê navigator.globalPrivacyControl = true e o servidor recebeu o cabeçalho Sec-GPC: 1."
                : !m.Gpc ? "navigator.globalPrivacyControl não vale true nesta página." : "O servidor não recebeu o cabeçalho Sec-GPC: 1."));

        bool? cookies = !HasTestCookie(firstPartyEcho) ? null : !HasTestCookie(thirdPartyEcho);
        checks.Add(new("cookies", "Cookies de terceiros num iframe", cookies,
            cookies is null ? "Inconclusivo: nem o próprio site conseguiu guardar o cookie de teste aqui."
                : cookies == true ? $"Um iframe de {ThirdParty} pôs um cookie e, no pedido seguinte, não o recebeu de volta — o próprio site recebeu o seu."
                : $"O iframe de {ThirdParty} recebeu de volta o cookie que pôs: os terceiros conseguem segui-lo de site em site."));

        var referrer = Header(referrerEcho, "Referer");
        bool? referrerOk = referrerEcho is null ? null : referrer is null || referrer == PageUrl;
        checks.Add(new("referrer", "Referrer para outro site", referrerOk,
            referrerOk is null ? "Inconclusivo: o pedido ao outro site não chegou."
                : referrer is null ? "A página pediu o endereço completo (unsafe-url) e o outro site não recebeu referrer nenhum."
                : referrerOk == true ? $"A página pediu o endereço completo (unsafe-url) e o outro site recebeu só «{referrer}»."
                : $"O outro site recebeu o endereço completo: «{referrer}»."));

        var language = Header(main, "Accept-Language") ?? string.Empty;
        var oneLanguage = language.Length > 0 && !language.Contains(',') && m.Languages.Count == 1;
        checks.Add(new("language", "Uma só língua", oneLanguage,
            oneLanguage ? $"O servidor recebeu «{language}» e a página vê navigator.languages = [{m.Languages[0]}]."
                : $"O servidor recebeu «{language}» e a página vê [{string.Join(", ", m.Languages)}]: a lista ajuda a distingui-lo."));

        // Lido no motor, sem criar uma ligação de teste: recolher candidatos abriria portas UDP à escuta e o Windows
        // pediria uma exceção na firewall — num teste que promete «tudo neste computador».
        bool? webRtc = !m.RtcApi ? true : webRtcPolicy is null ? null : Shields.PrivacyPreferences.IsWebRtcProtected(webRtcPolicy);
        checks.Add(new("webrtc", "WebRTC sem o IP da sua rede", webRtc,
            !m.RtcApi ? "O WebRTC não está disponível para esta página."
                : webRtcPolicy is null ? "Inconclusivo: não foi possível ler a política de IP do WebRTC no motor."
                : webRtc == true ? $"A política de IP do WebRTC em vigor é «{webRtcPolicy}»: sem proxy, nenhum candidato com o IP da sua rede chega à página. Lido no motor, sem abrir ligações nem portas."
                : $"A política de IP do WebRTC em vigor é «{webRtcPolicy}»: uma página pode descobrir o IP da sua rede."));

        checks.Add(new("lan", "A página não chega à sua rede local", lanBlocked && !m.LanReached,
            lanBlocked ? "Um pedido da página para 127.0.0.1 foi travado pelo Éteris antes de sair."
                : m.LanReached ? "A página conseguiu falar com este computador (127.0.0.1)." : "O pedido para 127.0.0.1 falhou, mas não foi o Éteris a travá-lo."));

        checks.Add(new("ads", "Sem as APIs de anúncios do Chromium", m.AdApis,
            m.AdApis ? "document.browsingTopics, navigator.joinAdInterestGroup, runAdAuction e window.sharedStorage não existem nesta página."
                : "A página ainda vê APIs de anúncios do Chromium."));

        checks.Add(new("canvas", "Impressão digital do canvas", m.CanvasBefore is null ? null : m.CanvasBefore != m.Canvas,
            m.CanvasBefore is null ? $"Primeira medição (impressão {m.Canvas}): carregue em «Repetir o teste» para comparar entre recargas."
                : m.CanvasBefore == m.Canvas ? $"Igual entre recargas ({m.Canvas}): o desenho do canvas deixa reconhecer este computador. A impressão digital borrada ainda está por fazer."
                : $"Mudou entre recargas ({m.CanvasBefore} → {m.Canvas}): o canvas não serve para o reconhecer."));
        return checks;
    }

    // As medições, corridas na página pelo DevTools (corpo de uma função async; devolve JSON).
    public const string MeasureScript = """
        const run = Math.random().toString(36).slice(2, 10);
        const out = { run };
        out.gpc = navigator.globalPrivacyControl === true;
        out.languages = Array.from(navigator.languages || []);
        out.adApis = !('browsingTopics' in document) && !('joinAdInterestGroup' in navigator) && !('runAdAuction' in navigator) && !('sharedStorage' in window);
        const frame = src => new Promise(resolve => {
          const f = document.createElement('iframe');
          f.style.display = 'none';
          f.onload = () => resolve(f);
          setTimeout(() => resolve(f), 3000);
          f.src = src;
          document.body.append(f);
        });
        try { await fetch('/set?run=' + run, { credentials: 'include' }); await fetch('/eco?k=c1&run=' + run, { credentials: 'include' }); } catch (e) {}
        await frame('https://terceiro-eteris.invalid/set?run=' + run);
        await frame('https://terceiro-eteris.invalid/eco?k=c3&run=' + run);
        try { await fetch('https://terceiro-eteris.invalid/eco?k=ref&run=' + run, { referrerPolicy: 'unsafe-url', mode: 'no-cors' }); } catch (e) {}
        try { await fetch('http://127.0.0.1:9/eteris-autoteste', { mode: 'no-cors' }); out.lanReached = true; } catch (e) { out.lanReached = false; }
        out.rtc = typeof RTCPeerConnection === 'function';
        const cv = document.createElement('canvas');
        cv.width = 240; cv.height = 60;
        const g = cv.getContext('2d');
        g.textBaseline = 'top'; g.font = '16px Arial'; g.fillStyle = '#f60'; g.fillRect(10, 10, 100, 30);
        g.fillStyle = '#069'; g.fillText('Éteris autoteste <canvas>', 4, 18);
        g.fillStyle = 'rgba(102, 204, 0, 0.7)'; g.fillText('Éteris autoteste <canvas>', 6, 20);
        const data = cv.toDataURL();
        let h = 2166136261;
        for (let i = 0; i < data.length; i++) { h ^= data.charCodeAt(i); h = Math.imul(h, 16777619); }
        out.canvas = (h >>> 0).toString(16);
        try { out.canvasBefore = sessionStorage.getItem('eteris-canvas'); sessionStorage.setItem('eteris-canvas', out.canvas); } catch (e) { out.canvasBefore = null; }
        return JSON.stringify(out);
        """;

    // Mostra os resultados na página (texto, nunca HTML vindo dos dados).
    public static string RenderScript(IReadOnlyList<Check> checks)
    {
        var json = JsonSerializer.Serialize(checks.Select(c => new { id = c.Id, title = c.Title, passed = c.Passed, detail = c.Detail }));
        return "(() => { const checks = " + json + """
            ;
            const list = document.getElementById('results');
            list.replaceChildren();
            let ok = 0, bad = 0;
            for (const c of checks) {
              if (c.passed === true) ok++; else if (c.passed === false) bad++;
              const row = document.createElement('div'); row.className = 'row'; row.setAttribute('role', 'listitem'); row.dataset.id = c.id;
              const mark = document.createElement('div');
              mark.className = 'mark ' + (c.passed === true ? 'ok' : c.passed === false ? 'bad' : 'off');
              mark.textContent = c.passed === true ? '✔' : c.passed === false ? '✘' : '○';
              mark.setAttribute('aria-label', c.passed === true ? 'Protegido' : c.passed === false ? 'Exposto' : 'Inconclusivo');
              const text = document.createElement('div'); text.className = 'text';
              const b = document.createElement('b'); b.textContent = c.title;
              const s = document.createElement('span'); s.textContent = c.detail;
              text.append(b, s); row.append(mark, text); list.append(row);
            }
            document.getElementById('score').textContent = ok + ' de ' + (ok + bad) + ' protegidos' + (checks.length - ok - bad ? ' · ' + (checks.length - ok - bad) + ' por decidir' : '');
            document.getElementById('status').textContent = 'Medido às ' + new Date().toLocaleTimeString('pt-PT') + ' — tudo neste computador.';
            document.body.classList.add('done');
            })();
            """;
    }

    public static string FailedScript(string reason)
        => "document.getElementById('status').textContent = " + JsonSerializer.Serialize("Não foi possível medir: " + reason) + ";";

    // A página de teste (servida em https://autoteste-eteris.invalid/): sem scripts próprios — as medições e os
    // resultados chegam pelo DevTools.
    public const string PageHtml = """
        <!doctype html>
        <html lang="pt">
        <head>
        <meta charset="utf-8">
        <meta name="color-scheme" content="dark">
        <title>Auto-teste de privacidade</title>
        <style>
          :root { --bg: #0b0d13; --text: #e9ebf2; --muted: #a3a9b8; --faint: #5e6577; --line: rgba(255,255,255,.08); --card: rgba(255,255,255,.03);
                  --ok: #34d399; --bad: #fb7185; --off: #868da0; }
          * { box-sizing: border-box; margin: 0; }
          body { background: var(--bg); color: var(--text); min-height: 100vh; font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; }
          main { width: min(760px, 92vw); margin: 0 auto; padding: 48px 0 80px; }
          h1 { font-size: 30px; font-weight: 300; margin-bottom: 8px; }
          h1 b { font-weight: 600; background: linear-gradient(90deg, #8b5cf6, #22d3ee, #34d399, #f472b6); -webkit-background-clip: text; background-clip: text; color: transparent; }
          .lead { color: var(--muted); font-size: 14px; line-height: 1.5; margin-bottom: 18px; }
          #score { font-size: 22px; font-weight: 600; margin-bottom: 4px; min-height: 28px; }
          #status { color: var(--faint); font-size: 12.5px; margin-bottom: 16px; }
          .card { background: var(--card); border: 1px solid var(--line); border-radius: 20px; overflow: hidden; }
          .row { display: flex; gap: 16px; padding: 16px 20px; align-items: flex-start; }
          .row + .row { border-top: 1px solid var(--line); }
          .mark { flex: none; width: 28px; height: 28px; border-radius: 50%; display: grid; place-items: center; font-size: 15px; font-weight: 700; }
          .mark.ok { background: rgba(52,211,153,.18); color: var(--ok); }
          .mark.bad { background: rgba(251,113,133,.18); color: var(--bad); }
          .mark.off { background: rgba(255,255,255,.06); color: var(--off); }
          .text b { display: block; font-weight: 500; font-size: 14.5px; margin-bottom: 3px; }
          .text span { color: var(--muted); font-size: 12.8px; line-height: 1.45; }
          .wait { padding: 26px 20px; color: var(--muted); font-size: 13.5px; }
          .wait::before { content: ''; display: inline-block; width: 10px; height: 10px; border-radius: 50%; margin-right: 10px; background: #22d3ee;
                          animation: pulse 1s ease-in-out infinite alternate; }
          @keyframes pulse { from { opacity: .25; } to { opacity: 1; } }
          @media (prefers-reduced-motion: reduce) { .wait::before { animation: none; } }
          footer { display: flex; justify-content: space-between; gap: 12px; align-items: center; margin-top: 16px; color: var(--faint); font-size: 12.5px; }
          footer a { color: #67e8f9; text-decoration: none; font-size: 13px; padding: 8px 14px; border-radius: 12px; border: 1px solid var(--line); }
          footer a:hover { background: rgba(255,255,255,.06); }
        </style>
        </head>
        <body>
        <main>
          <h1><b>Auto-teste</b> de privacidade</h1>
          <p class="lead">Esta página faz de site: mede o que um site consegue ver do Éteris — o pedido para não ser seguido,
            cookies de terceiros num iframe, o referrer, a língua, o WebRTC, a rede local e a impressão digital do canvas.
            Ela e o «outro site» são servidos pelo próprio Éteris com nomes que não existem na Internet: nada sai do computador.</p>
          <div id="score"></div>
          <div id="status">A medir… (cerca de 5 segundos)</div>
          <div class="card" id="results" role="list"><div class="wait">A medir o que esta página vê…</div></div>
          <footer><span>Volte à privacidade com Alt+← ou em eteris://privacy/.</span><a href="/">Repetir o teste</a></footer>
        </main>
        </body>
        </html>
        """;
}
