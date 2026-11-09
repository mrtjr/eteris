# Comparação de dois braços por pares de rondas (plano de desempenho 0.2.14, secção 3.3, «Estatística»).
#
# Uso: python -I estatistica.py <medicoes.csv> <braço A> <braço B> [--cenario fps] [--metrica cpu.total] [--delta 0.5,1,2]
#
# Para cada cenário e métrica presentes nos dois braços: o valor de cada ronda é a mediana das linhas dessa ronda
# (vários arranques numa ronda, por exemplo); as rondas em que algum dos dois teve atividade de fundo acima de 2% de um
# núcleo (fundo_alto=1, medir.ps1) ficam de fora. Mostra:
#   - mediana de A e de B e a razão A/B (mediana das razões por ronda) com o intervalo de 95% por bootstrap;
#   - a mediana das diferenças A-B com o intervalo de 95% por bootstrap. Decide-se pelo intervalo, nunca por uma
#     mediana sozinha: um intervalo que contém 0 (ou a razão 1,0x) não mostra diferença;
#   - sd, o desvio-padrão das diferenças, e quantos pares são precisos para detetar um efeito delta com 80% de
#     potência: n = 8*(sd/delta)^2. No A/A (D0) os dois braços são o mesmo binário: o sd é o ruído da bancada.
import argparse
import csv
import random
import re
import statistics as st

ap = argparse.ArgumentParser()
ap.add_argument('csv')
ap.add_argument('a')
ap.add_argument('b')
ap.add_argument('--cenario')
ap.add_argument('--metrica')
ap.add_argument('--delta', default='0.5,1,2')
ap.add_argument('--reamostras', type=int, default=10000)
args = ap.parse_args()
deltas = [float(x) for x in args.delta.split(',') if x]

vals = {}      # (braço, cenário, métrica, ronda) -> [valores]
noisy = set()  # (braço, cenário, ronda) com atividade de fundo alta
with open(args.csv, encoding='utf-8-sig') as fh:
    for r in csv.DictReader(fh):
        if r['Braco'] not in (args.a, args.b):
            continue
        if args.cenario and r['Cenario'] != args.cenario:
            continue
        try:
            v = float(r['Valor'])
        except ValueError:
            continue
        rnd = int(r['Ronda'])
        if r['Metrica'] == 'fundo_alto' and v >= 1:
            noisy.add((r['Braco'], r['Cenario'], rnd))
        vals.setdefault((r['Braco'], r['Cenario'], r['Metrica'], rnd), []).append(v)

rng = random.Random(20261109)


def boot_median(xs):
    if len(xs) < 3:
        return float('nan'), float('nan')
    meds = sorted(st.median(rng.choices(xs, k=len(xs))) for _ in range(args.reamostras))
    return meds[int(0.025 * len(meds))], meds[int(0.975 * len(meds)) - 1]


def rounds_of(arm, cen, met):
    return {k[3] for k in vals if k[0] == arm and k[1] == cen and k[2] == met}


print(f'{"cenario":<10} {"metrica":<36} {"n":>3} {"A":>9} {"B":>9} {"A/B":>6} {"IC95 A/B":>16} '
      f'{"A-B":>8} {"IC95 A-B":>18} {"sd":>7}  pares para delta={deltas}')
for cen, met in sorted({(k[1], k[2]) for k in vals}):
    if met == 'fundo_alto' or (args.metrica and not re.search(args.metrica, met)):
        continue
    rounds = sorted(rounds_of(args.a, cen, met) & rounds_of(args.b, cen, met))
    rounds = [r for r in rounds if (args.a, cen, r) not in noisy and (args.b, cen, r) not in noisy]
    if not rounds:
        continue
    A = [st.median(vals[(args.a, cen, met, r)]) for r in rounds]
    B = [st.median(vals[(args.b, cen, met, r)]) for r in rounds]
    d = [x - y for x, y in zip(A, B)]
    ratios = [x / y for x, y in zip(A, B) if y]
    sd = st.stdev(d) if len(d) > 1 else float('nan')
    lo, hi = boot_median(d)
    rlo, rhi = boot_median(ratios) if ratios else (float('nan'), float('nan'))
    ratio = st.median(ratios) if ratios else float('nan')
    need = ' '.join(f'{8 * (sd / x) ** 2:.0f}' if sd == sd and x else '-' for x in deltas)
    print(f'{cen:<10} {met:<36} {len(rounds):>3} {st.median(A):>9.2f} {st.median(B):>9.2f} {ratio:>6.2f} '
          f'[{rlo:>5.2f}, {rhi:>5.2f}]  {st.median(d):>8.2f} [{lo:>6.2f}, {hi:>6.2f}] {sd:>7.2f}  {need}')
