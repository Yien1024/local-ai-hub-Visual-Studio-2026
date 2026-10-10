# Local AI Hub —— Visual Studio 2026 的本地大模型插件

[English](README.md) · **中文**

在 VS 2026 里直接使用本机 **Ollama** 模型，并且**真的能联网搜索**，还能顺手管理模型、把模型一键注册进 Copilot 的自带模型（BYOM）。

**零 NuGet 依赖 · 零 API key · 纯离线可构建 · 不依赖 GitHub Copilot 云端**

```
工具 → 本地大模型对话(&L)...
```

> 💡 **为什么做这个**：VS 2026 的 Copilot 能接本地 Ollama 模型，但**没法让它联网**
> （这个 build 里 MCP 服务器恒为禁用），而且 BYOM 配置只要错一个字段，请求就会**静默消失**。
> 所以这个插件的定位不是"给 Copilot 打补丁"，而是**自己就是一个完整的本地模型客户端**。

---

## 为什么需要它

VS 2026 的 Copilot 有 "BYOM"（自带模型）可以接本地 Ollama，但实测有三个绕不过去的问题：

| 问题 | 实测证据 |
|---|---|
| **Copilot 的 MCP 装不上** | 自研 MCP 服务器状态恒为 `UserDisabled`，连微软自带的 Microsoft Learn MCP 也是；`EnabledMcpServers` 恒空、设置项被 `visibleWhen:'false'` 隐藏。12 条尝试路径全部失败 |
| **BYOM 配错一个字段就静默失效** | `CustomURL` 为 null 时，模型出现在选择器里、VS 也报 `1 usable provider`，但发消息**请求直接消失** —— 不报错、不产生任何 HTTP 请求 |
| **本地模型不会联网** | 模型知识有截止日期；且 Copilot 面板本身仍依赖 `api.githubcopilot.com` 初始化，受限网络下一直转圈，本地模型一个请求都收不到 |

这个插件把三件事一起解决：**它自己就是一个完整的本地模型客户端**（不依赖 Copilot 云端），联网能力做在里面（不依赖 MCP），并顺手帮你把 BYOM 配置写对。

---

## 功能

- **流式对话**：本机任意 Ollama 模型，逐字输出，可随时停止
- **联网搜索**：Bing（cn 节点）→ 360 → 百度 自动回退，**零 API key、零费用**
  - 按问题内容自动判定该不该搜（纯概念 / 纯代码 / 延续性追问不搜，省 ~11 秒和 ~1500 prompt token）
  - 检索词优先抽**报错原文**而不是整句话（实测：整句 → 教程首页；抽原文 → 5/5 正解页）
  - 注入前过**相关性闸门**（内容农场直接剔除，低于阈值不注入）
  - 搜不到就明确告诉模型"没搜到"，而不是硬塞垃圾进 prompt
  - 注入块带**时间锚点 + `[S1]` 来源编号 + 允许弃用无关结果**的规则
  - 抓正文有**硬墙钟上限**（`HttpClient` 的 timeout 不覆盖 DNS 解析，实测有页面挂 145 秒）
- **模型管理**：列表 / 拉取（带进度和速度）/ 删除 / 预加载常驻显存 / 从显存卸载
- **显存体检**：显示 `ollama ps` 的占用，`100% GPU` 还是 CPU 混合一目了然
- **BYOM 一键注册**：把本机模型写进 VS Copilot 的自带模型配置（写前自动备份）
- **搜索测试**：不经过模型，直接看每个引擎返回了什么、闸门留下了什么
- **运行日志面板**：每一步决策都能看到（跳过联网的理由、检索词、引擎、保留条数）
- **手工控制**：消息里写 `[search]` 强制联网、`[no-search]` 强制不联网

---

## 安装

### 前置

- Windows + **Visual Studio 2026**（在 Insiders 18.11 上开发验证；manifest 声明 `[18.0,19.0)`）
- **Ollama** 正在运行（`ollama list` 有输出即可）
- 至少一个模型，例如 `ollama pull qwen3-coder:30b`

### 装插件

1. **完全退出 Visual Studio**（不是最小化 —— `devenv.exe` / `copilot.exe` 会阻塞安装）
2. 双击 `dist/LocalAIHub-1.0.0.vsix`，UAC 弹窗点"是"
3. 重启 VS → **工具 → 本地大模型对话(&L)...**
   （也可从 **视图 → 其他窗口 → Local AI Hub** 打开）

命令行等价写法：

```powershell
& "<VS安装目录>\Common7\IDE\VSIXInstaller.exe" /quiet /instanceIds:<实例ID> "dist\LocalAIHub-1.0.0.vsix"
```

> 装到 `Program Files` 需要管理员权限，**UAC 必须点"是"**。
> 如果报 `BlockingProcessesException`，就是 VS 还开着。

### 卸载

```powershell
& "<VS安装目录>\Common7\IDE\VSIXInstaller.exe" /quiet /uninstall:LocalAIHub.9f1c4a02-7d3e-4b18-9c55-0a7f2e5b81d4
```

### 验证装好了

打开工具窗口后应生成 `%TEMP%\LocalAIHub\localaihub.log`，内容含 `LocalAIHubPackage.Initialize 完成`。

> **这是预期行为**：插件刻意**不自动加载**（不设 `AllowsBackgroundLoad`、不注册 MEF），
> 只在用户点开工具窗口时才实例化 —— 为了不拖慢 VS 启动。
> 所以在你点开菜单之前，`ActivityLog.xml` 里是**看不到**它的。

---

## 怎么用

1. 确认 Ollama 在跑：`ollama list`
2. 打开 **工具 → 本地大模型对话(&L)...**
3. 顶部选模型 → 输入问题 → **Ctrl+Enter** 发送
4. 想让 Copilot Chat 也能用本地模型：点 **BYOM…** → 注册选中模型 → 重启 VS

| 界面元素 | 作用 |
|---|---|
| 模型下拉框 | 本机已有模型；带 `[常驻显存]` 标记的已在显存里 |
| 模型管理… | 拉取（带进度）/ 删除 / 预加载常驻 / 卸载 |
| 联网搜索 + 模式 | `auto 自动` / `always 总是` / `never 从不` |
| BYOM… | 注册进 Copilot 自带模型，并显示当前配置内容 |
| 搜索测试… | 只跑搜索链路，不经过模型 |
| 状态栏 | 每次回答后的 tok/s、首字延迟、prompt token、联网明细 |
| 运行日志 | 每一步决策的原始日志 |

### 设置

**工具 → 选项 → Local AI Hub**，或直接编辑 `%LOCALAPPDATA%\LocalAIHub\settings.json`。

| 项 | 默认 | 说明 |
|---|---|---|
| Ollama 地址 | `http://127.0.0.1:11434` | |
| 搜索模式 | `auto` | `auto` 按问题判定 / `always` / `never` |
| 相关度阈值 | `0.30`（时效类 `0.15`） | 低于它的结果不注入 |
| 抓正文硬超时 | `8` 秒 | 防止 DNS 卡住把界面挂住 |
| 上下文 `num_ctx` | `65536` | 要和 `OLLAMA_CONTEXT_LENGTH` 对齐 |
| 最大输出 token | `16384` | thinking 模型给小了会正文为空（插件会自动加倍重试一次） |
| BYOM Base URL | `http://127.0.0.1:11434/v1` | |

---

## 自己构建

需要 VS 2026（提供 MSBuild 与 VS SDK 程序集）和 .NET SDK。**不需要 NuGet**、不需要联网。

```powershell
# 1) 逻辑层自测（不需要 VS，110 项）
dotnet build tools\CoreTests\CoreTests.csproj
.\tools\CoreTests\bin\Debug\net472\CoreTests.exe

# 2) 构建扩展
dotnet build src\LocalAIHub\LocalAIHub.csproj

# 3) 打包 VSIX（含两道校验闸门）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\pack.ps1
```

### 为什么是"手工"的

本机网络不可达 nuget.org，拿不到 `Microsoft.VSSDK.BuildTools`，所以：

- **VS SDK 程序集**直接引用本机 VS 安装目录（`<HintPath>`），不复制进 VSIX（`Private=false`）
- **JSON** 用手写的 `src/LocalAIHub.Core/Json.cs`（零依赖）
- **VSIX** 由 `scripts/pack.ps1` 手工封装（zip + `[Content_Types].xml` + 改名 manifest）
- **菜单/命令/工具窗口**全部由 `LocalAIHub.pkgdef` 手工注册（本机没有 `VSCTCompiler.exe`，所以不用 `.vsct`）

### 两个必须知道的坑（都已在脚本里防住）

1. **`<Prerequisites>` 会让 VSIXInstaller 拒绝整个包**
   带 `<Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor" .../>` 时引擎抛
   `InvalidExtensionPackageException`（"此文件不是有效的 VSIX 包"）；
   **只删掉这一个元素、其它一字不改，引擎立刻接受**。
   而 VS 自带的 `PackageManifestSchema.xsd` 是**允许**它的 —— 所以 schema 校验拦不住。
   复现工具：`tools/IlProbe`（直接加载 VSIXInstaller 自己用的引擎实现做逐项二分）。

2. **`pack.ps1` 必须保持纯 ASCII**
   Windows PowerShell 5.1 用 ANSI(GBK) 读 `.ps1`，文件里的中文会让解析器报出毫无关联的语法错误。

---

## 目录结构

```
├─ src/
│  ├─ LocalAIHub.Core/          # 纯逻辑层，不依赖 VS，可独立测试
│  │  ├─ Json.cs                #   手写 JSON（零依赖）
│  │  ├─ HttpFetcher.cs         #   带硬墙钟上限的 HTTP
│  │  ├─ SearchService.cs       #   Bing/360/百度 解析 + 抓正文
│  │  ├─ QueryDecision.cs       #   该不该搜 / 检索词抽取
│  │  ├─ ContextBlocks.cs       #   相关性闸门 + 注入块
│  │  ├─ OllamaClient.cs        #   Ollama API（流式/拉取/常驻）
│  │  ├─ ChatPipeline.cs        #   主链路
│  │  └─ ByomConfig.cs          #   Copilot BYOM 配置读写
│  └─ LocalAIHub/               # VSIX（net472 + WPF）
│     ├─ LocalAIHubPackage.cs   #   包入口（最轻量，不自动加载）
│     ├─ ChatControl.cs         #   聊天界面
│     ├─ ModelManagerWindow.cs  #   模型管理
│     ├─ ByomWindow.cs          #   BYOM 注册 + 搜索测试
│     ├─ HubOptionsPage.cs      #   工具→选项
│     └─ LocalAIHub.pkgdef      #   手工注册
├─ tools/
│  ├─ CoreTests/                # 110 项离线自测宿主
│  └─ IlProbe/                  # VSIX 引擎探针（定位安装被拒的根因）
├─ scripts/pack.ps1             # 手工 VSIX 打包 + 两道校验闸门
├─ bench/                       # 本机模型基准测试
└─ docs/                        # 实测事实、扩展性笔记、交付报告
```

---

## 已知限制（诚实清单）

- 只在 **VS 2026 Insiders 18.11.12224.323** 上构建和验证过。
- **联网依赖 Bing/360/百度的页面结构**。它们改版时是**降级**（退回通用解析器）而不是崩溃，但可能返回空结果——那时会明确显示"没搜到"，而不是假装搜过了。
- **相关度闸门比的是字面重合度，不是语义**。同义词、整句提问会有假阴性；兜底是"永不滤空 + 低置信度标注"。
- 百度会限制自动请求，所以排最后。
- 不支持视觉输入、不支持多模型并发、对话历史不落盘（关掉 VS 就没了）。
- `thinking` 模型（如 `qwen3:4b`）会先用输出预算思考：插件检测到"思考吃光预算"会自动加倍预算重试一次，但会额外花时间。
- **BYOM 注册功能未经真实使用验证**：单元测试全部重定向到临时文件（避免污染正在用的 VS 配置），第一次实际使用前会自动备份。

### 环境相关的坑（实测，写脚本的人会用到）

- **系统代理会拦 loopback**：本机注册表开了 `127.0.0.1:26561` 代理且**不豁免 localhost**，Python `urllib.getproxies()` 会命中它，导致直连 `127.0.0.1:11434` 全部返回 **404**（ollama CLI 用 Go，默认豁免 localhost 所以正常）。脚本里加 `ProxyHandler({})` 绕过。
- **`HttpClient`/`urlopen` 的 timeout 不覆盖 DNS 解析**，实测有页面挂 145 秒 → 必须自己加墙钟上限。

---

## 相关文档

| 文件 | 内容 |
|---|---|
| [docs/SHARED-FACTS.md](docs/SHARED-FACTS.md) | 实测事实（含 BYOM 的 `Endpoint` 枚举真值、VSIX 打包与安装的坑） |
| [docs/EXTENSIBILITY-NOTES.md](docs/EXTENSIBILITY-NOTES.md) | VS 2026 扩展可行性核实（含 10 条未证实项，都标了出处） |
| [docs/FINAL-REPORT.md](docs/FINAL-REPORT.md) | 交付报告与验收证据（含失败与纠正记录） |
| [bench/REPORT.md](bench/REPORT.md) | `qwen3-coder:30b` 本机基准数据 |

## License

MIT —— 见 [LICENSE](LICENSE)。
搜索后端会抓取第三方网页，使用前请自行确认符合目标站点的使用条款。
