# 共享事实（所有 Agent 必读，2026-10 实测）

> 本文件是团队唯一事实源。任何与本文件冲突的转述，以本文件为准。
> 新增事实请追加到文末"追加区"，不要改动已有条目（已有条目是实测结论）。

## 1. 主机硬件（实测）

| 项目 | 值 |
|---|---|
| CPU | 13th Gen Intel Core i5-13500（20 逻辑核） |
| 内存 | 47.8 GB |
| 推理 GPU | **Intel Arc Pro B60，24 GB**（注册表 `qwMemorySize`=24GB，驱动 32.0.101.9034） |
| 显示 GPU | Intel Arc A750 8 GB |
| 系统 | Windows 11，C: 238GB（剩 119.5GB）/ E: 477GB（剩 189.8GB） |
| 当前用户 | DESKTOP-A8UB9FP\zx，**非管理员** |
| WMI | `Get-CimInstance` 被拒绝访问（0x80041003）；用注册表/`System.IO.DriveInfo` 代替 |

## 2. 软件与路径（实测）

| 项目 | 值 |
|---|---|
| VS | Visual Studio Community 2026 **Insiders 18.11.12224.323**，实例 ID `bdfb6cb1` |
| VS 路径 | `E:\Visual Studio 2026\chanpin`，devenv：`...\Common7\IDE\devenv.exe` |
| MSBuild | `E:\Visual Studio 2026\chanpin\MSBuild\Current\Bin\MSBuild.exe`（18.11.0.42713） |
| VSIXInstaller | `E:\Visual Studio 2026\chanpin\Common7\IDE\VSIXInstaller.exe` |
| **VSSDK 工作负载** | **未安装**（无 `VSSDK` 目录）→ 必须用 NuGet `Microsoft.VSSDK.BuildTools` |
| .NET SDK | 10.0.401（`C:\Program Files\dotnet\sdk`） |
| Ollama | 0.40.0，`http://127.0.0.1:11434`，模型目录 `E:\AI-Models\ollama` |
| Ollama 环境变量 | `OLLAMA_CONTEXT_LENGTH=65536`、`OLLAMA_KV_CACHE_TYPE=q8_0`、`OLLAMA_KEEP_ALIVE=-1`、`OLLAMA_MODELS=E:\AI-Models\ollama` |
| Python | 3.11.9 |
| Node | v24.20.0（`E:\Node`） |
| 代理 | `C:\Users\zx\mcp\ollama-proxy.py`（44KB，监听 11435，已验证 200） |
| 搜索后端 | `C:\Users\zx\mcp\web-search-mcp.py`（18KB，纯标准库） |
| 工作区 | `C:\Users\zx\Documents`（可写；沙箱为 workspace-write） |

## 3. Ollama 模型（实测）

| 模型 | 大小 | 参数 | 量化 | 能力位 |
|---|---|---|---|---|
| `qwen3-coder:30b` | 16.12 GB | 30.5B (MoE 3B 激活) | IQ4_NL | `tools, completion`（**无 thinking**） |
| `qwen3:4b-local` | 2.33 GB | 4.0B | Q4_K_M | `tools, thinking, completion` |
| `smtek/Qwen3.8-27B:Q4_K_M` | ~17 GB | 27.3B (arch `qwen35`) | Q4_K_M | 拉取中；上游 README 称 vision+MTP |

## 4. VS 2026 BYOM 关键情报（上一轮反汇编 + 实测，极其重要）

- 配置文件：`%LOCALAPPDATA%\Microsoft\VisualStudio\Copilot\BringYourOwnModel\ConfiguredBringYourOwnModel_v2.json`（VS 会回写）
- **`CustomURL` 必须非 null**（提供商层级）。为 null 时：模型出现在选择器、VS 报 `1 usable provider`，但发消息**请求静默消失**（无 HTTP、无报错）。已实测。
- **`Endpoint` 枚举真值**（反汇编 `Microsoft.VisualStudio.Copilot.dll`）：
  `GitHub=1 OpenAI=2 Anthropic=3 Google=4 xAI=5 GitHubResponses=6 AzureFoundry=7 FoundryLocal=8 GitHubAnthropicMessages=9 Ollama=10`
- 自定义模型**只在 Agent (Preview) 模式生效**；VS 系统提示约 **16,951 token**（旧记录 25,262 字符）
- BYOM **不等于脱网**：聊天面板仍依赖 `api.githubcopilot.com` 初始化；受限网络下面板转圈
- **MCP 路线在本 build 有缺陷**：自研 MCP 服务器状态恒为 `UserDisabled`，连微软自带 Microsoft Learn MCP 也是；`EnabledMcpServers` 恒空且设置项被 `visibleWhen:'false'` 隐藏。已尝试 12 条路径全部失败（详见工作区 `VS2026-Ollama-MCP-交接说明.md`）
- **结论**：想在 VS 2026 里给本地模型加"联网+模型管理"，**只能走扩展/代理**，不能靠 Copilot MCP

## 5. 已实测性能基线（同一台机，历史数据）

| 项目 | 实测 |
|---|---|
| qwen3-coder:30b 热态生成 | 65–70 tok/s |
| 首请求冷启动 | 10–12 s |
| 长文生成（7758 tokens） | 40.7 tok/s |
| 联网端到端 | 5.9 s（不联网 3.0 s） |
| prompt 越大越慢 | 9.3K→18.4s；38.1K→248s；64.9K→694s（受热状态影响大，只当量级） |
| 上下文超限行为 | **HTTP 400 `exceed_context_size_error`，报错不截断** |
| 工具调用（26 工具 + stream） | 正常，`index` 递增；issue #16212 未复现 |

## 6. 前车之鉴（不要重犯）

- `Local LLM Chat for Visual Studio` 硬改版本声明装进 VS2026 后**实测让 VS 卡死**。任何一个新扩展都必须先证明"加载后 IDE 不卡"。
- 第三方 VSIX（`C:\Users\zx\Documents\local-llm-chat-vs-for-VS2026.vsix`）声明 `[17.0,19.0)`，但实测有问题。

## 7. 追加区（由各 Agent 追加，注明来源与时间）

### 7.1 VSIX 打包与安装（2026-10-10，Lead 实测，血泪）

| 事实 | 证据 |
|---|---|
| **`<Prerequisites>` 会让 VSIXInstaller 拒绝整个包** | 带 `<Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor" .../>` 时引擎抛 `InvalidExtensionPackageException`（"此文件不是有效的 VSIX 包"）；**只删掉这一个元素、其它一字不改，引擎立刻接受**。二分实验在 `tools\IlProbe` |
| **VS 自带的 schema 拦不住上面那条** | `Xml\Schemas\PackageManifestSchema.xsd`（targetNamespace 是 **2011**）**允许** Prerequisites，校验能过。同目录的 `VSIXManifestSchema.xsd` 是 **2010** 命名空间，根本不能校验现代 manifest |
| 校验 2011 manifest 的正确 schema | `E:\Visual Studio 2026\chanpin\Xml\Schemas\PackageManifestSchema.xsd` + 它 import 的 5 个同级文件，用 `XmlSchemaSet` + `XmlUrlResolver` 自动解析 |
| `pack.ps1` **必须是纯 ASCII** | PS 5.1 用 ANSI(GBK) 读 `.ps1`，文件里的中文会让解析器报出莫名其妙的语法错误（"缺少必需的属性"、"应输入 )" 之类）|
| 装 VSIX 时 **VS 必须完全退出** | 否则日志报 `BlockingProcessesException`，退出码 2004；`copilot.exe` 与 `copilot-language-server.exe` 会一并阻塞 |
| PowerShell 参数陷阱 | `XmlSchemaSet.Add($null, path)` 会因 `$null`→`""` 而抛异常，要从 xsd 里读 targetNamespace；`[xml]$x = ...` 会把对象回显到管道 |
| 用引擎自己的实现验证包 | `tools\IlProbe`：加载 `...\VsixInstaller\Microsoft.VisualStudio.ExtensionEngine.dll`，调用 **`InstallableExtensionImpl(string path, CultureInfo)`**（VSIXInstaller 真正走的路径），可把"包到底行不行"从猜测变成可复现实验 |

