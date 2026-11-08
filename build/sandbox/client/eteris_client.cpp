// DLL cliente do bootstrap da CEF (passo D de docs/sandbox.md). O bootstrap.exe da CEF, renomeado para o nome do
// Éteris, é o ponto de entrada de TODOS os processos: carrega a DLL com o mesmo nome do exe e chama RunWinMain com a
// informação do sandbox, que vive na pilha do bootstrap durante toda a execução.
//   - Processos filhos (--type=...): CefExecuteProcess com o esquema eteris:// registado e a informação do sandbox.
//   - Processo principal: arranca o .NET self-contained pela hostfxr.dll da pasta do exe (como o apphost faz) e deixa
//     o ponteiro do sandbox disponível ao .NET por EterisSandboxInfo() (CefSettings.WindowsSandboxInfo do CefSharp).
// Esta DLL é carregada em todos os filhos com sandbox: DllMain vazio, sem inicializadores estáticos com efeitos,
// importações diretas só do KERNEL32 (libcef, user32, shell32... por /DELAYLOAD) e CRT estática (/MT).
#include <windows.h>
#include <shellapi.h>

#include <string>

#include "include/cef_app.h"
#include "include/cef_sandbox_win.h"

#ifndef ETERIS_APP_DLL
#define ETERIS_APP_DLL L"Eteris.App.dll"
#endif

namespace {

void* g_sandbox_info = nullptr;

class EterisSubprocessApp : public CefApp {
 public:
  // O esquema eteris:// (páginas internas) tem de ser registado em todos os processos, com as mesmas opções do
  // processo principal (CefConfigurator: IsStandard, IsSecure, IsDisplayIsolated; CORS ligado por omissão).
  void OnRegisterCustomSchemes(CefRawPtr<CefSchemeRegistrar> registrar) override {
    registrar->AddCustomScheme("eteris", CEF_SCHEME_OPTION_STANDARD | CEF_SCHEME_OPTION_SECURE |
                                              CEF_SCHEME_OPTION_CORS_ENABLED | CEF_SCHEME_OPTION_DISPLAY_ISOLATED);
  }

  IMPLEMENT_REFCOUNTING(EterisSubprocessApp);
};

std::wstring ModulePath(HMODULE module) {
  std::wstring path(MAX_PATH, L'\0');
  for (;;) {
    const DWORD n = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (n == 0) return {};
    if (n < path.size()) {
      path.resize(n);
      return path;
    }
    path.resize(path.size() * 2);
  }
}

std::wstring DirectoryOf(const std::wstring& path) {
  const size_t slash = path.find_last_of(L"\\/");
  return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash);
}

// Erros do host do .NET (a hostfxr escreve-os aqui em vez de na consola): ficam num registo só quando há falha.
std::wstring g_host_errors;
void __cdecl HostErrorWriter(const wchar_t* message) {
  if (g_host_errors.size() < 64 * 1024) {
    g_host_errors += message;
    g_host_errors += L"\r\n";
  }
}

// Devolve o caminho do registo (vazio se não o conseguiu escrever).
std::wstring WriteBootstrapLog(const std::wstring& text) {
  OutputDebugStringW(text.c_str());
  wchar_t temp[MAX_PATH];
  const DWORD n = GetTempPathW(MAX_PATH, temp);
  if (n == 0 || n >= MAX_PATH) return {};
  const std::wstring path = std::wstring(temp) + L"Eteris-bootstrap.log";
  HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, OPEN_ALWAYS,
                            FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE) return {};
  const int bytes = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0,
                                        nullptr, nullptr);
  if (bytes > 0) {
    std::string utf8(static_cast<size_t>(bytes), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), utf8.data(), bytes, nullptr,
                        nullptr);
    DWORD written = 0;
    WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
  }
  CloseHandle(file);
  return path;
}

using hostfxr_error_writer_fn = void(__cdecl*)(const wchar_t* message);
using hostfxr_set_error_writer_fn = hostfxr_error_writer_fn(__cdecl*)(hostfxr_error_writer_fn writer);
using hostfxr_main_startupinfo_fn = int(__cdecl*)(int argc, const wchar_t** argv, const wchar_t* host_path,
                                                  const wchar_t* dotnet_root, const wchar_t* app_path);

// Arranque pelo Velopack (--veloapp-install, --veloapp-updated, ...): corre sem janela e com prazo, por isso uma falha
// do .NET aí não mostra aviso (fica só no registo).
bool IsVelopackHook(int argc, LPWSTR* argv) {
  for (int i = 1; i < argc; ++i)
    if (_wcsnicmp(argv[i], L"--veloapp-", 10) == 0) return true;
  return false;
}

// Quando o .NET nem chega a arrancar (hostfxr em falta, publicação incompleta, erro do host), o Éteris não abriria
// janela nenhuma: avisa com o caminho do registo, em vez de sair em silêncio.
void ReportStartupFailure(int argc, LPWSTR* argv, const std::wstring& detail, const std::wstring& log) {
  if (IsVelopackHook(argc, argv)) return;
  std::wstring text = L"O \u00C9teris n\u00E3o conseguiu arrancar.\r\n\r\n" + detail;
  if (!log.empty()) text += L"\r\n\r\nDetalhes: " + log;
  MessageBoxW(nullptr, text.c_str(), L"\u00C9teris", MB_OK | MB_ICONERROR);  // user32 por /DELAYLOAD
}

// Só no processo principal (nunca nos filhos com sandbox): .NET self-contained pela hostfxr da pasta do exe, com a
// mesma entrada que o apphost usa (hostfxr_main_startupinfo). Devolve o código de saída do Main.
int RunDotNet(int argc, LPWSTR* argv) {
  const std::wstring exe = ModulePath(nullptr);
  const std::wstring dir = DirectoryOf(exe);
  const std::wstring app = dir + L"\\" ETERIS_APP_DLL;
  HMODULE fxr = LoadLibraryExW((dir + L"\\hostfxr.dll").c_str(), nullptr,
                               LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
  auto run = fxr ? reinterpret_cast<hostfxr_main_startupinfo_fn>(GetProcAddress(fxr, "hostfxr_main_startupinfo"))
                 : nullptr;
  if (!run) {
    const std::wstring log = WriteBootstrapLog(L"Eteris: hostfxr.dll em falta ou sem hostfxr_main_startupinfo: " + dir + L"\r\n");
    ReportStartupFailure(argc, argv, L"Falta o .NET na pasta do programa (hostfxr.dll): " + dir, log);
    return static_cast<int>(0x80008083);  // StatusCode::CoreHostLibMissingFailure
  }
  if (auto set_writer = reinterpret_cast<hostfxr_set_error_writer_fn>(GetProcAddress(fxr, "hostfxr_set_error_writer")))
    set_writer(&HostErrorWriter);

  const LPWSTR argv0 = argv[0];
  argv[0] = const_cast<LPWSTR>(exe.c_str());
  const int code = run(argc, const_cast<const wchar_t**>(argv), exe.c_str(), dir.c_str(), app.c_str());
  argv[0] = argv0;
  // Só os erros do host (o .NET não arrancou ou não encontrou a aplicação); o código de saída normal do Éteris não.
  if (code != 0 && !g_host_errors.empty()) {
    const std::wstring log =
        WriteBootstrapLog(L"Eteris: o .NET terminou com o c\u00F3digo " + std::to_wstring(code) + L":\r\n" + g_host_errors);
    wchar_t hex[16];
    swprintf_s(hex, L"0x%08X", static_cast<unsigned>(code));
    ReportStartupFailure(argc, argv, std::wstring(L"Erro do .NET ") + hex + L".", log);
  }
  return code;
}

// Processo filho do Chromium: como o base::CommandLine do Chromium no Windows lê a linha de comando — argumentos do
// CommandLineToArgvW; interruptor = começa por «--», «-» ou «/», nome sem distinção de maiúsculas; um «--» sozinho
// acaba os interruptores. Só um ARGUMENTO --type=<valor> conta, nunca a sequência a meio de outro (um endereço com
// «--type=» na query, por exemplo).
bool IsChildProcess(int argc, LPWSTR* argv) {
  for (int i = 1; i < argc; ++i) {
    const wchar_t* a = argv[i];
    if (wcscmp(a, L"--") == 0) return false;
    if (a[0] == L'-' && a[1] == L'-')
      a += 2;
    else if (a[0] == L'-' || a[0] == L'/')
      a += 1;
    else
      continue;
    if (_wcsnicmp(a, L"type=", 5) == 0 && a[5] != L'\0') return true;
  }
  return false;
}

}  // namespace

// Ponteiro do sandbox para o .NET (Infrastructure/SandboxInfo.cs): nulo até o RunWinMain do processo principal o
// guardar. Válido enquanto RunWinMain corre, ou seja, durante toda a vida da aplicação .NET.
extern "C" __declspec(dllexport) void* EterisSandboxInfo() {
  return g_sandbox_info;
}

extern "C" __declspec(dllexport) int RunWinMain(HINSTANCE instance, LPWSTR, int, void* sandbox_info,
                                                cef_version_info_t*) {
  int argc = 0;
  LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);  // shell32 por /DELAYLOAD (o Chromium usa o mesmo)
  if (!argv || argc < 1) return static_cast<int>(0x80008081);  // StatusCode::InvalidArgFailure
  // Só os filhos têm o interruptor --type=: no processo principal o libcef.dll não é carregado aqui (fá-lo o
  // CefSharp). A CEF decide na mesma: sem o interruptor, CefExecuteProcess devolve -1.
  if (IsChildProcess(argc, argv)) {
    LocalFree(argv);
    CefMainArgs args(instance);
    CefRefPtr<EterisSubprocessApp> app(new EterisSubprocessApp());
    const int code = CefExecuteProcess(args, app.get(), sandbox_info);
    if (code >= 0) return code;
    argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc < 1) return static_cast<int>(0x80008081);
  }
  g_sandbox_info = sandbox_info;
  const int code = RunDotNet(argc, argv);
  LocalFree(argv);
  return code;
}
