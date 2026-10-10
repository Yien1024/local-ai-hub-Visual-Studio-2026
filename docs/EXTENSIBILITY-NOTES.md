# VS 2026 (18.11) 扩展可行性规格说明

> 作者：ext-researcher ｜ 任务：task-2 ｜ 日期：2026-10
> 本文件每条结论后标注证据类型：
> **[本机实测]** = 我在本机跑过的命令/读过的文件；**[官方文档]** = learn.microsoft.com / microsoft.github.io；**[NuGet]** = nuget.org 版本索引（本机直连失败，经镜像读取，见 §0.2）；**[未找到出处]** = 明确未证实，不猜。

---

## 0. 结论摘要（先看这里）

| 结论 | 值 | 证据 |
|---|---|---|
| 推荐方案 | **C：经典 `AsyncPackage` + VSIX，但用本机 DLL 引用 + 手写 VSIX 打包**（即"离线 A 方案"） | [本机实测] 编译通过 |
| `TargetFramework` | **`net472`** | [本机实测] `devenv.exe.config` |
| `Microsoft.VSSDK.BuildTools` 最新版 | **18.9.820** | [NuGet] |
| 是否需要 NuGet 还原 | **不需要**（本次实测零 `PackageReference` 编译通过） | [本机实测] |
| `InstallationTarget` | `Id="Microsoft.VisualStudio.Community"` `Version="[18.0, 19.0)"` + `ProductArchitecture amd64` | [本机实测] 微软随箱扩展的 manifest |
| `Prerequisite` CoreEditor | `Version="[18.0,19.0)"` | [本机实测] 同上 |
| 非管理员能否装扩展 | **能，走 per-user 目录** | [本机实测] ACL + 已装实例 |
| `Microsoft.VisualStudio.Extensibility` 是否 GA | **否，官方文档标题仍为 "(Preview)"** | [官方文档] |

**最重要的单条发现**：本机 `dotnet restore` 拉不到任何 NuGet 源（`NU1301`），但 **net472 经典 VSSDK 扩展可以在完全不用 NuGet 的情况下编译成功**——我已实测。详见 §8。

---

## 0.1 时间敏感的两条（Lead 已在等）

### TargetFramework = `net472`（不用 net8.0/net10.0）

```
文件: E:\Visual Studio 2026\chanpin\Common7\IDE\devenv.exe.config
第 8 行: <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.7.2" />
```
**[本机实测]** `devenv.exe` FileVersion = `18.11.12224.323 built by: insiders`。

→ VS 2026 主进程仍是 **.NET Framework 4.7.2**。进程内（in-proc）VSSDK 扩展必须 `net472`。
→ `net8.0-windows` / `net10.0-windows` 是 **out-of-proc** `Microsoft.VisualStudio.Extensibility` 的 TFM，与经典 `AsyncPackage` 不是同一条路，**不能混用**。

net472 目标包本机存在 **[本机实测]**：
```
C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2   ← 存在
C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8     ← 不存在
```

### `Microsoft.VSSDK.BuildTools` = `18.9.820`

```
源: https://nuget.azure.cn/v3-flatcontainer/microsoft.vssdk.buildtools/index.json  (HTTP 200)
18.x 线: 18.4.33, 18.5.38461, 18.5.40005, 18.5.40034, 18.6.38345, 18.8.739, 18.9.595, 18.9.820
17.x 线最新: 17.14.2142
```
**[NuGet]** 最新 = **18.9.820**。

> ⚠️ 但本机 **拉不到** 它（§0.2）。本次交付的编译方案**不需要**它。

---

## 0.2 本机网络限制（会挡住正常构建流程）

**[本机实测]** 执行：
```
dotnet restore probe.csproj
→ error NU1301: 无法加载源 https://api.nuget.org/v3/index.json 的服务索引。
             The SSL connection could not be established
             Authentication failed, 安全包中没有可用的凭证
```
- `api.nuget.org` 在本机被重定向到 `https://nuget.azure.cn`，但 TLS 握手失败。
- 改用 `https://nuget.azure.cn/v3/index.json` 作源，**同样 NU1301**。
- PowerShell `Invoke-RestMethod` 对两个域名均报"基础连接已经关闭"。
- 但 DSH 的 `web_fetch` 工具能取到 `nuget.azure.cn` 的 JSON（HTTP 200）→ 说明是**进程级出网被限制**，不是域名不通。

**结论：任何依赖 `dotnet restore` 的方案在本机当前状态下不可行。** 版本号我用 `web_fetch` 查到了，但**包体下不下来**。

---

## 1. VS 2026（Dev18 / 18.x）扩展项目的正确配置

### 1.1 `Microsoft.VSSDK.BuildTools` 版本

- NuGet 上最新：**18.9.820** **[NuGet]**
- 本机**不存在**任何 VSSDK BuildTools 的本地缓存 **[本机实测]**：
  - `E:\Visual Studio 2026\chanpin\VSSDK` → 不存在
  - `E:\Visual Studio 2026\chanpin\Common7\IDE\Extensions\Microsoft\VSSDK` → 不存在
  - `%USERPROFILE%\.nuget\packages` 下无 `vssdk*` / `visualstudio*` 目录
  - `MSBuild` 目录下无 `VSIX`/`VSSDK` 相关 `.targets`
  - 注册的源只有 `nuget.org` 与 `C:\Program Files (x86)\Microsoft SDKs\NuGetPackages\`（后者为空）
- 与 SHARED-FACTS §2"VSSDK 工作负载未安装"一致。

→ 若要走标准 A 方案（`PackageReference` + `Microsoft.VsSDK.targets` 自动打包 VSIX），需要联网还原。**当前不可行。**

### 1.2 `TargetFramework`

见 §0.1：**`net472`**。

### 1.3 `InstallationTarget` 的 Version 范围

**[本机实测]** 微软自己在 18.11 里随箱发布的扩展：
```
文件: E:\Visual Studio 2026\chanpin\Common7\IDE\Extensions\eaql4bhv.34k\extension.vsixmanifest
```
```xml
<Installation ExtensionType="VSSDK+VisualStudio.Extensibility" SystemComponent="true" Experimental="false">
  <InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[18.0, 19.0)">
    <ProductArchitecture>amd64</ProductArchitecture>
  </InstallationTarget>
</Installation>
```

→ **写 `[18.0, 19.0)`**，并加 `<ProductArchitecture>amd64</ProductArchitecture>`。

关于 `[17.0,19.0)`：能覆盖 18.11（18.11 落在区间内），但：
- 它把 17.x 也纳入声明范围，而 SHARED-FACTS §6 记录的卡死案例正是 `[17.0,19.0)` 的第三方 VSIX。这是**相关性，不是因果**，我没有证据证明区间本身导致卡死。
- 收紧到 `[18.0,19.0)` 语义更诚实（我们只在 18.11 上测过）。

**[本机实测] 一个反例，请注意**：本机已装且正常工作的第三方扩展 FittenCode 1.1.1 声明的是
`Version="[17.5.5, 18.0)"`（上界 18.0 **开区间**，本不该接受 18.11），却确实装在 18.11 上。
→ 说明**安装期的版本区间校验并不严格**。不要依赖它做安全边界。
→ 我**未能证实**这是"安装时 VS 是 17.x"还是"VS 忽略了区间"，所以此条只作为观察记录。

### 1.4 `Prerequisites` 里 `Microsoft.VisualStudio.Component.CoreEditor` 的版本范围

同一份微软随箱 manifest **[本机实测]**：
```xml
<Prerequisites>
  <Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor" Version="[18.0,19.0)" DisplayName="Visual Studio core editor" />
</Prerequisites>
```
→ 写 **`[18.0,19.0)`**。

对比：卡死的那个第三方 VSIX 用的是 `[17.0,19.0)` **[本机实测]**。

---

## 2. 工具窗口：`AsyncPackage` vs `Microsoft.VisualStudio.Extensibility`

### 2.1 两种写法都存在且都有官方文档

**A 方案（经典 in-proc）**：`AsyncPackage` + `ProvideToolWindow` + `ToolWindowPane`
- 直接使用 WPF 作为 `ToolWindowPane.Content`，无 RPC 序列化开销。
- **[本机实测]** 我已编译通过（§8.3）。

**B 方案（out-of-proc）**：`Microsoft.VisualStudio.Extensibility`
- 官方工具窗口文档：[Create Visual Studio tool windows](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/tool-window/tool-window?view=visualstudio)
- 写法：继承 `ToolWindow` + `[VisualStudioContribution]`，实现 `ToolWindowConfiguration`，内容通过 `GetContentAsync` 返回 `IRemoteUserControl`。
- **关键限制 [官方文档]**：原文——
  > "Because extensions in VisualStudio.Extensibility might be out-of-process from the IDE, we can't directly use WPF (Windows Presentation Foundation) as a presentation layer for content in Tool Windows. Instead, adding content to a tool window requires creating a **RemoteUserControl** and the corresponding data template for that control."

  → 工具窗口 UI 必须走 **Remote UI**（DataTemplate + XAML over RPC），不能用普通 WPF 控件树。对一个要流式输出 token 的聊天面板，这是**显著的额外复杂度**。

### 2.2 官方对两种模式的定性对比

来源：[Choose the right Visual Studio extensibility model for you](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/extensibility-models?view=visualstudio)（最后更新 2025-09-05）**[官方文档]**

关于 VSSDK：
> "Extensions that use VSSDK run in the same process as Visual Studio itself. Loading in the same process as Visual Studio means that an extension that has an access violation, infinite loop, or other problems **can crash or hang Visual Studio** and degrade the customer experience. And because extensions run in the same process as Visual Studio, they can only be built using .NET Framework."

关于 VisualStudio.Extensibility：
> "isolates bugs that arise from extensions from the rest of Visual Studio, and enables users to install extensions without restarting Visual Studio"

官方比较表（摘录）：

| | VSSDK | Community Toolkit | VisualStudio.Extensibility |
|---|---|---|---|
| Runtime Support | .NET Framework | .NET Framework | .NET |
| Isolation from Visual Studio | ❌ | ❌ | ✅ |
| Async Execution and API | ❌ | ❌ | ✅ |
| VS Scenario Breadth | ✅ | ✅ | ⏳ |
| Installable without Restart | ❌ | ❌ | ✅ |

**官方推荐原文**：
> "Our recommendation is that extenders start with VisualStudio.Extensibility when creating new extensions or enhancing existing ones, and use VSSDK or the Community Toolkit if you run into unsupported scenarios."

**官方也支持混合**：
> "We recommend that in this case, you should adopt a hybrid method of combining VisualStudio.Extensibility and VSSDK. You can author a VisualStudio.Extensibility extension that runs in process, which allows you to access VSSDK or Community Toolkit APIs."

### 2.3 风险对比（针对"不卡死 IDE"这一硬要求）

| | A：AsyncPackage (in-proc) | B：VisualStudio.Extensibility (out-of-proc) |
|---|---|---|
| 崩溃/挂起隔离 | ❌ 与 VS 同进程，官方明说可 hang VS | ✅ 官方明说 "won't crash if an extension crashes or hangs" |
| 网络 I/O 阻塞 UI 的风险 | **存在，需人工纪律**（见 §7） | ✅ 架构上不可能，天然异步 |
| UI 实现成本 | 低（直接用 WPF） | **高**（必须 Remote UI） |
| 本机可编译性 | ✅ **已实测通过** | ❌ **未实测**（NuGet 拉不到，且无 18.x SDK，见 §3） |
| 官方 GA 状态 | GA（VS 2026 随箱） | **Preview** |
| 是否需 VSSDK 工作负载 | 本机未装，但**可绕过**（§8） | 需要，且本机未装 |

---

## 3. `Microsoft.VisualStudio.Extensibility` 是否已 GA / 能否用于 18.11

### 3.1 GA 状态：**未 GA，仍是 Preview** **[官方文档]**

官方页面标题即为：
> **"About VisualStudio.Extensibility (Preview)"**
> "VisualStudio.Extensibility is in active development and is available as a **preview**."
（来源：[VisualStudio.Extensibility overview](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/visualstudio-extensibility?view=visualstudio)，最后更新 2025-06-13）

### 3.2 是否是 out-of-proc：**是，且这是它的核心卖点** **[官方文档]**

来源：[Introduction to VisualStudio.Extensibility for VSSDK users](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/get-started/oop-extensibility-model-overview?view=visualstudio)

> "**Increased reliability** – Extensions written with the new model are out-of-proc and **do not block the Visual Studio UI**. This means that if the extension crashes, VS will not crash as a result."

> "The extensions are hosted in an external ServiceHub process that communicates with Visual Studio via RPC."

→ 对"不卡死"这个需求，B 方案在**架构层面**是正解。

### 3.3 能否用于 18.11：**文档未声明支持 VS 2026** **[官方文档]**

官方 install 段落原文：
> "The current VisualStudio.Extensibility preview works with **Visual Studio 2022 version 17.9 Preview 1 or higher** with the `Visual Studio extension development` workload to be installed."

→ 文档只承诺 **VS 2022 17.9+**，**全文未提及 VS 2026 / 18.x**。

### 3.4 NuGet 版本状况：**没有 18.x 版本** **[NuGet]**

```
https://nuget.azure.cn/v3-flatcontainer/microsoft.visualstudio.extensibility.sdk/index.json
→ 17.0.156-alpha, 17.0.159-alpha, 17.2.18-alpha, 17.9.2092, 17.10.2084,
  17.11.40261, 17.12.40390, 17.13.40008, 17.14.40254, 17.14.40608   ← 最新
https://nuget.azure.cn/v3-flatcontainer/microsoft.visualstudio.extensibility.build/index.json
→ 同样最高 17.14.40608
```
`Microsoft.VisualStudio.SDK`（经典 VSSDK 元包）同样最高 **17.14.40265** **[NuGet]**。

→ **NuGet 上没有 18.x 的任何 VSSDK/Extensibility SDK**。VS 18.11 随箱的 out-of-proc 宿主却是 18.9：
```
E:\Visual Studio 2026\chanpin\Common7\IDE\Extensions\eaql4bhv.34k\Microsoft.VisualStudio.Extensibility.dll
  FileVersion = 18.9.131.64479  (ProductVersion 18.9.131+fbdf9fcd36)
Microsoft.VisualStudio.Extensibility.Contracts.dll  18.9.131.30262
Microsoft.VisualStudio.Extensibility.Framework.dll  18.9.131.64479
```
**[本机实测]**

### 3.5 ⚠️ 我没能证实的一条（重要）

**`Microsoft.VisualStudio.Extensibility.Sdk` 17.14.40608 构建出的扩展，是否真能被 VS 18.11 的 18.9 宿主加载运行——我没有证实。**

为什么没证实：
1. NuGet 拉不到（§0.2），无法还原 SDK 包来试编译；
2. 官方文档只声明支持 VS 2022 17.9+，未提 18.x；
3. 微软随箱那个 18.x 扩展的 `manifest.json` 里写着 `"sdk":"18.0"` **[本机实测]**，而 NuGet 上根本没有 `18.x` 的 Build 包来生成这个字段——**17.14.40608 是否生成 `sdk:"18.0"`，未知**。

→ 这是**选择 B 方案的最大未知风险**。任何选 B 的决定都必须先解决这件事。

---

## 4. VSIX 静默安装与退出码

### 4.1 安装器位置 **[本机实测]**
```
E:\Visual Studio 2026\chanpin\Common7\IDE\VSIXInstaller.exe
  FileVersion = 18.11.994.14109   Size = 75,096 bytes
```
它只有 75 KB —— 是个 **trampoline（转发器）**。二进制里能读到的字符串 **[本机实测]**：
```
VSIXInstaller.Trampoline
dd_VSIXInstaller.Trampoline_{0:yyyyMMddHHmmss}_{1:x4}.log
ExitCodeEngineInstallerNotFound
ExitCodeUnexpectedError
get_ExitCode
```
→ 真正的安装引擎不在这 75 KB 里。

### 4.2 静默安装命令

`VSIXInstaller.exe /quiet /instanceIds:<id> <path>` —— **[未找到出处]**
我**没有**找到微软官方文档来证实这个确切语法与 `instanceIds` 参数名。搜到的都是 StackOverflow / 社区页面，例如：
- [VSIXInstaller.exe exit code documentation (Stack Overflow)](https://stackoverflow.com/questions/21531596/vsixinstaller-exe-exit-code-documentation)
- [VSIX in Silent Mode (MSDN 归档论坛)](https://learn.microsoft.com/nl-nl/archive/msdn-technet-forums/f420d6a1-f372-4bab-9970-b9c235a680cc)

**我不猜。** 我**没有**运行 `VSIXInstaller.exe /?` —— 它是 GUI 应用，在当前无人值守环境下可能弹窗挂起。要在 Lead 的机器上确认，请在有交互桌面时手动跑一次。

### 4.3 退出码含义：**[未找到出处]**

官方**没有**公开 VSIXInstaller 退出码表。我在以下二进制里提取字符串 **[本机实测]**：
```
Common7\IDE\VSIXInstaller.exe
Common7\IDE\PrivateAssemblies\Microsoft.VisualStudio.ExtensionManager.dll
Common7\IDE\CommonExtensions\Platform\ExtensionManager\Microsoft.VisualStudio.ExtensionManager.Implementation.dll
```
能匹配到 `exitcode` 的只有 `get_ExitCode` / `exitCode` / `ExitCodeEngineInstallerNotFound` / `ExitCodeUnexpectedError`，**没有完整枚举**。

→ **不要依赖具体数字退出码做自动化判断**，改用 §5 的 ActivityLog 证据。

### 4.4 非管理员能否安装：**能，且有本机实例为证**

**[本机实测]** 当前用户 `DESKTOP-A8UB9FP\zx`，`IsInRole(Administrator)` = **False**。

**机器级目录不可写** **[本机实测]**：
```
E:\Visual Studio 2026\chanpin\Common7\IDE\Extensions
  Owner: BUILTIN\Administrators
  BUILTIN\Users -> ReadAndExecute, Synchronize
  写测试(Open Write) -> 访问被拒绝
```

**per-user 目录可写** **[本机实测]**：
```
C:\Users\zx\AppData\Local\Microsoft\VisualStudio\18.0_bdfb6cb1\Extensions
  Owner: BUILTIN\Administrators
  DESKTOP-A8UB9FP\zx -> FullControl          ← 当前用户完全控制
```

**最强证据：本机已有一个非管理员装入、且正常工作的第三方扩展** **[本机实测]**：
```
C:\Users\zx\AppData\Local\Microsoft\VisualStudio\18.0_bdfb6cb1\Extensions\cxlazeur.xvf\
  extension.vsixmanifest   (FittenCode.VisualStudio2, Version 1.1.1, Publisher FittenTech)
  FittenCode.dll / FittenCode.pkgdef
  Community.VisualStudio.Toolkit.dll  (415 KB)
  manifest.json / catalog.json
```
- 它的 `<Assets>` 是经典的 `Microsoft.VisualStudio.VsPackage` + `Microsoft.VisualStudio.MefComponent` **[本机实测]**。
- 机器级 `Extensions\iec3i2ia.k4n`（其 manifest.json 声称的 `extensionDir`）**并不存在**，也没有 junction/reparse point **[本机实测]** → 它是纯 per-user 安装。

> ⚠️ 注意区分：我用 `[IO.File]::WriteAllText` 写 per-user 目录时报"访问被拒绝"，**但那是 DSH 沙箱拦截，不是 NTFS 拒绝**。证据：同一命令写 `C:\Users\zx\.__probe2` 也被拒，而写工作区 `Documents` 成功；同时该目录 ACL 明确给当前用户 `FullControl`。**不要把这个错误归因为 Windows 权限。**

**结论**：非管理员**可以**安装 VSIX，走 per-user 路径。机器级安装需要管理员。

---

## 5. 自动验证扩展是否被加载

### 5.1 ActivityLog 路径 **[官方文档] + [本机实测]**

官方 [/Log (devenv.exe)](https://learn.microsoft.com/en-us/visualstudio/ide/reference/log-devenv-exe?view=vs-2022)：
> "By default, the log file is located here: `%APPDATA%\Microsoft\VisualStudio\<Version>\ActivityLog.xml`"
> 用法：`devenv /Log NameOfLogFile`（此开关**必须放在命令行最后**）

本机实际路径已存在 **[本机实测]**：
```
C:\Users\zx\AppData\Roaming\Microsoft\VisualStudio\18.0_bdfb6cb1\ActivityLog.xml
  Size = 461,408 bytes    LastWriteTime = 2026/10/4 1:21:38
```
（`18.0_bdfb6cb1` 中的 `bdfb6cb1` 与 SHARED-FACTS §2 的实例 ID 一致）

### 5.2 日志的实际结构 **[本机实测]**

根元素 `<activity>`，每个条目含 `<type>` / `<source>` / `<description>` / `<guid>` / `<time>`。
本机这份日志的分布：
- `<type>`：`Information` 560、`Warning` 9、`Error` 3
- `<source>` 前几名：`VisualStudio` 207、`VsHubClient` 120、`ContextRuleMonitorService` 99、`UI context rules` 77、`Profile manager` 33，另有 `Package Framework`、`AutoLoadManager`、`MefHosting` 等

### 5.3 如何用它"证明 package 已加载且无异常"

**可执行的判定思路**（基于上述真实结构，非官方文档承诺）：
1. `devenv /Log <path>` 启动（开关放最后），正常关闭 VS，确保日志被 flush。
2. **证明已加载**：在 `<activity>` 中检索你的包 GUID（`ProvideToolWindow`/`PackageRegistration` 的 `[Guid]`）或包类名，应出现来自 `VisualStudio` / `Package Framework` 的 `Information` 条目。
3. **证明无异常**：检索 `<type>Error</type>`，且错误条目的 `<source>`/`<description>` 不涉及你的包名/GUID。
4. ⚠️ **重要**：`<type>Error</type>` 在本机现有日志里**已经有 3 条**（属于 VS 自身）。所以判据必须是"**没有与我方 GUID 关联的 Error**"，而不是"日志里没有 Error"。

**我没能证实的**：上述"包加载会在 ActivityLog 留哪条确切的 description 文本"我没有实证样本（当前日志里没有我自己的扩展）。**[未找到出处]** 具体的 `Package Load` 描述字符串。

### 5.4 更硬的一条验证（我建议 Lead 用这个）

**[本机实测]** 已加载扩展的落地目录本身可枚举，且 VS 会写 per-user 扩展元数据缓存：
```
C:\Users\zx\AppData\Local\Microsoft\VisualStudio\18.0_bdfb6cb1\Extensions\
  cxlazeur.xvf\                    ← 已安装扩展的落地目录
  CompatibilityList.xml            ← VS 的兼容性黑名单（FileVersion=74, ChangesetId=43453559）
  ExtensionMetadata2.0.mpack       ← 扩展元数据缓存（2026/10/7 更新）
  ExtensionMetadataCache.mpack
  extensions.configurationchanged  ← 配置变更标记
```
→ 安装后 `cxlazeur.xvf` 这类随机目录出现 + `extensions.configurationchanged` 时间戳更新，是**安装成功**的直接证据。**[本机实测]**

### 5.5 `CompatibilityList.xml` 值得注意 **[本机实测]**

该文件是 VS 主动**禁用**某些扩展的机制，含 `<Action>Disable</Action>` / `<Action>Warning</Action>` 与 `VisualStudioVersion` 区间。例：
```xml
<Extension><ExtensionId>NuGetSolver.ecccc3de-...</ExtensionId>
  <ExtensionVersion>[0.1,)</ExtensionVersion><Action>Disable</Action>
  <VisualStudioVersion>[18.0,)</VisualStudioVersion></Extension>
```
→ 若你的扩展 ID 命中黑名单会被静默禁用。**排查"扩展没加载"时先查这个文件。**

---

## 6. BYOM 注册文件的写入格式

### 6.1 实测文件内容 **[本机实测]**

路径（与 SHARED-FACTS §4 一致）：
```
%LOCALAPPDATA%\Microsoft\VisualStudio\Copilot\BringYourOwnModel\ConfiguredBringYourOwnModel_v2.json
C:\Users\zx\AppData\Local\Microsoft\VisualStudio\Copilot\BringYourOwnModel\ConfiguredBringYourOwnModel_v2.json
Size = 868 bytes   LastWriteTime = 2026/10/4 14:33:50
```
当前完整内容：
```json
[
  {
    "Name": "ollama",
    "IsApiKeyAvailable": false,
    "Models": [
      {
        "Id": "qwen3-coder:30b",
        "DisplayName": "qwen3-coder:30b (Qwen3 Coder 30B)",
        "IsToolCallingEnabled": true,
        "IsVisionEnabled": false,
        "MaxInputTokens": 65536,
        "MaxOutputTokens": 8192,
        "ProviderName": "ollama",
        "IsCustom": true,
        "IsSelected": true
      },
      {
        "Id": "qwen3:4b-local",
        "DisplayName": "qwen3:4b-local (Qwen3 4B)",
        "IsToolCallingEnabled": true,
        "IsVisionEnabled": false,
        "MaxInputTokens": 65536,
        "MaxOutputTokens": 8192,
        "ProviderName": "ollama",
        "IsCustom": true,
        "IsSelected": true
      }
    ],
    "Endpoint": 10,
    "IsEnabled": true,
    "CustomURL": "http://127.0.0.1:11435/v1"
  }
]
```

### 6.2 格式要点（据实测内容 + SHARED-FACTS §4）

- 顶层是 **JSON 数组**（不是对象）。
- 每个元素是一个 **provider**，字段：`Name`、`IsApiKeyAvailable`、`Models[]`、`Endpoint`、`IsEnabled`、`CustomURL`。
- `Endpoint` 是**整数枚举**：`10` = Ollama（SHARED-FACTS §4 反汇编得到的枚举表，本机文件里 Ollama provider 正是 `Endpoint: 10` → **互相印证**）。
- `CustomURL` **必须非 null**（SHARED-FACTS §4 实测：为 null 时请求静默消失）。本机文件为非 null。
- 每个 model 的字段：`Id`、`DisplayName`、`IsToolCallingEnabled`、`IsVisionEnabled`、`MaxInputTokens`、`MaxOutputTokens`、`ProviderName`、`IsCustom`、`IsSelected`。

### 6.3 同目录还有备份文件 **[本机实测]**
```
ConfiguredBringYourOwnModel_v2.json                     868  2026/10/4 14:33:50
ConfiguredBringYourOwnModel_v2.json.bak-before-64k       868  2026/10/4 12:59:48
ConfiguredBringYourOwnModel_v2.json.bak-before-customurl 845  2026/10/4 1:15:58
```
→ 上一轮实测者手工加的备份。**这本身是个信号：改这个文件是会被 VS 回写的、需要先备份的操作。**

同目录还有 `McpServers\` 子目录。

### 6.4 "扩展写这个文件"是否比"用户手动配"更靠谱？

**证据能支持的部分：**
- 文件是**纯 JSON、无签名、无锁**，路径固定，扩展可写（该路径在用户 profile 下，扩展进程有权限）**[本机实测格式]**
- 手工配置**已被证明极容易出错**：SHARED-FACTS §4 记录了 `CustomURL` 为 null 时"模型出现在选择器、VS 报 `1 usable provider`，但发消息请求静默消失（无 HTTP、无报错）"——**没有任何错误提示**，用户几乎不可能自己诊断。**[SHARED-FACTS 实测]**

**证据不能支持的部分（必须诚实标注）：**
- **我没有验证过"扩展写这个文件后 VS 会正确加载"**。我没有做写入实验（任务限定只读）。
- **VS 会回写该文件**（SHARED-FACTS §4 明说"VS 会回写"，且本机文件 LastWriteTime 与模型配置时间一致）**[本机实测]**。这意味着扩展写入后可能被 VS 覆盖/规范化，**存在竞态**。
- 文件的 schema **没有官方文档**。搜不到 `ConfiguredBringYourOwnModel_v2.json` 的任何官方说明。**[未找到出处]** 该文件是 `Microsoft.VisualStudio.Extensibility.Copilot.Configuration.dll`（18.9.1237.3270，**[本机实测]** 存在于 `Extensions\eaql4bhv.34k\`）的私有实现细节，**随版本可能变化，无兼容性承诺**。

**我的判断（标注为判断，非事实）**：扩展代写**在"降低用户配置错误率"上明显更优**（尤其能自动保证 `CustomURL` 非 null），但因为它依赖无文档私有 schema + VS 会回写，**必须**做到：写前备份、写后校验、并显式提示"此文件由 VS 拥有，可能被覆盖"。**不要把它当作稳定 API。**

---

## 7. 反面证据：VS 扩展导致 IDE 卡死的已知原因

### 风险点 1：in-proc 扩展可直接挂起 VS（官方定性） **[官方文档]**

[Choose the right Visual Studio extensibility model for you](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/extensibility-models?view=visualstudio)：
> "Loading in the same process as Visual Studio means that an extension that has an access violation, infinite loop, or other problems **can crash or hang Visual Studio**"

同页针对 Community Toolkit / VSSDK 的线程问题：
> "the code itself is still executed from the UI thread, and **it's on the extension developer to figure out how to correctly switch to a background thread if you don't want to risk freezing the UI**"
> "Threading in Visual Studio is hard to get right and requires the proper use of `JoinableTaskFactory` to avoid deadlocks. **The struggle to write code that deals with threading correctly has been a constant source of bugs, even for our internal Visual Studio engineers.**"

→ 直接对应本任务："在 UI 线程做 HTTP"就是这一类错误。**这是官方承认的、最常见的卡死成因。**

### 风险点 2：MEF 部件构造期的 UI 线程亲和 → 死锁卡死（官方分析器文档） **[官方文档]**

来源：[VSSDK008 Avoid UI thread affinity in MEF Part construction](https://microsoft.github.io/VSSDK-Analyzers/analyzers/VSSDK008.html)（Microsoft.VisualStudio.SDK.Analyzers）

> "MEF is not a JoinableTaskFactory-aware library, and as MEF parts are often activated as singletons, **deadlocks can easily be introduced** when activating a MEF part incurs a dependency on the main thread."
> "When Visual Studio makes attempt to load a UI thread-affinitized part, its initialization will either throw an exception, rendering the MEF unusable, or **lead to a deadlock, freezing Visual Studio**."

**而且这个风险正在变大** —— 同页 Background 段：
> "Editor extensions in Visual Studio **17.14** may be preloaded on background threads when the following Preview Features are enabled: 'Initialize editor parts asynchronously during solution load', 'Asynchronously Load Documents During Solution Load'. **In the future releases, these Preview Features will be enabled by default.**"

→ 可执行结论：**不要在 MEF 导出类的构造函数 / `[ImportingConstructor]` / `OnImportsSatisfied` / 字段初始化器里碰 UI 线程或做同步阻塞**。用 `AsyncLazy` 或属性延迟获取服务。VS 18.x 正在把这条从"建议"变成"默认生效"。

### 风险点 3（本机实物证据，但**因果未证实**）：卡死 VSIX 的 manifest 差异

**[本机实测]** 对比三个 manifest：

| | 微软随箱（18.11，正常） | FittenCode 1.1.1（本机，正常工作） | LocalLLMChat 1.0.5（SHARED-FACTS §6 记录卡死） |
|---|---|---|---|
| `<Installation>` 的 `ExtensionType` | `VSSDK+VisualStudio.Extensibility` | **缺失** | **缺失** |
| InstallationTarget Version | `[18.0, 19.0)` | `[17.5.5, 18.0)` | `[17.0,19.0)` ×3 SKU |
| Prerequisite CoreEditor | `[18.0,19.0)` | `[17.0,)` | `[17.0,19.0)` |
| Assets | `MefComponent` + `VsPackage` | `VsPackage` + `MefComponent` | `VsPackage` |
| pkgdef `AllowsBackgroundLoad` | — | `dword:00000001` | `dword:00000001` |
| 是否卡死 | 否 | **否** | **是（SHARED-FACTS 实测）** |

**必须诚实说明：**
- 我**原本怀疑** `AllowsBackgroundLoad=1` 是卡死原因，**但这个假设被 FittenCode 证伪了** —— FittenCode 同样设了 `AllowsBackgroundLoad=dword:00000001`，却工作正常。**因果不成立，撤回该假设。**
- `ExtensionType` 属性缺失也是 FittenCode 与卡死者的**共同点**，所以同样**不能**解释差异。
- **我无法从 manifest 差异推出卡死原因。** 卡死的真正原因很可能在 `LocalLLMChatVS.dll`（129,536 bytes）的内部实现里 —— 我没有反编译它，也没有运行它（会再次卡死 VS）。**本条目仅作为"两份可对比样本"存档，不构成因果结论。**

### 风险点 4：`Microsoft.VisualStudio.Extensibility.Editor.Contracts.dll` 被打进 in-proc VSIX

**[本机实测]** 卡死的 VSIX 内容物：
```
LocalLLMChatVS.dll                                         129,536
Microsoft.VisualStudio.Extensibility.Editor.Contracts.dll   61,968   ← 值得警惕
Microsoft.VisualStudio.Linux.ConnectionManager.Store.dll    19,504   ← 与聊天功能无关
System.Text.Encodings.Web.dll                               79,664
LocalLLMChatVS.pkgdef                                        2,712
```
但它的 pkgdef 把包注册为**纯 in-proc**：
```
[$RootKey$\Packages\{7d8e9f0a-...}]
"InprocServer32"="$WinDir$\SYSTEM32\MSCOREE.DLL"
"Class"="LocalLLMChatVS.LocalLLMChatPackage"
"CodeBase"="$PackageFolder$\LocalLLMChatVS.dll"
```
→ 一个 in-proc VSPackage 携带 out-of-proc SDK 的 Contracts 程序集，**命名空间与架构上是混搭**。这**可能**带来程序集加载冲突，但我**没有证据**。**标注为"值得怀疑，未证实"。**

---

## 8. 给 Lead 的直接建议

### 8.1 推荐：**C 方案 —— 经典 A 方案的离线变体**

**一句话理由**：本机 NuGet 完全不可用（`NU1301`），而**经典 `AsyncPackage` + 本机 DLL 引用 + 手写 VSIX 这条路我已经实测编译成功**；B 方案虽然架构上更符合"不卡死"目标，但它是 Preview、没有 18.x SDK、必须走 Remote UI、且本机连包都拉不下来 —— **当前风险不可控**。

**但必须配合纪律来弥补 A 方案的架构缺陷**（§7 风险点 1、2）：
1. **绝不在 UI 线程做 HTTP。** 所有 Ollama 调用走 `Task.Run` / 后台线程 + `CancellationToken`。
2. 工具窗口初始化用 `AsyncLazy`，**不在构造函数/MEF 构造期**同步取服务。
3. `InitializeAsync` 里不要做可能长时间阻塞的事；`AllowsBackgroundLoad` 只在你确认初始化是纯后台时使用。
4. 用 `JoinableTaskFactory.SwitchToMainThreadAsync()` 回到 UI 线程更新界面，不要在后台线程碰 WPF 控件。

### 8.2 最小可编译 csproj（**已在本机实测通过**）

实测环境：`dotnet build`（.NET SDK 10.0.401），结果 `ext -> bin\Debug\net472\ext.dll`，**0 error**，1.58s。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <VsInstall>E:\Visual Studio 2026\chanpin</VsInstall>
  </PropertyGroup>
  <!-- 注意：没有任何 PackageReference。全部走本机 VS 安装目录。 -->
  <ItemGroup>
    <Reference Include="Microsoft.VisualStudio.Shell.15.0">
      <HintPath>$(VsInstall)\Common7\IDE\PublicAssemblies\Microsoft.VisualStudio.Shell.15.0.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Microsoft.VisualStudio.Shell.Framework">
      <HintPath>$(VsInstall)\Common7\IDE\PublicAssemblies\Microsoft.VisualStudio.Shell.Framework.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Microsoft.VisualStudio.Shell.Interop">
      <HintPath>$(VsInstall)\Common7\IDE\PublicAssemblies\Microsoft.VisualStudio.Shell.Interop.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Microsoft.VisualStudio.Shell.Interop.8.0">
      <HintPath>$(VsInstall)\Common7\IDE\PublicAssemblies\Microsoft.VisualStudio.Shell.Interop.8.0.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Microsoft.VisualStudio.OLE.Interop">
      <HintPath>$(VsInstall)\Common7\IDE\PublicAssemblies\Microsoft.VisualStudio.OLE.Interop.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Microsoft.VisualStudio.Imaging.Interop.14.0.DesignTime">
      <HintPath>$(VsInstall)\Common7\IDE\PublicAssemblies\Microsoft.VisualStudio.Imaging.Interop.14.0.DesignTime.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Microsoft.VisualStudio.Utilities">
      <HintPath>$(VsInstall)\Common7\IDE\Microsoft.VisualStudio.Utilities.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Microsoft.VisualStudio.Imaging">
      <HintPath>$(VsInstall)\Common7\IDE\Microsoft.VisualStudio.Imaging.dll</HintPath><Private>false</Private></Reference>
    <!-- ⚠️ 下面两条是唯一会 error CS1705 的坑，路径必须精确 -->
    <Reference Include="Microsoft.VisualStudio.Threading">
      <HintPath>$(VsInstall)\Common7\IDE\PublicAssemblies\Microsoft.VisualStudio.Threading.17.x\Microsoft.VisualStudio.Threading.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Microsoft.VisualStudio.Validation">
      <HintPath>$(VsInstall)\Common7\IDE\PrivateAssemblies\Microsoft.VisualStudio.Validation.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="System.ComponentModel.Composition" />
    <Reference Include="System.Xaml" />
    <Reference Include="PresentationCore" />
    <Reference Include="PresentationFramework" />
    <Reference Include="WindowsBase" />
  </ItemGroup>
</Project>
```

**踩坑记录（我第一遍就是这么失败的）**：
`Microsoft.VisualStudio.Shell.15.0.dll`（程序集版本 **18.0.0.0**，来自 VS 18.11）要求 `Microsoft.VisualStudio.Threading` 程序集版本 **18.7.0.0**。
用 `CommonExtensions\Microsoft\NuGet\MCP\` 下那份（文件版本 17.14.15，程序集 17.14.0.0）会直接编译失败：
```
CSC : error CS1705: 标识为"Microsoft.VisualStudio.Shell.15.0, Version=18.0.0.0"的程序集
      所使用的"Microsoft.VisualStudio.Threading, Version=18.7.0.0"版本高于所引用的
      "Microsoft.VisualStudio.Threading, Version=17.14.0.0"
```

**正确的 18.7 来源** **[本机实测]**：
```
Common7\IDE\PublicAssemblies\Microsoft.VisualStudio.Threading.17.x\Microsoft.VisualStudio.Threading.dll
  文件版本 18.7.65.46300  →  程序集版本 18.7.0.0
Common7\IDE\PrivateAssemblies\Microsoft.VisualStudio.Validation.dll
  文件版本 18.7.19.48119  →  程序集版本 18.7.0.0
```

### 8.3 实测验证过的源码形态

我编译的正是一个**真实的工具窗口扩展**（不是空壳），以下类型全部从本机 DLL 解析成功：
```csharp
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideToolWindow(typeof(ProbePane), Style = VsDockStyle.Tabbed,
                   Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
[Guid("11112222-3333-4444-5555-666677778888")]
public sealed class ProbePackage : AsyncPackage
{
    protected override async Task InitializeAsync(CancellationToken ct, IProgress<ServiceProgressData> p)
        => await JoinableTaskFactory.SwitchToMainThreadAsync(ct);
}

public sealed class ProbePane : ToolWindowPane
{
    public ProbePane() : base(null)
    {
        Caption = "Probe";
        Content = new System.Windows.Controls.TextBox { Text = "hello" };  // 原生 WPF，A 方案的优势
    }
}
```
输出：`ext.dll` 6,656 bytes，`ProcessorArchitecture=MSIL`，**输出目录只有 ext.dll / ext.pdb**（`<Private>false</Private>` 生效，没有把 VS 程序集复制进去）**[本机实测]**。

唯一警告：`CS0618: InstalledProductRegistrationAttribute 已过时 —— "Visual Studio no longer uses the data provided by this attribute. Use a VSIX manifest instead."`
→ **别用 `InstalledProductRegistration` attribute**，产品信息写在 VSIX manifest 里。

### 8.4 VSIX 打包：手写，不用 BuildTools

因为 `Microsoft.VSSDK.BuildTools` 拉不到（§0.2），VSIX 就是**普通 zip**。需要这些文件：
```
extension.vsixmanifest    ← manifest
[Content_Types].xml       ← OPC 必需
<YourExt>.dll
<YourExt>.pkgdef
Resources\Icon.png        ← 可选
```

`extension.vsixmanifest`（照抄微软随箱扩展的结构，§1.3）：
```xml
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
  <Metadata>
    <Identity Id="YourExt.YourGuid" Version="1.0.0" Language="en-US" Publisher="You" />
    <DisplayName>...</DisplayName>
    <Description>...</Description>
  </Metadata>
  <Installation ExtensionType="VSSDK">
    <InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[18.0, 19.0)">
      <ProductArchitecture>amd64</ProductArchitecture>
    </InstallationTarget>
  </Installation>
  <Prerequisites>
    <Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor" Version="[18.0,19.0)" DisplayName="Visual Studio core editor" />
  </Prerequisites>
  <Assets>
    <Asset Type="Microsoft.VisualStudio.VsPackage" Path="YourExt.pkgdef" />
  </Assets>
</PackageManifest>
```
> 注：`ExtensionType="VSSDK"` 是**我的推断**——微软随箱扩展写的是 `"VSSDK+VisualStudio.Extensibility"`（因为它同时有两种组件），而**本机两个纯经典扩展（FittenCode、LocalLLMChat）都完全省略了这个属性**。**[未找到出处]** 未找到该属性的取值规范。**安全做法：照 FittenCode 省略它**，或先手工装一次验证。

`pkgdef` **必须带 BOM** **[本机实测]**：
- 工作正常的 FittenCode：首字节 `FF FE`（UTF-16LE BOM）
- 卡死的 LocalLLMChat：首字节 `FF FE`（同样 UTF-16LE BOM）
- VS 随箱 pkgdef：首字节 `EF BB BF`（UTF-8 BOM）
→ **两种都能用，但都不能没有 BOM。**

工具窗口注册（照 FittenCode 的实际注册项）**[本机实测]**：
```
[$RootKey$\ToolWindows\{你的工具窗口GUID}]
@="{你的包GUID}"
"Name"="YourNamespace.YourPane"
"Orientation"="Right"
"Style"="Tabbed"
"Window"="{3AE79031-E1BC-11D0-8F78-00A0C9110057}"
```

### 8.5 若坚持选 B 方案，必须先解决的前置问题

1. 确认 `Microsoft.VisualStudio.Extensibility.Sdk` **17.14.40608** 生成的扩展能被 VS 18.11 的 18.9 宿主加载（**本文件 §3.5 的未证实项**）。
2. 解决 NuGet 不可达问题（否则连包都拿不到）。
3. 接受工具窗口必须走 Remote UI（§2.1）。
4. 接受 Preview 状态与"无 VS 2026 官方支持声明"（§3.1、§3.3）。

官方推荐的折中是**混合模式**：`VisualStudio.Extensibility` 扩展以 in-proc 方式运行，从而继续使用 VSSDK API（[in-proc extensions](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/get-started/in-proc-extensions)）。但**这仍然是 Preview**，且不解决本机 NuGet 问题。

---

## 9. 我没能证实的事项（完整清单，绝不猜）

| # | 未证实事项 | 原因 |
|---|---|---|
| 1 | **`Microsoft.VisualStudio.Extensibility.Sdk` 17.14.40608 能否在 VS 18.11 上加载运行** | NuGet 不可达无法试编译；官方只声明支持 VS 2022 17.9+；NuGet 上无 18.x 版本 |
| 2 | `VSIXInstaller.exe /quiet /instanceIds:<id>` 的确切语法 | **[未找到出处]** 无官方文档；未运行 `/?`（GUI 可能弹窗挂起） |
| 3 | VSIXInstaller 退出码的确切数值含义 | **[未找到出处]** 官方无公开表；二进制中只提取到 `ExitCodeEngineInstallerNotFound` / `ExitCodeUnexpectedError` |
| 4 | `manifest.json` 里 `"sdk":"18.0"` 字段由哪个包生成 | 无法还原 SDK 包验证 |
| 5 | `ExtensionType` 属性的取值规范（`"VSSDK"` 是否合法） | **[未找到出处]** 微软随箱写 `VSSDK+VisualStudio.Extensibility`，第三方两个样本都省略 |
| 6 | `ConfiguredBringYourOwnModel_v2.json` 的官方 schema | **[未找到出处]** 无任何官方文档，属私有实现 |
| 7 | 卡死 VSIX 的**真正**原因 | 只有 manifest 层面对比，无因果证据；未反编译/运行其 DLL |
| 8 | FittenCode 的 `[17.5.5, 18.0)` 为何被 18.11 接受 | 安装时 VS 版本未知 |
| 9 | ActivityLog 中"包已加载"的确切 description 文本 | 现有日志无我方扩展样本；**[未找到出处]** |
| 10 | 手写 VSIX 能否被 VS 18.11 接受安装 | 未实际安装（任务限定只读，且会污染用户的 VS 实例） |

---

## 10. 复现命令（供 Lead 与后续 Agent 核对）

```powershell
# 1) 确认 in-proc TFM
Select-String -Path "E:\Visual Studio 2026\chanpin\Common7\IDE\devenv.exe.config" -Pattern "supportedRuntime"

# 2) 确认 net472 目标包存在
Test-Path "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2"

# 3) 确认 Threading 版本（CS1705 的根源）
Get-ChildItem "E:\Visual Studio 2026\chanpin\Common7\IDE\PublicAssemblies\Microsoft.VisualStudio.Threading.17.x" |
  ForEach-Object { $_.VersionInfo.FileVersion }

# 4) 确认机器级目录对非管理员不可写
(Get-Acl "E:\Visual Studio 2026\chanpin\Common7\IDE\Extensions").Access |
  Where-Object IdentityReference -match "Users"

# 5) 确认 per-user 目录可写
(Get-Acl "$env:LOCALAPPDATA\Microsoft\VisualStudio\18.0_bdfb6cb1\Extensions").Access |
  Where-Object IdentityReference -match "zx"

# 6) ActivityLog 位置
Test-Path "$env:APPDATA\Microsoft\VisualStudio\18.0_bdfb6cb1\ActivityLog.xml"

# 7) BYOM 配置
Get-Content "$env:LOCALAPPDATA\Microsoft\VisualStudio\Copilot\BringYourOwnModel\ConfiguredBringYourOwnModel_v2.json" -Raw

# 8) 复现离线编译（在临时目录，不碰 src/）
dotnet build "$env:TEMP\vssdk-probe-2\ext.csproj" -v minimal
```

> 注：`nuget.azure.cn` 的版本索引我在本机是通过 DSH 的 `web_fetch` 工具取得的（`Invoke-RestMethod` 与 `dotnet restore` 均失败）。复现第 8 步不需要网络。
