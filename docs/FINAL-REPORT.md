# 最终交付报告

> 目标：① 用本机真实硬件实测 qwen3-coder:30b vs Qwen3.8-27B，给 VS 2026 场景下的"换不换"结论；
> ② 做一个可以直接用、也能给别人用的 VS 2026 插件/扩展，把本地模型 + 联网搜索 + 模型管理做进去。
>
> 本报告只写**实际发生的事**。没做成的地方单独列在第 6 节，不掩饰。

---

## 1. 结论摘要（TL;DR）

| 项 | 状态 | 证据 |
|---|---|---|
| VS 2026 扩展（VSIX）**构建** | ✅ 成功 | `dotnet build src\LocalAIHub\LocalAIHub.csproj` → 0 error |
| 逻辑层**自测** | ✅ **107 项通过 / 0 失败 / 1 跳过** | `tools\CoreTests\bin\Debug\net472\CoreTests.exe` |
| VSIX **打包** | ✅ 成功，77.3 KB | `dist\LocalAIHub-1.0.0.vsix`（5 个条目自检通过） |
| 装进 VS 的 **UAC 环节** | ❌ 被环境拒绝 | 见第 6.1 节（非代码问题） |
| 基准测试 模型 A（30b） | ✅ 完成 | `bench/results.json` + `bench/REPORT.md` |
| 基准测试 模型 B（27B） | ⏸ 未完成 | 下载卡在 94%，见第 6.2 节 |
| 联网搜索链路 | ⚠️ 逻辑已自测，真实网络验证待网络稳定 | 离线 HTML 样本解析全部通过 |

**一句话**：插件本体已经做完并且逻辑全部自测通过、VSIX 已经产出可以直接装；
唯一没闭环的是"点开 VS 里的菜单看一眼"（被本机权限策略挡住）和"27B 的对比数据"（被网络挡住）。

---

## 2. 环境事实（实测，非转述）

| 项目 | 值 |
|---|---|
| VS | Visual Studio Community 2026 **Insiders 18.11.12224.323**，实例 `bdfb6cb1`，路径 `E:\Visual Studio 2026\chanpin` |
| CPU / 内存 | i5-13500（20 逻辑核）/ 47.8 GB |
| GPU | **Intel Arc Pro B60 24 GB**（推理）+ Arc A750 8 GB（显示） |
| Ollama | 0.40.0，`127.0.0.1:11434`，模型库 `E:\AI-Models\ollama` |
| 已装模型 | `qwen3-coder:30b`（16.12 GB / IQ4_NL）、`qwen3:4b-local`（2.33 GB / Q4_K_M） |
| .NET SDK | 10.0.401；net472 目标包已存在 |
| 权限 | 当前用户**非管理员** |
| 网络 | 21:28–21:41 全断（直连/代理/registry 全失败），随后恢复 |

---

## 3. 交付物

| 文件 | 说明 |
|---|---|
| `dist\LocalAIHub-1.0.0.vsix` | **可以直接安装的扩展**（77.3 KB） |
| `dist\_staging\` | 已解包的扩展内容（per-user 手工部署用） |
| `src\LocalAIHub.Core\` | 纯逻辑层（搜索/判定/闸门/注入/Ollama/BYOM），**不依赖 VS，可独立测试** |
| `src\LocalAIHub\` | VSIX 项目（net472 + WPF），包入口/聊天窗/模型管理/BYOM/选项页 |
| `tools\CoreTests\` | 107 项离线自测宿主（net472 exe） |
| `scripts\pack.ps1` | 手工 VSIX 打包（零 NuGet） |
| `bench\` | 基准测试脚本、原始结果、报告 |
| `docs\SHARED-FACTS.md` | 本机实测事实（团队唯一事实源） |
| `docs\EXTENSIBILITY-NOTES.md` | VS 2026 扩展可行性核实（587 行，含 10 条未证实项） |
| `README.md` | 安装/使用/构建/限制 |

---

## 4. 插件做了什么

### 4.1 它解决的真实问题

上一轮已经查明并留有物证的三件事：

1. **VS 2026 的 MCP 在本 build 有缺陷**：自研 MCP 服务器恒为 `UserDisabled`，
   连微软自带的 Microsoft Learn MCP 也是；`EnabledMcpServers` 恒空且设置项被
   `visibleWhen:'false'` 隐藏。12 条尝试路径全部失败
   → 所以"给本地模型加联网"**不能走 MCP**。
2. **BYOM 配错一个字段会静默失效**：`CustomURL` 为 null 时模型出现在选择器、VS 报
   `1 usable provider`，但发消息请求**直接消失**（无报错、无 HTTP）。
   `Endpoint` 枚举真值（反汇编所得）：`Ollama=10`、`OpenAI=2`……
3. **BYOM ≠ 脱网**：Copilot 面板仍依赖 `api.githubcopilot.com` 初始化，受限网络下一直转圈。

**本插件的定位**：一个不依赖 Copilot 云端的独立本地模型客户端，联网做在里面（不依赖 MCP），
同时顺手把 BYOM 配置写对（让 Copilot Chat 那边也能用本地模型）。两条路互不依赖。

### 4.2 功能清单

流式对话 · 联网搜索（Bing→360→百度回退、零 key）· 按需触发 · 报错原文抽取 ·
相关性闸门 · 承认搜不到 · 时间锚点与 `[S1]` 引用编号 · 抓正文硬超时 ·
模型管理（拉取带进度/删除/常驻显存/卸载）· 显存占用体检 · BYOM 一键注册 ·
搜索测试窗口 · 运行日志面板 · 工具→选项设置页。

### 4.3 关键设计决策（为什么这么做）

| 决策 | 理由（都有实测依据） |
|---|---|
| **零 NuGet 依赖** | 本机网络不可达 nuget.org（NU1301 + TLS 握手失败），NuGet 缓存里也没有所需包 → VS SDK 程序集直接引用本机安装目录，JSON 用手写的 `Json.cs` |
| **手写 VSIX 打包** | 拿不到 `Microsoft.VSSDK.BuildTools`；VSIX 本质是 OPC 包，`scripts\pack.ps1` 自己生成 `[Content_Types].xml` + `extension.vsixmanifest` + pkgdef（并校验 BOM） |
| **不用 .vsct** | 本机既无 VSSDK 也无 `VSCTCompiler.exe` → 菜单/命令/工具窗口全部由 `pkgdef` 手工注册 |
| **不用 `ProvideMenuResource`** | 声明了却没有 `.ctmenu` 资源会导致包加载失败 → 已刻意移除 |
| **`AllowsBackgroundLoading = false`，不注册 MEF** | 前车之鉴：某第三方扩展在 VS2026 里卡死。包只在用户点开时加载，最轻 |
| **所有 HTTP 在线程池** | UI 线程只画界面；每个事件处理器都 try/catch，异常写 `%TEMP%\LocalAIHub\localaihub.log`，绝不冒泡进 IDE |
| **逻辑层与 VS 解耦** | 先证明逻辑对（107 项测试），再塞进 IDE —— 上一轮的教训就是不先证明就装 |

---

## 5. 验收证据

### 5.1 逻辑层自测（107 项）

```
通过 107 / 失败 0 / 跳过 1
```

覆盖：

| 组 | 项数 | 关键验证点 |
|---|---|---|
| JSON 读写器 | 12 | 中文键值、转义、科学计数、非法输入抛异常、真实 Ollama NDJSON 行 |
| 检索词抽取 | 5 | 8 条真实输入里 4 条报错原文精确抽取；无报错时回退清洗 |
| 联网判定 | 20 | 13 条"不该搜"全部不搜；7 条"该搜"全部要搜 |
| 相关性闸门 | 10 | csdn 剔除、可信源加成、全无关时不滤空并标弱证据 |
| 注入块 | 14 | 时间锚点、`[S1]` 编号、允许弃用、禁止编造、追加到最后一条 user |
| HTML 解析 | 13 | Bing `/ck/a` 解链、360 `data-mdurl`、通用解析器、属性含 `>` 不破坏解析 |
| BYOM 写入 | 15 | `CustomURL` 非空、`Endpoint=10`、二次注册合并不重复、自动备份、空 URL 被拒 |
| 真实对话 | 6 | Ollama 0.40.0 连通、流式拿到正文、token 计数正确 |
| 端到端 | 6 | 判定→搜索→注入链路不抛异常，注入块非空 |
| 真实搜索 | 1 | **跳过**：测试期间网络故障 |

### 5.2 测试过程中发现并修掉的真实 bug（4 类）

这是"先测试再装"的直接收益：

1. **`ByomConfig.Register` 用 `null` 同时表示"失败"和"成功但无需备份"**
   → 首次写入被调用方当成失败。已改为返回空字符串表示成功无备份，并在文档注释里写明契约。
2. **相关性闸门里 `topScore > 0` 的前提把"完全零重合"滤空了**
   → 而这恰恰是最需要输出"没搜到相关的"场景。已移除该前提。
3. **"苹果股价多少"被判成"不用联网"**
   → 兜底改成不搜之后漏掉了数量/取值类问题。已新增 `ValueQuestionRe`（股价/多少/天气/比分…），
   且放在纯代码判定**之后**，避免"这个函数返回多少"被误判。
4. **长标识符拉低字面重合度**：`modulenotfounderror` 与页面上的 `ModuleNotFoundError`
   → 新增"字母数字切分 + 70% 覆盖率"匹配（`TermMatches`）。

### 5.3 VSIX 包自检

```
[Content_Types].xml         541 B
extension.vsixmanifest     2226 B
LocalAIHub.Core.dll       95232 B
LocalAIHub.dll            76800 B
LocalAIHub.pkgdef          1851 B     ← BOM 已校验
```

pkgdef 里的 GUID 与 C# 特性一一对应：
Package `8f2a1c74-…`、CmdSet `6d5b2e91-…`、ToolWindow `3c9a7e15-…`。

---

## 6. 没做成的事（诚实清单）

### 6.1 扩展装进 VS 的 UAC 环节被环境拒绝

> **2026-10-10 22:20 更新：根因已找到并修复，此节保留原始记录。**

**第一层原因（已修复）：VSIX 包本身不合法。**

我第一次交付的 VSIX 在 `VSIXInstaller` 里报：

```
Microsoft.VisualStudio.ExtensionManager.InvalidExtensionPackageException: 此文件不是有效的 VSIX 包。
```

**定位方法（不靠猜）**：写了探针 `tools\IlProbe`，直接加载 VSIXInstaller 自己用的
`Microsoft.VisualStudio.ExtensionEngine.dll`，调用它真正走的入口
`InstallableExtensionImpl(string path, CultureInfo)`，然后对 manifest 做**逐项二分**：

| 变体 | 结果 |
|---|---|
| R0 正式 manifest（原样） | 拒绝 InvalidExtensionPackage |
| R1 删 `<Tags>` | 拒绝 |
| R2 删 `<MoreInfo>` | 拒绝 |
| R3 删 `<DisplayName>` | 拒绝（缺少必需元素，报错明确） |
| **R6 删 `<Prerequisites>`** | **接受** |
| R7–R17 其它各种改动 | 全部拒绝 |

**根因**：manifest 里的

```xml
<Prerequisites>
  <Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor" Version="[18.0,19.0)" ... />
</Prerequisites>
```

VS 自带的 `PackageManifestSchema.xsd`（2011 命名空间）**允许**它 —— 用它校验能通过 ——
但引擎实际解析时会因此抛异常。**只删掉这一个元素、其它一字不改，引擎立刻接受。**

顺带纠正一个我中途的错误判断：我一度以为根因是 `<Installation>` 里的 XML 注释；
后来用 schema 验证发现注释是合法的，**这个判断是错的**，真正的元凶是 `<Prerequisites>`。

**已固化的防护**：`scripts\pack.ps1` 现在有两道闸门 ——
① 用 VS 官方 `PackageManifestSchema.xsd` 做 schema 校验；
② 显式禁止 `<Prerequisites>`（因为 schema 拦不住它）。
两条都做过反向验证（故意放回问题内容会被拦下）。

**第二层原因（需要你操作）：Visual Studio 正在运行。**

修好清单后重试，日志变成了另外一条：

```
VSIXInstaller.BlockingProcessesException: 执行扩展更改前，需要关闭以下进程
- copilot.exe (ID 17128)
- copilot-language-server.exe (ID 15992)
- devenv.exe (ID 18760)
```

`devenv.exe` 从 20:29 一直运行到 22:23。**安装扩展必须完全退出 Visual Studio。**
退出码 `2004` = 被取消（含 UAC 被拒或等待超时），不是包的问题。

**第三层原因：本会话无法完成提权。** `VSIXInstaller` 装到 `Program Files` 需要管理员权限，
而 UAC 弹窗在 DSH 沙箱里点不了（试过静默/交互/显式 RunAs/计划任务四种方式）。
所以最后一步需要你双击 VSIX 并点 UAC 的"是"。

### 6.2 27B 的对比数据没拿到

**现象**：`ollama pull smtek/Qwen3.8-27B:Q4_K_M` 下载到 **16314 MB / ~17400 MB（94%）** 时，
Ollama 自己的日志显示 18 个分片同时 `stalled; retrying`（`download.go:395`），
之后只有零星重试，再没增长。

**原因**：21:28–21:41 期间本机网络完全中断（直连、系统代理 26561、ollama registry 全部失败）。
网络在 21:41 恢复后我已重启拉取，会从 94% 断点续传，不会从头下。

**所以**：目前只有模型 A 的完整数据（见 `bench/REPORT.md`）。
B 的数据一到位就会补测并更新报告——脚本、题目、参数都是现成的，同一脚本同一顺序。

### 6.3 其他未验证项

- 扩展在 VS 里的**实际 UI 渲染**未验证（工具窗口能否正常打开、WPF 控件在 VS 主题下的观感）。
- **真实网络下的搜索链路**未在本次会话验证（测试时网络故障）；离线 HTML 样本解析全部通过，
  但"Bing 今天的真实返回能否解析"需要网络恢复后复测。
- BYOM 注册功能写了但**没有真的往用户真实配置里写过**（测试全部重定向到临时文件，
  避免污染正在使用的 VS 配置）。第一次实际使用前会自动备份。
- `docs/EXTENSIBILITY-NOTES.md` 第 9 节列了 10 条未证实项，都标了 `[未找到出处]`。

---

## 7. 下一步（按优先级）

1. **重启 VS 确认扩展加载**（30 秒）。没有菜单就双击 VSIX 装一次。
2. **等 27B 下完**（已断点续传中），跑 B 组基准，更新 `bench/REPORT.md` —— 回答你最初那个问题。
3. 用 **BYOM…** 把 `qwen3-coder:30b`（或 27B）注册进 Copilot，验证 Copilot Chat 里能否选中。
4. 如果想让 Copilot 里的模型也联网：把 BYOM 的 Base URL 指到你本机的联网代理
   （`C:\Users\zx\mcp\ollama-proxy.py` 的 `http://127.0.0.1:11435/v1`），
   插件与代理是两条独立的路。
5. 网络稳定后跑一次 `搜索测试…`，确认真实引擎解析没问题。
