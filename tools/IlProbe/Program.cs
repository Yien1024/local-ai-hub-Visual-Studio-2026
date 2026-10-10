using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

// 决定性实验：逐个字段改我们的 manifest，用 VSIXInstaller 真正用的那个引擎实现
// （InstallableExtensionImpl(path, culture)）去试，看引擎到底接受哪种写法。
// 思路：从"最像微软随箱扩展"的最小清单开始，逐步加回我们的字段。
internal static class Program
{
    private const string EngineDir =
        @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\resources\app\ServiceHub\Services\Microsoft.VisualStudio.Setup.Service\VsixInstaller";

    private static string _baseVsix;
    private static string _tmpDir;
    private static ConstructorInfo _ctor;
    private static string _work;   // 当前用于试验的副本

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            // 连 Console 都可能因为引擎异常类型解析不到而炸，所以落盘一份。
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ilprobe-crash.log"), Describe(ex) + Environment.NewLine); }
            catch { }
            try { Console.WriteLine("CRASH: " + Describe(ex)); } catch { }
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        _baseVsix = args.Length > 0 ? args[0]
            : @"C:\Users\zx\Documents\vs-local-ai\dist\LocalAIHub-1.0.0.vsix";
        // 不要用系统 TEMP 根：受限环境下 %TEMP% 根不可写（踩过）。
        _tmpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_vsixprobe");
        Directory.CreateDirectory(_tmpDir);
        _work = Path.Combine(_tmpDir, "trial.vsix");

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var n = new AssemblyName(e.Name).Name + ".dll";
            var p = Path.Combine(EngineDir, n);
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        var engine = Assembly.LoadFrom(Path.Combine(EngineDir, "Microsoft.VisualStudio.ExtensionEngine.dll"));
        var impl = engine.GetType("Microsoft.VisualStudio.ExtensionManager.InstallableExtensionImpl");
        _ctor = impl.GetConstructors().First(c => c.GetParameters().Length == 2
                    && c.GetParameters()[0].ParameterType == typeof(string));

        Console.WriteLine("engine: " + engine.GetName().Version);
        Console.WriteLine("基线包: " + _baseVsix);
        Console.WriteLine("引擎版本锚点: 该构造函数为 VSIXInstaller 实际使用的入口");
        Console.WriteLine();

        // ---- 变体清单：从正式 manifest 出发，逐个删元素，找出触发 NRE 的那一个 ----
        var real = ReadManifestFrom(_baseVsix);
        var variants = new List<KeyValuePair<string, string>>
        {
            V("R0 正式 manifest（原样）", real),
            V("R1 删 <Tags>", Drop(real, "Tags")),
            V("R2 删 <MoreInfo>", Drop(real, "MoreInfo")),
            V("R3 删 <DisplayName>", Drop(real, "DisplayName")),
            V("R4 删 <Description>", Drop(real, "Description")),
            V("R5 删 <Dependencies>", Drop(real, "Dependencies")),
            V("R6 删 <Prerequisites>", Drop(real, "Prerequisites")),
            V("R7 删 Pro/Enterprise 目标（只留 Community）", KeepOnlyCommunity(real)),
            V("R8 Identity 去掉 Language", Regex.Replace(real, "\\s+Language=\"[^\"]*\"", "")),
            V("R9 Description 去掉 xml:space", real.Replace(" xml:space=\"preserve\"", "")),
            V("R10 把 Description 的 xmlns 前缀补全（xml:space -> 保留）", real),
            V("R11 Description 换成纯 ASCII 无 xml:space", AsciiDescription(real)),
            V("R12 Tags 换成单个英文词", Regex.Replace(real, "<Tags>[^<]*</Tags>", "<Tags>ollama</Tags>")),
            V("R13 MoreInfo/Tags 全删 + ASCII Description", Drop(Drop(AsciiDescription(real), "MoreInfo"), "Tags")),
            V("R14 Metadata 加 DisplayName 属性（按引擎报错字面理解）", AddMetadataAttribute(real)),
            V("R15 Language 改成 en-US", Regex.Replace(real, "Language=\"[^\"]*\"", "Language=\"en-US\"")),
            V("R16 去掉 Language（清空该属性）", Regex.Replace(real, "\\s+Language=\"[^\"]*\"", "")),
            V("R17 除了 Identity 的必需属性，其余全按引擎报错逐步排查：加 License 元素", AddLicense(real)),
        };

        foreach (var kv in variants)
        {
            MakeTrial(kv.Value);
            var result = TryEngine();
            var mark = result.StartsWith("OK") ? "接受" : "拒绝";
            Console.WriteLine("  [" + mark + "] " + kv.Key);
            if (!result.StartsWith("OK")) Console.WriteLine("         " + result);
        }
        return 0;
    }

    private static string AddLicense(string manifest)
    {
        return manifest.Replace("<Tags>", "<License>LICENSE.txt</License>\n    <Tags>");
    }

    /// <summary>删掉某个元素（含其内容）。</summary>
    private static string Drop(string manifest, string element)
    {
        return Regex.Replace(manifest,
            "<" + element + "(\\s[^>]*)?>.*?</" + element + ">\\s*", "",
            RegexOptions.Singleline);
    }

    /// <summary>把 Description 换成纯 ASCII 且不带 xml:space，用于测试是不是它触发了 NRE。</summary>
    private static string AsciiDescription(string manifest)
    {
        return Regex.Replace(manifest,
            "<Description[^>]*>.*?</Description>",
            "<Description>Local AI Hub for Visual Studio</Description>",
            RegexOptions.Singleline);
    }

    /// <summary>引擎报错说 Metadata 缺少必需的属性 DisplayName —— 按字面加一个属性试试。</summary>
    private static string AddMetadataAttribute(string manifest)
    {
        return manifest.Replace("<Metadata>", "<Metadata DisplayName=\"Local AI Hub\">");
    }

    private static string KeepOnlyCommunity(string manifest)
    {
        return Regex.Replace(manifest,
            "<InstallationTarget Id=\"Microsoft\\.VisualStudio\\.(Pro|Enterprise)\"[^>]*>.*?</InstallationTarget>\\s*", "",
            RegexOptions.Singleline);
    }

    private static KeyValuePair<string, string> V(string name, string manifest)
    {
        return new KeyValuePair<string, string>(name, manifest);
    }

    /// <summary>构造一个最小可用清单，按参数逐步加字段。</summary>
    private static string Minimal(string language = null, string publisher = null, string displayName = null,
        string description = null, string moreInfo = null, string tags = null, string license = null,
        string icon = null, bool threeTargets = false, bool withNdp = false, bool withPrereq = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<PackageManifest Version=\"2.0.0\" xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\">");
        sb.AppendLine("  <Metadata>");
        sb.Append("    <Identity Id=\"LocalAIHub.9f1c4a02-7d3e-4b18-9c55-0a7f2e5b81d4\" Version=\"1.0.0\"");
        if (language != null) sb.Append(" Language=\"" + language + "\"");
        sb.Append(" Publisher=\"" + (publisher ?? "LocalAIHub") + "\" />");
        sb.AppendLine();
        if (displayName != null) sb.AppendLine("    <DisplayName>" + displayName + "</DisplayName>");
        if (description != null) sb.AppendLine("    <Description>" + description + "</Description>");
        if (moreInfo != null) sb.AppendLine("    <MoreInfo>" + moreInfo + "</MoreInfo>");
        if (license != null) sb.AppendLine("    <License>" + license + "</License>");
        if (icon != null) sb.AppendLine("    <Icon>" + icon + "</Icon>");
        if (tags != null) sb.AppendLine("    <Tags>" + tags + "</Tags>");
        sb.AppendLine("  </Metadata>");
        sb.AppendLine("  <Installation InstalledByMsi=\"false\">");
        sb.AppendLine("    <InstallationTarget Id=\"Microsoft.VisualStudio.Community\" Version=\"[18.0,19.0)\">");
        sb.AppendLine("      <ProductArchitecture>amd64</ProductArchitecture>");
        sb.AppendLine("    </InstallationTarget>");
        if (threeTargets)
        {
            sb.AppendLine("    <InstallationTarget Id=\"Microsoft.VisualStudio.Pro\" Version=\"[18.0,19.0)\">");
            sb.AppendLine("      <ProductArchitecture>amd64</ProductArchitecture>");
            sb.AppendLine("    </InstallationTarget>");
            sb.AppendLine("    <InstallationTarget Id=\"Microsoft.VisualStudio.Enterprise\" Version=\"[18.0,19.0)\">");
            sb.AppendLine("      <ProductArchitecture>amd64</ProductArchitecture>");
            sb.AppendLine("    </InstallationTarget>");
        }
        sb.AppendLine("  </Installation>");
        if (withNdp)
        {
            sb.AppendLine("  <Dependencies>");
            sb.AppendLine("    <Dependency Id=\"Microsoft.Framework.NDP\" DisplayName=\"Microsoft .NET Framework\" Version=\"[4.7.2,)\" />");
            sb.AppendLine("  </Dependencies>");
        }
        if (withPrereq)
        {
            sb.AppendLine("  <Prerequisites>");
            sb.AppendLine("    <Prerequisite Id=\"Microsoft.VisualStudio.Component.CoreEditor\" Version=\"[18.0,19.0)\" DisplayName=\"Visual Studio core editor\" />");
            sb.AppendLine("  </Prerequisites>");
        }
        sb.AppendLine("  <Assets>");
        sb.AppendLine("    <Asset Type=\"Microsoft.VisualStudio.VsPackage\" Path=\"LocalAIHub.pkgdef\" />");
        sb.AppendLine("  </Assets>");
        sb.AppendLine("</PackageManifest>");
        return sb.ToString();
    }

    /// <summary>把基线包的清单替换成给定内容，生成试验包。</summary>
    private static void MakeTrial(string manifestXml)
    {
        if (File.Exists(_work)) File.Delete(_work);
        File.Copy(_baseVsix, _work);
        using (var zip = ZipFile.Open(_work, ZipArchiveMode.Update))
        {
            var e = zip.Entries.FirstOrDefault(x =>
                x.FullName.Equals("extension.vsixmanifest", StringComparison.OrdinalIgnoreCase));
            if (e != null) e.Delete();
            var ne = zip.CreateEntry("extension.vsixmanifest");
            using (var w = new StreamWriter(ne.Open(), new UTF8Encoding(false)))
                w.Write(manifestXml);
        }
    }

    private static string ReadManifestFrom(string vsix)
    {
        using (var zip = ZipFile.OpenRead(vsix))
        {
            var e = zip.Entries.First(x => x.FullName == "extension.vsixmanifest");
            using (var sr = new StreamReader(e.Open(), Encoding.UTF8)) return sr.ReadToEnd();
        }
    }

    private static string TryEngine()
    {
        try
        {
            var o = _ctor.Invoke(new object[] { _work, System.Globalization.CultureInfo.CurrentUICulture });
            return "OK (" + (o == null ? "null" : o.GetType().Name) + ")";
        }
        catch (TargetInvocationException tie)
        {
            var ex = tie.InnerException;
            return Describe(ex);
        }
        catch (Exception ex) { return Describe(ex); }
    }

    /// <summary>
    /// 只取类型名和消息：直接 ToString() 会因为引擎的异常类型在宿主里解析不到
    /// 而抛 "由于 Exception.ToString() 失败"（已经踩过）。
    /// </summary>
    private static string Describe(Exception ex)
    {
        if (ex == null) return "(no exception)";
        var sb = new StringBuilder();
        var depth = 0;
        while (ex != null && depth < 5)
        {
            string t, m;
            try { t = ex.GetType().FullName; } catch { t = "(type unavailable)"; }
            try { m = ex.Message; } catch { m = "(message unavailable)"; }
            if (depth > 0) sb.Append(" | inner: ");
            sb.Append(t).Append(": ").Append(m == null ? "" : m.Replace("\r", " ").Replace("\n", " "));
            try { ex = ex.InnerException; } catch { ex = null; }
            depth++;
        }
        return sb.ToString();
    }
}
