# Resumo dos contadores do Éteris (ETERIS_TEST_CONTADORES, Infrastructure/PerfCounters.cs) numa janela de tempo.
#
# Uso: python -I contadores.py <contadores.tsv> [de_s] [ate_s]
#   (segundos desde a criação do processo; sem eles, o ficheiro inteiro). Média por segundo, máximo e total de cada
#   contador. Um contador que existe mas não mexeu numa linha vale 0 nesse segundo (wpf.lotes em repouso: a prova do
#   E1 é média 0).
import sys

path = sys.argv[1]
lo = float(sys.argv[2]) * 1000 if len(sys.argv) > 2 else float('-inf')
hi = float(sys.argv[3]) * 1000 if len(sys.argv) > 3 else float('inf')
names, per = set(), []
with open(path, encoding='utf-8') as fh:
    for line in fh:
        f = line.rstrip('\n').split('\t')
        if len(f) >= 3 and f[1] == 'info':
            print('info:', '\t'.join(f[2:]))
            if f[2].startswith('wpf.lotes=MediaContext'):
                names.add('wpf.lotes')
        if len(f) < 2 or f[1] != 'seg':
            continue
        kv = dict(x.split('=', 1) for x in f[2:] if '=' in x)
        names.update(kv)
        if lo < float(f[0]) <= hi:
            per.append({k: float(v) for k, v in kv.items()})
print(f'{len(per)} segundos')
for n in sorted(names):
    xs = [s.get(n, 0.0) for s in per]
    if xs:
        print(f'{n:<22} média/s={sum(xs) / len(xs):9.2f}  máx={max(xs):9.0f}  total={sum(xs):11.0f}')
