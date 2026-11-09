# Braços da fase 2 do plano de desempenho (E14, E15): cada funcionalidade do motor sozinha contra L1, com o mesmo
# binário (ETERIS_TEST_AJUSTES). Copiar para fora do repositório e pôr o caminho da publicação L1. Ver build/perf/README.md,
# «Fase 2». Uma de cada vez pela ordem do plano; o Graphite (e14.6) por último e só com a decisão D-3.
@{
  Bracos = @(
    @{ Nome = 'L1'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.14\Eteris.Browser.exe' }
    @{ Nome = 'L1-e14.1'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.14\Eteris.Browser.exe'; Ambiente = @('ETERIS_TEST_AJUSTES=e14.1') }
    @{ Nome = 'L1-e14.2'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.14\Eteris.Browser.exe'; Ambiente = @('ETERIS_TEST_AJUSTES=e14.2') }
    @{ Nome = 'L1-e14.3'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.14\Eteris.Browser.exe'; Ambiente = @('ETERIS_TEST_AJUSTES=e14.3') }
    # A cache persistente da GPU só liga com a cache dos shaders no disco (e3, decisão D-4): o braço de controlo é o L1-e3.
    @{ Nome = 'L1-e3'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.14\Eteris.Browser.exe'; Ambiente = @('ETERIS_TEST_AJUSTES=e3') }
    @{ Nome = 'L1-e3-e14.4'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.14\Eteris.Browser.exe'; Ambiente = @('ETERIS_TEST_AJUSTES=e3,e14.4') }
    @{ Nome = 'L1-e14.5'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.14\Eteris.Browser.exe'; Ambiente = @('ETERIS_TEST_AJUSTES=e14.5') }
    # Memória (E15): com o D9 (rastro.ps1 -Memoria) e o B-MEM.
    @{ Nome = 'L1-e15.1'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.14\Eteris.Browser.exe'; Ambiente = @('ETERIS_TEST_AJUSTES=e15.1') }
  )
}
