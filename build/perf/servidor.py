# Servidor local das páginas de teste das medições de desempenho (build/perf/README.md).
#
# Uso: python -I -u servidor.py <pasta das páginas> <porta> <ficheiro do registo>
#   - serve a pasta em 127.0.0.1:<porta> (só no próprio PC), sempre com Cache-Control: no-store;
#   - responde 204 a /p/<sinal>?... (sinais das páginas: painted, fps…) e a /b?... (sinais das páginas da troca de abas);
#   - regista cada pedido numa linha do registo: «<ms desde 1970> <caminho do pedido>». Os scripts esperam pelos sinais
#     lendo o registo a partir de uma posição (comum.ps1, Wait-Beacon).
# Aceita qualquer nome no cabeçalho Host: com ETERIS_TEST_HOST_RULES (ou --host-resolver-rules nas referências) os
# nomes *.eteris-teste.example chegam aqui e contam como sites diferentes para o Chromium (B-MEM C3L/C4L, B-CARGA).
import sys, time, threading, http.server, socketserver

root, port, log_path = sys.argv[1], int(sys.argv[2]), sys.argv[3]
lock = threading.Lock()
log = open(log_path, 'a', encoding='utf-8', buffering=1)


def note(path):
    with lock:
        log.write(f'{time.time() * 1000:.0f} {path}\n')


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **k):
        super().__init__(*a, directory=root, **k)

    def log_message(self, *a):
        pass

    def end_headers(self):
        self.send_header('Cache-Control', 'no-store')
        super().end_headers()

    def do_GET(self):
        note(self.path)
        if self.path.startswith('/p/') or self.path.startswith('/b?'):
            self.send_response(204)
            self.end_headers()
            return
        return super().do_GET()


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


Server(('127.0.0.1', port), Handler).serve_forever()
