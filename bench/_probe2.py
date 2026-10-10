import json, urllib.request, urllib.error

BASE = "http://127.0.0.1:11434"
paths = ["/", "/api/version", "/api/tags", "/v1/models", "/api/ps", "/api/chat", "/api/generate", "/health", "/v1/chat/completions"]
for p in paths:
    url = BASE + p
    try:
        req = urllib.request.Request(url)
        with urllib.request.urlopen(req, timeout=20) as f:
            data = f.read().decode(errors="replace")
            print("GET %-24s -> %s  %s" % (p, f.status, data[:220].replace("\n", " ")))
    except urllib.error.HTTPError as e:
        body = e.read().decode(errors="replace")[:220].replace("\n", " ")
        print("GET %-24s -> HTTP %s  %s  hdrs=%s" % (p, e.code, body, dict(e.headers)))
    except Exception as e:
        print("GET %-24s -> %s: %s" % (p, type(e).__name__, e))
