using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LocalAIHub.Core
{
    /// <summary>
    /// 判断"这句话要不要联网"，以及"检索词该用什么"。
    /// 规则全部来自上一轮实测（见 docs/SHARED-FACTS.md 与工作区的优化方案文档），
    /// 不是拍脑袋写的：
    ///   - 87.5% 的兜底 True 太宽，每次白搜 = 11 秒 + 1500 prompt token
    ///   - 检索词用整句会把结果带偏（整句→教程首页；抽报错原文→5/5 正解页）
    /// </summary>
    public static class QueryDecision
    {
        // ---- 时效 / 外部信息关键词 ----
        private static readonly string[] RealtimeKeywords =
        {
            "最新", "现在", "今天", "目前", "最近", "版本", "价格", "新闻", "消息",
            "官网", "网站", "下载", "安装包", "更新", "发布", "上市", "行情",
            "天气", "汇率", "比分", "直播",
        };

        private static readonly string[] PlatformKeywords =
        {
            "b站", "bilibili", "哔哩哔哩", "知乎", "微博", "淘宝", "京东",
            "github", "官网", "网站", "链接",
        };

        // ---- 需要新信息的代码题（训练数据可能过时） ----
        private static readonly string[] CodeSearchKeywords =
        {
            "报错", "错误", "异常", "error", "exception", "traceback", "bug",
            "失败", "不行", "不能用", "无法", "不生效", "不工作",
            "版本", "最新", "更新", "新特性", "新功能", "deprecated", "弃用",
            "文档", "api", "怎么用", "如何使用", "用法", "示例", "教程",
            "配置", "环境", "依赖", "兼容", "升级", "迁移",
        };

        // ---- 纯代码题（模型自己会，别搜） ----
        private static readonly string[] CodePureSignals =
        {
            "写一个", "写段", "实现", "改一下", "优化", "重构", "封装",
            "函数", "算法", "数据结构", "排序", "查找", "遍历",
        };

        private static readonly Regex PureKnowledgeRe = new Regex(
            "^(?:请|麻烦)?(?:简单|大概|简要|通俗|简短|一句话|用一句话|用几句?话)?\\s*" +
            "(?:解释|介绍|说明|讲讲|说说|讲解|科普|描述)(?:一下|下)?\\s*" +
            "|^什么是|^啥是|^什么叫|^为啥|^为什么",
            RegexOptions.Compiled);

        private static readonly Regex ContinuationRe = new Regex(
            "^(?:请|麻烦)?\\s*(?:继续|接着|往下|再说|好的?|行吧?|嗯+|哦+|是的|对的?|可以|" +
            "ok|okay|go on|continue|还有呢?|还有吗|再来|下一个|然后呢|接下来|" +
            "看一下|看看|分析一下|检查一下|review|谢谢|多谢|辛苦了)" +
            "[。.!！?？~～、\\s]*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CodePatterns = new Regex(
            "(\\.py|\\.cs|\\.js|\\.ts|\\.java|\\.cpp|\\.c|\\.h|\\.go|\\.rs|\\.vue|\\.jsx|\\.tsx|" +
            "\\.json|\\.xml|\\.yaml|\\.yml|\\.md|" +
            "[A-Za-z]:\\\\|" +
            "\\{.*\\}|\\(.*\\)|=>|def |function |class |import |from .* import|" +
            "Traceback|Exception|Error|error CS\\d+|error TS\\d+)",
            RegexOptions.Compiled);

        private static readonly Regex CapabilityQRe = new Regex(
            "(?:你|您)?\\s*(?:能|可以|能否|可否|会不会|是否能|是否可以|能不能|可不可以)" +
            "\\s*(?:联网|上网|搜索|联网搜索|访问互联网|浏览网页|查资料|搜一下)",
            RegexOptions.Compiled);

        private static readonly Regex EntityQRe = new Regex(
            "(是谁|是什么|介绍(?:一下|下)?|讲讲|说说看|了解|关于).{0,20}$", RegexOptions.Compiled);

        /// <summary>
        /// 数量 / 取值类问题（"苹果股价多少"、"今天天气怎么样"、"谁赢了"）。
        /// 实测踩到的 bug：兜底改成不搜之后，这类问题被漏掉了——它们既没有
        /// 时效关键词，也没有"是什么"这种实体句式，但答案一定在模型的知识之外。
        /// 所以必须单独认出来，否则"兜底不搜"会误伤真实需求。
        /// </summary>
        private static readonly Regex ValueQuestionRe = new Regex(
            "(多少|多少钱|几多|股价|价格|市值|汇率|利率|油价|金价|房价|票价|比分|排名|排第几|" +
            "天气|气温|温度|涨停|跌了|涨了|票房|销量|粉丝数|关注数|播放量|在线人数|" +
            "谁赢|谁输|获胜|冠军|得第几|多少分)",
            RegexOptions.Compiled);

        // 报错原文优先（实测：整句检索 0/5 相关，抽原文 5/5 正解页）
        private static readonly Regex ErrRe = new Regex(
            "((?:[A-Za-z_][A-Za-z0-9_.]*)?(?:Error|Exception|Warning)\\b[^\\n]{0,120})" +
            "|(\\b(?:error|warning)\\s+[A-Z]{2}\\d{3,5}\\b[^\\n]{0,80})" +
            "|(\\b[A-Za-z_][A-Za-z0-9_]*\\.[A-Za-z_][A-Za-z0-9_]*\\([^)\\n]{0,80}\\))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ErrTailRe = new Regex(
            "[\\s，,。；;：:]*(?:怎么|如何|怎样|怎么办|怎么解决|如何解决|解决方法|" +
            "原因|是什么|请问|谢谢|急)[^\\n]*$",
            RegexOptions.Compiled);

        private static readonly Regex ErrDanglingRe = new Regex(
            "\\s+(?:from|in|at|on|of|to|with|for|by|line|the|a|an)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MetaPrefixRe = new Regex(
            "^(?:请问|麻烦问下|问下|问一下|帮我(?:查|搜|找|看)?|" +
            "你能(?:否)?|你可以(?:吗|么)?|能不能|可不可以|可以不可以|会不会|" +
            "是否能|是否可以|能否|可否|知不知道|知道吗|" +
            "你知道|你了解|你听说过|你认识|你有没有听说过|你有没有了解过)[，,。.\\s]*",
            RegexOptions.Compiled);

        private static readonly Regex DatePrefixRe = new Regex(
            "^(?:现在|今天|当前|目前|眼下)[是为]?\\s*" +
            "(?:\\d{4}年\\d{1,2}月(?:\\d{1,2}日?)?)?" +
            "[，,\\s]*",
            RegexOptions.Compiled);

        private static readonly Regex TrailInstrRe = new Regex(
            "[？?]\\s*(?:请|麻烦|只|仅)?\\s*(?:回答|告诉我|说明|输出|给出|用).*$|" +
            "只?回答[^，。！？\\n]*$|" +
            "(?:请|麻烦)\\s*(?:用|以|给出|提供)[^，。！？\\n]*$",
            RegexOptions.Compiled);

        private static readonly Regex TrailParticleRe = new Regex("[吗么吧呢啊呀]+$", RegexOptions.Compiled);

        public static bool IsCapabilityQuestion(string text)
        {
            return !string.IsNullOrEmpty(text) && CapabilityQRe.IsMatch(text);
        }

        /// <summary>这句话需不需要联网。保守：不确定就不搜（白搜一次 11 秒）。</summary>
        public static bool NeedsWebSearch(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length < 2) return false;
            var t = text.Trim();

            // "你能联网吗" 这类元问题：搜它只会得到词典释义
            if (IsCapabilityQuestion(t)) return false;
            // "继续/好的/还有吗"：没有任何可检索内容
            if (ContinuationRe.IsMatch(t)) return false;

            var tl = t.ToLowerInvariant();

            if (RealtimeKeywords.Any(k => t.Contains(k))) return true;
            if (PlatformKeywords.Any(k => tl.Contains(k))) return true;
            // 代码类时效问题要放在纯概念判定之前，否则"解释一下 React 19 有什么新特性"会被跳过
            if (CodeSearchKeywords.Any(k => tl.Contains(k))) return true;

            // 纯概念题且短：模型自己会
            if (PureKnowledgeRe.IsMatch(t) && t.Length <= 40) return false;

            if (EntityQRe.IsMatch(t)) return true;

            // 纯代码/纯操作类先判（"把 E:\... 里的文件列出来" 这种不该搜）
            if (CodePureSignals.Any(k => t.Contains(k))) return false;
            if (CodePatterns.IsMatch(t)) return false;

            // 数量/取值类问题：答案必然在模型知识之外，必须搜
            if (ValueQuestionRe.IsMatch(t)) return true;

            // 兜底：不搜。（旧版本这里是 True，导致 87.5% 的请求都在白搜）
            return false;
        }

        /// <summary>把用户那句话清洗成检索词（去日期前缀、元问题、格式要求）。</summary>
        public static string CleanQuery(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var q = text;

            if (q.Contains("新闻") || q.Contains("消息") || q.Contains("动态"))
            {
                q = Regex.Replace(q, "^(?:今天|现在|目前|最近|近期|这两天|这段时间)\\s*" +
                                    "(?:有什么|有哪些|有啥|发生了|出了)?\\s*", "");
                q = Regex.Replace(q, "(?:重要的|最新的|近期的|最近的)\\s*", "");
            }
            q = DatePrefixRe.Replace(q, "");
            q = MetaPrefixRe.Replace(q, "");

            var m = Regex.Match(q, "^[^，。！？\\n]*[？?]\\s*(.+)$", RegexOptions.Singleline);
            if (m.Success) q = m.Groups[1].Value;

            q = TrailInstrRe.Replace(q, "");
            q = TrailParticleRe.Replace(q, "");
            q = q.Trim(' ', '，', ',', '。', '.', '!', '！', '？', '?', '\n');
            if (q.Length > 60) q = q.Substring(0, 60).TrimEnd(' ', '，', ',', '。', '.');
            return q.Length > 0 ? q : text.Trim();
        }

        /// <summary>优先用报错原文/标识符当检索词，抽不到再退回整句清洗。</summary>
        public static string ExtractSearchQuery(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var m = ErrRe.Match(text);
            if (m.Success)
            {
                var lit = ErrDanglingRe.Replace(ErrTailRe.Replace(m.Value.Trim(), ""), "").Trim();
                lit = lit.Trim(' ', '，', ',', '。', '；', ';', '：', ':', '\'', '"');
                if (lit.Length >= 6 && lit.Length <= 300) return lit;
            }
            return CleanQuery(text);
        }

        /// <summary>是否是时效类问题（决定用更宽的相关度阈值）。</summary>
        public static bool IsRealtime(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return RealtimeKeywords.Any(k => text.Contains(k));
        }
    }
}
