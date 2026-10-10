import json, subprocess, sys, time, urllib.request

print("=== 1. subprocess pipe test ===")
try:
    r = subprocess.run(["ollama", "ps"], capture_output=True, text=True, timeout=60)
    print("OK rc=", r.returncode)
    print(r.stdout.strip()[:400])
except Exception as e:
    print("FAILED:", type(e).__name__, e)

print("=== 2. python -c pipe test ===")
try:
    r = subprocess.run([sys.executable, "-c", "print(6*7)"], capture_output=True, text=True, timeout=60)
    print("OK rc=", r.returncode, "out=", r.stdout.strip(), "err=", r.stderr.strip()[:200])
except Exception as e:
    print("FAILED:", type(e).__name__, e)

print("=== 3. ollama version/tags ===")
try:
    with urllib.request.urlopen("http://127.0.0.1:11434/api/version", timeout=30) as f:
        print(f.read().decode())
except Exception as e:
    print("FAILED:", e)

print("=== 4. non-stream chat timing fields (model A) ===")
body = {
    "model": "qwen3-coder:30b",
    "messages": [{"role": "user", "content": "Reply with exactly: PONG"}],
    "stream": False,
    "options": {"temperature": 0.7, "seed": 42, "num_predict": 32},
}
t0 = time.time()
try:
    req = urllib.request.Request(
        "http://127.0.0.1:11434/api/chat",
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=600) as f:
        d = json.loads(f.read().decode())
    print("wall=%.2f" % (time.time() - t0))
    print("keys=", sorted(d.keys()))
    print("msg=", json.dumps(d.get("message"), ensure_ascii=False)[:300])
    print("finish_reason=", d.get("done_reason"))
    for k in ("total_duration", "load_duration", "prompt_eval_count", "prompt_eval_duration", "eval_count", "eval_duration"):
        print(" ", k, "=", d.get(k))
except Exception as e:
    print("FAILED:", type(e).__name__, e)

print("=== 5. tools support ===")
tobj = [{
    "type": "function",
    "function": {
        "name": "get_weather",
        "description": "Get current weather for a city",
        "parameters": {
            "type": "object",
            "properties": {"city": {"type": "string"}, "unit": {"type": "string", "enum": ["c", "f"]}},
            "required": ["city"],
        },
    },
}]
body = {
    "model": "qwen3-coder:30b",
    "messages": [{"role": "user", "content": "What is the weather in Beijing right now? Use the tool."}],
    "tools": tobj,
    "stream": False,
    "options": {"temperature": 0.7, "seed": 42, "num_predict": 256},
}
try:
    req = urllib.request.Request(
        "http://127.0.0.1:11434/api/chat",
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=600) as f:
        d = json.loads(f.read().decode())
    print("done_reason=", d.get("done_reason"))
    print("message=", json.dumps(d.get("message"), ensure_ascii=False)[:800])
except Exception as e:
    print("FAILED:", type(e).__name__, e)

print("=== 6. ollama show ===")
for m in ("qwen3-coder:30b",):
    try:
        r = subprocess.run(["ollama", "show", m], capture_output=True, text=True, timeout=60)
        print(r.stdout.strip()[:900])
    except Exception as e:
        print("FAILED:", e)
