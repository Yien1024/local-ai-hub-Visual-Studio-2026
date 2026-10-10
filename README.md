# Local AI Hub for Visual Studio 2026

**English** · [中文文档](README.zh-CN.md)

Use local **Ollama** models directly inside Visual Studio 2026 — with **web search that actually works**, model management, and one-click registration into Copilot's bring-your-own-model (BYOM) list.

**Zero NuGet dependencies · Zero API keys · Builds fully offline · Does not depend on the GitHub Copilot cloud**

```
Tools → 本地大模型对话(&L)...   (Local model chat)
```

> 💡 **Why this exists**: on VS 2026 you can point Copilot at a local Ollama model, but
> you cannot make it search the web (MCP servers stay disabled in this build), and the
> BYOM config silently drops requests if one field is wrong. This extension is a
> self-contained local-model client instead of a Copilot add-on.

---

## Why you need it

VS 2026's Copilot supports "BYOM" (bring your own model) for local Ollama. In practice three problems block the obvious setup:

| Problem | Measured evidence |
|---|---|
| **Copilot's MCP servers won't enable** | A self-authored MCP server stays `UserDisabled`, and so does Microsoft's own Microsoft Learn MCP. `EnabledMcpServers` stays empty and the setting is hidden behind `visibleWhen:'false'`. All 12 workarounds failed. |
| **One wrong BYOM field silently kills every request** | With `CustomURL` null, the model appears in the picker and VS reports `1 usable provider`, but sending a message **produces no request at all** — no error, no HTTP traffic. |
| **Local models can't search** | Model knowledge has a cutoff. Worse, the Copilot pane itself still needs `api.githubcopilot.com` to initialise, so on restricted networks it spins forever while your local model receives nothing. |

This extension solves all three: **it is a complete local-model client of its own** (no Copilot cloud needed), the web search lives inside it (no MCP needed), and it writes the BYOM config correctly for you.

---

## Features

- **Streaming chat** with any local Ollama model, with a stop button
- **Web search**: Bing (China node) → 360 → Baidu automatic fallback, **no API keys, no cost**
  - Auto-decides whether a question needs the web (pure concepts / pure coding / follow-ups skip it, saving ~11 s and ~1500 prompt tokens)
  - Extracts the **verbatim error string** as the query instead of the whole sentence (measured: whole sentence → tutorial homepages; verbatim error → 5/5 on-topic fixes)
  - Runs a **relevance gate** before injection (content farms dropped, low scores not injected)
  - When nothing relevant is found it tells the model so, instead of stuffing noise into the prompt
  - Injected block carries a **time anchor**, `[S1]` source numbers, and permission to ignore irrelevant results
  - Page fetching has a **hard wall-clock deadline** (`HttpClient`'s timeout does not cover DNS resolution — one page hung for 145 s in testing)
- **Model management**: list / pull (with progress and speed) / delete / preload and keep resident / unload from VRAM
- **VRAM check**: shows `ollama ps` placement — whether it is `100% GPU` or spilling to CPU
- **BYOM registration**: writes your local models into Copilot's bring-your-own-model config (auto-backup before writing)
- **Search test**: exercise the search chain without involving the model
- **Run log panel**: every decision is visible (why search was skipped, the query, the engine, how many results survived)
- **Manual control**: put `[search]` or `[no-search]` anywhere in a message

---

## Install

### Prerequisites

- Windows + **Visual Studio 2026** (developed and verified on Insiders 18.11; the manifest declares `[18.0,19.0)`)
- **Ollama** running (`ollama list` returns something)
- At least one model, e.g. `ollama pull qwen3-coder:30b`

### Steps

1. **Fully exit Visual Studio** — not minimized. `devenv.exe` / `copilot.exe` block the install.
2. Double-click `dist/LocalAIHub-1.0.0.vsix` and approve the UAC prompt.
3. Restart VS → **Tools → 本地大模型对话(&L)...**
   (also available under **View → Other Windows → Local AI Hub**)

Command-line equivalent:

```powershell
& "<VS install dir>\Common7\IDE\VSIXInstaller.exe" /quiet /instanceIds:<instance id> "dist\LocalAIHub-1.0.0.vsix"
```

> Installing into `Program Files` needs administrator rights, so **the UAC prompt must be approved**.
> If you get `BlockingProcessesException`, Visual Studio is still running.

### Uninstall

```powershell
& "<VS install dir>\Common7\IDE\VSIXInstaller.exe" /quiet /uninstall:LocalAIHub.9f1c4a02-7d3e-4b18-9c55-0a7f2e5b81d4
```

### Verify it loaded

After opening the tool window, `%TEMP%\LocalAIHub\localaihub.log` should appear and contain `LocalAIHubPackage.Initialize 完成`.

> **This is by design**: the extension deliberately does **not** auto-load (no `AllowsBackgroundLoad`, no MEF components).
> It is only instantiated when you open the tool window, so it cannot slow down VS startup —
> which means it will **not** show up in `ActivityLog.xml` until you actually open it.

---

## Usage

1. Make sure Ollama is running: `ollama list`
2. Open **Tools → 本地大模型对话(&L)...**
3. Pick a model, type a question, press **Ctrl+Enter**
4. To let Copilot Chat use the same local models: click **BYOM…**, select models, register, restart VS

| UI element | Purpose |
|---|---|
| Model dropdown | Local models; `[常驻显存]` marks ones already resident in VRAM |
| 模型管理… (Model manager) | Pull (with progress) / delete / preload / unload |
| 联网搜索 + mode | `auto` / `always` / `never` |
| BYOM… | Register into Copilot BYOM and show the current config |
| 搜索测试… (Search test) | Run the search chain only, bypassing the model |
| Status bar | tok/s, first-token latency, prompt tokens, search details per answer |
| Run log | Raw log of every decision |

### Settings

**Tools → Options → Local AI Hub**, or edit `%LOCALAPPDATA%\LocalAIHub\settings.json`.

| Setting | Default | Notes |
|---|---|---|
| Ollama endpoint | `http://127.0.0.1:11434` | |
| Search mode | `auto` | `auto` decides per question / `always` / `never` |
| Relevance threshold | `0.30` (`0.15` for time-sensitive questions) | Results below this are not injected |
| Page-fetch deadline | `8` s | Prevents a DNS stall from freezing the UI |
| Context `num_ctx` | `65536` | Keep aligned with `OLLAMA_CONTEXT_LENGTH` |
| Max output tokens | `16384` | Too low makes thinking models return empty content (the extension auto-retries once with double the budget) |
| BYOM Base URL | `http://127.0.0.1:11434/v1` | |

---

## Build it yourself

Requires VS 2026 (provides MSBuild and the VS SDK assemblies) and the .NET SDK. **No NuGet, no network needed.**

```powershell
# 1) logic self-tests (no VS required, 110 checks)
dotnet build tools\CoreTests\CoreTests.csproj
.\tools\CoreTests\bin\Debug\net472\CoreTests.exe

# 2) build the extension
dotnet build src\LocalAIHub\LocalAIHub.csproj

# 3) package the VSIX (with two validation gates)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\pack.ps1
```

### Why everything is hand-rolled

This machine cannot reach nuget.org, so `Microsoft.VSSDK.BuildTools` is unavailable. Therefore:

- **VS SDK assemblies** are referenced straight from the local VS install (`<HintPath>`), not copied into the VSIX (`Private=false`)
- **JSON** uses the hand-written `src/LocalAIHub.Core/Json.cs` (zero dependencies)
- **VSIX packaging** is done by `scripts/pack.ps1` (zip + `[Content_Types].xml` + renamed manifest)
- **Menus/commands/tool window** are registered directly in `LocalAIHub.pkgdef` (no `VSCTCompiler.exe` available offline, so no `.vsct`)

### Two traps you must know (both guarded by the scripts)

1. **A `<Prerequisites>` element makes VSIXInstaller reject the whole package.**
   With `<Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor" .../>` present, the engine throws
   `InvalidExtensionPackageException` ("not a valid VSIX package"). **Deleting only that element, changing
   nothing else, makes it accept the package immediately.** VS's shipped `PackageManifestSchema.xsd`
   *allows* it — so schema validation alone cannot catch this.
   Reproduce with `tools/IlProbe`, which loads VSIXInstaller's own engine and bisects the manifest.

2. **`pack.ps1` must stay pure ASCII.**
   Windows PowerShell 5.1 reads `.ps1` files using the ANSI codepage (GBK here); non-ASCII source
   produces bizarre, unrelated parse errors.

---

## Repository layout

```
├─ src/
│  ├─ LocalAIHub.Core/          # pure logic, no VS dependency, independently testable
│  │  ├─ Json.cs                #   hand-written JSON (zero dependencies)
│  │  ├─ HttpFetcher.cs         #   HTTP with a hard wall-clock deadline
│  │  ├─ SearchService.cs       #   Bing/360/Baidu parsers + page fetching
│  │  ├─ QueryDecision.cs       #   should-we-search / query extraction
│  │  ├─ ContextBlocks.cs       #   relevance gate + injection block
│  │  ├─ OllamaClient.cs        #   Ollama API (streaming/pull/keep-alive)
│  │  ├─ ChatPipeline.cs        #   the main pipeline
│  │  └─ ByomConfig.cs          #   Copilot BYOM config read/write
│  └─ LocalAIHub/               # the VSIX (net472 + WPF)
│     ├─ LocalAIHubPackage.cs   #   package entry point (minimal, no auto-load)
│     ├─ ChatControl.cs         #   chat UI
│     ├─ ModelManagerWindow.cs  #   model manager
│     ├─ ByomWindow.cs          #   BYOM registration + search test
│     ├─ HubOptionsPage.cs      #   Tools → Options page
│     └─ LocalAIHub.pkgdef      #   hand-written registration
├─ tools/
│  ├─ CoreTests/                # 110 offline self-tests
│  └─ IlProbe/                  # VSIX engine probe (found the install rejection cause)
├─ scripts/pack.ps1             # hand-rolled VSIX packaging + two validation gates
├─ bench/                       # local model benchmarks
└─ docs/                        # measured facts, extensibility notes, delivery report
```

---

## Known limitations (honest list)

- Only built and verified on **VS 2026 Insiders 18.11.12224.323**.
- **Web search depends on Bing/360/Baidu page structure.** When they redesign, the parsers **degrade**
  to a generic fallback rather than crash — but may return nothing, in which case the plugin says so
  explicitly rather than pretending it searched.
- **The relevance gate compares literal overlap, not semantics.** Synonyms and whole-sentence queries
  can produce false negatives; the fallback is "never return empty + mark as low confidence".
- Baidu rate-limits automated requests, so it is tried last.
- No vision input, no concurrent multi-model use, no conversation persistence (closing VS loses history).
- `thinking` models (e.g. `qwen3:4b`) spend output budget on reasoning first. The extension detects
  "thinking ate the budget" and retries once with double the budget, but that costs extra time.
- **The BYOM registration path is not yet verified in real use**: all tests redirect to a temp file to
  avoid touching a live VS config. It auto-backups before the first real write.

### Environment traps (measured, useful if you write similar tooling)

- **A system proxy can capture loopback.** This machine has `127.0.0.1:26561` set as the system proxy
  **without a loopback exemption**, so Python's `urllib.getproxies()` picks it up and every direct call
  to `127.0.0.1:11434` returns **404** (the Go-based `ollama` CLI exempts localhost, so it appears fine).
  Fix: `ProxyHandler({})` in scripts.
- **`HttpClient` / `urlopen` timeouts do not cover DNS resolution.** One page hung for 145 s in testing,
  so a wall-clock deadline is mandatory.

---

## Documentation

| File | Contents |
|---|---|
| [docs/SHARED-FACTS.md](docs/SHARED-FACTS.md) | Measured facts (including the BYOM `Endpoint` enum values and VSIX packaging/install traps) |
| [docs/EXTENSIBILITY-NOTES.md](docs/EXTENSIBILITY-NOTES.md) | VS 2026 extensibility feasibility review (10 unverified items, each marked with its evidence) |
| [docs/FINAL-REPORT.md](docs/FINAL-REPORT.md) | Delivery report with acceptance evidence, including failures and corrections |
| [bench/REPORT.md](bench/REPORT.md) | `qwen3-coder:30b` benchmark numbers on this machine |

## License

MIT — see [LICENSE](LICENSE).
The search backend fetches third-party pages; check that this complies with the target sites' terms of use.
