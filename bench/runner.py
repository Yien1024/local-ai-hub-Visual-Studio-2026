#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
vs-local-ai benchmark runner: qwen3-coder:30b (A) vs smtek/Qwen3.8-27B:Q4_K_M (B)

Pure standard library. Reproducible: identical tasks, identical order, identical
sampling parameters for both models.

Usage:
    python runner.py --model qwen3-coder:30b --tag A
    python runner.py --model smtek/Qwen3.8-27B:Q4_K_M --tag B
    python runner.py --model <m> --tag A --only cold,throughput   # partial rerun

Everything is merged into results.json keyed by tag, so both runs must be run
from the same directory. Raw model output is stored verbatim for every test.

Notes / hazards discovered while building this:
  * A Windows system proxy (registry ProxyEnable=1 -> 127.0.0.1:26561) is picked
    up by urllib's getproxies(), and it does NOT bypass loopback. Without
    ProxyHandler({}) every request to 127.0.0.1:11434 returns HTTP 404.
  * C# answers are verified by real compilation + execution using the Roslyn
    csc.exe from the VS 2026 install against the .NET 10.0.12 ref pack.
    `dotnet run file.cs` does NOT work here (needs nuget.org, unreachable).
"""

import argparse
import asyncio  # noqa: F401  (used inside generated harnesses)
import datetime
import hashlib
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

# --------------------------------------------------------------------------
# config
# --------------------------------------------------------------------------
BASE = "http://127.0.0.1:11434"
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))  # bypass system proxy
HERE = os.path.dirname(os.path.abspath(__file__))
RESULTS = os.path.join(HERE, "results.json")
TMPROOT = os.path.join(HERE, "_tmp")

TEMPERATURE = 0.7
SEED = 42

CSC = r"E:\Visual Studio 2026\chanpin\MSBuild\Current\Bin\Roslyn\csc.exe"
NETREF = r"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.12\ref\net10.0"
NETRUNTIME = "10.0.12"

TEST_ORDER = [
    "cold_start", "warmup", "throughput_short", "throughput_long",
    "latency_short", "latency_long", "needle", "resource_snapshot",
    "vs_realistic", "code", "tools", "tool_native", "chinese",
]
PY_EXEC_TIMEOUT = 30
CS_COMPILE_TIMEOUT = 120
CS_RUN_TIMEOUT = 60


def now_iso():
    return datetime.datetime.now().astimezone().isoformat(timespec="seconds")


def log(msg):
    print("[%s] %s" % (datetime.datetime.now().strftime("%H:%M:%S"), msg), flush=True)


# --------------------------------------------------------------------------
# ollama helpers
# --------------------------------------------------------------------------
def http_json(path, body, timeout, raw_bytes=False):
    """POST JSON. Returns (data, wall_s, error_str)."""
    url = BASE + path
    data = json.dumps(body).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    t0 = time.time()
    try:
        with OPENER.open(req, timeout=timeout) as f:
            payload = f.read().decode("utf-8", errors="replace")
        wall = time.time() - t0
        if raw_bytes:
            return payload, wall, None
        return json.loads(payload), wall, None
    except urllib.error.HTTPError as e:
        wall = time.time() - t0
        try:
            detail = e.read().decode("utf-8", errors="replace")
        except Exception:
            detail = ""
        return None, wall, "HTTP %s %s: %s" % (e.code, e.reason, detail[:600])
    except Exception as e:
        wall = time.time() - t0
        return None, wall, "%s: %s" % (type(e).__name__, e)


def http_get(path, timeout=30):
    try:
        with OPENER.open(BASE + path, timeout=timeout) as f:
            return f.read().decode("utf-8", errors="replace"), None
    except Exception as e:
        return None, "%s: %s" % (type(e).__name__, e)


def run_cli(args, timeout=120):
    try:
        r = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
        return r.stdout, r.returncode
    except Exception as e:
        return "ERROR %s: %s" % (type(e).__name__, e), -1


def ollama_ps():
    """Parse `ollama ps` by zipping the header names onto the (2+ space separated) values.
    NOTE: this Ollama build prints NAME ID SIZE PROCESSOR CONTEXT RUNNER UNTIL."""
    out, rc = run_cli(["ollama", "ps"])
    rows = []
    lines = [l.rstrip() for l in out.splitlines() if l.strip()]
    header = re.split(r"\s{2,}", lines[0].strip()) if lines else []
    for line in lines[1:]:
        parts = re.split(r"\s{2,}", line.strip())
        row = {}
        for i, key in enumerate(header):
            row[key.lower()] = parts[i] if i < len(parts) else ""
        if not row:
            continue
        row["is_100pct_gpu"] = row.get("processor", "").strip().startswith("100%")
        rows.append(row)
    return {"t": now_iso(), "rc": rc, "raw": out, "header": header, "rows": rows}


def analyze_stream(rec):
    """Ollama 0.40 does NOT emit cumulative eval_count in intermediate stream chunks
    (verified: 732 chunks, only the final one carries eval_count). Chunks correspond
    ~1:1 to generated tokens, so window throughput is estimated from chunk arrival
    times scaled by eval_count/chunk_count. Method is recorded in the output."""
    chunks = rec.get("stream_chunks") or []
    n = len(chunks)
    out = {"chunk_count": n,
           "chunks_with_cumulative_eval": sum(1 for c in chunks if c.get("eval_count")),
           "method": "estimated_from_chunk_timing"}
    ev = rec.get("eval_count")
    if n < 8 or not ev:
        out["note"] = "数据不足，未计算分段吞吐"
        return out
    per_chunk = ev / n
    t0, t1 = chunks[0]["t"], chunks[-1]["t"]
    span = t1 - t0

    def window(fa, fb):
        ta, tb = t0 + span * fa, t0 + span * fb
        cnt = sum(1 for c in chunks if ta <= c["t"] <= tb)
        if tb - ta <= 0:
            return None
        return round(cnt * per_chunk / (tb - ta), 2)

    out["est_tps_by_time_quartile"] = {"q1_first": window(0, 0.25), "q2": window(0.25, 0.5),
                                       "q3": window(0.5, 0.75), "q4_last": window(0.75, 1.0)}
    out["assumption"] = ("单个 chunk≈1 token（共 %d chunk / %s token）；"
                         "分段吞吐=窗口内 chunk 数×(eval_count/chunk数)/窗口时长" % (n, ev))
    if out["est_tps_by_time_quartile"]["q1_first"] and out["est_tps_by_time_quartile"]["q4_last"]:
        q1 = out["est_tps_by_time_quartile"]["q1_first"]
        q4 = out["est_tps_by_time_quartile"]["q4_last"]
        out["degradation_q4_vs_q1_pct"] = round((q1 - q4) / q1 * 100, 1)
    return out


def ollama_list():
    out, rc = run_cli(["ollama", "list"], timeout=180)
    return {"t": now_iso(), "rc": rc, "raw": out}


def model_capabilities(model):
    out, rc = run_cli(["ollama", "show", model], timeout=180)
    caps = []
    m = re.search(r"Capabilities\s*\n((?:\s+\S+\s*\n?)+)", out)
    if m:
        caps = m.group(1).split()
    return caps, out


def unload(model, wait_s=120):
    """Ask ollama to unload the model, then poll /api/ps until it is gone."""
    http_json("/api/generate", {"model": model, "keep_alive": 0}, timeout=120)
    t0 = time.time()
    while time.time() - t0 < wait_s:
        data, _, err = http_json_get_json("/api/ps")
        if err:
            break
        names = [m.get("name") or m.get("model") for m in data.get("models", [])]
        if not any(n and (n == model or n.split(":")[0] == model.split(":")[0]) for n in names):
            return {"unloaded": True, "waited_s": round(time.time() - t0, 2), "ps": data}
        time.sleep(2)
    return {"unloaded": False, "waited_s": round(time.time() - t0, 2), "ps": None}


def free_other_models(target):
    """Unload every model in /api/ps that is not the one under test.

    Rationale: with OLLAMA_KEEP_ALIVE=-1 models stay resident forever. The
    previously tested model would still hold ~20 GB of the 24 GB card, which
    would make the next model look like it does not fit in VRAM. Recorded in
    results so the reader can see the starting state was clean.
    """
    data, _, err = http_json_get_json("/api/ps")
    freed = []
    if err or not data:
        return {"error": err, "freed": freed}
    for m in data.get("models", []):
        name = m.get("name") or m.get("model")
        if not name or name == target:
            continue
        _, wall, uerr = http_json("/api/generate", {"model": name, "keep_alive": 0}, timeout=120)
        freed.append({"name": name, "unload_wall_s": round(wall, 2), "error": uerr,
                      "size_before": m.get("size")})
        time.sleep(1)
    after, _, _ = http_json_get_json("/api/ps")
    return {"freed": freed, "ps_after_free": after}


def http_json_get_json(path, timeout=30):
    txt, err = http_get(path, timeout=timeout)
    if err:
        return None, None, err
    try:
        return json.loads(txt), None, None
    except Exception as e:
        return None, None, "json: %s" % e


# --------------------------------------------------------------------------
# generation
# --------------------------------------------------------------------------
def gen_native(model, prompt, num_predict, think=None, tools=None, stream=False,
               timeout=1800, system=None, extra_options=None):
    """Call /api/chat. Returns a record with raw response + derived metrics."""
    messages = []
    if system:
        messages.append({"role": "system", "content": system})
    messages.append({"role": "user", "content": prompt})
    options = {"temperature": TEMPERATURE, "seed": SEED, "num_predict": num_predict}
    if extra_options:
        options.update(extra_options)
    body = {"model": model, "messages": messages, "stream": bool(stream), "options": options}
    if think is not None:
        body["think"] = think
    if tools:
        body["tools"] = tools

    rec = {"endpoint": "/api/chat", "t_start": now_iso(), "request_body": body,
           "options": options, "prompt_chars": len(prompt)}
    if not stream:
        rec["prompt_sha256"] = hashlib.sha256(prompt.encode("utf-8")).hexdigest()
        rec["prompt_preview"] = prompt[:400] + ("..." if len(prompt) > 400 else "")
        data, wall, err = http_json("/api/chat", body, timeout=timeout)
        rec["wall_s"] = round(wall, 3)
        rec["t_end"] = now_iso()
        if err:
            rec["error"] = err
            return rec
        rec["raw"] = data
        msg = data.get("message") or {}
        rec["text"] = msg.get("content") or ""
        rec["thinking"] = msg.get("thinking") or ""
        rec["tool_calls_native"] = msg.get("tool_calls")
        rec["done_reason"] = data.get("done_reason")
        fill_metrics(rec, data, wall)
        return rec

    # streaming
    req = urllib.request.Request(BASE + "/api/chat", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    chunks = []
    text_parts = []
    t0 = time.time()
    try:
        with OPENER.open(req, timeout=timeout) as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line.decode("utf-8", errors="replace"))
                except Exception:
                    continue
                chunks.append({"t": round(time.time() - t0, 3),
                               "eval_count": obj.get("eval_count"),
                               "eval_duration": obj.get("eval_duration"),
                               "done": obj.get("done", False)})
                if obj.get("message", {}).get("content"):
                    text_parts.append(obj["message"]["content"])
                if obj.get("done"):
                    final = obj
                    break
            else:
                final = None
        rec["wall_s"] = round(time.time() - t0, 3)
        rec["text"] = "".join(text_parts)
        rec["stream_chunks"] = chunks
        if final:
            rec["raw_final"] = final
            rec["done_reason"] = final.get("done_reason")
            fill_metrics(rec, final, rec["wall_s"])
        else:
            rec["error"] = "stream ended without a done=true chunk"
    except Exception as e:
        rec["wall_s"] = round(time.time() - t0, 3)
        rec["error"] = "%s: %s" % (type(e).__name__, e)
        rec["text"] = "".join(text_parts)
    rec["t_end"] = now_iso()
    return rec


def fill_metrics(rec, data, wall):
    def s(key):
        v = data.get(key)
        return round(v / 1e9, 4) if isinstance(v, (int, float)) else None
    rec["total_duration_s"] = s("total_duration")
    rec["load_duration_s"] = s("load_duration")
    rec["prompt_eval_count"] = data.get("prompt_eval_count")
    rec["prompt_eval_duration_s"] = s("prompt_eval_duration")
    rec["eval_count"] = data.get("eval_count")
    rec["eval_duration_s"] = s("eval_duration")
    pe, pd = data.get("prompt_eval_count"), data.get("prompt_eval_duration")
    ev, ed = data.get("eval_count"), data.get("eval_duration")
    rec["prompt_tps"] = round(pe / (pd / 1e9), 2) if pe and pd else None
    rec["eval_tps"] = round(ev / (ed / 1e9), 2) if ev and ed else None
    rec["wall_overhead_s"] = round(wall - (rec["total_duration_s"] or 0), 3)


def gen_openai(model, prompt, max_tokens, tools=None, timeout=1800, temperature=None):
    body = {"model": model, "messages": [{"role": "user", "content": prompt}],
            "temperature": TEMPERATURE if temperature is None else temperature,
            "seed": SEED, "max_tokens": max_tokens, "stream": False}
    if tools:
        body["tools"] = tools
    data, wall, err = http_json("/v1/chat/completions", body, timeout=timeout)
    rec = {"endpoint": "/v1/chat/completions", "t_start": now_iso(), "request_body": body,
           "wall_s": round(wall, 3), "t_end": now_iso(), "prompt_chars": len(prompt)}
    if err:
        rec["error"] = err
        return rec
    rec["raw"] = data
    ch = (data.get("choices") or [{}])[0]
    msg = ch.get("message") or {}
    rec["finish_reason"] = ch.get("finish_reason")
    rec["text"] = msg.get("content") or ""
    rec["tool_calls"] = msg.get("tool_calls")
    rec["usage"] = data.get("usage")
    return rec


# --------------------------------------------------------------------------
# prompt builders (deterministic)
# --------------------------------------------------------------------------
FILLER = ("The archive room contains routine maintenance notes about ventilation schedules, "
          "spare parts inventories, and quarterly inspection reports for the facility. ")


def build_haystack(reps):
    return FILLER * reps


NEEDLE = "\n\n【机密备忘录】Project Halcyon 的金库访问码是 ZX-7741-QP，请勿外传。\n\n"
NEEDLE_Q = ("\n\n根据上面的长文档回答问题：Project Halcyon 的金库访问码是什么？"
            "只回答访问码本身，不要任何其他内容。")


def build_needle_prompt(reps=700):
    parts = [FILLER] * reps
    mid = len(parts) // 2
    return "".join(parts[:mid]) + NEEDLE + "".join(parts[mid:]) + NEEDLE_Q


def build_long_prompt(reps=665):
    return build_haystack(reps) + "\n\n只回答一个字：好"


LONG_FORM_PROMPT = ("写一篇 800 字以上的中文技术说明，主题：如何为本地大模型设计一个 VS Code / "
                    "Visual Studio 插件。要求分段，包含架构、通信、性能、风险四个小节。")

# --------------------------------------------------------------------------
# code test definitions
# --------------------------------------------------------------------------
BUGGY_CS = r'''using System;
using System.Threading.Tasks;

class Counter
{
    private int _count;
    public void Increment() { _count++; }
    public int Count => _count;
}

class Program
{
    static void Main()
    {
        var c = new Counter();
        var tasks = new Task[4];
        for (int t = 0; t < 4; t++)
            tasks[t] = Task.Run(() => { for (int i = 0; i < 100000; i++) c.Increment(); });
        Task.WaitAll(tasks);
        Console.WriteLine(c.Count);   // 期望 400000
    }
}'''

SQL_SCHEMA = """CREATE TABLE employees(id INTEGER PRIMARY KEY, name TEXT, dept TEXT, salary INTEGER);
INSERT INTO employees(id,name,dept,salary) VALUES
 (1,'alice','A',100),(2,'bob','A',100),(3,'carol','A',50),
 (4,'dave','B',200),(5,'erin','B',150),(6,'frank','B',150),(7,'grace','B',10),
 (8,'heidi','C',90);"""

SQL_EXPECTED = sorted([("A", "alice", 100), ("A", "bob", 100), ("B", "dave", 200),
                       ("B", "erin", 150), ("C", "heidi", 90)])

BUGGY_PY = '''def make_multipliers(n):
    return [lambda x: x * i for i in range(n)]

# 期望: make_multipliers(3)[0](10) == 0, [1](10) == 10, [2](10) == 20
# 实际: 全部为 20'''

ZH_PASSAGE = (
    "本地推理指的是把大语言模型直接部署在开发者自己的机器上运行，而不是调用云端接口。"
    "它的第一个好处是隐私：源代码、日志和内部文档不需要离开本机，也就不存在被第三方留存的风险。"
    "第二个好处是成本可预测：一次性买显卡之后，日常调用不再按 token 计费，重度使用反而更便宜。"
    "第三个好处是可离线：在飞机上或内网隔离环境里依然能工作。"
    "但代价同样明显：显存决定能跑多大的模型，消费级显卡通常只能跑量化后的中小模型，"
    "推理速度也远低于云端集群；此外模型知识存在截止日期，需要自己维护检索或联网方案。")

ZH_CODE = '''def f(n, memo={}):
    if n in memo:
        return memo[n]
    memo[n] = n if n < 2 else f(n - 1) + f(n - 2)
    return memo[n]

print([f(i) for i in range(5)])'''

CARGO = ("只输出一个 ```%s 代码块，不要任何解释文字。")

CODE_TASKS = [
    {
        "id": "code-01-cs-async-file",
        "category": "C#/.NET 语法与 API + async 文件 IO",
        "num_predict": 1024,
        "grader": "exec_csharp",
        "grade_note": "真编译+真运行（Roslyn csc + dotnet 10.0.12）",
        "setup_files": {"data.txt": "ok line\nERROR one\nfine\nERROR two\nERROR three\nlast\n"},
        "expected_stdout": "3",
        "prompt": ("写一个完整的 C# 控制台程序（.NET 10，含 Main）。要求：异步读取当前目录下的 "
                   "data.txt 的全部行，统计包含子串 \"ERROR\"（区分大小写）的行数，"
                   "并把这个数字打印到标准输出（只打印这个数字，不要其他字符）。\n"
                   + CARGO % "csharp"),
    },
    {
        "id": "code-02-cs-linq",
        "category": "C#/.NET LINQ",
        "num_predict": 1024,
        "grader": "exec_csharp",
        "grade_note": "真编译+真运行，逐行比对输出",
        "expected_lines": ["Carol:305", "Alice:165", "Bob:100"],
        "prompt": ("写一个完整的 C# 控制台程序（.NET 10，含 Main）。给定订单数据："
                   "Alice 120、Bob 80、Alice 45、Carol 300、Bob 20、Dave 10、Carol 5（客户名 金额）。"
                   "用 LINQ 按客户汇总金额，按总额降序输出前 3 名，每行格式严格为 `客户名:总额`"
                   "（总额为整数，无小数、无空格）。\n" + CARGO % "csharp"),
    },
    {
        "id": "code-03-cs-bugfix-thread",
        "category": "bug 修复（C# 并发）",
        "num_predict": 1024,
        "grader": "exec_csharp",
        "grade_note": "真编译+真运行，必须稳定输出 400000；并静态检查是否保留并行结构",
        "expected_stdout": "400000",
        "require_tokens": [["Interlocked", "lock", "SemaphoreSlim", "Monitor", "Mutex"]],
        "prompt": ("下面这段 C# 程序本应输出 400000，但实际经常输出更小的值。请修复它，"
                   "使输出恒为 400000，并且保留\"4 个任务并行各加 100000 次\"的结构"
                   "（不允许改成单线程累加）。输出完整的可编译程序。\n\n```csharp\n"
                   + BUGGY_CS + "\n```\n" + CARGO % "csharp"),
    },
    {
        "id": "code-04-regex-ipv4",
        "category": "正则表达式（Python）",
        "num_predict": 768,
        "grader": "exec_python",
        "grade_note": "真执行：3 组用例逐组断言",
        "harness": "ipv4",
        "prompt": ("用 Python（只允许标准库 re）实现函数 `def extract(text):`，返回 text 中所有"
                   "合法 IPv4 地址组成的列表（按出现顺序）。要求：四段十进制、每段 0-255；"
                   "不接受前导零（如 01.2.3.4）；不接受 999.1.1.1 或 256.256.256.256；"
                   "不接受缺少段数的形式。\n" + CARGO % "python"),
    },
    {
        "id": "code-05-sql-top2-per-dept",
        "category": "SQL（窗口函数）",
        "num_predict": 512,
        "grader": "exec_sqlite",
        "grade_note": "真执行：在 SQLite 中建表灌数据并比对结果集",
        "prompt": ("给定表 employees(id INTEGER, name TEXT, dept TEXT, salary INTEGER)。"
                   "写一条 SQL 查询，返回每个部门薪资最高的 2 名员工；并列时按 name 升序优先。"
                   "输出列必须为 dept, name, salary，结果按 dept 升序、salary 降序排列。"
                   "使用标准 SQL（需要能在 SQLite 3.39 上执行）。\n" + CARGO % "sql"),
    },
    {
        "id": "code-06-python-merge-intervals",
        "category": "Python 算法/数据结构",
        "num_predict": 768,
        "grader": "exec_python",
        "grade_note": "真执行：5 组用例",
        "harness": "merge",
        "prompt": ("实现 `def merge_intervals(intervals):`，输入是若干 [start, end] 区间"
                   "（可能乱序、可能重叠、可能首尾相接），返回合并后的区间列表，"
                   "按 start 升序；首尾相接（如 [1,3] 与 [3,5]）必须合并。返回 list[list[int]]。\n"
                   + CARGO % "python"),
    },
    {
        "id": "code-07-python-asyncio-limit",
        "category": "并发/async（Python asyncio）",
        "num_predict": 768,
        "grader": "exec_python",
        "grade_note": "真执行：用可注入的假 fetch 统计真实并发峰值",
        "harness": "asyncio_limit",
        "prompt": ("用 asyncio 实现 `async def fetch_all(urls, limit):`：并发处理 urls，"
                   "但任意时刻最多只有 limit 个 \"在飞\"；结果顺序必须与 urls 一致。"
                   "不要自己定义 fetch —— 作用域里已经有一个 `async def fetch(url)` 可用，"
                   "它返回一个值，你的代码必须 await 它。仅使用标准库 asyncio。\n"
                   + CARGO % "python"),
    },
    {
        "id": "code-08-python-bugfix-closure",
        "category": "bug 修复（Python 闭包延迟绑定）",
        "num_predict": 768,
        "grader": "exec_python",
        "grade_note": "真执行：检查 n=3/5/0 的行为",
        "harness": "closure",
        "prompt": ("修复下面的 Python 函数，使 make_multipliers(3) 返回的三个函数分别把入参乘以 "
                   "0、1、2（当前三个都乘以 2），并保证对任意 n>=0 都正确。"
                   "函数名必须仍为 make_multipliers，返回值必须仍是函数列表。\n\n```python\n"
                   + BUGGY_PY + "\n```\n" + CARGO % "python"),
    },
    {
        "id": "code-09-python-longest-palindrome",
        "category": "算法（字符串）",
        "num_predict": 768,
        "grader": "exec_python",
        "grade_note": "真执行：6 组用例（含性能用例）",
        "harness": "palindrome",
        "prompt": ("实现 `def longest_palindrome(s):`，返回 s 中最长的回文子串；"
                   "若有多个等长的最长回文，返回最先出现的那个；空串返回空串。\n"
                   + CARGO % "python"),
    },
    {
        "id": "code-10-dotnet-httpclient-static",
        "category": "C#/.NET API 知识（静态判定）",
        "num_predict": 768,
        "grader": "static_tokens",
        "grade_note": "无法执行（ASP.NET Core 宿主不可离线构建）→ 仅静态判定，见 REPORT 局限节",
        "require_tokens": [["AddHttpClient"], ["IHttpClientFactory", "HttpClient 注入", "构造函数注入", "constructor injection"]],
        "prompt": ("在 ASP.NET Core（.NET 10）里，应该如何注册和使用 HttpClient？"
                   "先输出一个 ```csharp 代码块给出 Program.cs 的最小注册代码，"
                   "然后用一句话说明为什么不应该每次请求都 new HttpClient()。"),
    },
]

# --------------------------------------------------------------------------
# tool-calling tests
# --------------------------------------------------------------------------
TOOLS = [
    {"type": "function", "function": {
        "name": "get_weather",
        "description": "Get the current weather for a city",
        "parameters": {"type": "object",
                       "properties": {"city": {"type": "string", "description": "City name"},
                                      "unit": {"type": "string", "enum": ["c", "f"]}},
                       "required": ["city"]}}},
    {"type": "function", "function": {
        "name": "calculate",
        "description": "Evaluate a basic arithmetic expression",
        "parameters": {"type": "object",
                       "properties": {"expression": {"type": "string", "description": "e.g. 12*7"},
                                      "a": {"type": "number"}, "b": {"type": "number"},
                                      "op": {"type": "string", "enum": ["add", "sub", "mul", "div"]}},
                       "required": ["expression"]}}},
    {"type": "function", "function": {
        "name": "search_files",
        "description": "Search files by keyword in the workspace",
        "parameters": {"type": "object",
                       "properties": {"keyword": {"type": "string"}, "max_results": {"type": "integer"}},
                       "required": ["keyword"]}}},
]

TOOL_TESTS = [
    {"id": "tool-01-single", "num_predict": 256,
     "prompt": "北京现在的天气怎么样？请调用工具获取。",
     "expect_tool": "get_weather", "expect_arg_keys": ["city"],
     "expect_no_tool": False},
    {"id": "tool-02-pick-right", "num_predict": 256,
     "prompt": "帮我算一下 1234 * 5678 等于多少，请调用合适的工具。",
     "expect_tool": "calculate", "expect_arg_keys": [],
     "expect_no_tool": False},
    {"id": "tool-03-no-tool-needed", "num_predict": 128,
     "prompt": "用中文回答：1 加 1 等于几？直接回答，不需要调用任何工具。",
     "expect_tool": None, "expect_arg_keys": [], "expect_no_tool": True},
]

# --------------------------------------------------------------------------
# VS 2026 realistic workload: ~17K-token system prompt + 26 tools
# (SHARED-FACTS §4: the VS 2026 Agent system prompt is ~16,951 tokens)
# --------------------------------------------------------------------------
VS_TOOL_NAMES = [
    "read_file", "write_file", "list_dir", "search_code", "run_terminal", "get_diagnostics",
    "rename_symbol", "find_references", "format_document", "run_tests", "git_status", "git_diff",
    "git_commit", "create_file", "delete_file", "build_solution", "debug_attach", "set_breakpoint",
    "install_package", "list_packages", "open_in_browser", "query_database", "read_clipboard",
    "write_clipboard", "list_processes", "kill_process",
]
VS_TARGET_TOOL = "read_file"


def build_vs_tools():
    tools = []
    for name in VS_TOOL_NAMES:
        props = {"path": {"type": "string", "description": "Target path relative to the workspace root"}}
        req = ["path"]
        if name in ("search_code", "find_references"):
            props = {"query": {"type": "string"}, "path": {"type": "string"}}
            req = ["query"]
        elif name in ("run_terminal",):
            props = {"command": {"type": "string"}}
            req = ["command"]
        elif name in ("git_commit",):
            props = {"message": {"type": "string"}}
            req = ["message"]
        tools.append({"type": "function", "function": {
            "name": name,
            "description": "Visual Studio workspace tool: %s" % name.replace("_", " "),
            "parameters": {"type": "object", "properties": props, "required": req}}})
    return tools


VS_SYSTEM_PREFIX = (
    "You are an AI coding assistant embedded in Visual Studio 2026, running in Agent (Preview) mode. "
    "You help the user read, edit, build and debug .NET and C++ solutions in their workspace. "
    "Prefer calling a workspace tool over guessing file contents. Never invent file contents. "
)


def build_vs_system(reps=680):
    return VS_SYSTEM_PREFIX + build_haystack(reps) + \
        "\n\nAlways answer in the user's language. Never fabricate tool results."


VS_USER_PROMPT = "请读取 src/Program.cs 的内容并告诉我它的入口点是什么。"

# --------------------------------------------------------------------------
# Chinese tests
# --------------------------------------------------------------------------
ZH_TASKS = [
    {"id": "zh-01-math", "num_predict": 256, "grader": "zh_math",
     "prompt": "一个工程队 3 天修了 180 米路。照这个速度，修 420 米需要多少天？只回答一个数字。",
     "expect_regex": r"7"},
    {"id": "zh-02-strict-json", "num_predict": 256, "grader": "zh_json",
     "prompt": ("只输出一个 JSON 对象（不要 markdown 代码块、不要任何其他字符），"
                "包含字段：name（字符串）、version（整数）、tags（字符串数组，至少 2 个元素）。内容自定。")},
    {"id": "zh-03-constraint", "num_predict": 384, "grader": "zh_constraint",
     "prompt": ("用纯中文写一段 50 到 80 个字的说明，主题是：为什么要给本地模型固定 temperature。"
                "不得出现任何英文字母或阿拉伯数字。只输出这段说明本身。")},
    {"id": "zh-04-summary", "num_predict": 384, "grader": "zh_summary",
     "prompt": ("把下面这段话压缩成不超过 60 个字的中文摘要，必须同时包含\"本地推理\"和\"隐私\""
                "这两个词，只输出摘要：\n\n" + ZH_PASSAGE)},
    {"id": "zh-05-explain-code", "num_predict": 256, "grader": "zh_code",
     "prompt": "下面这段 Python 会打印什么？只回答打印结果本身。\n\n```python\n" + ZH_CODE + "\n```"},
]


# --------------------------------------------------------------------------
# graders
# --------------------------------------------------------------------------
def extract_code(text, lang):
    """Pull the best fenced code block out of a model answer."""
    if not text:
        return "", "empty"
    blocks = re.findall(r"```([A-Za-z0-9_+#-]*)[ \t]*\r?\n(.*?)```", text, re.S)
    if not blocks:
        return text.strip(), "no-fence-whole-text"
    norm = [b for b in blocks if (b[0] or "").lower().replace("cs", "csharp") == lang.replace("cs", "csharp")]
    if norm:
        return max(norm, key=lambda b: len(b[1]))[1].strip(), "fence-lang-match"
    return max(blocks, key=lambda b: len(b[1]))[1].strip(), "fence-largest-any-lang"


def exec_csharp(code, workdir, setup_files=None):
    ev = {"method": "exec_csharp", "compiled": False, "ran": False}
    os.makedirs(workdir, exist_ok=True)
    for name, content in (setup_files or {}).items():
        with open(os.path.join(workdir, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(content)
    src = os.path.join(workdir, "prog.cs")
    with open(src, "w", encoding="utf-8", newline="\n") as f:
        f.write(code)
    refs = [os.path.join(NETREF, d) for d in os.listdir(NETREF) if d.endswith(".dll")]
    rsp = os.path.join(workdir, "prog.rsp")
    with open(rsp, "w", encoding="utf-8", newline="\n") as f:
        f.write("-nologo\n-target:exe\n-langversion:latest\n-nullable:enable\n")
        f.write('-out:"%s"\n' % os.path.join(workdir, "prog.dll"))
        for r in refs:
            f.write('-r:"%s"\n' % r)
        f.write('"%s"\n' % src)
    t0 = time.time()
    try:
        r = subprocess.run([CSC, "@" + rsp], capture_output=True, text=True,
                           timeout=CS_COMPILE_TIMEOUT, cwd=workdir)
        ev["compile_s"] = round(time.time() - t0, 2)
        ev["compile_rc"] = r.returncode
        ev["compile_stderr"] = (r.stdout + r.stderr)[:3000]
    except Exception as e:
        ev["compile_error"] = "%s: %s" % (type(e).__name__, e)
        return ev, None
    if r.returncode != 0:
        return ev, None
    ev["compiled"] = True
    with open(os.path.join(workdir, "prog.runtimeconfig.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"runtimeOptions": {"tfm": "net10.0",
                                      "framework": {"name": "Microsoft.NETCore.App",
                                                    "version": NETRUNTIME}}}, f)
    t0 = time.time()
    try:
        r2 = subprocess.run(["dotnet", os.path.join(workdir, "prog.dll")],
                            capture_output=True, text=True, timeout=CS_RUN_TIMEOUT, cwd=workdir)
        ev["run_s"] = round(time.time() - t0, 2)
        ev["run_rc"] = r2.returncode
        ev["stdout"] = r2.stdout
        ev["stderr"] = r2.stderr[:2000]
        ev["ran"] = True
        return ev, r2.stdout
    except subprocess.TimeoutExpired:
        ev["run_error"] = "timeout after %ss" % CS_RUN_TIMEOUT
        return ev, None
    except Exception as e:
        ev["run_error"] = "%s: %s" % (type(e).__name__, e)
        return ev, None


def exec_python(code, harness, workdir):
    ev = {"method": "exec_python", "harness": harness, "ran": False}
    os.makedirs(workdir, exist_ok=True)
    body = HARNESSES[harness]
    script = code + "\n\n# ---------------- harness (added by runner) ----------------\n" + body
    path = os.path.join(workdir, "solution.py")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(script)
    t0 = time.time()
    try:
        r = subprocess.run([sys.executable, path], capture_output=True, text=True,
                           timeout=PY_EXEC_TIMEOUT, cwd=workdir)
        ev["run_s"] = round(time.time() - t0, 2)
        ev["run_rc"] = r.returncode
        ev["stdout"] = r.stdout[-4000:]
        ev["stderr"] = r.stderr[-3000:]
        ev["ran"] = True
        return ev, r.stdout
    except subprocess.TimeoutExpired:
        ev["run_error"] = "timeout after %ss" % PY_EXEC_TIMEOUT
        return ev, None
    except Exception as e:
        ev["run_error"] = "%s: %s" % (type(e).__name__, e)
        return ev, None


def exec_sqlite(query, schema=SQL_SCHEMA):
    ev = {"method": "exec_sqlite", "ran": False, "query": query}
    con = sqlite3.connect(":memory:")
    try:
        con.executescript(schema)
        cur = con.execute(query)
        rows = cur.fetchall()
        ev["rows"] = [list(r) for r in rows]
        ev["row_count"] = len(rows)
        ev["ran"] = True
        ev["sqlite_version"] = sqlite3.sqlite_version
    except Exception as e:
        ev["error"] = "%s: %s" % (type(e).__name__, e)
    finally:
        con.close()
    return ev


def static_judge(text, groups):
    """Pass requires at least one alternative from every group to appear in text."""
    detail = []
    ok = True
    for group in groups:
        hit = [t for t in group if t in text]
        detail.append({"group": group, "hits": hit})
        if not hit:
            ok = False
    return ok, detail


HARNESSES = {
"ipv4": r'''
import re as _re
FAIL = []
def _check(name, got, want):
    if got != want:
        FAIL.append("%s: got=%r want=%r" % (name, got, want))
try:
    _check("basic", extract("ip 192.168.1.1 and 10.0.0.255 ok"), ["192.168.1.1", "10.0.0.255"])
    _check("reject", extract("bad 999.1.1.1 and 01.2.3.4 and 256.256.256.256"), [])
    _check("edge", extract("edge 0.0.0.0 255.255.255.255"), ["0.0.0.0", "255.255.255.255"])
    _check("mixed", extract("v4 8.8.8.8, v6 ::1, short 1.2.3"), ["8.8.8.8"])
except Exception as e:
    import traceback; FAIL.append("EXCEPTION: " + traceback.format_exc()[-800:])
if FAIL:
    print("FAIL"); [print(" - " + f) for f in FAIL]
else:
    print("PASS")
''',

"merge": r'''
def _norm(x):
    return [list(i) for i in x]
FAIL = []
def _check(name, got, want):
    try:
        g = _norm(got)
    except Exception as e:
        FAIL.append("%s: cannot normalize %r (%s)" % (name, got, e)); return
    if g != want:
        FAIL.append("%s: got=%r want=%r" % (name, g, want))
cases = [
    ([[1,3],[2,6],[8,10],[15,18]], [[1,6],[8,10],[15,18]]),
    ([[1,4],[4,5]], [[1,5]]),
    ([[5,6],[1,2]], [[1,2],[5,6]]),
    ([], []),
    ([[1,10],[2,3]], [[1,10]]),
]
try:
    for i, (inp, want) in enumerate(cases):
        _check("case%d" % i, merge_intervals([list(x) for x in inp]), want)
except Exception:
    import traceback; FAIL.append("EXCEPTION: " + traceback.format_exc()[-800:])
if FAIL:
    print("FAIL"); [print(" - " + f) for f in FAIL]
else:
    print("PASS")
''',

"asyncio_limit": r'''
import asyncio as _aio, traceback as _tb
FAIL = []
_state = {"cur": 0, "max": 0, "calls": 0}
async def fetch(url):
    _state["cur"] += 1
    _state["max"] = max(_state["max"], _state["cur"])
    _state["calls"] += 1
    await _aio.sleep(0.05)
    _state["cur"] -= 1
    return len(url)
_urls = ["u%d" % i for i in range(12)]
try:
    got = _aio.run(fetch_all(list(_urls), 3))
    want = [len(u) for u in _urls]
    if list(got) != want:
        FAIL.append("order/content: got=%r want=%r" % (got, want))
    if _state["max"] > 3:
        FAIL.append("concurrency exceeded limit: max_in_flight=%d" % _state["max"])
    if _state["max"] < 3:
        FAIL.append("not concurrent: max_in_flight=%d (expected 3)" % _state["max"])
    if _state["calls"] != len(_urls):
        FAIL.append("fetch called %d times, expected %d" % (_state["calls"], len(_urls)))
except Exception:
    FAIL.append("EXCEPTION: " + _tb.format_exc()[-900:])
if FAIL:
    print("FAIL"); [print(" - " + f) for f in FAIL]
else:
    print("PASS  max_in_flight=%d" % _state["max"])
''',

"closure": r'''
FAIL = []
def _check(name, got, want):
    if got != want:
        FAIL.append("%s: got=%r want=%r" % (name, got, want))
try:
    fns = make_multipliers(3)
    _check("n3-len", len(fns), 3)
    for i in range(3):
        _check("n3-idx%d" % i, fns[i](10), 10 * i)
    fns5 = make_multipliers(5)
    _check("n5-len", len(fns5), 5)
    for i in range(5):
        _check("n5-idx%d" % i, fns5[i](7), 7 * i)
    _check("n0", list(make_multipliers(0)), [])
    _check("negative", list(make_multipliers(-1)), [])
except Exception:
    import traceback; FAIL.append("EXCEPTION: " + traceback.format_exc()[-900:])
if FAIL:
    print("FAIL"); [print(" - " + f) for f in FAIL]
else:
    print("PASS")
''',

"palindrome": r'''
import time as _t
FAIL = []
def _check(name, got, want):
    if got != want:
        FAIL.append("%s: got=%r want=%r" % (name, got, want))
cases = [("babad", "bab"), ("cbbd", "bb"), ("a", "a"), ("", ""),
         ("forgeeksskeegfor", "geeksskeeg"), ("abcba", "abcba"),
         ("abcdefghij" * 20, "a")]
try:
    for i, (inp, want) in enumerate(cases):
        t0 = _t.time()
        got = longest_palindrome(inp)
        dt = _t.time() - t0
        if dt > 5:
            FAIL.append("case%d too slow: %.1fs" % (i, dt))
        _check("case%d(len=%d)" % (i, len(inp)), got, want)
except Exception:
    import traceback; FAIL.append("EXCEPTION: " + traceback.format_exc()[-900:])
if FAIL:
    print("FAIL"); [print(" - " + f) for f in FAIL]
else:
    print("PASS")
''',
}


def grade_code(task, rec, workdir):
    """Returns (grade_dict)."""
    text = rec.get("text") or ""
    code, how = extract_code(text, TASK_LANG.get(task["id"], "csharp"))
    g = {"grader": task["grader"], "grade_note": task["grade_note"],
         "extract": how, "code_chars": len(code)}
    if rec.get("error"):
        g.update(passed=False, detail="生成失败：" + rec["error"])
        return g
    if not code.strip():
        g.update(passed=False, detail="未能从回答中提取到代码")
        return g

    if task["grader"] == "exec_csharp":
        ev, stdout = exec_csharp(code, workdir, task.get("setup_files"))
        g["exec"] = ev
        if not ev.get("compiled"):
            g.update(passed=False, detail="编译失败")
            return g
        if not ev.get("ran"):
            g.update(passed=False, detail="运行失败：" + str(ev.get("run_error")))
            return g
        out = (stdout or "").strip()
        if "expected_stdout" in task:
            ok = out == task["expected_stdout"]
            detail = "stdout=%r 期望=%r" % (out, task["expected_stdout"])
        else:
            lines = [l.strip() for l in out.splitlines() if l.strip()]
            pat = re.compile(r"^([A-Za-z]+)\s*[:：]\s*(\d+)$")
            parsed = []
            for l in lines:
                m = pat.match(l)
                if m:
                    parsed.append("%s:%s" % (m.group(1), m.group(2)))
            ok = parsed == task["expected_lines"]
            detail = "parsed=%r 期望=%r raw=%r" % (parsed, task["expected_lines"], out[:200])
        g.update(passed=ok, detail=detail)
        return g

    if task["grader"] == "exec_python":
        ev, stdout = exec_python(code, task["harness"], workdir)
        g["exec"] = ev
        if not ev.get("ran"):
            g.update(passed=False, detail="执行失败：" + str(ev.get("run_error")))
            return g
        g.update(passed=(stdout or "").strip().startswith("PASS"),
                 detail=(stdout or "").strip()[:600])
        return g

    if task["grader"] == "exec_sqlite":
        q = code.strip().rstrip(";").strip()
        ev = exec_sqlite(q)
        g["exec"] = ev
        if not ev["ran"]:
            g.update(passed=False, detail="SQL 执行失败：" + ev.get("error", ""))
            return g
        got = sorted([tuple(r) for r in ev["rows"]])
        exp = [tuple(r) for r in SQL_EXPECTED]
        ok = got == exp
        detail = "got=%r 期望=%r" % (got, exp)
        if not ok and sorted([r[0] for r in got]) == sorted([r[0] for r in exp]):
            detail += "（部门集合一致）"
        if not ok and sorted([r[2] for r in got]) == sorted([r[2] for r in exp]):
            detail += "（薪资多重集一致，仅并列取舍不同）"
        g.update(passed=ok, detail=detail)
        return g

    if task["grader"] == "static_tokens":
        ok, detail = static_judge(text, task["require_tokens"])
        g.update(passed=ok, detail=json.dumps(detail, ensure_ascii=False)[:800],
                 warning="静态判定，未执行")
        return g

    g.update(passed=False, detail="未知 grader")
    return g


TASK_LANG = {
    "code-01-cs-async-file": "csharp", "code-02-cs-linq": "csharp",
    "code-03-cs-bugfix-thread": "csharp", "code-04-regex-ipv4": "python",
    "code-05-sql-top2-per-dept": "sql", "code-06-python-merge-intervals": "python",
    "code-07-python-asyncio-limit": "python", "code-08-python-bugfix-closure": "python",
    "code-09-python-longest-palindrome": "python", "code-10-dotnet-httpclient-static": "csharp",
}


def grade_tools(rec, expect_tool, expect_arg_keys, expect_no_tool):
    g = {"grader": "openai_tools"}
    if rec.get("error"):
        g.update(passed=False, detail="请求失败：" + rec["error"])
        return g
    fr = rec.get("finish_reason")
    tcs = rec.get("tool_calls") or []
    g["finish_reason"] = fr
    g["tool_call_count"] = len(tcs)
    args_checks = []
    for tc in tcs:
        raw = (tc.get("function") or {}).get("arguments")
        try:
            parsed = json.loads(raw) if isinstance(raw, str) else raw
            args_checks.append({"valid_json": True, "type": type(parsed).__name__,
                                "parsed": parsed, "raw": raw})
        except Exception as e:
            args_checks.append({"valid_json": False, "error": str(e), "raw": raw})
    g["arguments_checks"] = args_checks
    names = [(tc.get("function") or {}).get("name") for tc in tcs]
    if expect_no_tool:
        ok = (not tcs) and fr == "stop"
        g.update(passed=ok, detail="finish_reason=%s tool_calls=%d（期望不调用工具且 stop）" % (fr, len(tcs)))
        return g
    ok = (expect_tool in names) and fr == "tool_calls"
    det = ["finish_reason=%s (期望 tool_calls)" % fr, "names=%r (期望含 %s)" % (names, expect_tool)]
    if ok and expect_arg_keys and args_checks and args_checks[0]["valid_json"]:
        parsed = args_checks[0]["parsed"] or {}
        missing = [k for k in expect_arg_keys if k not in parsed]
        if missing:
            ok = False
        det.append("args=%s missing=%r" % (json.dumps(parsed, ensure_ascii=False), missing))
    if any(not a["valid_json"] for a in args_checks):
        ok = False
        det.append("存在非合法 JSON 的 arguments")
    g.update(passed=ok, detail="; ".join(det))
    return g


def grade_code_zh(task, rec):
    g = {"grader": task["grader"]}
    if rec.get("error"):
        g.update(passed=False, detail="请求失败：" + rec["error"])
        return g
    text = (rec.get("text") or "").strip()
    g["answer"] = text[:600]
    kind = task["grader"]
    if kind == "zh_math":
        nums = re.findall(r"\d+(?:\.\d+)?", text)
        ok = bool(nums) and nums[0] == "7"
        g.update(passed=ok, detail="提取到的数字=%r（期望 7）" % nums[:5])
    elif kind == "zh_json":
        try:
            obj = json.loads(text)
            ok = (isinstance(obj, dict) and isinstance(obj.get("name"), str)
                  and isinstance(obj.get("version"), int)
                  and isinstance(obj.get("tags"), list) and len(obj.get("tags")) >= 2)
            g.update(passed=ok, detail="严格 JSON 解析成功，schema 校验=%s" % ok)
        except Exception as e:
            inner = extract_code(text, "json")[0]
            try:
                obj = json.loads(inner)
                ok = isinstance(obj, dict) and isinstance(obj.get("version"), int)
                g.update(passed=False, detail="包含非 JSON 字符（代码块/多余文本），严格解析失败：%s" % e)
            except Exception:
                g.update(passed=False, detail="非合法 JSON：%s" % e)
        g["strict_only"] = True
    elif kind == "zh_constraint":
        n = len(re.findall(r"[\u4e00-\u9fff]", text))
        bad = re.findall(r"[A-Za-z0-9]", text)
        ok = (50 <= n <= 80) and not bad
        g.update(passed=ok, detail="中文字数=%d（期望50-80） 违规ASCII字符=%d个 %r" % (n, len(bad), bad[:10]))
    elif kind == "zh_summary":
        n = len(re.findall(r"[\u4e00-\u9fff]", text))
        kw = [k for k in ("本地推理", "隐私") if k in text]
        ok = (n <= 60) and len(kw) == 2
        g.update(passed=ok, detail="中文字数=%d（期望<=60） 命中关键词=%r" % (n, kw))
    elif kind == "zh_code":
        norm = re.sub(r"\s+", "", text)
        ok = "0,1,1,2,3" in norm
        g.update(passed=ok, detail="归一化答案含 '0,1,1,2,3' = %s" % ok)
    return g


# --------------------------------------------------------------------------
# runner
# --------------------------------------------------------------------------
def load_results():
    if os.path.exists(RESULTS):
        try:
            with open(RESULTS, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception as e:
            log("WARN 无法解析已有 results.json (%s)，将重建" % e)
    return {"meta": {}, "models": {}}


def save_results(res):
    tmp = RESULTS + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(res, f, ensure_ascii=False, indent=1)
    os.replace(tmp, RESULTS)


RETRYABLE_MARKERS = ("timed out", "timeout", "connectionreset", "remotedisconnected",
                     "connectionaborted", "urlerror", "incompleteread", "connectionrefused")


def is_transport_error(err):
    """Transport-level failure (timeout / dropped connection) -> the attempt is INVALID
    and must be retried and excluded from statistics. A real HTTP 400 from the engine
    (e.g. exceed_context_size_error) is NOT retryable -- it is a legitimate result."""
    if not err:
        return False
    low = err.lower()
    return any(k in low for k in RETRYABLE_MARKERS)


def attach_retry_info(g, rec):
    g["invalid_attempts"] = rec.get("invalid_attempt_count", 0)
    if rec.get("retested_after_invalid"):
        g["retested_after_invalid"] = True
    return g


def do_gen(model, modelrec, test_id, label, think, attempts=2, **kw):
    """Run one generation, stash it, save incrementally.

    If the first attempt dies at the transport layer (timeout / reset), it is kept
    as an INVALID record (`<id>::invalid_attemptN`) and the item is re-measured;
    only the valid attempt feeds the statistics.
    """
    log("  gen %s ..." % test_id)
    invalid = []
    rec = None
    for i in range(attempts + 1):
        rec = gen_native(model, think=think, **kw)
        if rec.get("error") and is_transport_error(rec["error"]) and i < attempts:
            rec["invalid"] = True
            rec["invalid_reason"] = "transport error, retried: " + rec["error"]
            rec["id"] = test_id
            invalid.append(rec)
            log("    INVALID attempt %d (%s) -> 重测该项" % (i + 1, rec["error"][:100]))
            time.sleep(10)
            continue
        break
    rec["id"] = test_id
    rec["label"] = label
    if invalid:
        rec["retested_after_invalid"] = True
        rec["invalid_attempt_count"] = len(invalid)
        for j, bad in enumerate(invalid):
            modelrec["runs"]["%s::invalid_attempt%d" % (test_id, j + 1)] = bad
    if rec.get("error"):
        log("    ERROR %s (wall=%.1fs)" % (rec["error"][:120], rec.get("wall_s", -1)))
    else:
        log("    ok wall=%.1fs eval=%s tok @ %s tok/s prompt=%s tok @ %s tok/s" % (
            rec.get("wall_s"), rec.get("eval_count"), rec.get("eval_tps"),
            rec.get("prompt_eval_count"), rec.get("prompt_tps")))
    modelrec["runs"][test_id] = rec
    save_results(CURRENT)
    return rec


CURRENT = {}


def main():
    global CURRENT
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--tag", required=True)
    ap.add_argument("--only", default="", help="comma list of phase names")
    args = ap.parse_args()
    only = set(x.strip() for x in args.only.split(",") if x.strip())
    model = args.model
    tag = args.tag

    os.makedirs(TMPROOT, exist_ok=True)
    CURRENT = load_results()
    CURRENT.setdefault("meta", {})
    CURRENT["meta"].update({
        "script": "runner.py", "ollama_base": BASE,
        "temperature": TEMPERATURE, "seed": SEED,
        "think_handling": "think=false 显式传入（仅当模型具备 thinking 能力时）；A 无 thinking",
        "last_run": now_iso(),
    })
    caps, show_out = model_capabilities(model)
    CURRENT["meta"].setdefault("ollama_show", {})[model] = show_out[:1500]
    think = False if "thinking" in caps else None
    log("model=%s tag=%s capabilities=%r think=%r" % (model, tag, caps, think))

    mrec = CURRENT["models"].setdefault(tag, {})
    mrec.update({"tag": tag, "model": model, "capabilities": caps})
    mrec.setdefault("started", now_iso())          # never clobber provenance of a partial rerun
    mrec["last_run_at"] = now_iso()
    mrec.setdefault("runs", {})
    mrec.setdefault("tests", {})
    mrec.setdefault("ollama_list_at_start", ollama_list())
    # Free VRAM: unload any other resident model BEFORE measuring this one.
    if os.environ.get("BENCH_NO_FREE_VRAM") != "1":
        fr = free_other_models(model)
        mrec["freed_other_models"] = fr
        if fr.get("freed"):
            log("已卸载其他常驻模型以释放显存: %s" % json.dumps(
                [x["name"] for x in fr["freed"]], ensure_ascii=False))
    save_results(CURRENT)

    def want(phase):
        return (not only) or (phase in only)

    # ---------------- cold start ----------------
    if want("cold_start"):
        log("PHASE cold_start")
        u = unload(model)
        mrec["unload_before_cold"] = u
        save_results(CURRENT)
        if not u["unloaded"]:
            log("  WARN 未能确认卸载（%.1fs 后仍在 ps 中）" % u["waited_s"])
        rec = do_gen(model, mrec, "cold_start", "冷启动（卸载后首个请求）", think,
                     prompt="Reply with exactly: PONG", num_predict=32, timeout=1800)
        mrec["ps_after_cold"] = ollama_ps()
        save_results(CURRENT)

    # ---------------- warmup ----------------
    if want("warmup"):
        log("PHASE warmup")
        do_gen(model, mrec, "warmup", "预热请求（不计入统计）", think,
               prompt="用一句话说明什么是内存泄漏。", num_predict=64, timeout=1800)

    # ---------------- throughput short (3 reps) ----------------
    if want("throughput_short"):
        log("PHASE throughput_short")
        reps = []
        for i in range(3):
            r = do_gen(model, mrec, "throughput_short_rep%d" % i,
                       "热态短生成 tok/s（第%d次）" % (i + 1), think,
                       prompt="写一段 200 字左右的中文说明，介绍什么是向量数据库。",
                       num_predict=256, timeout=1800)
            reps.append({k: r.get(k) for k in ("wall_s", "eval_count", "eval_tps",
                                               "prompt_eval_count", "prompt_tps", "t_start", "error")})
        vals = [x["eval_tps"] for x in reps if x.get("eval_tps")]
        mrec["tests"]["throughput_short"] = {
            "reps": reps,
            "eval_tps_min": min(vals) if vals else None,
            "eval_tps_max": max(vals) if vals else None,
            "eval_tps_median": sorted(vals)[len(vals) // 2] if vals else None,
        }
        save_results(CURRENT)

    # ---------------- throughput long (streaming) ----------------
    if want("throughput_long"):
        log("PHASE throughput_long")
        rec = do_gen(model, mrec, "throughput_long", "长文持续吞吐（streaming, num_predict=1200）",
                     think, prompt=LONG_FORM_PROMPT, num_predict=1200, timeout=3600, stream=True)
        q = analyze_stream(rec)
        mrec["tests"]["throughput_long"] = {"stream_analysis": q,
            "generation": {k: rec.get(k) for k in
            ("wall_s", "eval_count", "eval_tps", "prompt_eval_count", "prompt_tps", "done_reason", "error")}}
        save_results(CURRENT)

    # ---------------- latency ----------------
    if want("latency"):
        log("PHASE latency")
        r1 = do_gen(model, mrec, "latency_short", "短 prompt 端到端延迟", think,
                    prompt="用一句话解释什么是缓存。", num_predict=96, timeout=1800)
        mrec["tests"]["latency_short"] = {"wall_s": r1.get("wall_s"),
                                          "prompt_eval_count": r1.get("prompt_eval_count"),
                                          "total_duration_s": r1.get("total_duration_s")}
        lp = build_long_prompt()
        r2 = do_gen(model, mrec, "latency_long", "~16K prompt 端到端延迟", think,
                    prompt=lp, num_predict=24, timeout=3600)
        mrec["tests"]["latency_long"] = {"wall_s": r2.get("wall_s"),
                                         "prompt_eval_count": r2.get("prompt_eval_count"),
                                         "prompt_tps": r2.get("prompt_tps"),
                                         "eval_count": r2.get("eval_count"),
                                         "total_duration_s": r2.get("total_duration_s"),
                                         "prompt_chars": len(lp)}
        save_results(CURRENT)

    # ---------------- needle ----------------
    if want("needle"):
        log("PHASE needle")
        np_ = build_needle_prompt()
        rec = do_gen(model, mrec, "needle_17k", "17K 捞针（needle 位于 50% 深度）", think,
                     prompt=np_, num_predict=64, timeout=3600)
        text = rec.get("text") or ""
        norm = re.sub(r"[\s\-_]", "", text).upper()
        hit = "ZX7741QP" in norm
        mrec["tests"]["needle"] = {
            "passed": hit,
            "answer": text.strip()[:300],
            "prompt_eval_count": rec.get("prompt_eval_count"),
            "prompt_chars": len(np_),
            "wall_s": rec.get("wall_s"),
            "prompt_tps": rec.get("prompt_tps"),
            "error": rec.get("error"),
            "invalid_attempts": rec.get("invalid_attempt_count", 0),
        }
        save_results(CURRENT)

    # ---------------- resource snapshot ----------------
    if want("resource_snapshot"):
        log("PHASE resource_snapshot")
        snap = ollama_ps()
        for row in snap["rows"]:
            row["is_100pct_gpu"] = row["processor"].strip().startswith("100%")
        mrec["resource_snapshot"] = snap
        save_results(CURRENT)
        log("  ps: %s" % json.dumps(snap["rows"], ensure_ascii=False))

    # ---------------- VS 2026 realistic workload ----------------
    if want("vs_realistic"):
        log("PHASE vs_realistic")
        vs_tools = build_vs_tools()
        vs_sys = build_vs_system()
        rec = gen_native(model, VS_USER_PROMPT, num_predict=256, think=think, tools=vs_tools,
                         system=vs_sys, timeout=3600)
        rec["id"] = "vs_realistic"
        rec["label"] = "VS 2026 真实形态：~17K system prompt + 26 tools"
        mrec["runs"]["vs_realistic"] = rec
        save_results(CURRENT)
        names = [(tc.get("function") or {}).get("name")
                 for tc in (rec.get("tool_calls_native") or rec.get("tool_calls") or [])]
        g = {"category": "VS 真实形态（长 system + 26 tools）",
             "passed": VS_TARGET_TOOL in names,
             "prompt_eval_count": rec.get("prompt_eval_count"),
             "wall_s": rec.get("wall_s"),
             "prompt_tps": rec.get("prompt_tps"),
             "tool_names": names,
             "tool_count_offered": len(vs_tools),
             "system_prompt_chars": len(vs_sys),
             "detail": "finish/done_reason=%s 调用工具=%r（期望含 %s）"
                       % (rec.get("done_reason"), names, VS_TARGET_TOOL),
             "error": rec.get("error"),
             "invalid_attempts": rec.get("invalid_attempt_count", 0)}
        mrec["tests"]["vs_realistic"] = g
        save_results(CURRENT)
        log("    -> %s | %s" % ("PASS" if g["passed"] else "FAIL", g["detail"][:200]))

    # ---------------- code ----------------
    if want("code"):
        log("PHASE code")
        for task in CODE_TASKS:
            wid = os.path.join(TMPROOT, tag, task["id"])
            rec = do_gen(model, mrec, task["id"], task["category"], think,
                         prompt=task["prompt"], num_predict=task["num_predict"], timeout=3600)
            g = grade_code(task, rec, wid)
            attach_retry_info(g, rec)
            g["category"] = task["category"]
            g["prompt"] = task["prompt"]
            mrec["tests"][task["id"]] = g
            save_results(CURRENT)
            log("    -> %s | %s" % ("PASS" if g.get("passed") else "FAIL", str(g.get("detail"))[:160]))

    # ---------------- tools ----------------
    if want("tools"):
        log("PHASE tools")
        for t in TOOL_TESTS:
            invalid = []
            for attempt in range(3):
                rec = gen_openai(model, t["prompt"], t["num_predict"], tools=TOOLS, timeout=1800)
                if rec.get("error") and is_transport_error(rec["error"]) and attempt < 2:
                    rec["invalid"] = True
                    rec["invalid_reason"] = "transport error, retried: " + rec["error"]
                    invalid.append(rec)
                    log("    INVALID attempt %d (%s) -> 重测" % (attempt + 1, rec["error"][:100]))
                    time.sleep(10)
                    continue
                break
            rec["id"] = t["id"]
            rec["prompt"] = t["prompt"]
            if invalid:
                rec["retested_after_invalid"] = True
                rec["invalid_attempt_count"] = len(invalid)
                for j, bad in enumerate(invalid):
                    bad["id"] = t["id"]
                    mrec["runs"]["%s::invalid_attempt%d" % (t["id"], j + 1)] = bad
            mrec["runs"][t["id"]] = rec
            g = grade_tools(rec, t["expect_tool"], t["expect_arg_keys"], t["expect_no_tool"])
            attach_retry_info(g, rec)
            g["category"] = "OpenAI 风格工具调用"
            g["prompt"] = t["prompt"]
            mrec["tests"][t["id"]] = g
            save_results(CURRENT)
            log("    -> %s | %s" % ("PASS" if g.get("passed") else "FAIL", str(g.get("detail"))[:200]))

    # ---------------- native tool endpoint (for the finish_reason comparison) ----------------
    if want("tool_native"):
        log("PHASE tool_native")
        t = TOOL_TESTS[1]  # same prompt/tools as tool-02, but native /api/chat
        rec = gen_native(model, t["prompt"], num_predict=t["num_predict"], think=think,
                         tools=TOOLS, timeout=1800)
        rec["id"] = "tool_native_endpoint"
        rec["prompt"] = t["prompt"]
        mrec["runs"]["tool_native_endpoint"] = rec
        tc = rec.get("tool_calls_native") or []
        names = [(x.get("function") or {}).get("name") for x in tc]
        args_types = [type((x.get("function") or {}).get("arguments")).__name__ for x in tc]
        mrec["tests"]["tool_native_endpoint"] = {
            "category": "工具调用（原生 /api/chat 对照）",
            "passed": bool(names),
            "done_reason": rec.get("done_reason"),
            "tool_names": names,
            "arguments_python_types": args_types,
            "note": "原生端点在工具调用成功时 done_reason 仍为 stop；"
                    "arguments 已是解析后的对象而非 JSON 字符串。"
                    "要按 OpenAI 语义用 finish_reason 判定，请走 /v1/chat/completions。",
            "error": rec.get("error"),
        }
        save_results(CURRENT)
        log("    -> done_reason=%s names=%r arg_types=%r"
            % (rec.get("done_reason"), names, args_types))

    # ---------------- chinese ----------------
    if want("chinese"):
        log("PHASE chinese")
        for t in ZH_TASKS:
            rec = do_gen(model, mrec, t["id"], "中文理解/指令遵循", think,
                         prompt=t["prompt"], num_predict=t["num_predict"], timeout=1800)
            g = grade_code_zh(t, rec)
            attach_retry_info(g, rec)
            g["category"] = "中文理解与指令遵循"
            g["prompt"] = t["prompt"]
            mrec["tests"][t["id"]] = g
            save_results(CURRENT)
            log("    -> %s | %s" % ("PASS" if g.get("passed") else "FAIL", str(g.get("detail"))[:160]))

    # ---------------- summary ----------------
    mrec["ps_at_end"] = ollama_ps()
    mrec["ollama_list_at_end"] = ollama_list()
    mrec["finished"] = now_iso()
    mrec["summary"] = summarize(mrec)
    save_results(CURRENT)
    log("DONE %s" % json.dumps(mrec["summary"], ensure_ascii=False))


def summarize(mrec):
    tests = mrec.get("tests", {})
    code = {k: v for k, v in tests.items() if k.startswith("code-")}
    zh = {k: v for k, v in tests.items() if k.startswith("zh-")}
    tools = {k: v for k, v in tests.items() if k.startswith("tool-")}
    def acc(d):
        if not d:
            return None
        p = sum(1 for v in d.values() if v.get("passed"))
        return {"passed": p, "total": len(d), "rate": round(p / len(d), 3)}
    return {
        "code": acc(code), "chinese": acc(zh), "tools": acc(tools),
        "needle": bool(tests.get("needle", {}).get("passed")),
        "throughput_short_eval_tps_median": tests.get("throughput_short", {}).get("eval_tps_median"),
        "throughput_long": tests.get("throughput_long", {}).get("generation", {}).get("eval_tps"),
        "cold_start_wall_s": mrec.get("runs", {}).get("cold_start", {}).get("wall_s"),
        "latency_short_wall_s": tests.get("latency_short", {}).get("wall_s"),
        "latency_long_wall_s": tests.get("latency_long", {}).get("wall_s"),
        "latency_long_prompt_tokens": tests.get("latency_long", {}).get("prompt_eval_count"),
        "vs_realistic": tests.get("vs_realistic", {}).get("passed"),
    }


if __name__ == "__main__":
    main()
