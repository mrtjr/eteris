// Processo filho mínimo do motor do Éteris (páginas, GPU, utilitários): só o CEF, sem .NET.
// Passo B de docs/sandbox.md: provar que o Éteris funciona sem o CefSharp.BrowserSubprocess, que é .NET e por isso
// não pode correr dentro do sandbox. Ainda sem sandbox (o terceiro argumento de CefExecuteProcess fica nullptr).
#include <windows.h>
#include "include/cef_app.h"

namespace {

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

}  // namespace

int APIENTRY wWinMain(HINSTANCE instance, HINSTANCE, LPWSTR, int) {
  CefMainArgs args(instance);
  CefRefPtr<EterisSubprocessApp> app(new EterisSubprocessApp());
  return CefExecuteProcess(args, app.get(), nullptr);
}
