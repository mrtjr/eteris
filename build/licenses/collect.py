"""Junta na pasta licenses/ os textos completos das licenças de tudo o que o Éteris distribui.

Corre-se sempre que muda uma dependência (CEF/CefSharp, pacotes NuGet, crates do motor de bloqueio):

    python build/licenses/collect.py

Fontes: a cache local do NuGet e do Cargo (os pacotes exatos que entram na compilação), o resources.pak
do CEF (créditos do Chromium) e build/licenses/static/ (textos que os pacotes não trazem, obtidos uma vez
das fontes oficiais: gnu.org e os repositórios do Velopack, SQLitePCLRaw e EF Core).
"""
import glob
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
    cef = package_dir("chromiumembeddedframework.runtime.win-x64", cef_version)
    copy(os.path.join(cef, "LICENSE.txt"), "CEF-LICENSE.txt", "Chromium Embedded Framework", "BSD-3-Clause")
    pak = os.path.join(cef, "runtimes", "win-x64", "native", "resources.pak")
    # Motor com codecs (docs/motor.md): com a compilação própria da CEF, os créditos saem do resources.pak dela, que é o
    # que vai na instalação (o csproj troca o runtime inteiro).
    own_pak = os.path.join(ROOT, "build", "cef-codecs", "out", "runtime", "resources.pak")
    own = os.path.exists(own_pak)
    subprocess.run(["node", os.path.join(ROOT, "build", "licenses", "chromium-credits.js"), own_pak if own else pak,
                    os.path.join(OUT, "Chromium-credits.html")], check=True)
    label = (f"Chromium e os seus componentes (CEF {cef_version}, compilação própria, codecs do Windows)" if own
             else f"Chromium e os seus componentes (CEF {cef_version})")
    index.append((label, "BSD-3-Clause e outras (ver o ficheiro)", "Chromium-credits.html"))

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

    with open(os.path.join(OUT, "INDEX.md"), "w", encoding="utf-8") as f:
        f.write("# Licenças\n\nTextos completos das licenças de tudo o que o Éteris distribui. "
                "Gerado por `build/licenses/collect.py`; não editar à mão.\n\n"
                "| Componente | Licença | Ficheiro |\n|---|---|---|\n")
        for component, license, name in index:
            f.write(f"| {component} | {license} | `{name}` |\n")
    print(f"{len(index)} entradas em licenses/INDEX.md")


if __name__ == "__main__":
    main()
