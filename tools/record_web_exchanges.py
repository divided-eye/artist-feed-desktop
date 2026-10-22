"""Run the web version's tests and record every stand-in HTTP exchange, for the desktop version's replay."""
import json, sys, os
from urllib.parse import urlsplit, parse_qsl, urlencode
import httpx

OUT = sys.argv[1]
seen = {}

def key_of(request):
    u = urlsplit(str(request.url))
    q = sorted((k, v) for k, v in parse_qsl(u.query, keep_blank_values=True) if k != "client_id")
    body = request.content.decode("utf-8", "replace") if request.content else ""
    if body:
        try:
            body = json.dumps(json.loads(body), sort_keys=True)
        except ValueError:
            pass
    return f"{request.method} {u.scheme}://{u.netloc.lower()}{u.path}?{urlencode(q)} {body}".strip()

_orig = httpx.MockTransport.__init__
def patched(self, handler):
    def rec(request):
        r = handler(request)
        try:
            text = r.content.decode("utf-8")
        except Exception:
            text = r.content.decode("latin-1")
        k = key_of(request)
        seen.setdefault(k, [])
        entry = {"status": r.status_code, "type": r.headers.get("content-type", ""), "text": text}
        if entry not in seen[k]:
            seen[k].append(entry)
        return r
    _orig(self, rec)
httpx.MockTransport.__init__ = patched

import pytest
code = pytest.main(["-q", "-p", "no:cacheprovider", "tests/test_app.py"])
with open(OUT, "w") as f:
    json.dump(seen, f, ensure_ascii=False, indent=0, sort_keys=True)
print("exit", code, "keys", len(seen))
