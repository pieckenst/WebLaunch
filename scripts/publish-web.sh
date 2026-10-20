#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet publish LaunchApp/LaunchApp.csproj -c Release -o artifacts/web
python3 - <<'PY'
from pathlib import Path
root=Path('artifacts/web/wwwroot')
p=root/'index.html'
p.write_text(p.read_text().replace('<base href="/"', '<base href="/WebLaunch/"'))
(root/'404.html').write_text(p.read_text())
(root/'.nojekyll').touch()
PY
