# -*- coding: utf-8 -*-
"""Self-test: run every grader with a known-good reference answer.
Proves the harness can PASS a correct solution (so a model FAIL means the model's answer, not our bug)."""
import os, sys, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import runner as R

REF = {
"code-01-cs-async-file": r'''```csharp
using System;
using System.IO;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        int n = 0;
        foreach (var line in await File.ReadAllLinesAsync("data.txt"))
            if (line.Contains("ERROR")) n++;
        Console.WriteLine(n);
    }
}
```''',

"code-02-cs-linq": r'''```csharp
using System;
using System.Linq;

class Program
{
    static void Main()
    {
        var orders = new[] { ("Alice",120),("Bob",80),("Alice",45),("Carol",300),("Bob",20),("Dave",10),("Carol",5) };
        var top = orders.GroupBy(o => o.Item1).Select(g => (Name: g.Key, Total: g.Sum(o => o.Item2)))
                        .OrderByDescending(x => x.Total).Take(3);
        foreach (var t in top) Console.WriteLine($"{t.Name}:{t.Total}");
    }
}
```''',

"code-03-cs-bugfix-thread": r'''```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

class Counter
{
    private int _count;
    public void Increment() { Interlocked.Increment(ref _count); }
    public int Count => Volatile.Read(ref _count);
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
        Console.WriteLine(c.Count);
    }
}
```''',

"code-04-regex-ipv4": r'''```python
import re
_OCT = r'(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)'
_PAT = re.compile(r'(?<![\d.])' + r'(?:' + _OCT + r'\.){3}' + _OCT + r'(?![\d.])')

def extract(text):
    return _PAT.findall(text)
```''',

"code-05-sql-top2-per-dept": r'''```sql
SELECT dept, name, salary FROM (
  SELECT dept, name, salary,
         ROW_NUMBER() OVER (PARTITION BY dept ORDER BY salary DESC, name ASC) AS rn
  FROM employees) t
WHERE rn <= 2
ORDER BY dept ASC, salary DESC
```''',

"code-06-python-merge-intervals": r'''```python
def merge_intervals(intervals):
    out = []
    for s, e in sorted([list(i) for i in intervals]):
        if out and s <= out[-1][1]:
            out[-1][1] = max(out[-1][1], e)
        else:
            out.append([s, e])
    return out
```''',

"code-07-python-asyncio-limit": r'''```python
import asyncio

async def fetch_all(urls, limit):
    sem = asyncio.Semaphore(limit)
    async def one(u):
        async with sem:
            return await fetch(u)
    return await asyncio.gather(*(one(u) for u in urls))
```''',

"code-08-python-bugfix-closure": r'''```python
def make_multipliers(n):
    return [lambda x, i=i: x * i for i in range(n)]
```''',

"code-09-python-longest-palindrome": r'''```python
def longest_palindrome(s):
    if not s:
        return ""
    best_l, best_r = 0, 0
    def expand(l, r):
        while l >= 0 and r < len(s) and s[l] == s[r]:
            l -= 1; r += 1
        return l + 1, r - 1
    for i in range(len(s)):
        for l, r in (expand(i, i), expand(i, i + 1)):
            if (r - l) > (best_r - best_l):
                best_l, best_r = l, r
    return s[best_l:best_r + 1]
```''',

"code-10-dotnet-httpclient-static": r'''```csharp
builder.Services.AddHttpClient<IMyService, MyService>(c =>
{
    c.BaseAddress = new Uri("https://api.example.com/");
    c.Timeout = TimeSpan.FromSeconds(10);
});
```
每次都 new HttpClient() 会导致 TIME_WAIT 套接字耗尽（socket exhaustion），并且无法感知 DNS 变更，因此应该用 IHttpClientFactory 管理生命周期。''',
}

fails = []
for t in R.CODE_TASKS:
    tid = t["id"]
    rec = {"text": REF[tid], "error": None}
    wid = os.path.join(R.TMPROOT, "selftest", tid)
    g = R.grade_code(t, rec, wid)
    status = "PASS" if g.get("passed") else "FAIL"
    print("%-38s %s  %s" % (tid, status, str(g.get("detail"))[:150]))
    if not g.get("passed"):
        fails.append(tid)

# tool grading self-test
good_tool = {"finish_reason": "tool_calls", "tool_calls": [
    {"function": {"name": "get_weather", "arguments": '{"city":"Beijing","unit":"c"}'}}], "error": None}
g = R.grade_tools(good_tool, "get_weather", ["city"], False)
print("%-38s %s  %s" % ("tool-ref-should-PASS", "PASS" if g["passed"] else "FAIL", g["detail"][:120]))
if not g["passed"]:
    fails.append("tool-ref")

bad_no_tool = {"finish_reason": "tool_calls", "tool_calls": [
    {"function": {"name": "get_weather", "arguments": '{"city":"Beijing"}'}}], "error": None}
g = R.grade_tools(bad_no_tool, None, [], True)
print("%-38s %s (期望 FAIL)" % ("tool-spurious-should-FAIL", "PASS" if g["passed"] else "FAIL"))
if g["passed"]:
    fails.append("tool-negative")

bad_json = {"finish_reason": "tool_calls", "tool_calls": [
    {"function": {"name": "calculate", "arguments": '{bad json'}}], "error": None}
g = R.grade_tools(bad_json, "calculate", [], False)
print("%-38s %s (期望 FAIL)" % ("tool-badjson-should-FAIL", "PASS" if g["passed"] else "FAIL"))
if g["passed"]:
    fails.append("tool-badjson")

# chinese grading self-test
for t in R.ZH_TASKS:
    sample = {"zh-01-math": "7", "zh-02-strict-json": '{"name":"bench","version":2,"tags":["a","b"]}',
              "zh-03-constraint": "本地模型每次运行都要保持一致，否则同一份提示词会得到不同答案，难以比较优劣，也难以复现问题，所以需要固定采样参数。" ,
              "zh-04-summary": "本地推理把模型放在本机运行，核心优势是隐私与可控成本。",
              "zh-05-explain-code": "[0, 1, 1, 2, 3]"}[t["id"]]
    g = R.grade_code_zh(t, {"text": sample, "error": None})
    print("%-38s %s  %s" % (t["id"] + " (ref)", "PASS" if g["passed"] else "FAIL", str(g.get("detail"))[:130]))
    if not g["passed"]:
        fails.append(t["id"] + "-ref")

print("\nSELFTEST:", "ALL PASS" if not fails else "FAILURES: %r" % fails)
print("tmp dirs under:", R.TMPROOT)
