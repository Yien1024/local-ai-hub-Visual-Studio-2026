import json, os
HERE = r"C:\Users\zx\Documents\vs-local-ai\bench"
R = json.load(open(os.path.join(HERE, "results.json"), encoding="utf-8"))
m = R["models"]["A"]
sc = m["runs"]["throughput_long"].get("stream_chunks", [])
print("stream chunks:", len(sc), "with eval_count:", sum(1 for c in sc if c.get("eval_count")))
print("first 5:", sc[:5])
print("last 3:", sc[-3:])
print()
print("ps raw text:")
print(repr(m["resource_snapshot"]["raw"]))
print()
print("ps_at_end raw:")
print(repr(m.get("ps_at_end", {}).get("raw")))
print()
lt = m["runs"]["latency_long"]
print("latency_long total_duration=%.3f wall=%.3f load=%.4f prompt_eval_s=%.3f" % (
    lt["total_duration_s"], lt["wall_s"], lt["load_duration_s"], lt["prompt_eval_duration_s"]))
nd = m["runs"]["needle_17k"]
print("needle total=%.3f wall=%.3f pe_s=%.3f answer=%r" % (nd["total_duration_s"], nd["wall_s"], nd["prompt_eval_duration_s"], m["tests"]["needle"]["answer"]))
print()
print("code-10 raw answer:")
print(m["runs"]["code-10-dotnet-httpclient-static"]["text"])
print()
print("zh-03 raw answer:")
print(repr(m["runs"]["zh-03-constraint"]["text"]))
print()
print("tool-01 finish/args:", json.dumps(m["runs"]["tool-01-single"]["tool_calls"], ensure_ascii=False))
print("tool-03 text:", repr(m["runs"]["tool-03-no-tool-needed"]["text"])[:200], "finish:", m["runs"]["tool-03-no-tool-needed"]["finish_reason"])
