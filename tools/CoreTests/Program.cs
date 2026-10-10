using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LocalAIHub.Core;

namespace LocalAIHub.CoreTests
{
    /// <summary>
    /// 离线自测：把 Core 的逻辑（JSON、检索词抽取、联网判定、相关性闸门、
    /// 注入块、HTML 解析、BYOM 写入、真实搜索、真实模型对话）逐条验证。
    /// 不需要 VS、不需要联网（联网类测试会在网络不可用时标 SKIP）。
    ///
    /// 跑法：dotnet build tools\CoreTests\CoreTests.csproj
    ///       .\tools\CoreTests\bin\Debug\net472\CoreTests.exe [-v]
    /// </summary>
    internal static class Program
    {
        private static int _pass, _fail, _skip;
        private static readonly List<string> Failures = new List<string>();
        private static bool _verbose;

        private static int Main(string[] args)
        {
            _verbose = args.Any(a => a == "-v" || a == "--verbose");
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Console.WriteLine("=== LocalAIHub.Core 自测 ===");
            Console.WriteLine("日志: " + Log.FilePath);
            Console.WriteLine();

            Section("1. JSON 读写器（手写，零依赖）", TestJson);
            Section("2. 检索词抽取（报错原文优先）", TestQueryExtraction);
            Section("3. 联网判定（该搜才搜）", TestSearchDecision);
            Section("4. 相关性闸门与排名（反跑题）", TestRankAndGate);
            Section("5. 注入块构造（引用编号/时间锚点/承认搜不到）", TestContextBlocks);
            Section("6. HTML 解析器（离线样本，不依赖网络）", TestHtmlParsers);
            Section("7. BYOM 配置写入（重定向到临时文件，不动真实 VS 配置）", TestByom);
            Section("8. 真实搜索（引擎链 Bing→360→百度）", TestLiveSearch);
            Section("9. 真实模型对话（Ollama 流式）", TestLiveChat);
            Section("10. 完整链路（判定→搜索→注入→回答）", TestEndToEnd);

            Console.WriteLine();
            Console.WriteLine("=================================================");
            Console.WriteLine("通过 " + _pass + " / 失败 " + _fail + " / 跳过 " + _skip);
            if (Failures.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("失败清单：");
                foreach (var f in Failures) Console.WriteLine("  x " + f);
            }
            Console.WriteLine("=================================================");
            return _fail == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ 小框架

        private static void Section(string title, Action body)
        {
            Console.WriteLine("--- " + title + " ---");
            try { body(); }
            catch (Exception ex)
            {
                _fail++;
                Failures.Add(title + " 抛出异常: " + ex.Message);
                Console.WriteLine("  x 整节抛异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            Console.WriteLine();
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("  v " + name); }
            else
            {
                _fail++;
                Failures.Add(name + (detail == null ? "" : " :: " + detail));
                Console.WriteLine("  x " + name + (detail == null ? "" : "  [" + detail + "]"));
            }
        }

        private static void Check(string name, bool ok) { Check(name, ok, null); }

        private static void Skip(string name, string why)
        {
            _skip++;
            Console.WriteLine("  ~ 跳过 " + name + "（" + why + "）");
        }

        private static void Info(string text) { Console.WriteLine("    . " + text); }

        private static void Eq<T>(string name, T actual, T expected)
        {
            var ok = EqualityComparer<T>.Default.Equals(actual, expected);
            Check(name, ok, ok ? null : "实际=" + Show(actual) + " 期望=" + Show(expected));
        }

        private static string Show(object v)
        {
            if (v == null) return "(null)";
            var s = v.ToString();
            return s.Length > 120 ? s.Substring(0, 120) + "..." : s;
        }

        /// <summary>把任务跑在后台线程并等待，避免在同步上下文里死等。</summary>
        private static bool RunSync(Func<Task> action, int timeoutSeconds, out Exception error)
        {
            Exception captured = null;
            var t = Task.Run(async () =>
            {
                try { await action(); }
                catch (Exception ex) { captured = ex; }
            });
            var finished = t.Wait(TimeSpan.FromSeconds(timeoutSeconds));
            error = captured;
            return finished;
        }

        // ------------------------------------------------------------ 1. JSON

        private static void TestJson()
        {
            var text = "{\"a\":1,\"b\":[true,false,null,\"x\\ny\"],\"cn\":\"值\",\"nested\":{\"k\":-2.5e2}}";
            var obj = Json.ParseObject(text);
            Eq("整数解析", obj.GetLong("a"), 1L);
            Eq("中文键", obj.GetString("cn"), "值");
            Eq("转义换行", obj.GetArray("b")[3].AsString(), "x" + "\n" + "y");
            Eq("嵌套科学计数", obj.GetObject("nested").GetDouble("k"), -250.0);
            Check("数组长度", obj.GetArray("b").Count == 4);

            var round = Json.ParseObject(Json.Serialize(obj));
            Eq("序列化往返（中文）", round.GetString("cn"), "值");
            Eq("序列化往返（嵌套）", round.GetObject("nested").GetDouble("k"), -250.0);

            var pretty = Json.Serialize(new Dictionary<string, object> { { "x", new List<object> { 1, 2 } } }, true);
            Check("pretty 输出含换行", pretty.Contains("\n"));

            var bad = new[] { "{", "{\"a\":}", "[1,2", "{\"a\" 1}" };
            foreach (var b in bad)
            {
                var threw = false;
                try { Json.Parse(b); } catch (FormatException) { threw = true; } catch { }
                Check("非法 JSON 抛 FormatException: " + b, threw);
            }

            var ndjson = "{\"model\":\"qwen3-coder:30b\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"done\":true,\"prompt_eval_count\":17,\"eval_count\":42}";
            var line = Json.ParseObject(ndjson);
            Eq("NDJSON 行解析 content", line.GetObject("message").GetString("content"), "hi");
            Eq("NDJSON 行 done", line.GetBool("done"), true);
            Eq("NDJSON 行 eval_count", line.GetLong("eval_count"), 42L);
        }

        // ------------------------------------------------------------ 2. 检索词

        private static void TestQueryExtraction()
        {
            var inputs = new[]
            {
                "Python报错 ModuleNotFoundError: No module named requests 怎么解决",
                "Python报错 ModuleNotFoundError: No module named cv2 怎么解决",
                "Python报错 TypeError: NoneType object has no attribute len 怎么解决",
                "运行Python脚本报错 ImportError: cannot import name getargspec from",
            };
            var expected = new[]
            {
                "ModuleNotFoundError: No module named requests",
                "ModuleNotFoundError: No module named cv2",
                "TypeError: NoneType object has no attribute len",
                "ImportError: cannot import name getargspec",
            };
            for (var i = 0; i < inputs.Length; i++)
                Eq("抽取: " + Shorten(inputs[i]), QueryDecision.ExtractSearchQuery(inputs[i]), expected[i]);

            var cleaned = QueryDecision.ExtractSearchQuery("请问 React 19 有哪些新特性？请只回答三条");
            Check("无报错原文时回退清洗（不含'请问'/'请只回答'）",
                !cleaned.Contains("请问") && !cleaned.Contains("请只回答"), cleaned);
            if (_verbose) Info("清洗结果: " + cleaned);

            var news = QueryDecision.CleanQuery("今天有什么重要的AI科技新闻");
            Check("新闻类清洗掉时间/形容词", !news.Contains("今天") && !news.Contains("重要的"), news);
        }

        private static string Shorten(string s)
        {
            return s.Length <= 34 ? s : s.Substring(0, 34) + "...";
        }

        // ------------------------------------------------------------ 3. 联网判定

        private static void TestSearchDecision()
        {
            var noSearch = new[]
            {
                "解释一下什么是 MoE 架构",
                "什么是 WBI 签名",
                "帮我写一个快速排序",
                "这段代码帮我重构一下",
                "def foo(): pass 这里有什么问题",
                "把 E:\\AI-Models 里的 gguf 都列出来",
                "继续", "好的", "还有吗", "谢谢", "分析一下", "看一下",
                "你能联网吗",
            };
            foreach (var t in noSearch)
                Check("不搜: " + t, !QueryDecision.NeedsWebSearch(t));

            var search = new[]
            {
                "React 19 有哪些新特性",
                "Vue 3.5 有什么新功能",
                "Ollama 最新版本是多少",
                "b站何同学有多少粉丝",
                "苹果股价多少",
                "Python报错 ModuleNotFoundError: No module named requests 怎么解决",
                "今天有什么重要的AI科技新闻",
            };
            foreach (var t in search)
                Check("要搜: " + t, QueryDecision.NeedsWebSearch(t));

            Check("代码时效判定优先于纯概念判定",
                QueryDecision.NeedsWebSearch("解释一下 React 19 有什么新特性"));
            Check("时效识别", QueryDecision.IsRealtime("Ollama 最新版本是多少"));
            Check("非时效识别", !QueryDecision.IsRealtime("帮我写一个快速排序"));
            Check("能力类问题识别", QueryDecision.IsCapabilityQuestion("你能联网吗"));
        }

        // ------------------------------------------------------------ 4. 闸门

        private static void TestRankAndGate()
        {
            var results = new List<SearchResult>
            {
                new SearchResult { Title = "Windows 下如何安装 Python", Url = "https://blog.csdn.net/sensen_kiss/141940274", Snippet = "勾选 Add Python to PATH" },
                new SearchResult { Title = "ModuleNotFoundError: No module named 'requests' 解决方法", Url = "https://cloud.tencent.com/developer/article/2425767", Snippet = "ModuleNotFoundError: No module named requests 的三种解决方式" },
                new SearchResult { Title = "python.org", Url = "https://www.python.org/", Snippet = "Welcome to Python" },
                new SearchResult { Title = "No module named requests - Stack Overflow", Url = "https://stackoverflow.com/questions/12345/no-module-named-requests", Snippet = "ModuleNotFoundError No module named requests pip install" },
                new SearchResult { Title = "无协议链接", Url = "ftp://example.com/x", Snippet = "x" },
            };
            const string q = "ModuleNotFoundError: No module named requests";

            List<SearchResult> kept; double top; bool weak;
            ContextBlocks.RankAndGate(results, q, 0.30, 4, true, out kept, out top, out weak);

            Check("内容农场(csdn)被剔除", kept.All(r => !r.Url.Contains("csdn.net")));
            Check("非 http 链接被剔除", kept.All(r => r.Url.StartsWith("http")));
            Check("Stack Overflow 排第一（可信源加成）",
                kept.Count > 0 && kept[0].Url.Contains("stackoverflow.com"),
                kept.Count > 0 ? kept[0].Url : "empty");
            Check("相关性高的被保留", kept.Any(r => r.Url.Contains("cloud.tencent.com")));
            Check("最高分 > 阈值", top > 0.30, "top=" + top.ToString("0.00"));
            Check("不是弱证据", !weak);
            if (_verbose) Info("kept=" + kept.Count + " top=" + top.ToString("0.00"));

            var irrelevant = new List<SearchResult>
            {
                new SearchResult { Title = "菜鸟教程 Python 基础教程", Url = "https://www.runoob.com/python/python-tutorial.html", Snippet = "Python 简介" },
            };
            ContextBlocks.RankAndGate(irrelevant, q, 0.30, 4, true, out kept, out top, out weak);
            Info("全无关用例: kept=" + kept.Count + " top=" + top.ToString("0.000") + " weak=" + weak
                 + " 输入条数=" + irrelevant.Count);
            Check("全无关时永不滤空（保留 1 条）", kept.Count == 1);
            Check("全无关时标记低置信度", weak);

            ContextBlocks.RankAndGate(irrelevant, q, 0.30, 4, false, out kept, out top, out weak);
            Check("neverEmpty=false 时滤空", kept.Count == 0);

            ContextBlocks.RankAndGate(new List<SearchResult>(), q, 0.3, 4, true, out kept, out top, out weak);
            Check("空输入安全", kept.Count == 0 && top == 0);
        }

        // ------------------------------------------------------------ 5. 注入块

        private static void TestContextBlocks()
        {
            var kept = new List<SearchResult>
            {
                new SearchResult { Title = "标题一", Url = "https://a.example/1", Snippet = "摘要一" },
                new SearchResult { Title = "标题二", Url = "https://b.example/2", Snippet = "摘要二" },
            };
            var rendered = ContextBlocks.RenderSources(kept, "bing");
            Check("来源编号 [S1]", rendered.Contains("[S1]"));
            Check("来源编号 [S2]", rendered.Contains("[S2]"));
            Check("含引擎名", rendered.Contains("bing"));

            var block = ContextBlocks.BuildContextBlock("测试检索词", 2, rendered,
                new FetchedPage { Text = "正文内容" }, false);
            Check("注入块含时间锚点", block.Contains("检索时间："));
            Check("注入块含检索词", block.Contains("测试检索词"));
            Check("注入块允许忽略无关结果", block.Contains("不相关就忽略"));
            Check("注入块要求标来源", block.Contains("[S1]"));
            Check("注入块要求先依据后结论", block.Contains("先给判断依据"));
            Check("注入块含正文", block.Contains("正文内容"));

            var weakBlock = ContextBlocks.BuildContextBlock("q", 1, rendered, null, true);
            Check("弱证据标注", weakBlock.Contains("低置信度"));

            var empty = ContextBlocks.BuildEmptyContextBlock("q", "引擎返回 8 条，全部低于阈值");
            Check("空结果块承认搜不到", empty.Contains("没有找到与问题相关的内容"));
            Check("空结果块禁止编造链接", empty.Contains("不要编造检索结果"));

            var msgs = new List<ChatMessage>
            {
                new ChatMessage("user", "第一句"),
                new ChatMessage("assistant", "回答"),
                new ChatMessage("user", "第二句"),
            };
            ContextBlocks.AppendToLastUserMessage(msgs, "【注入】xxx");
            Check("注入到最后一条 user", msgs[2].Content.EndsWith("【注入】xxx"));
            Check("前一条 user 未被污染", msgs[0].Content == "第一句");
        }

        // ------------------------------------------------------------ 6. HTML 解析

        private static void TestHtmlParsers()
        {
            var bingHtml = "<html><body><ol>"
                + "<li class=\"b_algo\"><h2><a href=\"https://example.com/a\">Result A</a></h2><p>Snippet A here</p></li>"
                + "<li class=\"b_algo\"><h2><a href=\"https://www.bing.com/ck/a?u=a1aHR0cHM6Ly9leGFtcGxlLmNvbS9i\">Result B</a></h2><p>Snippet B</p></li>"
                + "</ol></body></html>";
            var bing = SearchService.ParseBing(bingHtml, 8);
            Check("Bing 解析出 2 条", bing.Count == 2, "got " + bing.Count);
            Check("Bing 标题正确", bing.Count > 0 && bing[0].Title == "Result A");
            Check("Bing 摘要正确", bing.Count > 0 && bing[0].Snippet == "Snippet A here");
            Check("Bing /ck/a 链接被解开",
                bing.Count > 1 && bing[1].Url == "https://example.com/b",
                bing.Count > 1 ? bing[1].Url : "n/a");

            var soHtml = "<html><body>"
                + "<li class=\"res-list\"><h3><a href=\"https://so.com/link\">标题X</a></h3>"
                + "<p class=\"res-desc\">描述X</p><a data-mdurl=\"https://real.example/x\">x</a></li>"
                + "</body></html>";
            var so = SearchService.Parse360(soHtml, 8);
            Check("360 解析出 1 条", so.Count == 1, "got " + so.Count);
            Check("360 用 data-mdurl 的真实链接", so.Count > 0 && so[0].Url == "https://real.example/x",
                so.Count > 0 ? so[0].Url : "n/a");

            var genericHtml = "<html><body><h2><a href=\"https://g.example/1\">通用标题一</a></h2>尾巴文字"
                + "<h3><a href=\"http://self.example/2\">站内链接</a></h3></body></html>";
            var gen = SearchService.SearchGeneric(genericHtml, "self.example", 8);
            Check("通用解析器取出外链", gen.Count == 1, "got " + gen.Count);
            Check("通用解析器排除自身域名", gen.Count > 0 && gen[0].Url == "https://g.example/1");

            Check("HTML 反转义", SearchService.StripTags("<b>a</b>&amp;b&lt;c") == "a &b<c",
                SearchService.StripTags("<b>a</b>&amp;b&lt;c"));
            Check("script/style 被剥掉",
                SearchService.StripTags("<script>var x=1;</script>正文") == "正文");
            Check("属性里的 > 不破坏解析",
                SearchService.StripTags("<a data-x=\"a>b\">文字</a>") == "文字",
                SearchService.StripTags("<a data-x=\"a>b\">文字</a>"));

            var terms = SearchService.QueryTerms("ModuleNotFoundError requests 报错");
            Check("英文词提取", terms.Contains("modulenotfounderror"));
            Check("中文 2-gram 提取", terms.Contains("报错"));
        }

        // ------------------------------------------------------------ 7. BYOM

        private static void TestByom()
        {
            // 用"自己所在目录"而不是系统 TEMP 根：不同环境下 TEMP 可能不可写
            var tmpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_byom_test");
            try { Directory.CreateDirectory(tmpDir); } catch { }
            var tmp = Path.Combine(tmpDir, "LocalAIHub-test-byom.json");
            if (File.Exists(tmp)) File.Delete(tmp);
            foreach (var old in Directory.GetFiles(tmpDir, "*.bak-localaihub-*")) File.Delete(old);

            ByomConfig.PathOverride = tmp;
            try
            {
                var spec = new ByomConfig.ProviderSpec
                {
                    Name = "ollama-local",
                    BaseUrl = "http://127.0.0.1:11434/v1",
                    Endpoint = ByomConfig.EndpointOllama,
                };
                spec.Models.Add(new ByomConfig.ModelSpec { Id = "qwen3-coder:30b", DisplayName = "qwen3-coder:30b（本地）", MaxInputTokens = 61440 });
                spec.Models.Add(new ByomConfig.ModelSpec { Id = "smtek/Qwen3.8-27B:Q4_K_M", DisplayName = "Qwen3.8-27B（本地）", MaxInputTokens = 61440 });

                string err;
                var backup = ByomConfig.Register(spec, true, out err);
                Info("Register -> 返回=" + (backup == null ? "(null=失败)" : "\"" + backup + "\"")
                     + "  err=" + (err ?? "(null)") + "  路径=" + tmp);
                Check("首次写入成功（返回非 null 即成功）", backup != null && err == null,
                    "err=" + (err ?? "(null)") + " backup=" + (backup ?? "(null)"));
                Check("文件已生成", File.Exists(tmp));

                var parsed = Json.Parse(File.ReadAllText(tmp)).AsArray();
                Check("顶层是数组", parsed != null && parsed.Count == 1);
                var p = parsed[0].AsObject();
                Eq("提供商名", p.GetString("Name"), "ollama-local");
                Eq("CustomURL 非空（关键！）", p.GetString("CustomURL"), "http://127.0.0.1:11434/v1");
                Eq("Endpoint=Ollama(10)", p.GetLong("Endpoint"), 10L);
                Eq("IsEnabled", p.GetBool("IsEnabled"), true);
                Eq("模型数", p.GetArray("Models").Count, 2);

                spec.Models.Clear();
                spec.Models.Add(new ByomConfig.ModelSpec { Id = "qwen3-coder:30b", DisplayName = "改过名字", MaxInputTokens = 32768 });
                ByomConfig.Register(spec, false, out err);
                var parsed2 = Json.Parse(File.ReadAllText(tmp)).AsArray();
                Eq("二次注册不产生重复提供商", parsed2.Count, 1);
                var models2 = parsed2[0].AsObject().GetArray("Models");
                Eq("二次注册不重复模型", models2.Count, 2);
                var m0 = models2[0].AsObject();
                Eq("已有模型被更新", m0.GetString("DisplayName"), "改过名字");
                Eq("已有模型 tokens 被更新", m0.GetLong("MaxInputTokens"), 32768L);

                Check("有备份文件", Directory.GetFiles(tmpDir, "*.bak-localaihub-*").Length >= 1);

                var described = ByomConfig.Describe();
                Check("Describe 含提供商", described.Contains("ollama-local"));

                var badSpec = new ByomConfig.ProviderSpec { Name = "x", BaseUrl = "" };
                ByomConfig.Register(badSpec, false, out err);
                Check("空 BaseUrl 被拒绝", err != null && err.Contains("BaseUrl"));
            }
            finally
            {
                ByomConfig.PathOverride = null;
            }
        }

        // ------------------------------------------------------------ 8. 真实搜索

        private static void TestLiveSearch()
        {
            SearchResponse resp = null;
            Exception ex;
            var finished = RunSync(async () =>
            {
                using (var http = new HttpFetcher())
                {
                    var svc = new SearchService(http);
                    resp = await svc.SearchAsync("Ollama 最新版本", 8);
                }
            }, 60, out ex);

            if (!finished) { Skip("真实搜索", "60 秒超时"); return; }
            if (ex != null) { Check("真实搜索不抛异常", false, ex.Message); return; }
            if (resp == null || resp.RawCount == 0)
            {
                Skip("真实搜索", "搜索引擎不可达：" + (resp == null ? "unknown" : resp.Error));
                return;
            }
            Check("搜索引擎返回结果", resp.RawCount > 0);
            Info("引擎=" + resp.Engine + " 条数=" + resp.RawCount);
            Check("结果有标题", resp.Results.All(r => !string.IsNullOrEmpty(r.Title)));
            Check("结果有 http 链接", resp.Results.All(r => r.Url.StartsWith("http")));
            for (var i = 0; i < Math.Min(3, resp.Results.Count); i++)
                Info("  [" + (i + 1) + "] " + resp.Results[i].Title + "  " + resp.Results[i].Url);
        }

        // ------------------------------------------------------------ 9. 真实对话

        private static void TestLiveChat()
        {
            const string endpoint = "http://127.0.0.1:11434";
            string version = null;
            Exception ignored;
            RunSync(async () =>
            {
                using (var c = new OllamaClient(endpoint)) version = await c.GetVersionAsync();
            }, 10, out ignored);
            if (version == null) { Skip("真实模型对话", "Ollama 未运行"); return; }
            Info("Ollama 版本 " + version);

            using (var client = new OllamaClient(endpoint))
            {
                List<OllamaModel> models = null;
                Exception lex;
                var okList = RunSync(async () => { models = await client.ListModelsAsync(); }, 20, out lex);
                if (!okList || lex != null) { Check("列模型", false, lex == null ? "超时" : lex.Message); return; }
                Check("列出模型", models != null && models.Count > 0);
                foreach (var m in models) Info("  " + m.DisplayName + "  " + m.SizeText + "  " + m.DetailText);

                List<RunningModel> running = null;
                RunSync(async () => { running = await client.ListRunningAsync(); }, 15, out ignored);
                if (running != null && running.Count > 0)
                    foreach (var r in running) Info("  运行中: " + r.Summary);
                else Info("  当前没有模型常驻显存");

                var target = models.FirstOrDefault(m => m.DisplayName.Contains("4b"))
                             ?? models.OrderBy(m => m.Size).First();
                Info("用它做流式对话测试: " + target.DisplayName);

                var sb = new StringBuilder();
                GenerationStats stats = null;
                Exception cex;
                // 预算给足：本地 thinking 模型（qwen3:4b）会先用预算思考，
                // 给 64 的话正文还没开始就截断了（实测过这个坑）。
                const int budget = 2048;
                var okChat = RunSync(async () =>
                {
                    stats = await client.ChatStreamAsync(target.DisplayName,
                        new List<ChatMessage> { new ChatMessage("user", "只回答两个字：收到") },
                        0.7, 4096, budget, false, d => sb.Append(d));
                }, 180, out cex);

                if (!okChat) { Skip("流式对话", "180 秒未完成"); return; }
                if (cex != null) { Check("流式对话无异常", false, cex.Message); return; }
                Check("流式对话拿到正文", sb.Length > 0, "len=" + sb.Length);
                if (stats != null)
                {
                    Info("统计: " + stats.OneLine()
                         + " done_reason=" + (stats.DoneReason ?? "?")
                         + " reasoning=" + stats.ReasoningChars + " 字");
                    Check("没有错误字段", string.IsNullOrEmpty(stats.Error), stats.Error);
                }
                Info("回答: " + Show(sb.ToString()));
                if (stats != null)
                {
                    Check("有 completion token 计数", stats.CompletionTokens > 0);
                    Check("有 prompt token 计数", stats.PromptTokens > 0);
                }
            }
        }

        // ------------------------------------------------------------ 10. 端到端

        private static void TestEndToEnd()
        {
            var settings = SettingsStore.Load();
            Info("设置文件: " + SettingsStore.FilePath);
            Info("Ollama: " + settings.OllamaEndpoint + "  搜索模式=" + settings.SearchMode
                 + "  温度=" + settings.Temperature + "  ctx=" + settings.ContextLength);

            using (var pipe = new ChatPipeline(settings))
            {
                string reason;
                Check("[no-search] 强制不搜", !pipe.DecideSearch("最新消息 [no-search]", out reason));
                Check("[search] 强制搜", pipe.DecideSearch("写个快速排序 [search]", out reason));
                Check("纯代码题不搜", !pipe.DecideSearch("帮我写一个快速排序", out reason));
                Info("判定理由示例: " + reason);

                InjectionResult inj = null;
                Exception iex;
                var ok = RunSync(async () =>
                {
                    inj = await pipe.BuildInjectionAsync("Ollama 最新版本是多少", CancellationToken.None);
                }, 90, out iex);
                if (!ok) { Skip("注入链路", "90 秒超时"); return; }
                if (iex != null) { Check("注入链路无异常", false, iex.Message); return; }
                Check("注入链路返回结果", inj != null);
                if (inj == null) return;
                Info("理由: " + inj.Reason);
                Info("检索词: " + inj.Query + "  引擎: " + inj.Engine
                     + "  raw=" + inj.Raw + " kept=" + inj.Kept
                     + " 搜索 " + inj.SearchSeconds.ToString("0.00") + "s"
                     + " 抓正文 " + (inj.Fetched ? inj.FetchSeconds.ToString("0.00") + "s" : "跳过"));
                if (inj.Block != null)
                {
                    Check("注入块非空", inj.Block.Length > 50);
                    Info("注入块长度: " + inj.Block.Length + " 字符");
                    if (_verbose)
                        Console.WriteLine(inj.Block.Substring(0, Math.Min(1200, inj.Block.Length)));
                }
                else
                {
                    Skip("注入块内容", "未联网（" + inj.Reason + "）");
                }
            }
        }
    }
}
