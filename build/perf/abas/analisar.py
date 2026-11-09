# Análise das capturas da troca de abas (B-ABAS), o mesmo critério para o Éteris, o Chrome e o Brave.
# Por fotograma depois da ação, compara a área da página com o estado ANTES (mediana dos 5 fotogramas antes da ação) e
# DEPOIS (mediana dos 10 últimos). Fotograma «errado» = longe dos dois (fração de píxeis com |dL|>30 acima de 3% em
# ambos), classificado em mica, branco, preto, interno (#0B0D13, fundo das páginas internas do Éteris) ou outro
# (conteúdo parcial ou antigo deslocado). Também:
#  - descarta as capturas em que a janela medida não esteve SEMPRE em primeiro plano (.fg contra .hwnd);
#  - tempos desde a tecla (fim do envio menos 30 ms), além de desde action-start;
#  - 1.ª mudança (1.º fotograma diferente do ANTES) e cor mediana dos fotogramas errados;
#  - folha de contacto da 1.ª captura válida e das capturas com errados.
# Uso: python -I analisar.py <pasta> <etiqueta> [topo_px_reduzidos] [ignorar_primeiras_n]
#   topo: altura (em píxeis da captura reduzida) das barras do navegador, fora da área da página (Éteris ~23, Brave ~21).
import sys, glob, os, re, json, numpy as np
from PIL import Image, ImageDraw
d, tag = sys.argv[1], sys.argv[2]; top = int(sys.argv[3]) if len(sys.argv) > 3 else 21; skip = int(sys.argv[4]) if len(sys.argv) > 4 else 0
FR = 0.03
def num(p): return int(re.search(r'-(\d+)\.txt$', p).group(1))
tot = {'caps': 0, 'discarded_fg': 0, 'skipped': 0, 'bad_caps': 0, 'bad_frames': 0, 'bad_ms': 0, 'kinds': {}, 'chrome_bad_frames': 0}
lats, lats_k, firsts, ovh = [], [], [], []
sheet_done = False
def sheet(full, idx, ms, t_key, out, w, h):
    sh = Image.new('RGB', (w * len(idx), h + 12), 'white'); dr = ImageDraw.Draw(sh)
    for c, k in enumerate(idx):
        sh.paste(Image.fromarray(full[k, :, :, 2::-1].copy()), (c * w, 12)); dr.text((c * w + 2, 0), f'f{k} {ms[k]-t_key:+.0f}ms', fill='black')
    sh.save(out)
for f in sorted(glob.glob(os.path.join(d, tag + '-*.txt')), key=num):
    pre = f[:-4]; kk = num(f)
    if not os.path.exists(pre + '.raw'): continue
    if kk <= skip: tot['skipped'] += 1; continue
    L = open(f).read().split(); w, h, n = int(L[0]), int(L[1]), int(L[2]); ticks = np.array([int(x) for x in L[3:3 + n]], dtype=np.int64)
    hw = int(open(pre + '.hwnd').read().split()[0]); fg = [int(x) for x in open(pre + '.fg').read().split()]
    if any(x != hw for x in fg):
        tot['discarded_fg'] += 1; print(os.path.basename(pre), 'DESCARTADA: janela nao esteve sempre em primeiro plano'); continue
    full = np.fromfile(pre + '.raw', dtype=np.uint8)[: n * w * h * 4].reshape(n, h, w, 4)
    rgb = full[..., :3].astype(np.int16)
    lum = (0.0722 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.2126 * rgb[..., 2])
    ms = (ticks - ticks[0]) / 1e4
    act = {}
    for l in open(pre + '.action', encoding='utf-8-sig'):
        k, v = l.split(); act[k] = (int(v) - ticks[0]) / 1e4
    t_key = act['action-end'] - 30; ovh.append(t_key - act['action-start'])
    ka = int(np.searchsorted(ms, act['action-start']))
    page = lum[:, top:, 4:-4]; chrome = lum[:, 2:top, 4:-4]
    def st(a, k0, k1): return np.median(a[k0:k1], axis=0)
    pb, pa = st(page, max(0, ka - 5), ka), st(page, n - 10, n)
    cb, ca = st(chrome, max(0, ka - 5), ka), st(chrome, n - 10, n)
    bad = []; cbad = []; first = None
    for k in range(ka, n - 10):
        fb = (np.abs(page[k] - pb) > 30).mean(); fa = (np.abs(page[k] - pa) > 30).mean()
        if first is None and fb > FR: first = k
        if fb > FR and fa > FR:
            px = rgb[k, top:, 4:-4]; b, g, r = px[..., 0], px[..., 1], px[..., 2]
            mica = ((r >= 30) & (r <= 50) & (g >= 36) & (g <= 52) & (b >= 22) & (b <= 40) & (g > b + 6)).mean()
            white = ((b > 245) & (g > 245) & (r > 245)).mean(); black = ((b < 10) & (g < 10) & (r < 10)).mean()
            internal = ((abs(r - 0x0B) < 5) & (abs(g - 0x0D) < 5) & (abs(b - 0x13) < 5)).mean()
            kind = 'mica' if mica > .3 else 'preto' if black > .5 else 'interno' if internal > .5 else 'branco' if white > .8 else 'outro'
            med = np.median(px.reshape(-1, 3), axis=0).astype(int)[::-1]
            unif = float((np.abs(px - np.median(px.reshape(-1, 3), axis=0)) < 12).all(axis=-1).mean())
            bad.append((k, round(float(ms[k] - t_key)), kind, round(float(min(fb, fa)), 2), '#%02X%02X%02X' % tuple(med), round(unif, 2)))
        cfb = (np.abs(chrome[k] - cb) > 30).mean(); cfa = (np.abs(chrome[k] - ca) > 30).mean()
        if cfb > .15 and cfa > .15: cbad.append((k, round(float(ms[k] - t_key)), round(float(min(cfb, cfa)), 2)))
    lat = None
    for k in range(ka, n):
        if all((np.abs(page[j] - pa) > 30).mean() <= FR for j in range(k, n)): lat = k; break
    changed = (np.abs(pb - pa) > 30).mean()
    tot['caps'] += 1; tot['bad_frames'] += len(bad); tot['bad_caps'] += bool(bad); tot['chrome_bad_frames'] += len(cbad)
    if bad: tot['bad_ms'] += round(float(ms[bad[-1][0]] - ms[bad[0][0]] + 16.7))
    for b_ in bad: tot['kinds'][b_[2]] = tot['kinds'].get(b_[2], 0) + 1
    if lat is not None: lats.append(round(float(ms[lat] - act['action-start']))); lats_k.append(round(float(ms[lat] - t_key)))
    if first is not None: firsts.append(round(float(ms[first] - t_key)))
    print(os.path.basename(pre), f'mudou={changed:.2f} 1a_mudanca={None if first is None else round(float(ms[first]-t_key))} lat_tecla={None if lat is None else round(float(ms[lat]-t_key))}',
          f'errados={len(bad)} {bad[:8]} moldura={len(cbad)} {cbad[:4]}')
    if bad or not sheet_done:
        k0 = max(ka - 1, 0); k1 = (lat if lat is not None else n - 1)
        idx = sorted(set([k0] + list(range(max(first or ka, ka) - 1, min(k1 + 2, n)))[:10] + [b_[0] for b_ in bad][:8] + [n - 1]))
        sheet(full, idx[:14], ms, t_key, pre + ('-bad.png' if bad else '-seq.png'), w, h); sheet_done = True
med = lambda a: float(np.median(a)) if a else None
tot.update({'lat_med': med(lats), 'lat_max': max(lats) if lats else None, 'lat_tecla_med': med(lats_k), 'lat_tecla_min': min(lats_k) if lats_k else None,
            'lat_tecla_max': max(lats_k) if lats_k else None, '1a_mudanca_med': med(firsts), 'teclas_ms_med': round(med(ovh), 1) if ovh else None})
print('TOTAL ' + json.dumps(tot, ensure_ascii=False))
