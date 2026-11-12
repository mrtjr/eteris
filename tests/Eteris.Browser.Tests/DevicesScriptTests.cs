using System.Diagnostics;
using System.IO;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// enumerateDevices antes da permissão (docs/paridade.md #6): o Éteris dava os nomes e os deviceId de todos os
// dispositivos a qualquer site. O script da sessão corre aqui no Node, sobre um MediaDevices de imitação com
// dispositivos como os medidos, e o resultado é comparado com o que o Chrome dá sem permissão.
public class DevicesScriptTests
{
    private const string Setup = """
        class MediaDeviceInfo {
          constructor(kind, label, deviceId, groupId) { this._ = { kind, label, deviceId, groupId }; }
          get kind() { return this._.kind; } get label() { return this._.label; }
          get deviceId() { return this._.deviceId; } get groupId() { return this._.groupId; }
          toJSON() { return { ...this._ }; }
        }
        class InputDeviceInfo extends MediaDeviceInfo { getCapabilities() { return { real: true }; } }
        const real = [
          new InputDeviceInfo('audioinput', 'Predefinição - Microfone (GAMING HEADSET)', 'default', 'g1'),
          new InputDeviceInfo('audioinput', 'Microfone (GAMING HEADSET)', 'bbdf2bca', 'g1'),
          new InputDeviceInfo('videoinput', 'OBS Virtual Camera', '6ff78693', 'g2'),
          new MediaDeviceInfo('audiooutput', 'Predefinição - LG ULTRAWIDE', 'default', 'g3'),
          new MediaDeviceInfo('audiooutput', 'Alto-falantes (GAMING HEADSET)', '12bd191c', 'g1'),
        ];
        class MediaDevices {
          enumerateDevices() { return Promise.resolve(real.slice()); }
          getUserMedia(c) {
            const tracks = k => (c && c[k]) ? [{}] : [];
            return Promise.resolve({ getAudioTracks: () => tracks('audio'), getVideoTracks: () => tracks('video') });
          }
        }
        globalThis.self = globalThis;
        globalThis.MediaDevices = MediaDevices;
        globalThis.MediaDeviceInfo = MediaDeviceInfo;
        globalThis.InputDeviceInfo = InputDeviceInfo;
        const states = PERMISSIONS;
        Object.defineProperty(globalThis, 'navigator', { configurable: true, value: {
          mediaDevices: new MediaDevices(),
          permissions: { query: ({ name }) => states[name] === 'throw' ? Promise.reject(new TypeError(name)) : Promise.resolve({ state: states[name] || 'prompt' }) },
        } });
        const show = l => l.map(d => [d.kind, d.label, d.deviceId, d.groupId].join('|')).join(';');
        const print = s => process.stdout.write(String(s));
        """;

    private const string Hidden = "audioinput|||;videoinput|||;audiooutput|||";

    [Fact]
    public void WithoutPermission_OneBlankEntryPerKind_LikeChrome()
    {
        var output = Run("{}", """
            const list = await navigator.mediaDevices.enumerateDevices();
            const proto = list.every(d => d instanceof MediaDeviceInfo) && list[0] instanceof InputDeviceInfo && !(list[2] instanceof InputDeviceInfo);
            const json = JSON.stringify(list);
            const keys = list.map(d => Object.keys(d).length).join(',');
            const caps = JSON.stringify(list[0].getCapabilities());
            print([show(list), proto, json, keys, caps].join('\n'));
            """);
        var lines = output.Split('\n');
        Assert.Equal(Hidden, lines[0]);
        Assert.Equal("true", lines[1]);
        Assert.Equal("""[{"deviceId":"","kind":"audioinput","label":"","groupId":""},{"deviceId":"","kind":"videoinput","label":"","groupId":""},{"deviceId":"","kind":"audiooutput","label":"","groupId":""}]""", lines[2]);
        Assert.Equal("0,0,0", lines[3]);
        Assert.Equal("{}", lines[4]);
    }

    [Fact]
    public void MicrophonePermission_ShowsTheAudioDevicesOnly()
    {
        var output = Run("{ microphone: 'granted' }", "print(show(await navigator.mediaDevices.enumerateDevices()));");
        Assert.Equal("audioinput|Predefinição - Microfone (GAMING HEADSET)|default|g1;audioinput|Microfone (GAMING HEADSET)|bbdf2bca|g1;"
                     + "videoinput|||;audiooutput|Predefinição - LG ULTRAWIDE|default|g3;audiooutput|Alto-falantes (GAMING HEADSET)|12bd191c|g1", output);
    }

    [Fact]
    public void CameraPermission_ShowsTheCameraOnly()
    {
        var output = Run("{ camera: 'granted', microphone: 'denied' }", "print(show(await navigator.mediaDevices.enumerateDevices()));");
        Assert.Equal("audioinput|||;videoinput|OBS Virtual Camera|6ff78693|g2;audiooutput|||", output);
    }

    // O motor do Éteris não guarda a permissão de média (permissions.query fica «prompt»): um getUserMedia aceite
    // neste documento é o sinal de que o utilizador deu a câmara ou o microfone, como no Chrome.
    [Fact]
    public void AcceptedGetUserMedia_ShowsThatKind()
    {
        var output = Run("{}", """
            const before = show(await navigator.mediaDevices.enumerateDevices());
            await navigator.mediaDevices.getUserMedia({ video: true });
            const after = show(await navigator.mediaDevices.enumerateDevices());
            print(before + '\n' + after);
            """);
        var lines = output.Split('\n');
        Assert.Equal(Hidden, lines[0]);
        Assert.Equal("audioinput|||;videoinput|OBS Virtual Camera|6ff78693|g2;audiooutput|||", lines[1]);
    }

    [Fact]
    public void PermissionQueryFailing_KeepsTheDevicesHidden()
    {
        var output = Run("{ camera: 'throw', microphone: 'throw' }", "print(show(await navigator.mediaDevices.enumerateDevices()));");
        Assert.Equal(Hidden, output);
    }

    // As funções trocadas parecem as do motor: mesmo nome e mesmo número de argumentos.
    [Fact]
    public void WrappedFunctions_KeepNameAndLength()
    {
        var output = Run("{}", """
            const p = MediaDevices.prototype;
            print([p.enumerateDevices.name, p.enumerateDevices.length, p.getUserMedia.name, p.getUserMedia.length].join(','));
            """);
        Assert.Equal("enumerateDevices,0,getUserMedia,0", output);
    }

    [Fact]
    public void SessionScript_IncludesTheDevicesScript() => Assert.Contains(PrivacySignals.DevicesScript, PrivacySignals.SessionScript);

    private static string Run(string permissions, string body)
        => NodeRunner.Run(Setup.Replace("PERMISSIONS", permissions) + "\n" + PrivacySignals.DevicesScript
                          + "\n(async () => {\n" + body + "\n})().catch(e => { console.error(e); process.exit(1); });");
}
