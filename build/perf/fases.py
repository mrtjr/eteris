# Fases do arranque a partir dos rastros do Éteris (ETERIS_TRACE_STARTUP; medir.ps1 -Rastro ou arranque-ab.ps1).
#
# Uso: python -I fases.py <rastro.tsv> [<rastro.tsv> ...]   (aceita padrões: python -I fases.py "rastos/L1-r*.tsv")
#
# Medianas, entre os rastros dados, do tempo de cada marca desde a criação do processo, da fase até à marca anterior,
# do JIT dessa fase (ms) e da memória privada. As linhas «filho» (Infrastructure/StartupTrace.Children.cs) dão a hora
# de CRIAÇÃO de cada processo filho no mesmo relógio; renderer.processo = o 1.º renderer (D8: está no caminho crítico,
# antes de cef.navegar e de aba.1.commit?).
import glob
import statistics as st
import sys

files = [f for a in sys.argv[1:] for f in (glob.glob(a) or [a])]
if not files:
    sys.exit('uso: fases.py <rastro.tsv> ...')
t, jit, priv, order = {}, {}, {}, []
children = {}
for path in files:
    seen, kids = set(), {}
    with open(path, encoding='utf-8') as fh:
        for line in fh:
            f = line.rstrip('\n').split('\t')
            if len(f) < 3:
                continue
            if f[1] == 'filho':
                kids.setdefault(f[2], []).append(float(f[0]))
                continue
            if f[1] != 'mark' or f[2] in seen or f[2].startswith('bd.'):
                continue
            seen.add(f[2])
            kv = dict(x.split('=', 1) for x in f[3:] if '=' in x)
            if f[2] not in t:
                t[f[2]], jit[f[2]], priv[f[2]] = [], [], []
                order.append(f[2])
            t[f[2]].append(float(f[0]))
            jit[f[2]].append(float(kv.get('jitms', 'nan')))
            priv[f[2]].append(float(kv.get('priv', 'nan')))
    for kind, ts in kids.items():
        ts.sort()
        for i, x in enumerate(ts, 1):
            children.setdefault(f'{kind}#{i}', []).append(x)
    if 'renderer' in kids:
        children.setdefault('renderer.processo', []).append(min(kids['renderer']))


def med(v):
    return st.median(v) if v else float('nan')


print(f'{len(files)} rastros')
print(f"{'marca':<22}{'t(ms)':>8}{'fase(ms)':>10}{'jit(ms)':>9}{'priv(MB)':>10}  n")
prev, pj = 0.0, 0.0
for k in sorted(order, key=lambda k: med(t[k])):
    m, j = med(t[k]), med(jit[k])
    print(f'{k:<22}{m:8.0f}{m - prev:10.0f}{(j - pj):9.0f}{med(priv[k]):10.1f}  {len(t[k])}')
    prev, pj = m, j
if children:
    print('-- processos filhos (hora de criação desde a criação do Éteris) --')
    for k in sorted(children, key=lambda k: med(children[k])):
        print(f'{k:<30}{med(children[k]):8.0f}  n={len(children[k])}')
