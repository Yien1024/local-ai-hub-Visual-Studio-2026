import json
R = json.load(open("results.json", encoding="utf-8"))
m = R["models"]["A"]
v = m["runs"]["vs_realistic"]; t = m["tests"]["vs_realistic"]
print("vs_realistic: wall=%.2f prompt_tok=%s prompt_tps=%s eval=%s done=%s tools=%r" % (
    v["wall_s"], v["prompt_eval_count"], v["prompt_tps"], v["eval_count"], v["done_reason"], t["tool_names"]))
print("  system chars=%s tools offered=%s" % (t["system_prompt_chars"], t["tool_count_offered"]))
print("  tool_calls raw:", json.dumps(v["tool_calls_native"], ensure_ascii=False)[:300])
print("  text:", repr(v["text"])[:300])
print()
print("provenance: started=%s last_run_at=%s finished=%s" % (m["started"], m.get("last_run_at"), m.get("finished")))
print("test keys:", sorted(m["tests"].keys()))
print("stream analysis:", json.dumps(m["tests"]["throughput_long"]["stream_analysis"], ensure_ascii=False))
print("needle:", json.dumps({k:m["tests"]["needle"][k] for k in ("passed","answer","prompt_eval_count","wall_s","prompt_tps")}, ensure_ascii=False))
