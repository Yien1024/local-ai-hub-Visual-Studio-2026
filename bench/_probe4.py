import json, time, urllib.request

OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))

FILLER = ("The archive room contains routine maintenance notes about ventilation schedules, "
          "spare parts inventories, and quarterly inspection reports for the facility. ")


def call(path, body, timeout=900):
    req = urllib.request.Request("http://127.0.0.1:11434" + path,
                                 data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    t0 = time.time()
    with OPENER.open(req, timeout=timeout) as f:
        return json.loads(f.read().decode()), time.time() - t0


# --- calibration: tokens per filler repetition ---
n = 400
text = FILLER * n
print("filler chars:", len(text), "words:", len(text.split()), "reps:", n)
body = {"model": "qwen3-coder:30b",
        "messages": [{"role": "user", "content": text + "\n\nReply with exactly: OK"}],
        "stream": False, "options": {"temperature": 0.7, "seed": 42, "num_predict": 8}}
try:
    d, w = call("/api/chat", body)
    print("prompt_eval_count=%s  reps=%d  tok/rep=%.2f  wall=%.1f" % (
        d.get("prompt_eval_count"), n, d.get("prompt_eval_count", 0) / n, w))
except Exception as e:
    print("CALIB FAILED:", type(e).__name__, e)

# --- does model A accept think:false ---
for extra in ({"think": False}, {"think": True}):
    b = {"model": "qwen3-coder:30b", "messages": [{"role": "user", "content": "Say OK"}],
         "stream": False, "options": {"temperature": 0.7, "num_predict": 16}}
    b.update(extra)
    try:
        d, w = call("/api/chat", b)
        print("think=%s -> OK msg=%s" % (extra["think"], json.dumps(d.get("message"), ensure_ascii=False)[:120]))
    except Exception as e:
        print("think=%s -> %s: %s" % (extra["think"], type(e).__name__, e))
        try:
            print("   body:", e.read().decode()[:200])
        except Exception:
            pass

# --- OpenAI-compat tools ---
tools = [{"type": "function", "function": {"name": "get_weather", "description": "Get current weather for a city",
          "parameters": {"type": "object", "properties": {"city": {"type": "string"}, "unit": {"type": "string", "enum": ["c", "f"]}}, "required": ["city"]}}}]
b = {"model": "qwen3-coder:30b", "messages": [{"role": "user", "content": "What is the weather in Beijing right now? Use the tool."}],
     "tools": tools, "stream": False, "temperature": 0.7, "seed": 42, "max_tokens": 256}
try:
    d, w = call("/v1/chat/completions", b)
    ch = d["choices"][0]
    print("v1 finish_reason=", ch.get("finish_reason"))
    print("v1 tool_calls=", json.dumps(ch["message"].get("tool_calls"), ensure_ascii=False)[:400])
    print("v1 usage=", d.get("usage"))
    print("v1 raw keys=", sorted(d.keys()))
except Exception as e:
    print("v1 tools FAILED:", type(e).__name__, e)
    try:
        print("   body:", e.read().decode()[:300])
    except Exception:
        pass

# --- native /api/chat tools ---
b2 = {"model": "qwen3-coder:30b", "messages": [{"role": "user", "content": "What is the weather in Beijing right now? Use the tool."}],
      "tools": tools, "stream": False, "options": {"temperature": 0.7, "seed": 42, "num_predict": 256}}
try:
    d, w = call("/api/chat", b2)
    print("native done_reason=", d.get("done_reason"))
    print("native message=", json.dumps(d.get("message"), ensure_ascii=False)[:400])
except Exception as e:
    print("native tools FAILED:", type(e).__name__, e)
