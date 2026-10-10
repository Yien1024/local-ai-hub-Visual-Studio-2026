using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LocalAIHub.Core
{
    /// <summary>
    /// 一个够用就好的 JSON 读写器（零依赖）。
    ///
    /// 为什么不用 System.Text.Json / Newtonsoft：
    ///   本机网络不可达 nuget.org，NuGet 缓存里也没有这两个包；
    ///   而 VS 自带的 Newtonsoft 版本在不同目录有 13.0.3/13.0.4 两个版本，
    ///   从扩展里引用它要赌 VS 的绑定重定向。手写 300 行换"绝不会因为
    ///   JSON 库版本问题在别人机器上炸"，对要分发给别人的插件来说是划算的。
    ///
    /// 用法（这就是全部 API）：
    ///   var obj = Json.Parse(text);                  // -> Dictionary / List / string / double / bool / null
    ///   obj.GetObject("messages")                    // 便捷取值
    ///   Json.Serialize(dict)                         // -> 紧凑 JSON
    /// </summary>
    public static class Json
    {
        // ---------------- 读取 ----------------

        public static object Parse(string text)
        {
            if (text == null) return null;
            var p = new Parser(text);
            p.SkipWs();
            var value = p.ParseValue();
            p.SkipWs();
            if (!p.AtEnd) throw new FormatException("JSON 尾部有多余内容，位置 " + p.Pos);
            return value;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            var v = Parse(text);
            var d = v as Dictionary<string, object>;
            if (d == null && v != null) throw new FormatException("顶层不是 JSON 对象");
            return d;
        }

        private sealed class Parser
        {
            private readonly string _s;
            public int Pos;
            public Parser(string s) { _s = s; }
            public bool AtEnd => Pos >= _s.Length;

            public void SkipWs()
            {
                while (Pos < _s.Length)
                {
                    var c = _s[Pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') Pos++;
                    else break;
                }
            }

            public object ParseValue()
            {
                SkipWs();
                if (AtEnd) throw new FormatException("JSON 意外结束");
                var c = _s[Pos];
                switch (c)
                {
                    case '{': return ParseObj();
                    case '[': return ParseArr();
                    case '"': return ParseString();
                    case 't':
                        Expect("true"); return true;
                    case 'f':
                        Expect("false"); return false;
                    case 'n':
                        Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            private void Expect(string lit)
            {
                if (Pos + lit.Length > _s.Length || string.CompareOrdinal(_s, Pos, lit, 0, lit.Length) != 0)
                    throw new FormatException("JSON 字面量错误，位置 " + Pos);
                Pos += lit.Length;
            }

            private Dictionary<string, object> ParseObj()
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                Pos++; // {
                SkipWs();
                if (Pos < _s.Length && _s[Pos] == '}') { Pos++; return d; }
                while (true)
                {
                    SkipWs();
                    if (AtEnd || _s[Pos] != '"') throw new FormatException("JSON 对象键必须是字符串，位置 " + Pos);
                    var key = ParseString();
                    SkipWs();
                    if (AtEnd || _s[Pos] != ':') throw new FormatException("JSON 对象缺 ':'，位置 " + Pos);
                    Pos++;
                    d[key] = ParseValue();
                    SkipWs();
                    if (AtEnd) throw new FormatException("JSON 对象未闭合");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == '}') { Pos++; return d; }
                    throw new FormatException("JSON 对象分隔符错误，位置 " + Pos);
                }
            }

            private List<object> ParseArr()
            {
                var list = new List<object>();
                Pos++; // [
                SkipWs();
                if (Pos < _s.Length && _s[Pos] == ']') { Pos++; return list; }
                while (true)
                {
                    list.Add(ParseValue());
                    SkipWs();
                    if (AtEnd) throw new FormatException("JSON 数组未闭合");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == ']') { Pos++; return list; }
                    throw new FormatException("JSON 数组分隔符错误，位置 " + Pos);
                }
            }

            private string ParseString()
            {
                Pos++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw new FormatException("JSON 字符串未闭合");
                    var c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw new FormatException("JSON 转义未完成");
                    var e = _s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > _s.Length) throw new FormatException("\\u 转义不完整");
                            var hex = _s.Substring(Pos, 4);
                            Pos += 4;
                            sb.Append((char)ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            break;
                        default: throw new FormatException("未知 JSON 转义 \\" + e);
                    }
                }
            }

            private object ParseNumber()
            {
                var start = Pos;
                if (Pos < _s.Length && (_s[Pos] == '-' || _s[Pos] == '+')) Pos++;
                while (Pos < _s.Length)
                {
                    var c = _s[Pos];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') Pos++;
                    else break;
                }
                var raw = _s.Substring(start, Pos - start);
                double d;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw new FormatException("非法 JSON 数字 '" + raw + "'，位置 " + start);
            }
        }

        // ---------------- 写入 ----------------

        public static string Serialize(object value, bool pretty = false)
        {
            var sb = new StringBuilder();
            Write(sb, value, pretty, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v, bool pretty, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is int || v is long || v is short || v is byte)
            {
                sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (v is double || v is float || v is decimal)
            {
                var d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("0"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var dict = v as IDictionary<string, object>;
            if (dict != null) { WriteObject(sb, dict, pretty, depth); return; }
            var arr = v as IEnumerable;
            if (arr != null) { WriteArray(sb, arr, pretty, depth); return; }
            WriteString(sb, v.ToString());
        }

        private static void WriteObject(StringBuilder sb, IDictionary<string, object> d, bool pretty, int depth)
        {
            sb.Append('{');
            var first = true;
            foreach (var kv in d)
            {
                if (!first) sb.Append(',');
                if (pretty) { sb.Append('\n'); Indent(sb, depth + 1); }
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                if (pretty) sb.Append(' ');
                Write(sb, kv.Value, pretty, depth + 1);
            }
            if (pretty && !first) { sb.Append('\n'); Indent(sb, depth); }
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable arr, bool pretty, int depth)
        {
            sb.Append('[');
            var first = true;
            foreach (var item in arr)
            {
                if (!first) sb.Append(',');
                if (pretty) { sb.Append('\n'); Indent(sb, depth + 1); }
                first = false;
                Write(sb, item, pretty, depth + 1);
            }
            if (pretty && !first) { sb.Append('\n'); Indent(sb, depth); }
            sb.Append(']');
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            sb.Append(new string(' ', depth * 2));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);   // 非 ASCII 直接输出 UTF-8 字符（调用方保证 UTF-8 编码）
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- 便捷读取扩展 ----------------

        public static Dictionary<string, object> AsObject(this object v)
        {
            return v as Dictionary<string, object>;
        }

        public static List<object> AsArray(this object v)
        {
            return v as List<object>;
        }

        public static string AsString(this object v)
        {
            return v == null ? null : (v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        public static double AsDouble(this object v, double fallback = 0)
        {
            if (v == null) return fallback;
            if (v is double) return (double)v;
            if (v is string) { double d; return double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback; }
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        public static long AsLong(this object v, long fallback = 0)
        {
            if (v == null) return fallback;
            if (v is double) return (long)(double)v;
            if (v is string) { long l; return long.TryParse((string)v, NumberStyles.Integer, CultureInfo.InvariantCulture, out l) ? l : fallback; }
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        public static bool AsBool(this object v, bool fallback = false)
        {
            if (v == null) return fallback;
            if (v is bool) return (bool)v;
            var s = v as string;
            if (s != null) return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
            return fallback;
        }

        /// <summary>取对象字段；不存在返回 null。</summary>
        public static object Get(this Dictionary<string, object> obj, string key)
        {
            if (obj == null) return null;
            object v;
            return obj.TryGetValue(key, out v) ? v : null;
        }

        public static Dictionary<string, object> GetObject(this Dictionary<string, object> obj, string key)
        {
            return obj.Get(key).AsObject();
        }

        public static List<object> GetArray(this Dictionary<string, object> obj, string key)
        {
            return obj.Get(key).AsArray();
        }

        public static string GetString(this Dictionary<string, object> obj, string key)
        {
            return obj.Get(key).AsString();
        }

        public static long GetLong(this Dictionary<string, object> obj, string key, long fallback = 0)
        {
            return obj.Get(key).AsLong(fallback);
        }

        public static double GetDouble(this Dictionary<string, object> obj, string key, double fallback = 0)
        {
            return obj.Get(key).AsDouble(fallback);
        }

        public static bool GetBool(this Dictionary<string, object> obj, string key, bool fallback = false)
        {
            return obj.Get(key).AsBool(fallback);
        }
    }
}
