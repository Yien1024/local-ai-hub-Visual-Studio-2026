# -*- coding: utf-8 -*-
"""Compact evidence dump from results.json for report writing."""
import json, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(HERE, "results.json"), "r", encoding="utf-8") as f:
    R = json.load(f)

for tag, m in R["models"].items():
    print("=" * 100)
    print("TAG %s  model=%s  caps=%r  started=%s finished=%s"
          % (tag, m["model"], m.get("capabilities"), m.get("started"), m.get("finished")))
    print("summary:", json.dumps(m.get("summary", {}), ensure_ascii=False))
    runs = m.get("runs", {})
    cs = runs.get("cold_start", {})
    print("\n-- cold start --")
    print("  wall=%.2fs load_duration=%.2fs total_duration=%.2fs prompt_eval=%s eval=%s"
          % (cs.get("wall_s", -1), cs.get("load_duration_s") or -1, cs.get("total_duration_s") or -1,
             cs.get("prompt_eval_count"), cs.get("eval_count")))
    ps = m.get("resource_snapshot", {}).get("rows", [])
    print("\n-- ps snapshot --")
    for r in ps:
        print("  ", json.dumps(r, ensure_ascii=False))
    tl = m.get("tests", {}).get("throughput_long", {})
    print("\n-- throughput_long --", json.dumps(tl.get("summary"), ensure_ascii=False),
          json.dumps(tl.get("generation"), ensure_ascii=False))
    print("\n-- tests --")
    for k, v in sorted(m.get("tests", {}).items()):
        if k.startswith(("code-", "tool-", "zh-", "needle")):
            print("  %-38s %-5s %s" % (k, "PASS" if v.get("passed") else "FAIL",
                                       str(v.get("detail"))[:150]))
    print("\n-- runs timing --")
    for k, v in sorted(runs.items()):
        print("  %-28s wall=%7s prompt=%6s eval=%5s pre=%8s eval_tps=%7s err=%s"
              % (k, v.get("wall_s"), v.get("prompt_eval_count"), v.get("eval_count"),
                 v.get("prompt_tps"), v.get("eval_tps"), (v.get("error") or "")[:60]))
    print("\n-- ollama list at end --")
    print("  " + (m.get("ollama_list_at_end", {}).get("raw", "").replace("\n", " | ")))
