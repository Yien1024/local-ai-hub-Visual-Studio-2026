import json, os, urllib.request

print("proxy env:", {k: v for k, v in os.environ.items() if "proxy" in k.lower()})
try:
    print("getproxies():", urllib.request.getproxies())
except Exception as e:
    print("getproxies failed:", e)

opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
for p in ["/", "/api/version", "/api/tags", "/api/ps"]:
    try:
        with opener.open("http://127.0.0.1:11434" + p, timeout=20) as f:
            print("NOPROXY GET %-16s -> %s %s" % (p, f.status, f.read().decode(errors="replace")[:200].replace("\n", " ")))
    except Exception as e:
        print("NOPROXY GET %-16s -> %s: %s" % (p, type(e).__name__, e))
