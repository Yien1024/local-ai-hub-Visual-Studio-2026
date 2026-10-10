import sys, os, json, time
sys.path.insert(0, r"C:\Users\zx\Documents\vs-local-ai\bench")
import runner as R
print("ps before:", json.dumps(R.ollama_ps()["rows"], ensure_ascii=False))
# dry-run the free-vram helper against a non-existent target so nothing is unloaded yet
print("(dry run only; real unload happens at B run start)")
