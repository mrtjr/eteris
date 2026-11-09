# D0 (A/A): o mesmo binário nos dois braços, perfis diferentes. ronda.ps1 -Config <cópia> -Rondas 10; depois
# python -I estatistica.py <csv> A1 A2 dá o desvio-padrão das diferenças (σd) e quantos pares pede cada efeito.
@{
  Bracos = @(
    @{ Nome = 'A1'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.13\Eteris.Browser.exe' }
    @{ Nome = 'A2'; Navegador = 'eteris'; Exe = 'C:\medicoes\pub-0.2.13\Eteris.Browser.exe' }
  )
}
