using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LocalAIHub.Core
{
    /// <summary>
    /// 读写 Visual Studio 2026 的 "自带模型"(BYOM) 配置文件。
    ///
    /// 这是整件事里最"有情报价值"的一段：配置文件的格式、Endpoint 枚举真值、
    /// 以及 CustomURL 为 null 时请求会静默消失这个坑，都是上一轮实测/反汇编得出的
    /// （详见 docs/SHARED-FACTS.md 第 4 节）。
    /// 插件替用户写这个文件 = 用户不用再去猜这些字段。
    /// </summary>
    public static class ByomConfig
    {
        /// <summary>反汇编 Microsoft.VisualStudio.Copilot.dll 得到的 Endpoint 枚举真值。</summary>
        public const int EndpointOllama = 10;

        /// <summary>测试用：把读写重定向到别的路径，避免动用户真实的 VS 配置。</summary>
        public static string PathOverride { get; set; }

        public static string FilePath
        {
            get
            {
                if (!string.IsNullOrEmpty(PathOverride)) return PathOverride;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "VisualStudio", "Copilot", "BringYourOwnModel",
                    "ConfiguredBringYourOwnModel_v2.json");
            }
        }

        public sealed class ProviderSpec
        {
            public string Name { get; set; }
            public string BaseUrl { get; set; }
            public int Endpoint { get; set; }
            public List<ModelSpec> Models { get; set; }

            public ProviderSpec()
            {
                Endpoint = EndpointOllama;
                Models = new List<ModelSpec>();
            }
        }

        public sealed class ModelSpec
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
            public bool ToolCalling { get; set; }
            public bool Vision { get; set; }
            public int MaxInputTokens { get; set; }
            public int MaxOutputTokens { get; set; }

            public ModelSpec()
            {
                ToolCalling = true;
                Vision = false;
                MaxInputTokens = 65536;
                MaxOutputTokens = 8192;
            }
        }

        /// <summary>读取现有配置；文件不存在或损坏返回空列表 + 错误说明。</summary>
        public static List<Dictionary<string, object>> Read(out string error)
        {
            error = null;
            var list = new List<Dictionary<string, object>>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                var parsed = Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                var arr = parsed.AsArray();
                if (arr == null) { error = "顶层不是数组"; return list; }
                foreach (var item in arr)
                {
                    var obj = item.AsObject();
                    if (obj != null) list.Add(obj);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("ByomConfig.Read", ex);
            }
            return list;
        }

        /// <summary>打印一份人可读的摘要（给 UI 显示"Copilot 现在能用哪些本地模型"）。</summary>
        public static string Describe()
        {
            string err;
            var providers = Read(out err);
            var sb = new StringBuilder();
            sb.AppendLine("配置文件：" + FilePath);
            if (err != null) sb.AppendLine("读取错误：" + err);
            if (providers.Count == 0) sb.AppendLine("（还没有任何 BYOM 提供商）");
            foreach (var p in providers)
            {
                sb.AppendLine("提供商: " + p.GetString("Name")
                              + "  端点数=" + p.GetLong("Endpoint")
                              + "  URL=" + (p.GetString("CustomURL") ?? "(null！会导致请求静默消失)"))
                    ;
                var models = p.GetArray("Models");
                if (models != null)
                {
                    foreach (var m in models)
                    {
                        var mo = m.AsObject();
                        sb.AppendLine("    · " + mo.GetString("Id")
                                      + "  tools=" + mo.GetBool("IsToolCallingEnabled")
                                      + "  in=" + mo.GetLong("MaxInputTokens")
                                      + "  out=" + mo.GetLong("MaxOutputTokens")
                                      + "  selected=" + mo.GetBool("IsSelected"));
                    }
                }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 把一组模型注册进 BYOM（同名提供商合并，不覆盖用户的其它提供商）。
        /// 写之前自动备份到 .bak-localaihub-&lt;时间戳&gt;。
        /// <para>
        /// 返回值契约：**null = 失败**（原因在 error 里）；非 null = 成功，
        /// 内容是备份文件路径，没有备份时为空字符串。不要用返回值判空来当布尔用。
        /// </para>
        /// </summary>
        public static string Register(ProviderSpec spec, bool alsoSelect, out string error)
        {
            error = null;
            try
            {
                if (spec == null || string.IsNullOrEmpty(spec.Name))
                {
                    error = "provider name required";
                    return null;
                }
                if (string.IsNullOrEmpty(spec.BaseUrl))
                {
                    error = "BaseUrl required（为空会导致 VS 发请求时静默丢弃，见 SHARED-FACTS 第 4 节）";
                    return null;
                }

                string readErr;
                var providers = Read(out readErr);

                // 找一个同名的现有提供商
                Dictionary<string, object> target = null;
                foreach (var p in providers)
                {
                    if (string.Equals(p.GetString("Name"), spec.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        target = p;
                        break;
                    }
                }
                if (target == null)
                {
                    target = new Dictionary<string, object>();
                    providers.Add(target);
                }

                target["Name"] = spec.Name;
                target["IsApiKeyAvailable"] = false;
                target["IsEnabled"] = true;
                target["Endpoint"] = spec.Endpoint;
                target["CustomURL"] = spec.BaseUrl;   // ← 这个字段必须有值

                var existingModels = new List<object>();
                var byId = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                var oldModels = target.GetArray("Models");
                if (oldModels != null)
                {
                    foreach (var m in oldModels)
                    {
                        var mo = m.AsObject();
                        if (mo == null) continue;
                        var id = mo.GetString("Id");
                        if (id != null && !byId.ContainsKey(id)) byId[id] = mo;
                        existingModels.Add(m);
                    }
                }

                foreach (var m in spec.Models)
                {
                    Dictionary<string, object> mo;
                    if (!byId.TryGetValue(m.Id ?? "", out mo))
                    {
                        mo = new Dictionary<string, object>();
                        existingModels.Add(mo);
                    }
                    mo["Id"] = m.Id;
                    mo["DisplayName"] = m.DisplayName ?? m.Id;
                    mo["IsToolCallingEnabled"] = m.ToolCalling;
                    mo["IsVisionEnabled"] = m.Vision;
                    mo["MaxInputTokens"] = m.MaxInputTokens;
                    mo["MaxOutputTokens"] = m.MaxOutputTokens;
                    mo["ProviderName"] = spec.Name;
                    mo["IsCustom"] = true;
                    mo["IsSelected"] = alsoSelect;
                }
                target["Models"] = existingModels;

                var json = Json.Serialize(providers, true);
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string backup = null;
                if (File.Exists(path))
                {
                    backup = path + ".bak-localaihub-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(path, backup, true);
                }
                File.WriteAllText(path, json, new UTF8Encoding(false));

                Log.Info("BYOM 已注册 " + spec.Models.Count + " 个模型 -> " + path
                         + (backup != null ? "（备份 " + Path.GetFileName(backup) + "）" : "（原文件不存在，无需备份）"));
                // 契约：返回 null 只表示失败；"成功但没有备份"返回空字符串。
                // （第一版这里直接 return backup，于是"首次写入"被调用方当成了失败。）
                return backup ?? "";
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                Log.Error("ByomConfig.Register", ex);
                return null;
            }
        }

        /// <summary>一键：把当前 Ollama 的模型注册进 Copilot BYOM。</summary>
        public static string RegisterOllama(HubSettings settings, IEnumerable<OllamaModel> models,
            bool alsoSelect, out string error)
        {
            var spec = new ProviderSpec
            {
                Name = "ollama-local",
                BaseUrl = settings.ByomBaseUrl,
                Endpoint = EndpointOllama,
            };
            foreach (var m in models)
            {
                spec.Models.Add(new ModelSpec
                {
                    Id = m.DisplayName,
                    DisplayName = m.DisplayName + "（本地）",
                    ToolCalling = true,
                    MaxInputTokens = Math.Min(settings.ContextLength, 61440),
                    MaxOutputTokens = settings.MaxOutputTokens,
                });
            }
            return Register(spec, alsoSelect, out error);
        }
    }
}
