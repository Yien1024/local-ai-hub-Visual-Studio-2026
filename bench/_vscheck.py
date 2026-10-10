import sys, os
sys.path.insert(0, r"C:\Users\zx\Documents\vs-local-ai\bench")
import runner as R
t = R.build_vs_tools(); s = R.build_vs_system()
print("tools:", len(t), "names ok:", [x["function"]["name"] for x in t][:6], "...", R.VS_TARGET_TOOL in [x["function"]["name"] for x in t])
print("system chars:", len(s), "filler reps:", 680)
print("tool json chars:", len(__import__("json").dumps(t)))
