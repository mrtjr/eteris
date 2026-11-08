namespace Eteris.Browser.Shields;

// Sinais de privacidade que o Éteris envia a todos os sites: o Global Privacy Control, no cabeçalho de cada pedido e
// no JavaScript, que é onde os avisos de cookies e o Consent Mode o leem.
// O «Não rastrear» (DNT: 1) deixou de ir (medido, docs/paridade.md #6): o Chrome e o Brave não o enviam, valia 2,03
// bits no Cover Your Tracks contra 0,40 de quem não o envia, e o navigator.doNotTrack ficava null, incoerente com o
// cabeçalho. O GPC é o sinal com valor legal (CCPA) e é o que os sites leem hoje.
public static class PrivacySignals
{
    public static readonly IReadOnlyList<(string Name, string Value)> Headers = [("Sec-GPC", "1")];

    // navigator.globalPrivacyControl === true. Registado pelo ScriptletInjector uma vez por aba (antes de qualquer
    // script da página, em todos os frames do processo, nunca retirado); se o motor já expuser o valor, não mexe.
    public const string GpcScript = """
        (() => {
          try {
            const proto = Navigator.prototype;
            const own = Object.getOwnPropertyDescriptor(proto, 'globalPrivacyControl');
            if (own && own.get && own.get.call(navigator) === true) return;
            Object.defineProperty(proto, 'globalPrivacyControl', {
              get: function globalPrivacyControl() { return true; }, configurable: true, enumerable: true,
            });
          } catch (e) { }
        })();
        """;

    // Dicas do cliente de alta entropia no JavaScript: navigator.userAgentData.getHighEntropyValues() dava a versão
    // exata do Windows, a versão completa do Chromium, a arquitetura e o modelo — os mesmos dados que o Éteris tira dos
    // cabeçalhos (RequestIdentity.IsIdentifyingHint). Passam a valores genéricos, coerentes com o User-Agent reduzido:
    // versão «<maior>.0.0.0», Windows «10.0.0», x86 de 64 bits, sem modelo. As marcas, o telemóvel e a plataforma (as
    // dicas de baixa entropia, que já vão em todos os pedidos) ficam; um argumento inválido continua a ser recusado
    // pelo próprio motor. toJSON() só tem as de baixa entropia e não muda.
    public const string HintsScript = """
        (() => {
          try {
            const UAData = self.NavigatorUAData;
            if (!UAData || typeof UAData.prototype.getHighEntropyValues !== 'function') return;
            const original = UAData.prototype.getHighEntropyValues;
            const full = brand => ({ brand: brand.brand, version: String(brand.version).split('.')[0] + '.0.0.0' });
            Object.defineProperty(UAData.prototype, 'getHighEntropyValues', {
              value: function getHighEntropyValues(hints) {
                return original.call(this, hints).then(real => {
                  const out = { brands: real.brands, mobile: real.mobile, platform: real.platform };
                  const brands = Array.isArray(real.brands) ? real.brands : [];
                  const main = brands.find(b => !/not.?a.?brand/i.test(b.brand)) || brands[0];
                  for (const hint of Array.from(hints || [])) {
                    switch (hint) {
                      case 'architecture': out.architecture = 'x86'; break;
                      case 'bitness': out.bitness = '64'; break;
                      case 'model': out.model = ''; break;
                      case 'platformVersion': out.platformVersion = '10.0.0'; break;
                      case 'uaFullVersion': out.uaFullVersion = main ? full(main).version : ''; break;
                      case 'fullVersionList': out.fullVersionList = brands.map(full); break;
                      case 'wow64': out.wow64 = false; break;
                      case 'formFactors': out.formFactors = ['Desktop']; break;
                    }
                  }
                  return out;
                });
              },
              writable: true, configurable: true, enumerable: true,
            });
          } catch (e) { }
        })();
        """;

    // Dispositivos de câmara, microfone e som (medido, docs/paridade.md #6): o motor dava a qualquer site, sem pedir
    // nada, a lista completa com o nome de cada dispositivo e um deviceId por site que sobrevive a reiniciar — o CEF
    // responde «sim» a CheckMediaAccessPermission (libcef/browser/media_access_query.cc; a correção no motor está em
    // docs/motor.md). Até lá, como o Chrome sem permissão: uma entrada por tipo, com nome, deviceId e groupId vazios.
    // Os reais aparecem, por tipo, quando o site tem a permissão (permissions.query «granted») ou quando um
    // getUserMedia deste documento foi aceite (áudio → microfone e saídas de som; vídeo → câmara). mediaDevices não
    // existe nos workers: o registo da sessão cobre todos os frames.
    public const string DevicesScript = """
        (() => {
          try {
            const MD = self.MediaDevices;
            if (!MD || typeof MD.prototype.enumerateDevices !== 'function') return;
            const realEnumerate = MD.prototype.enumerateDevices;
            const realGetUserMedia = MD.prototype.getUserMedia;
            const accepted = { audio: false, video: false };
            const group = kind => kind === 'videoinput' ? 'video' : 'audio';
            const granted = name => {
              try { return navigator.permissions.query({ name }).then(s => s.state === 'granted', () => false); }
              catch (e) { return Promise.resolve(false); }
            };
            const blank = real => {
              const proto = Object.getPrototypeOf(real);
              const fields = { deviceId: '', kind: real.kind, label: '', groupId: '' };
              const out = Object.create(proto);
              for (const key of Object.keys(fields)) Object.defineProperty(out, key, { value: fields[key] });
              Object.defineProperty(out, 'toJSON', { value: function toJSON() { return Object.assign({}, fields); } });
              if (typeof proto.getCapabilities === 'function')
                Object.defineProperty(out, 'getCapabilities', { value: function getCapabilities() { return {}; } });
              return out;
            };
            const filter = (list, shown) => {
              const out = [];
              const seen = new Set();
              for (const device of list) {
                if (shown[group(device.kind)]) { out.push(device); continue; }
                if (seen.has(device.kind)) continue;
                seen.add(device.kind);
                out.push(blank(device));
              }
              return out;
            };
            Object.defineProperty(MD.prototype, 'enumerateDevices', {
              value: function enumerateDevices() {
                return Promise.all([realEnumerate.call(this), granted('microphone'), granted('camera')])
                  .then(([list, microphone, camera]) => filter(list, { audio: accepted.audio || microphone, video: accepted.video || camera }));
              },
              writable: true, configurable: true, enumerable: true,
            });
            if (typeof realGetUserMedia === 'function') {
              Object.defineProperty(MD.prototype, 'getUserMedia', {
                value: function getUserMedia() {
                  return realGetUserMedia.apply(this, arguments).then(stream => {
                    try {
                      if (stream.getAudioTracks().length > 0) accepted.audio = true;
                      if (stream.getVideoTracks().length > 0) accepted.video = true;
                    } catch (e) { }
                    return stream;
                  });
                },
                writable: true, configurable: true, enumerable: true,
              });
            }
          } catch (e) { }
        })();
        """;

    // O que se regista uma vez por sessão do DevTools, antes de qualquer script da página (ScriptletInjector).
    public static readonly string SessionScript = Compose(GpcScript, HintsScript, DevicesScript)!;

    // Páginas onde o sinal vale: os sites e as páginas internas (eteris://privacy/ verifica-o ao vivo).
    public static bool AppliesTo(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "eteris";

    public static string? ScriptFor(string url) => AppliesTo(url) ? GpcScript : null;

    // Junta partes de script (as vazias ficam de fora), pela ordem dada.
    public static string? Compose(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
        return present.Count == 0 ? null : string.Join("\n;\n", present);
    }
}
