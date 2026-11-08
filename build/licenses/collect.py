"""Junta na pasta licenses/ os textos completos das licenças de tudo o que o Éteris distribui.

Corre-se sempre que muda uma dependência (CEF/CefSharp, pacotes NuGet, crates do motor de bloqueio):

    python build/licenses/collect.py

Fontes: a cache local do NuGet e do Cargo (os pacotes exatos que entram na compilação), o resources.pak
do CEF (créditos do Chromium) e build/licenses/static/ (textos que os pacotes não trazem, obtidos uma vez
das fontes oficiais: gnu.org e os repositórios do Velopack, SQLitePCLRaw e EF Core; a MPL-2.0 é a do adblock-rust).

Com o motor com codecs da plataforma (build/cef-codecs/out/runtime, docs/motor.md), a licença da CEF e os créditos
do Chromium saem dessa compilação (LICENSE.txt e CREDITS.html, com a LGPL do FFmpeg), e juntam-se o texto da
licença do FFmpeg e a oferta do código-fonte correspondente, com os commits e os patches do manifest.json.
"""
import glob
import html
import json
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "licenses")
STATIC = os.path.join(ROOT, "build", "licenses", "static")
NUGET = os.path.join(os.path.expanduser("~"), ".nuget", "packages")
CARGO = glob.glob(os.path.join(os.path.expanduser("~"), ".cargo", "registry", "src", "*"))
CSPROJ = os.path.join(ROOT, "src", "Eteris.Browser", "Eteris.Browser.csproj")
CARGO_LOCK = os.path.join(ROOT, "src", "Eteris.Shields", "Cargo.lock")

index = []  # (componente, licença, ficheiro)

# Textos canónicos para crates que declaram a licença sem incluir o ficheiro.
STANDARD_TEXTS = {"Apache-2.0": "Apache-2.0.txt", "MIT": "MIT-template.txt"}


def authors(manifest):
    match = re.search(r'^authors\s*=\s*\[([^\]]*)\]', manifest, re.M)
    names = re.findall(r'"([^"<]+?)\s*(?:<[^>]*>)?"', match.group(1)) if match else []
    return ", ".join(n.strip() for n in names) or "os autores do crate"


def repository(manifest):
    match = re.search(r'^repository\s*=\s*"([^"]+)"', manifest, re.M)
    return match.group(1) if match else "(não indicado)"


def copy(src, name, component, license):
    shutil.copyfile(src, os.path.join(OUT, name))
    index.append((component, license, name))


# Oferta do código-fonte do FFmpeg (LGPL-2.1, secção 6): o FFmpeg vai ligado de forma estática no libcef.dll da
# compilação própria. Preenchida com o manifest.json do runtime.
FFMPEG_SOURCE = """FFmpeg no Éteris: licença e código-fonte correspondente
=======================================================

Esta instalação do Éteris usa uma compilação própria do Chromium Embedded Framework, com os «codecs da plataforma»:
CEF {cef_version} (ramo {cef_branch}, commit {cef_commit}) e Chromium {chromium_version} (commit {chromium_commit}),
compilados com proprietary_codecs=true e ffmpeg_branding=Chromium. O H.264/HEVC é descodificado pela placa gráfica
através do Windows (D3D11VideoDecoder) e o AAC pelo Media Foundation do Windows; nenhum descodificador com patentes vai
incluído (nem o OpenH264).

O libcef.dll inclui, ligado de forma estática, o FFmpeg (commit {ffmpeg_commit} de
https://chromium.googlesource.com/chromium/third_party/ffmpeg, configuração {ffmpeg_config}), sob a GNU Lesser
General Public License, versão 2.1 ou posterior: o texto completo está em FFmpeg-LICENSE.txt e nos créditos do
Chromium (Chromium-credits.html). Essa configuração só tem desmultiplexadores, parsers e descodificadores livres
(FLAC, MP3, Vorbis, Opus, PCM); o patch do Éteris junta-lhe o desmultiplexador ADTS e o parser AAC, que não
descodificam nada.

Código-fonte correspondente
---------------------------
- FFmpeg: https://chromium.googlesource.com/chromium/third_party/ffmpeg/+/{ffmpeg_commit}
- Chromium: https://chromium.googlesource.com/chromium/src/+/{chromium_commit}
- CEF: https://github.com/chromiumembeddedframework/cef/commit/{cef_commit}
- Alterações do Éteris (vão com a instalação, em licenses\\cef-codecs\\patches, e estão no repositório do Éteris, em
  https://github.com/mrtjr/eteris/tree/main/build/cef-codecs):
{patches}
- Script de compilação, com os argumentos GN ({gn_defines}):
  build/cef-codecs/build.ps1 no mesmo repositório; como reconstruir: build/cef-codecs/README.md.

Com estes ficheiros pode compilar de novo o libcef.dll, com um FFmpeg alterado por si, e pô-lo no lugar do que vem na
instalação: o Éteris liga-se ao libcef.dll de forma dinâmica.

Oferta escrita: durante pelo menos três anos a contar da última distribuição desta versão do Éteris, qualquer pessoa
pode pedir uma cópia do código-fonte completo correspondente (FFmpeg, Chromium e CEF nos commits acima, com as
alterações do Éteris), num suporte habitual para troca de software e pelo custo da cópia, abrindo um pedido em
https://github.com/mrtjr/eteris/issues.
"""


def ffmpeg_license(credits):
    """O texto da licença do FFmpeg tal como vem nos créditos do Chromium (LICENSE.md e a LGPL-2.1 completa)."""
    text = open(credits, encoding="utf-8").read()
    start = text.find('<span class="title">ffmpeg</span>')
    if start < 0:
        sys.exit("Os créditos da compilação própria não têm o FFmpeg: recompile com build/cef-codecs/build.ps1")
    block = re.search(r"<pre>(.*?)</pre>", text[start:], re.S)
    license_text = html.unescape(block.group(1)) if block else ""
    if "GNU LESSER GENERAL PUBLIC LICENSE" not in license_text:
        sys.exit("Os créditos da compilação própria não têm a LGPL do FFmpeg")
    return license_text


def package_dir(package_id, version=None):
    base = os.path.join(NUGET, package_id.lower())
    if version:
        path = os.path.join(base, version)
        if not os.path.isdir(path):
            sys.exit(f"Pacote em falta na cache do NuGet: {package_id} {version} (faça dotnet restore)")
        return path
    versions = sorted(os.listdir(base), key=lambda v: [int(x) if x.isdigit() else x for x in re.split(r"[.-]", v)])
    return os.path.join(base, versions[-1])


def main():
    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    os.makedirs(os.path.join(OUT, "rust"))

    csproj = open(CSPROJ, encoding="utf-8").read()
    refs = dict(re.findall(r'<PackageReference Include="([^"]+)" Version="([^"]+)"', csproj))

    # O próprio Éteris e o que é GPL (listas de filtros, scriptlets do uBlock).
    copy(os.path.join(STATIC, "GPL-3.0.txt"), "GPL-3.0.txt", "Éteris; listas de filtros; scriptlets do uBlock Origin", "GPL-3.0")
    # Fundos da nova aba: imagens geradas por IA pelo autor do Éteris; a ficha de origem e termos vive ao lado delas.
    copy(os.path.join(ROOT, "src", "Eteris.Browser", "Browser", "Wallpapers", "README.md"),
         "Wallpapers-NOTICE.txt", "Fundos da nova aba (imagens geradas por IA)", "GPL-3.0-or-later; geradas por IA")

    # Motor do navegador.
    cefsharp = package_dir("CefSharp.Common.NETCore", refs["CefSharp.Wpf.NETCore"])
    copy(os.path.join(cefsharp, "LICENSE"), "CefSharp-LICENSE.txt", f"CefSharp {refs['CefSharp.Wpf.NETCore']}", "BSD-3-Clause")
    nuspec = open(glob.glob(os.path.join(cefsharp, "*.nuspec"))[0], encoding="utf-8").read()
    cef_version = re.search(r'id="chromiumembeddedframework\.runtime" version="\[?([^"\],]+)', nuspec, re.I).group(1)
    # Motor com codecs (docs/motor.md): com a compilação própria da CEF (a mesma condição do csproj: libcef.dll e
    # manifest.json), o runtime inteiro é o dela — a licença, os créditos e o FFmpeg também.
    own_runtime = os.path.join(ROOT, "build", "cef-codecs", "out", "runtime")
    own = all(os.path.exists(os.path.join(own_runtime, f)) for f in ("libcef.dll", "manifest.json"))
    if own:
        manifest = json.load(open(os.path.join(own_runtime, "manifest.json"), encoding="utf-8-sig"))
        if manifest.get("mode") != "platform-codecs":
            sys.exit(f"Motor com codecs no modo {manifest.get('mode')!r}: só o modo platform-codecs se distribui")
        if manifest["cef"]["version"] != cef_version:
            sys.exit(f"Motor com codecs da CEF {manifest['cef']['version']}, o CefSharp espera {cef_version}")
        own_licenses = os.path.join(own_runtime, "licenses")
        copy(os.path.join(own_licenses, "LICENSE.txt"), "CEF-LICENSE.txt",
             f"Chromium Embedded Framework {cef_version} (compilação própria, codecs do Windows)", "BSD-3-Clause")
        copy(os.path.join(own_licenses, "CREDITS.html"), "Chromium-credits.html",
             f"Chromium {manifest['chromium']['version']} e os seus componentes (CEF {cef_version}, compilação própria)",
             "BSD-3-Clause, LGPL-2.1-or-later (FFmpeg) e outras (ver o ficheiro)")
        with open(os.path.join(OUT, "FFmpeg-LICENSE.txt"), "w", encoding="utf-8") as f:
            f.write(ffmpeg_license(os.path.join(own_licenses, "CREDITS.html")))
        index.append((f"FFmpeg (no libcef.dll; sem descodificadores com patentes, commit {manifest['ffmpeg']['commit'][:12]})",
                      "LGPL-2.1-or-later", "FFmpeg-LICENSE.txt"))
        patches = "\n".join(f"  {p['file']} (aplica-se a {p['repo']}; SHA256 {p['sha256']})" for p in manifest["patches"])
        with open(os.path.join(OUT, "FFmpeg-SOURCE.txt"), "w", encoding="utf-8") as f:
            f.write(FFMPEG_SOURCE.format(
                cef_version=cef_version, cef_branch=manifest["cef"]["branch"], cef_commit=manifest["cef"]["commit"],
                chromium_version=manifest["chromium"]["version"], chromium_commit=manifest["chromium"]["commit"],
                ffmpeg_commit=manifest["ffmpeg"]["commit"], ffmpeg_config=manifest["ffmpeg"]["config"],
                gn_defines=manifest["gn_defines"], patches=patches))
        index.append(("FFmpeg e motor com codecs: código-fonte correspondente (oferta da LGPL)", "LGPL-2.1-or-later",
                      "FFmpeg-SOURCE.txt"))
    else:
        cef = package_dir("chromiumembeddedframework.runtime.win-x64", cef_version)
        copy(os.path.join(cef, "LICENSE.txt"), "CEF-LICENSE.txt", "Chromium Embedded Framework", "BSD-3-Clause")
        pak = os.path.join(cef, "runtimes", "win-x64", "native", "resources.pak")
        subprocess.run(["node", os.path.join(ROOT, "build", "licenses", "chromium-credits.js"), pak,
                        os.path.join(OUT, "Chromium-credits.html")], check=True)
        index.append((f"Chromium e os seus componentes (CEF {cef_version})", "BSD-3-Clause e outras (ver o ficheiro)",
                      "Chromium-credits.html"))

    # .NET (vai dentro da instalação, que é autónoma).
    for runtime, name in [("microsoft.netcore.app.runtime.win-x64", ".NET"), ("microsoft.windowsdesktop.app.runtime.win-x64", "WPF / Windows Desktop")]:
        d = package_dir(runtime)
        tag = "dotnet" if "netcore" in runtime else "windowsdesktop"
        files = {f.upper(): f for f in os.listdir(d)}
        license_file = files.get("LICENSE.TXT") or files.get("LICENSE")
        copy(os.path.join(d, license_file), f"{tag}-LICENSE.txt", name, "MIT")
        if "THIRD-PARTY-NOTICES.TXT" in files:
            copy(os.path.join(d, files["THIRD-PARTY-NOTICES.TXT"]), f"{tag}-THIRD-PARTY-NOTICES.txt",
                 name + " (componentes de terceiros)", "várias")

    # Outros pacotes NuGet.
    copy(os.path.join(package_dir("CommunityToolkit.Mvvm", refs["CommunityToolkit.Mvvm"]), "License.md"),
         "CommunityToolkit.Mvvm-LICENSE.txt", "CommunityToolkit.Mvvm", "MIT")
    copy(os.path.join(package_dir("QRCoder", refs["QRCoder"]), "LICENSE.txt"), "QRCoder-LICENSE.txt", "QRCoder", "MIT")
    # SDK do WebView2 (motor de vídeo): licença ao estilo BSD-3 + avisos de terceiros. O runtime em si é um
    # componente do Windows e não é distribuído com o Éteris.
    webview2 = package_dir("Microsoft.Web.WebView2", refs["Microsoft.Web.WebView2"])
    copy(os.path.join(webview2, "LICENSE.txt"), "Microsoft.Web.WebView2-LICENSE.txt", "Microsoft.Web.WebView2 (SDK do motor de vídeo)", "BSD-3-Clause")
    copy(os.path.join(webview2, "NOTICE.txt"), "Microsoft.Web.WebView2-NOTICE.txt", "Microsoft.Web.WebView2 (avisos de terceiros)", "várias")
    copy(os.path.join(STATIC, "velopack-LICENSE.txt"), "Velopack-LICENSE.txt", "Velopack", "MIT")
    copy(os.path.join(ROOT, "src", "Eteris.Browser", "Browser", "Vendor", "readability", "LICENSE.md"),
         "Readability-LICENSE.txt", "Readability.js (modo de leitura, Mozilla)", "Apache-2.0")
    copy(os.path.join(STATIC, "Apache-2.0.txt"), "Apache-2.0.txt", "Texto completo da Apache-2.0 (Readability, SQLitePCLRaw, crates)", "Apache-2.0")
    copy(os.path.join(STATIC, "microsoft-data-sqlite-LICENSE.txt"), "Microsoft.Data.Sqlite-LICENSE.txt", "Microsoft.Data.Sqlite", "MIT")
    copy(os.path.join(STATIC, "sqlitepclraw-LICENSE.txt"), "SQLitePCLRaw-LICENSE.txt", "SQLitePCLRaw", "Apache-2.0")
    with open(os.path.join(OUT, "SQLite-public-domain.txt"), "w", encoding="utf-8") as f:
        f.write("O SQLite (https://sqlite.org) está no domínio público: https://sqlite.org/copyright.html\n")
    index.append(("SQLite", "domínio público", "SQLite-public-domain.txt"))

    # Motor de bloqueio (Rust): cada crate com os seus próprios ficheiros de licença.
    lock = open(CARGO_LOCK, encoding="utf-8").read()
    crates = re.findall(r'\[\[package\]\]\nname = "([^"]+)"\nversion = "([^"]+)"\nsource = "registry', lock)
    for name, version in crates:
        src = next((os.path.join(r, f"{name}-{version}") for r in CARGO if os.path.isdir(os.path.join(r, f"{name}-{version}"))), None)
        if src is None:
            sys.exit(f"Crate em falta na cache do Cargo: {name} {version} (faça cargo fetch em src/Eteris.Shields)")
        manifest = open(os.path.join(src, "Cargo.toml"), encoding="utf-8").read()
        license = (re.search(r'^license\s*=\s*"([^"]+)"', manifest, re.M) or [None, "ver ficheiros"])[1]
        files = [f for f in os.listdir(src) if re.match(r"(?i)^(licen[cs]e|copying|unlicense|notice)", f) and os.path.isfile(os.path.join(src, f))]
        target = os.path.join(OUT, "rust", f"{name}-{version}")
        os.makedirs(target)
        if not files:
            # Alguns crates não trazem o ficheiro: junta-se o texto canónico da licença declarada.
            if license not in STANDARD_TEXTS:
                sys.exit(f"Crate sem ficheiro de licença e com licença sem texto canónico guardado: {name} {version} ({license})")
            text = open(os.path.join(STATIC, STANDARD_TEXTS[license]), encoding="utf-8").read()
            with open(os.path.join(target, "LICENSE"), "w", encoding="utf-8") as out:
                out.write(text.replace("{copyright}", authors(manifest)))
            with open(os.path.join(target, "NOTE.txt"), "w", encoding="utf-8") as note:
                note.write(f"O crate {name} {version} declara a licença {license} mas não inclui o ficheiro; "
                           f"o texto ao lado é o texto canónico dessa licença. Repositório: {repository(manifest)}" + os.linesep)
        for f in files:
            shutil.copyfile(os.path.join(src, f), os.path.join(target, f))
        index.append((f"{name} {version} (motor de bloqueio)", license, f"rust/{name}-{version}/"))

    # A Public Suffix List (https://publicsuffix.org) vai compilada no eteris_shields.dll pelo crate psl: o código do
    # crate é MIT/Apache-2.0 (acima), os dados da lista são MPL-2.0. É ela que decide o «site» (Shields/PublicSuffix.cs).
    psl = next((v for n, v in crates if n == "psl"), None)
    if psl is None:
        sys.exit("O crate psl saiu do Cargo.lock: atualize a entrada da Public Suffix List em collect.py")
    copy(os.path.join(STATIC, "MPL-2.0.txt"), "PublicSuffixList-MPL-2.0.txt",
         f"Public Suffix List (dados, no crate psl {psl} dentro do eteris_shields.dll)", "MPL-2.0")

    with open(os.path.join(OUT, "INDEX.md"), "w", encoding="utf-8") as f:
        f.write("# Licenças\n\nTextos completos das licenças de tudo o que o Éteris distribui. "
                "Gerado por `build/licenses/collect.py`; não editar à mão.\n\n"
                "| Componente | Licença | Ficheiro |\n|---|---|---|\n")
        for component, license, name in index:
            f.write(f"| {component} | {license} | `{name}` |\n")
    print(f"{len(index)} entradas em licenses/INDEX.md")


if __name__ == "__main__":
    main()
