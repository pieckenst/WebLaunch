"""Serve the production Pages artifact under /WebLaunch/, including SPA fallback."""
from http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler
from pathlib import Path
from urllib.parse import urlsplit, unquote
root = Path(__file__).resolve().parents[1] / 'artifacts/web/wwwroot'
class Handler(SimpleHTTPRequestHandler):
    def translate_path(self, path):
        route = unquote(urlsplit(path).path)
        if not route.startswith('/WebLaunch/'):
            return str(root / '__not_found__')
        relative = route[len('/WebLaunch/'):]
        target = (root / relative).resolve()
        if not target.is_relative_to(root):
            return str(root / '__not_found__')
        if not target.is_file() and not Path(relative).suffix:
            return str(root / 'index.html')
        return str(target)
ThreadingHTTPServer(('127.0.0.1', 5148), Handler).serve_forever()
