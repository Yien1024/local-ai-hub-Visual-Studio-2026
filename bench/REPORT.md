# 基准测试报告：qwen3-coder:30b（A） vs smtek/Qwen3.8-27B:Q4_K_M（B）

- 报告时间：2026-10-10 21:4x（+08:00）
- 执行者：bench-runner
- 脚本：`vs-local-ai/bench/runner.py`（纯标准库，参数化模型名）
- 原始数据：`vs-local-ai/bench/results.json`（每条任务的完整原始输出与 API 计量字段）
- 硬件与软件：见 `vs-local-ai/docs/SHARED-FACTS.md`（本报告不重复探测）
- **当前状态（定稿版，2026-10-10 22:0x）：模型 A 全部维度已完成并通过复核；模型 B = 未测** —— 下载卡在 **91.6%**（16,314.29 MB / ~17.4 GB），原因是网络中断（21:41 已恢复并断点续传，预计还需 30–60 分钟，详见 §7.1）。
- **"值不值得换"的当前建议见 §6.1：不换**（理由全部来自 A 的已测事实）；**涉及"A 与 B 谁更强"的任何结论一律标记为"待 B"**，请勿混用（§6.5 专门区分了"可以单独采信"与"必须等 B"的两类结论）。
- 任务核心问题"Qwen3.8-27B 是否比 qwen3-coder:30b 强、强在哪"**目前无数据可答**，报告中所有关于 B 的表述均为预登记的待验证假设（§6.4）。

---

## 0. TL;DR（模型 A 的事实基线）

| 维度 | 实测值（模型 A = qwen3-coder:30b） |
|---|---|
| 热态生成 | **68.10 tok/s**（中位数，3 次：67.35 / 68.10 / 68.34） |
| 长文持续生成（736 tok） | **63.87 tok/s**，首段 68.06 → 末段 61.43 tok/s（**衰减 9.7%**） |
| 冷启动（卸载后首个请求） | **14.98 s** 墙钟，其中 `load_duration` = **14.77 s** |
| 短 prompt 端到端 | **0.741 s**（15 prompt tok → 37 tok 输出） |
| ~16K prompt 端到端 | **51.89 s**（实际 15,975 prompt tok，prompt 处理 308.4 tok/s） |
| 17K 捞针 | **精确命中**（16,870 prompt tok，答案就是 `ZX-7741-QP`，43.31 s） |
| VS 真实形态（18.5K system + 26 tools） | **通过**：正确调用 `read_file`，arguments = `{"path":"src/Program.cs"}`，78.78 s |
| 代码能力 | **9/10 自动判定通过**（9 道真编译/真执行；第 10 道静态判定 FAIL，人工复核为"实质正确"，见 §5.4） |
| 工具调用（OpenAI 风格） | **3/3**：`finish_reason` 符合预期，`arguments` 全部为合法 JSON |
| 中文理解与指令遵循 | **4/5**（失败项：字数与"禁英文字母"双重违约） |
| 资源占用 | **SIZE 20 GB / PROCESSOR 100% GPU / CONTEXT 65536**（RUNNER llamacpp，UNTL Forever） |

所有数字均可按 §8 的证据索引在 `results.json` 中逐条复核。

---

## 1. 测试方法（可复现）

### 1.1 固定参数（两个模型完全相同）

| 参数 | 值 | 传参位置 |
|---|---|---|
| temperature | **0.7** | `/api/chat` → `options.temperature`；`/v1/chat/completions` → 顶层 `temperature` |
| seed | **42** | 同上（额外固定，便于复现；两模型一致） |
| think | 仅当模型自身 capability 含 `thinking` 时显式传 `think=false` | 避免思维链把 tok/s 与代码判定带偏；A 无 thinking 能力（`ollama show` 的 Capabilities 只有 `tools`），实测传 `think=true` 返回 HTTP 400 |
| num_predict / max_tokens | 逐题显式指定（记录在 results.json 每题的 `request_body`） | 防止跑飞 |
| 顺序 | 冷启动 → warmup → 吞吐 → 延迟 → 捞针 → 资源快照 → VS 真实形态 → 代码 → 工具 → 中文 | 两模型同一顺序 |
| 预热 | 冷启动测试后紧跟一次 warmup 请求（不计入统计） | — |

复现命令：

```powershell
cd C:\Users\zx\Documents\vs-local-ai\bench
python runner.py --model qwen3-coder:30b            --tag A
python runner.py --model smtek/Qwen3.8-27B:Q4_K_M   --tag B
python -m json.tool results.json > $null   # 校验可解析
```

### 1.2 判定方式（诚实说明哪些是真验证、哪些是静态判定）

| 判定方式 | 用在哪 | 强度 |
|---|---|---|
| `exec_csharp` | 代码题 1/2/3 | **真编译 + 真运行**：用 VS 2026 自带 Roslyn `csc.exe` + .NET 10.0.12 引用包离线编译成 dll，再用 `dotnet` 执行，比对 stdout。编译/运行耗时与 stderr 全部存进 `tests.<id>.exec` |
| `exec_python` | 代码题 4/6/7/8/9 | **真执行**：模型代码 + 注入式测试 harness 拼成一个脚本，子进程执行，断言全部通过才 PASS |
| `exec_sqlite` | 代码题 5 | **真执行**：在内存 SQLite 里建表灌数据，跑模型给的 SQL，结果集与期望集合逐行比对（`sqlite_version` 记录在案） |
| `static_tokens` | 代码题 10 | **仅静态关键词判定，未执行**（ASP.NET Core 宿主无法离线构建）。这类判定的局限见 §5.4 与 §7 |
| 客观规则 | 中文 5 题、捞针、工具调用 | 数字精确匹配 / 严格 JSON 解析 / 中文字数统计 / `finish_reason` 与 `json.loads(arguments)` / 捞针答案精确匹配 |

**harness 自身先经过自检**：用 10 份人工参考解 + 3 个反例（幻觉调用工具、非法 JSON arguments、无需工具却调用）跑过一遍，确认"正确答案必 PASS、错误答案必 FAIL"。自检过程中发现并修正了**一处我方测试用例的错误**（原用例把 `"ab"*100` 的最长回文预期写成 `"aba"`，实际 `(ab)^99a` 才是正确答案），修正后全部通过。自检脚本：`_selftest.py`。

### 1.3 一个必须记录的坑（不是 Ollama 的问题）

本机注册表开了系统代理 `ProxyEnable=1 → 127.0.0.1:26561`。Python 的 `urllib.request.getproxies()` 会读注册表拿到该代理，且**不豁免 loopback**，导致访问 `http://127.0.0.1:11434/api/*` 全部返回 **HTTP 404**（伪装得极像"路径写错"）。`ollama` CLI 用 Go，默认不代理 localhost，所以 `ollama list` 一切正常 —— 两者行为不一致。`runner.py` 用 `build_opener(ProxyHandler({}))` 绕过。任何后续直连 Ollama 的脚本若遇到莫名 404，先查这一点。

**网络路径确认（应 Lead 要求）**：**A 和 B 走的是完全相同的网络路径**，没有中间代理、没有走 11435 的 ollama-proxy：

- `runner.py` 里 `BASE = "http://127.0.0.1:11434"`，且所有请求经 `OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))` 发出 → **直连 Ollama 原生端口，显式禁用一切代理**；
- 证据一：`getproxies()` 实测返回 `{'http': 'http://127.0.0.1:26561', ...}`，被 `ProxyHandler({})` 覆盖为直连；
- 证据二：绕过代理后 `GET /api/version` 返回 `{"version":"0.40.0"}`；
- 证据三：Ollama 自己的访问日志里，我发出的每一条测试请求来源都是 `127.0.0.1`（`[GIN] ... | 127.0.0.1 | POST "/api/chat"`），说明请求是直连进入 Ollama 进程的，没有经过第二个 HTTP 跳转。这意味着**不存在"代理转发开销"这一项干扰变量**，A/B 的网络路径可比。
- 唯一的方法学差别不在网络路径，而在**测试窗口的系统状态**（B 可能仍在下载或刚下完），见 §7.1。

---

## 2. 吞吐（模型 A）

| 指标 | 值 | 证据（results.json 路径） |
|---|---|---|
| 热态短生成 3 次 | 67.35 / 68.10 / 68.34 tok/s，**中位数 68.10** | `models.A.tests.throughput_short.reps[*].eval_tps` |
| 长文持续（num_predict=1200，实际生成 736 tok，11.725 s） | **63.87 tok/s** | `models.A.runs.throughput_long.eval_tps` |
| 分段吞吐（估算，按 chunk 到达时间切片） | 首段 **68.06** → 末段 **61.43** tok/s，**衰减 9.7%** | `models.A.tests.throughput_long.stream_analysis.est_tps_by_time_quartile` |
| 冷启动 | 墙钟 **14.979 s**，`load_duration` **14.77 s**，`total_duration` 14.98 s | `models.A.runs.cold_start` |
| 冷启动后立即第二次 | 0.699 s（warmup） | `models.A.runs.warmup.wall_s` |

**关于分段吞吐的方法说明（重要）**：Ollama 0.40 的 streaming 响应**不给中间 chunk 累计 `eval_count`**（实测 732 个 chunk 中只有最后 1 个带该字段），所以无法用官方计量做滑动窗口。改用"chunk 到达时间 × 每 chunk 约 1 token"估算（732 chunk / 736 token），该估算公式与假设已写入 `stream_analysis.assumption`。**它是估算值，不是官方计量值**，但两个模型用同一方法，可比。

**与历史基线对照**（SHARED-FACTS §5）：历史热态 65–70 tok/s → 本次 68.10，一致；历史冷启动 10–12 s → 本次 14.98 s，偏高约 25–50%（可能与本机当时另有 16 GB partial blob 写入、以及历史测量条件不同有关，见 §7）。

---

## 3. 延迟与上下文（模型 A）

| 场景 | prompt tok | 墙钟 | prompt 处理速率 | 输出 tok |
|---|---|---|---|---|
| 短 prompt | 15 | **0.741 s** | 103.45 tok/s（样本太小，无意义） | 37 |
| ~16K prompt | 15,975 | **51.894 s**（其中 prompt_eval 51.799 s） | **308.4 tok/s** | 2 |
| 17K 捞针 | 16,870 | **43.313 s** | **394.1 tok/s** | 9 |
| VS 真实形态 | 18,549（+26 tools） | **78.78 s** | **273.5 tok/s** | 38 |

证据：`models.A.runs.latency_short` / `latency_long` / `needle_17k` / `vs_realistic`。

**上下文能力**：17K 捞针**精确命中** —— 针（`Project Halcyon 的金库访问码是 ZX-7741-QP`）埋在整个 haystack 的 **50% 深度**，问题在末尾，模型回答原文就是 `ZX-7741-QP`（`models.A.tests.needle.answer`），没有幻觉、没有代称、没有多余解释。

**与真实 VS 场景的对齐**：SHARED-FACTS §4 记录 VS 2026 Agent 模式的系统提示约 **16,951 token**。所以上表第 2、4 行不是"人造压力测试"，而是**每发一条消息都要付的成本**：A 在 16–18.5K prompt 下每轮要先花 **52–79 秒**做 prompt 处理。

---

## 4. 资源占用（模型 A）

`ollama ps` 原文（`models.A.resource_snapshot.raw`）：

```
NAME               ID              SIZE      PROCESSOR    CONTEXT    RUNNER      UNTIL
qwen3-coder:30b    cc7437df24ee    20 GB     100% GPU     65536      llamacpp    Forever
qwen3:4b-local     a6ceb965ae79    2.9 GB    100% GPU     4096       llamacpp    Forever
```

- **PROCESSOR = 100% GPU** ✅（符合任务要求，无 CPU 混跑）
- SIZE 20 GB（模型文件 17 GB + 65536 上下文 q8_0 KV cache 等）
- CONTEXT 65536（来自 `OLLAMA_CONTEXT_LENGTH=65536`）
- UNTIL = Forever（`OLLAMA_KEEP_ALIVE=-1`），所以测完仍常驻显存 —— 这也意味着**测 B 之前需要留意 A 是否仍占着 20 GB 显存**（我在 B 测试前会显式卸载 A，见 §7 的说明）。

---

## 5. 能力维度明细（模型 A）

### 5.1 代码：9/10 通过，其中 9 道为真编译/真执行

| # | 题目 | 类别 | 判定 | 结果 | 耗时 | 输出 tok |
|---|---|---|---|---|---|---|
| 1 | 异步读文件统计 ERROR 行数 | C#/.NET 语法与 API + async IO | csc 真编译 + `dotnet` 真运行，比对 stdout | **PASS** stdout=`3` | 2.31 s | 111 |
| 2 | LINQ 汇总订单取前 3 | C#/.NET LINQ | 真编译真运行，逐行比对 | **PASS** `Carol:305 / Alice:165 / Bob:100` | 3.12 s | 186 |
| 3 | 修复多线程计数器（应恒为 400000） | bug 修复（C# 并发） | 真编译真运行 | **PASS** stdout=`400000`（用了 `Interlocked.Increment`） | 2.82 s | 157 |
| 4 | 合法 IPv4 提取（拒绝 `01.2.3.4`/`999.x`） | 正则 | 真执行 3 组用例 | **PASS** 全过 | 4.02 s | 245 |
| 5 | 每部门薪资前 2（含并列按 name） | SQL | 内存 SQLite 真执行，结果集比对 | **PASS** 与期望集合完全一致 | 1.22 s | 62 |
| 6 | 区间合并（含首尾相接） | Python 算法 | 真执行 5 组 | **PASS** 全过 | 2.37 s | 140 |
| 7 | asyncio 限流并发（顺序保序） | 并发/async | 真执行，注入假 fetch 统计**真实并发峰值** | **PASS** `max_in_flight=3`，结果保序 | 1.40 s | 74 |
| 8 | 修复闭包延迟绑定 | bug 修复（Python） | 真执行 n=3/5/0/-1 | **PASS** 全过 | 0.72 s | 29 |
| 9 | 最长回文子串（等长取最先出现） | 算法 | 真执行 7 组 | **PASS** 全过 | 2.76 s | 171 |
| 10 | `AddHttpClient` 注册 + 解释为何不 new | C#/.NET API 知识 | **静态关键词（未执行）** | **FAIL（自动）/ 实质正确（人工，见 5.4）** | 1.44 s | 83 |

证据：`models.A.tests.code-*`（含 `exec.compile_s`、`exec.run_s`、`exec.stdout`、`exec.stderr`）与 `models.A.runs.code-*`（完整原始回答）。

### 5.2 工具调用（OpenAI 风格 `/v1/chat/completions`）：3/3

| 用例 | finish_reason | 调用 | arguments 是否合法 JSON | 结论 |
|---|---|---|---|---|
| 单工具（北京天气，3 选 1） | `tool_calls` ✅ | `get_weather` ✅ | ✅ `{"city":"北京","unit":"c"}` | PASS |
| 需选对工具（1234×5678） | `tool_calls` ✅ | `calculate` ✅ | ✅ | PASS |
| 不该调工具（1+1，已声明 26/3 个工具在册） | `stop` ✅ | **0 次调用** ✅ | — | PASS（无幻觉调用） |

证据：`models.A.tests.tool-*`（`finish_reason`、`tool_call_count`、`arguments_checks[0].valid_json/parsed`）与 `models.A.runs.tool-*`。

补充（原生 `/api/chat` 端点对照，证据：`models.A.tests.tool_native_endpoint` 与 `models.A.runs.tool_native_endpoint`）：同一个 prompt 走原生端点时，工具确实被调用了（`calculate`），但 **`done_reason` 仍然是 `stop`**，且 `arguments` 已经是解析好的 Python `dict`（`arguments_python_types = ['dict']`），而 OpenAI 兼容端点给的是 JSON **字符串**、`finish_reason` 才是 `tool_calls`。**要用 `finish_reason` 做判据，必须走 `/v1/chat/completions`。**

### 5.3 中文理解与指令遵循：4/5

| # | 题目 | 客观判据 | 结果 |
|---|---|---|---|
| 1 | 数学应用题（180 米/3 天 → 420 米几天） | 只答数字且为 7 | **PASS** 回答 `7` |
| 2 | 严格只输出 JSON（name/version/tags） | 原文能被 `json.loads` 解析且 schema 正确 | **PASS** 无代码块、无多余字符 |
| 3 | 50–80 字纯中文，禁英文字母与数字 | 中文字数 ∈[50,80] 且 ASCII 字母/数字 = 0 | **FAIL**：中文字数 **101**（超限），且出现 **33 个 ASCII 字母**（把 `temperature` 原样写成拉丁字母） |
| 4 | 摘要 ≤60 字且必含"本地推理""隐私" | 中文字数 ≤60 且两个关键词都命中 | **PASS** 42 字，两词均命中 |
| 5 | 读 Python 代码说输出 | 归一化后含 `0,1,1,2,3` | **PASS** |

证据：`models.A.tests.zh-*` 与 `models.A.runs.zh-*`（原始回答全文）。

### 5.4 必须点名的判定分歧：第 10 题

自动判定 **FAIL**，但人工复核认为该回答**实质正确**：

- 它输出了 `builder.Services.AddHttpClient();`（这就是 `IHttpClientFactory` 的注册入口，等价写法），**并通过**了"必须含 `AddHttpClient`"这一组；
- 它未通过第二组关键词（要求出现 `IHttpClientFactory` / "构造函数注入" 之一）。它给的理由是"会导致 TCP 连接泄漏和端口耗尽，HttpClient 实例应该被复用"——**方向正确**（socket 耗尽），但没提到"DNS 变更不刷新"这个更精确的点，也没有显式使用/命名 `IHttpClientFactory`。

**所以：这道题的严格判据（关键词）比"人手评分"更严。** 我在代码正确率上同时给出两个数：
- **严格（预注册判据、自动）：9/10 = 90%**
- **放宽（人工复核）：10/10 = 100%**（本题不给满分理由：未提 DNS，属细节缺失）

这个分歧也是本报告"最不可靠的一处"的候选之一（另一个是长 prompt 延迟的方差，见 §7）。

---

## 6. "换 / 不换"建议

### 6.1 明确建议：**在现有证据下，不换**（继续用 `qwen3-coder:30b`）

理由（全部基于本报告已实测的事实，不依赖 B）：

1. **"不换"的代价是可量化的、而且很小**：A 已经被证明能在这台机器上以 **100% GPU** 跑通 VS 2026 真实形态（18.5K system prompt + 26 tools 下正确调用 `read_file`），代码题真编译真执行 **9/10**，工具调用 **3/3**，17K 捞针**精确命中**。也就是说，**当前方案没有已知的硬伤**。
2. **"换"的收益完全没有证据**：B 一次都没测成（§7.1）。在没有 B 的任何数字之前换模型，等于用一个**未经验证的未知**替换一个**已验证可用**的方案。
3. **"换"的风险是具体且已识别的**：B 是 **27.3B dense**（A 是 30.5B MoE、仅 3B 激活），理论吞吐差距可达数倍；且 27B Q4_K_M（~17 GB）+ 65536 q8_0 KV cache 在 **24 GB** 卡上很可能掉出 100% GPU（§6.3.1）。**一旦混合 CPU，交互延迟劣化到不可用，这是硬失败，不是"慢一点"。**
4. **换模型不会改善最痛的那一环**：VS 每轮对话要先处理 ~16,951 token 的系统提示（实测 A 需 52–79 s）。这项成本由 VS 的提示词规模决定，**换任何模型都还在**；B 若 prompt 处理不显著更快，换了也白换。

**这个建议的推翻条件（预先登记，避免事后找理由）**：B 实测同时满足 §6.3 的**全部 7 条判据**——尤其是第 6 条（`ollama ps` 显示 **100% GPU，无 CPU 混合**）与第 5 条（生成 ≥ ~48 tok/s）、并且代码/中文通过率确实优于 A——则建议改为**换（至少代码场景换）**。B 数据一到，以实测为准覆盖本节。

> **诚实标注**：本节是"基于当前证据的决策建议"，不是"B 更差的结论"。B 完全可能更强——只是**现在没有任何数据支持这句话**。§6.4 的预期与本节的区别在于：本节只依赖 A 的已测事实，§6.4 是待验证假设。

### 6.2 当前可以确定的（A 侧结论，可独立成立）

1. **A 在"VS 2026 真实形态"下是能用的，而且工具调用这块很干净**：18.5K prompt + 26 tools 下正确选中 `read_file` 且参数精确（`{"path":"src/Program.cs"}`），3 个工具用例 3/3，零幻觉调用，arguments 全为合法 JSON。这直接回答了 SHARED-FACTS §4 那个现实问题——本地模型接进 VS 的 Agent 模式，工具协议这一层 A 不拖后腿。
2. **A 的瓶颈不是生成，而是每轮的 prompt 处理**：生成 68 tok/s 很快，但每发一条消息要先付 16–18.5K token 的系统提示，实测 **52–79 秒**才开始出字。换任何模型，只要 VS 还是塞 17K 系统提示，这个成本都在——**B 若 prompt 处理不显著更快，换成 B 不会改善体感**。
3. **A 的 17K 捞针是精确命中的**，说明它在这个上下文长度上没有明显的"中间遗忘"。

### 6.3 B 必须具备什么，才值得换（预先登记的判据，避免事后找理由）

| # | 判据 | 门槛 | 数据来源 |
|---|---|---|---|
| 1 | 代码真执行通过率 | **≥ 10/10**（严格判据）优于 A 的 9/10 | 同一 harness |
| 2 | 中文指令遵循 | **5/5** 优于 A 的 4/5 | 同一判据 |
| 3 | 17K 捞针 | 必须命中（与 A 持平） | 同 |
| 4 | 工具调用 | 3/3 + VS 真实形态必须通过 | 同 |
| 5 | 生成吞吐 | 不低于 A 的 **0.7 倍**（即 ≥ ~48 tok/s），否则交互体验明显变差 | 同 |
| 6 | **PROCESSOR 必须 100% GPU 且不触发 CPU 混跑** | 硬门槛：一旦混合，延迟会劣化到不可用 | `ollama ps` |
| 7 | 长 prompt 每轮成本 | ≤ A 的 52–79 s（同量级即可） | 同 |

只要第 6 条不满足（27B Q4_K_M + 65536 KV 在 24 GB 卡上很可能吃紧），**结论就直接是"不换"**——哪怕它代码更强，因为 VS 里每一次对话都要等。

### 6.3.1 预先登记的 B 侧故障排查协议（避免把显存问题误当"模型慢"）

应 Lead 要求，写死一条判读顺序，**先排除硬件装载问题，再谈模型快慢**：

1. **B 一加载完，立刻看 `ollama ps` 的 PROCESSOR 列**（`runner.py` 已把每次快照写进 `models.B.resource_snapshot.rows[].processor` 与 `.is_100pct_gpu`）。若不是 `100% GPU`，而是 `xx%/yy% CPU/GPU` 之类的混合，则**该次全部吞吐/延迟数字标记为"受 CPU 卸载污染"，不作为模型能力评价依据**，只作为"此配置在 24 GB 卡上装不下"的证据。
2. **测试开始前必须先卸载 A**：A 常驻 20 GB（`OLLAMA_KEEP_ALIVE=-1`）。若不卸载，B 必然被挤到 CPU，得出的会是**假的**"B 装不下"结论。`runner.py` 已在每次运行开始时自动卸载所有非目标模型并记录到 `models.<tag>.freed_other_models`（本次 A 的记录：卸载了 `qwen3:4b-local`；B 的运行会记录卸载 A 的过程）。
3. 若 `ollama ps` 显示 100% GPU 但 **tok/s 仍 < 40**，再按顺序怀疑：(a) 生成被 `num_predict` 之外的额外开销拖累（看 `wall_s` 与 `total_duration_s` 的差）；(b) 该 dense 27B 模型本身的计算量（27.3B 全激活 vs A 的 3B 激活，理论吞吐差距可达数倍，这属于真实结论而非噪音）；(c) 下载/磁盘 IO 干扰（记录时间点，见 §7.1）。
4. **超时策略（应 Lead 要求）**：`runner.py` 现在把传输层超时/连接重置的请求判为 **INVALID**，单独存成 `<test_id>::invalid_attemptN`（带 `invalid: true` 与 `invalid_reason`），自动重测该题，**只有有效的那次进入统计**；测试记录里会带 `invalid_attempts` 与 `retested_after_invalid` 字段。注意：Ollama 引擎自己返回的 HTTP 400（例如 `exceed_context_size_error`）**不算 INVALID**，那是真实结果，如实计入。

### 6.4 我的倾向（在 B 数据出来之前的可证伪假设，不是结论）

按 5.5 节的判据，我的**预期**是：B（27.3B dense）的代码/中文质量大概率 ≥ A，但**吞吐会明显低于 A**（dense 27B 对 30B-MoE-3B激活 是数量级差异），并且**很可能在 24 GB 显存上掉出 100% GPU**。若两条都成立，建议是：**代码质量用 B、日常交互用 A（不换）**，除非 B 的实测吞吐意外地 ≥ 48 tok/s 且全程 100% GPU。**以上是待验证假设，B 测完我会用实测数字替换本节。**

### 6.5 A 的结论可以单独成立吗？（应 Lead 要求单列，防止误用）

**可以。但要区分两类结论，混用就会出错。**

#### 6.5.1 可以单独采信的（A 的内部自洽结论，不依赖 B）

这些结论只描述"模型 A 在这台机器上表现如何"，逻辑上不需要 B 就能成立，且每条都能在 `results.json` 里找到原始记录（§8 证据索引）：

| 结论 | 为什么可以单独成立 |
|---|---|
| 热态生成 **68.10 tok/s**、长文 **63.87 tok/s**、衰减 **9.7%** | 纯自测指标，不含任何比较 |
| 冷启动 **14.98 s**（`load_duration` 14.77 s） | 同上 |
| 短 prompt **0.741 s**；~16K prompt **51.89 s**；17K 捞针 **43.31 s**；VS 真实形态 **78.78 s** | 同上（但方差见 §7.2，只当量级） |
| 17K 捞针**精确命中** | 判定是"答案字符串是否精确匹配"，与 B 无关 |
| 代码 **9/10**（9 道真编译/真执行） | 判定是"能否编译、能否运行、断言是否通过"，客观且与 B 无关 |
| 工具调用 **3/3** + `finish_reason`/`arguments` 合法 | 同上 |
| VS 真实形态（18.5K + 26 tools）正确调用 `read_file` | 同上 |
| 中文 **4/5**（客观判据） | 同上 |
| `ollama ps`：**100% GPU / 20 GB / CONTEXT 65536** | 硬件事实 |

**一个内部一致性检查，用于支持"这些数字不是噪声"**：三次独立的热态测量为 67.35 / 68.10 / 68.34 tok/s，**离散度 < 1.5%**，且与 SHARED-FACTS §5 的历史基线 65–70 tok/s 吻合。说明裸 Ollama 的短生成吞吐测量在这台机器上是**稳定可复现**的 —— 因此 A 的吞吐基线可信（长 prompt 那几项例外，见 §7.2）。

#### 6.5.2 不能单独成立的（必须等 B）

| 结论 | 为什么现在不能说 |
|---|---|
| "Qwen3.8-27B 比 qwen3-coder:30b 强 / 弱" | B 零数据 |
| "强在哪" | 同上 |
| **"值不值得换"** | 同上，且关键取决于 B 的 PROCESSOR 是否 100% GPU（§6.3.1） |
| "A 的 68 tok/s 算快还是慢" | 单机单模型，没有可比对象 |
| "A 的代码正确率约 90%"（作为总体估计） | 只有 10 道题，单题权重 10%，样本量不足以支持总体比例的外推；**只能说"这 10 道题里 9 道通过"** |
| "B 的 vision / MTP 有没有用" | 完全未测 |

#### 6.5.3 读者最可能误用的两个地方（提前标注）

1. **§6.2 第 1 条**说"A 在 VS 真实形态下可用"，这是**A 自身**的结论；它不蕴含"A 比 B 更适合 VS"。
2. **§6.4 的倾向性预期**（B 质量更高但更慢、可能掉出 GPU）**只是假设**，写在报告里是为了预先登记判据、防止事后找理由，**不是发现**。任何引用都应标注"待验证"。

---

## 7. 本次测试的局限与不可外推之处

### 7.1 模型 B 完全未测（最重要的局限）

B（`smtek/Qwen3.8-27B:Q4_K_M`）在本次报告成稿时**尚未拉取完成**，因此 B 的所有维度（吞吐/延迟/代码/工具/中文/捞针/资源）状态均为 **未测**。

拉取卡住有 Ollama 自己的日志为证（`%LOCALAPPDATA%\Ollama\server.log`）：

```
21:28:59  downloading 11fdb2dc2fde in 18 1 GB part(s)
21:31:48  11fdb2dc2fde part 0/1/2/3/4/5/7/8/9/10/11/12/14/15 stalled; retrying...
21:34:29  11fdb2dc2fde part 1 stalled; retrying...
21:38:13  11fdb2dc2fde part 8 stalled; retrying...
21:38:45  11fdb2dc2fde part 12 stalled; retrying...
```

- 已落盘 blob（**我独立测量**）：`E:\AI-Models\ollama\blobs\sha256-11fdb2dc...-partial` = **16,314.29 MB（15.93 GiB）**
- **Lead 报告的真实进度**（按该 digest 的所有分片合计）：**15.93 GB / ~17.4 GB = 91.6%**，网络恢复后以 **2–4 MB/s** 续传，预计还需 **30–60 分钟**（Lead 口径，2026-10-10 约 22:05）
- `ollama list` 中仍无该模型
- 官方提示原文：*"If this persists, press ctrl-c to exit, then 'ollama pull' to find a faster connection."*

**结论：本节报告定稿为"仅模型 A 的完整版"。模型 B = 未测 —— 下载卡在 91.6%，原因是网络中断（已于 21:41 恢复并断点续传）。脚本（`runner.py`）与全部题目已就绪并已通过自检，模型一到即可用同一脚本、同一参数、同一顺序补测，无需改代码。**

**对已有数据的影响（好的一面）**：正因下载自 21:31:48 起就基本停滞，而 A 的主测试窗口是 21:35:15–21:37:57、VS 真实形态是 21:40:10–21:41:29，**A 的测量基本没有下载流量干扰**。但不能说"绝对零干扰"：21:38:13/21:38:45 仍有重试动作发生在测试窗口边缘。B 的测试窗口将处于**不同**的系统状态（可能仍在拉取、或刚拉完），因此 **A 与 B 的吞吐数字不构成严格同条件对照**，只能算"同一脚本、同一参数、同一顺序、同一台机"。我会在 B 测完后补测 A 的关键项（用 `--tag A_recheck`，不覆盖 A 的原始记录）以提供同条件下的公平对照。

### 7.2 测量噪声：长 prompt 处理速率方差很大（**本报告最不可靠的数字**）

三段几乎同类的工作负载：

| 场景 | prompt tok | prompt 处理速率 |
|---|---|---|
| ~16K 纯文本 | 15,975 | 308.4 tok/s |
| 17K 纯文本 + 捞针 | 16,870 | 394.1 tok/s |
| 18.5K + 26 tools | 18,549 | 273.5 tok/s |

**最大/最小 = 1.44 倍**，而 prompt 规模只差 16%。也就是说，单次测量的"长 prompt 端到端延迟/处理速率"在这台机器上**误差可达 ±30%**。上表每个数字都只有 **1 次采样**（16K/17K/VS 形态各测一次），因此 §3 的 51.89 s / 43.31 s / 78.78 s 只应当作**量级**使用，不能当精确值。可能原因：GPU 时钟/功耗状态、首个大 batch 的显存分配、partial blob 写入的磁盘占用、以及 prompt 的前缀缓存是否命中。**要拿到可信值需要重复 3–5 次取中位数——本次时间窗内没做。**

### 7.3 篇幅与并发限制

- 长文持续吞吐只测到 **736 输出 token**（num_predict=1200，模型自己停在了 736）。**更长的输出（数千 token）是否继续衰减，未测。** 历史基线里 7758 token 长文是 40.7 tok/s，与本次 63.87 不是一个长度量级，不能互相印证。
- **未测并发多请求吞吐**（有意为之）：任务要求"测试进行中不要并发跑别的推理请求以免污染数据"，所以我全程串行。多用户/多请求场景的吞吐未测。
- 冷启动只有 **1 次**采样（14.98 s）；历史基线 10–12 s 与本次差 25–50%，我无法用单次采样判断是环境差异还是回归。

### 7.4 判定侧的局限

- **10 道代码题里有 1 道（第 10 题）是静态关键词判定，未执行**。这类判定的固有缺陷已在 §5.4 实证：同一个回答，自动判 FAIL、人工判"实质正确"。
- C# 代码虽然是**真编译真运行**，但编译用的是 `csc.exe` + 手写 `runtimeconfig.json` 的离线方式（`dotnet run file.cs` 在本机不可用：需要 nuget.org，当前不可达）。这与"在真实 VS/MSBuild 工程里编译"仍有差别：**未覆盖 MSBuild 属性、多文件工程、NuGet 包引用、ASP.NET Core 宿主**等场景。
- 代码题共 10 道、中文 5 道，**样本量小**，单题权重 10%–20%，不足以区分"模型能力差 5%"这种细粒度差异；只看趋势。
- 中文题用的是客观可判定的题型（数学、严格 JSON、字数约束、摘要关键词、代码输出），**这些不能代表中文写作/文学/长文连贯性**——那类只能人工评分，本次未做。
- 全部测试为**单轮**（无多轮对话、无 tool 结果回灌后再决策）。VS Agent 模式是多轮的，**多轮场景未测**。

### 7.5 不可外推

- 所有数字来自**这一台机器**（i5-13500 / Arc Pro B60 24 GB / Ollama 0.40.0 / `OLLAMA_CONTEXT_LENGTH=65536`）。换显存大小、换 Ollama 版本、改 KV cache 类型（q8_0）、改上下文长度，数字都会变。
- **不能把本报告的数字外推到"VS 里的实际体感"**：本报告测的是"裸 Ollama 的模型能力"，VS 扩展还叠了 IDE 侧 IPC、系统提示组装、可能的二次请求等开销。端到端体感属于另一个任务（task-2 / 扩展实现）的验证范围。
- B 的规格（27.3B、arch `qwen35`、上游 README 称 vision+MTP）**未被本次测试触及**：Ollama 0.40 是否完整支持该架构、vision/MTP 是否可用，**均未测**。

---

## 8. 证据索引（结论 → results.json 路径）

| 结论 | results.json 路径 |
|---|---|
| 热态 68.10 tok/s | `models.A.tests.throughput_short.eval_tps_median`，逐次在 `.reps[*].eval_tps` |
| 长文 63.87 tok/s + 衰减 9.7% | `models.A.tests.throughput_long.generation.eval_tps`、`.stream_analysis` |
| 冷启动 14.98 s / load 14.77 s | `models.A.runs.cold_start.wall_s`、`.load_duration_s` |
| 短 prompt 0.741 s | `models.A.runs.latency_short.wall_s` |
| 16K 51.89 s / 308.4 tok/s | `models.A.runs.latency_long`（`prompt_eval_count`=15975） |
| 捞针命中 `ZX-7741-QP` | `models.A.tests.needle.answer` / `.passed` |
| VS 真实形态通过 | `models.A.tests.vs_realistic`、`models.A.runs.vs_realistic.tool_calls_native` |
| 代码 9/10 及每题原始回答 | `models.A.tests.code-*`（判定+exec 证据）、`models.A.runs.code-*`（原文） |
| 工具 3/3 | `models.A.tests.tool-*`、`models.A.runs.tool-*` |
| 中文 4/5 | `models.A.tests.zh-*`、`models.A.runs.zh-*` |
| 100% GPU / 20 GB / 65536 | `models.A.resource_snapshot.raw` 与 `.rows[0]` |
| 完整请求体（可复现每题参数） | 每条 run 的 `.request_body` |

**复核命令**：

```powershell
cd C:\Users\zx\Documents\vs-local-ai\bench
python -m json.tool results.json > $null          # 可解析性
python _summary.py                                 # 打印全部关键明细
python _selftest.py                                # 判定 harness 自检（参考解必 PASS、反例必 FAIL）
```

---

## 9. 附录

### 9.1 产物与证据脚本清单（应 Lead 要求，可直接引用）

全部位于 `C:\Users\zx\Documents\vs-local-ai\bench\`（下列均为实际执行过的文件，非示意）。

| 文件 | 它证明了什么（可引用的一句话结论） |
|---|---|
| `runner.py` | 主测试脚本（纯标准库，参数化模型名，支持 `--only <phase>` 增量续跑）。内含 `ProxyHandler({})` 绕过系统代理、C# 离线编译执行、SQLite 真执行、transient 失败判 INVALID 并重测、`free_other_models()` 释放显存 |
| `results.json` | **全部原始数据**（525.9 KB）：每条任务的完整请求体、模型原始输出、API 计量字段（`load_duration`/`prompt_eval_count`/`eval_count`/各 duration）、判定细节与 exec 证据。`python -m json.tool` 通过 |
| `REPORT.md` | 本报告 |
| `_selftest.py` | **判定 harness 自身可信**：10 份人工参考解全部 PASS、3 个反例（幻觉调用工具 / 非法 JSON arguments / 无需工具却调用）全部 FAIL。并证明我方曾发现并修正一处**出题错误**（`"ab"*100` 的最长回文预期写错） |
| `_summary.py` | 从 `results.json` 打印全部关键明细（吞吐/延迟/每题判定/逐次耗时的紧凑表），用于复核报告数字与 JSON 的一致性 |
| `_posthoc_stream.py` | 对**已存档**的 stream 数据补算分段吞吐，保证两模型用同一算法从同一原始数据推导（`models.A.tests.throughput_long.stream_analysis`） |
| `_probe.py` | 证明：本机 `subprocess` 管道可用；`ollama show` 给出 A 的 capability = `tools, completion`（**无 thinking**）与默认采样参数 |
| `_probe2.py` | **证明系统代理这个坑**：`/`、`/api/version`、`/api/tags`、`/api/ps`、`/api/chat`、`/v1/models` **全部返回 HTTP 404**（Content-Length 0），而 `ollama` CLI 同时正常 |
| `_probe3.py` | **证明解法**：`urllib.request.getproxies()` 返回 `http://127.0.0.1:26561`（注册表代理）；加 `ProxyHandler({})` 后同一批路径全部 200，`/api/version` = `0.40.0` |
| `_probe4.py` | 证明三件事：① prompt 长度标定（1 个 FILLER 重复 = **24.04 token**，用于构造 16K/17K 输入）；② `think=true` 对 A 返回 **HTTP 400 `does not support thinking`**；③ `/v1/chat/completions` 工具调用返回 `finish_reason=tool_calls` 且 `arguments` 为 JSON **字符串**，而原生 `/api/chat` 的 `done_reason=stop` 且 `arguments` 已是对象 |
| `_dbg.py` | 定位上述**出题错误**：证明 `longest_palindrome("ab"*100)` 的正确答案是 199 字符的 `(ab)^99a`（本身是回文），原预期 `"aba"` 错误 —— 若不自检，两个模型都会被误判 FAIL |
| `_cstest/`（`NuGet.config`、`hello.cs`、`h.cs`、`h.rsp`、`h.dll`、`h.runtimeconfig.json`） | 证明 **C# 执行路线**：`dotnet run file.cs` 在本机**不可用**（NU1301 无法访问 nuget.org / NuGet.config 清源后 NU1101 缺 runtime pack），而 **Roslyn `csc.exe` + 手写 `runtimeconfig.json` + `dotnet prog.dll` 可用**（编译 1.9 s、运行 0.06 s）—— 这是代码题 1/2/3 能"真编译真运行"的基础 |
| `_vscheck.py` | 证明 VS 真实形态的构造正确：26 个工具、目标工具 `read_file` 在册，system prompt 107,103 字符 |
| `_vsnum.py` | 取出 VS 真实形态与捞针的关键数字（`prompt_eval_count`=18549 / 16870，工具名 `read_file`）用于报告 §3 |
| `_inspect.py` | 取出边界用例的**原始回答全文**（第 10 题、zh-03、tool-01 的 arguments）供人工复核自动判定 —— §5.4 的"自动 FAIL / 人工实质正确"分歧就是它发现的 |
| `_freedry.py` | 验证 `ollama ps` 解析改为"按表头 zip 取值"后正确（`runner=llamacpp`、`until=Forever`），修正了此前按列位硬解析导致 `until` 错位的问题 |
| `_tmp/`（36 个文件） | 每道代码题的**实际执行现场**：模型给出的源码、`data.txt`、编译产物 `prog.dll`、python harness 拼装后的 `solution.py`。任何人可进去重跑 |

> 注：`__pycache__/` 是 Python 导入缓存，非证据。

### 9.2 其他已记录但未纳入主表的事实

- `ollama show qwen3-coder:30b`：arch `qwen3moe`，30.5B，context length 262144，量化 IQ4_NL，Capabilities = `tools, completion`（**无 thinking**），默认参数 temperature 0.7 / top_p 0.8 / top_k 20 / repeat_penalty 1.05 / num_predict 8192。原文存于 `results.json` → `meta.ollama_show`。
- 传 `think=true` → `HTTP 400 {"error":"\"qwen3-coder:30b\" does not support thinking"}`。
- `OLLAMA_KEEP_ALIVE=-1` 导致模型常驻：**测 B 前必须先卸载 A**，否则 20 GB 常驻显存会直接影响 B 能否 100% GPU 装载。runner 会在 B 的 `cold_start` 阶段先对自己执行卸载，但**不会**卸载别的模型；我会在下 B 之前手动 `keep_alive:0` 卸载 A。
- 本机 `ollama ps` 的实际列是 `NAME ID SIZE PROCESSOR CONTEXT RUNNER UNTIL`（比常见文档多一列 RUNNER），按固定列位硬解析会错位——`runner.py` 改为按表头 zip 取值。

---

## 10. 结论（定稿版：仅模型 A）

1. **模型 A（qwen3-coder:30b）在这台机器上是"能进 VS 2026 Agent 模式"的**：100% GPU、68 tok/s 生成、17K 捞针精确命中、18.5K+26 tools 工具选择正确、代码题真编译真执行 9/10（严格判据）、工具调用 3/3。**这些是 A 的内部自洽结论，可以单独采信（§6.5.1）。**
2. **它真正的成本在每轮的 17K 系统提示上**：52–79 秒的 prompt 处理才轮到生成，这是 VS 场景的体感瓶颈。
3. **"值不值得换成 Qwen3.8-27B"目前无法回答** —— B 一次都没测成（下载卡在 91.6%，网络中断所致）。所有关于 B 的表述都是**待验证假设**（§6.4），不是结论。**涉及"A 与 B 谁更强"的任何结论一律标记为"待 B"（§6.5.2）。**
4. **补测已就绪**：`runner.py` 与全部题目通过自检，B 一到即可用同一脚本、同一参数、同一顺序跑完全部维度（含 §6.3.1 的显存污染排查协议与 INVALID 重测协议），随后更新 §0/§2/§3/§5/§6/§10。
5. **本次报告最需要复核的两处**（主动标注，见 §7.2 与 §5.4）：长 prompt 处理速率的单次采样方差可达 **±30%**（308.4 vs 394.1 tok/s，prompt 规模只差 16%）；第 10 题代码为**静态关键词判定**，自动判 FAIL 而人工复核为"实质正确"。
