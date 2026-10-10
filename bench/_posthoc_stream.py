# -*- coding: utf-8 -*-
"""Post-hoc: apply the same stream analysis to already-recorded runs (e.g. model A),
so both models get identical derived metrics from identical raw data."""
import json, os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import runner as R

path = os.path.join(HERE, "results.json")
with open(path, "r", encoding="utf-8") as f:
    res = json.load(f)

changed = []
for tag, m in res["models"].items():
    rec = m.get("runs", {}).get("throughput_long")
    if not rec or not rec.get("stream_chunks"):
        continue
    if m.get("tests", {}).get("throughput_long", {}).get("stream_analysis", {}).get("method") == \
       "estimated_from_chunk_timing":
        continue
    a = R.analyze_stream(rec)
    m.setdefault("tests", {}).setdefault("throughput_long", {})["stream_analysis"] = a
    changed.append((tag, a))

for tag, a in changed:
    print(tag, json.dumps(a, ensure_ascii=False))

if changed:
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(res, f, ensure_ascii=False, indent=1)
    os.replace(tmp, path)
    print("results.json updated")
else:
    print("nothing to do")

# re-print ps rows to verify the new parser
for tag, m in res["models"].items():
    for snap in ("resource_snapshot", "ps_at_end"):
        rows = m.get(snap, {}).get("rows", [])
        if rows:
            print(tag, snap, json.dumps(rows[0], ensure_ascii=False))
            break
