# Braços da bateria (ronda.ps1 -Config). Copiar para fora do repositório e pôr os caminhos das publicações a medir
# (a pasta de saída do build/publish.ps1, como cópia portátil; numa instalação os interruptores ETERIS_TEST_* não
# funcionam). Linhas de base do plano: L0 = 0.2.13 publicada; L1 = 0.2.14 com o sandbox; L1c = L1 sem o sandbox.
@{
  Bracos = @(
    @{ Nome = 'L0'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.13\Eteris.Browser.exe' }
    @{ Nome = 'L1'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.14\Eteris.Browser.exe' }
    @{ Nome = 'L1-puro'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.14\Eteris.Browser.exe'; Ambiente = @('ETERIS_TEST_PURO=1') }
    @{ Nome = 'L1c'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.14\Eteris.Browser.exe'; Extra = @('--eteris-no-sandbox') }
    @{ Nome = 'chrome'; Navegador = 'chrome'; PerfilNovo = $true; ApagarPerfil = $true }
    @{ Nome = 'brave'; Navegador = 'brave'; PerfilNovo = $true; ApagarPerfil = $true }
    @{ Nome = 'firefox'; Navegador = 'firefox'; PerfilNovo = $true; ApagarPerfil = $true }
  )
}
