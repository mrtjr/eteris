// Sinais das páginas de teste para o servidor local (servidor.py): /p/<sinal>?r=<etiqueta>&v=<valor>.
// ?r=<etiqueta> identifica a ronda. ?sinal=0 tira os sinais periódicos (fps): cada um é um pedido que passa pelo
// tratador de pedidos do Éteris e pelo serviço de rede e entra na CPU medida; os fps leem-se então do título.
var Q = new URLSearchParams(location.search);
var TAG = Q.get('r') || 'x';
var PERIODIC = Q.get('sinal') !== '0';
function beacon(kind, v, extra) {
  try { fetch('/p/' + kind + '?r=' + encodeURIComponent(TAG) + '&v=' + v + (extra || '') + '&n=' + Math.random(), { cache: 'no-store' }).catch(function () {}); } catch (e) {}
}
function fpsMeter(label) {
  var c = 0, t0 = performance.now();
  function f(t) {
    c++;
    if (t - t0 >= 1000) {
      var fps = Math.round(c * 1000 / (t - t0));
      document.title = label + ' ' + fps + ' fps';
      if (PERIODIC) beacon('fps', fps);
      c = 0; t0 = t;
    }
    requestAnimationFrame(f);
  }
  requestAnimationFrame(f);
}
