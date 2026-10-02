using System.Globalization;
using OmniSubs.Glossary;
using OmniSubs.Media;
using OmniSubs.Subtitles;

namespace OmniSubs.Recognition;

/// <summary>
/// 一次请求问什么。提示词由三段拼出：段在影片里的位置说明、规则段、术语段。
/// 回答的格式契约（一段 SRT，外加一行 <c>GLOSSARY:</c>，后者只在问过术语时才有）由解析器认，不属于可替换的部分，档位能换的是规则段的措辞。
/// </summary>
internal static class Prompt
{
    /// <summary>念对白用的规则。</summary>
    private const string DialogueRules =
        """
        这段音频是影视对白的一部分，可能是任何一种语言。逐句翻译成简体中文，输出 SRT 字幕。

        只翻译人声对白。不要音效、不要画面描写、不要说话人标注、不要任何解释或说明文字。
        听不清的地方写「…」，不要靠猜补齐。

        时间戳规则：
        采用 HH:MM:SS,mmm --> HH:MM:SS,mmm 格式。
        开始时间取该句第一个字开始发声的时刻，结束时间取最后一个字收声的时刻，紧贴人声边界。
        两句之间有明显停顿（约 0.4 秒以上）就必须拆成两条，各自标注时间，不要合并成一条。
        时间戳必须严格递增，互不重叠。

        每一条字幕不超过 40 个汉字，过长时在词义完整处断句并各标各的时间。
        """;

    /// <summary>
    /// 抄画面文字用的规则。采样间隔与写进容器帧率的是同一个来源（<see cref="Defaults.FrameInterval"/>），两处必须一致。
    /// </summary>
    private static readonly string VideoRules = string.Create(
        CultureInfo.InvariantCulture,
        $"""
        这段画面取自影视，按每 {Defaults.FrameInterval.TotalSeconds:0.##} 秒一张重新取样。

        只抄写画面上叠加的文字：标题、字卡、招牌、海报、字幕板上的字。
        不要描写画面内容，不要转写听不见的对话，不要说话人标注、不要任何解释或说明文字。
        看不清的地方写「…」，不要靠猜补齐。
        画面上没有文字的时候就什么都不输出，不要输出空条目。

        时间戳规则：
        采用 HH:MM:SS,mmm --> HH:MM:SS,mmm 格式。
        开始时间取这些字开始出现的时刻，结束时间取它们消失的时刻。
        时间戳必须严格递增，互不重叠。

        文字一律译成简体中文；人名、地名、招牌上的专有名词按中文里的通行译法翻译。
        每一条不超过 40 个汉字。
        """);

    /// <summary>
    /// 第一次处理时用的一段：还没有任何既定译名，由模型为这一段定名。
    /// </summary>
    private const string IntroduceTerms =
        """

        另外，把本段出现的人名、地名、专有名词记下来，在字幕之后另起一行，用这个格式输出：

        GLOSSARY: 原文 → 译名; 原文 → 译名

        人名、地名、专有名词都按中文里的通行译法翻译；原文与中文共用汉字的写法保留原样。
        如果本段没有专有名词，就输出 GLOSSARY: (空)
        """;

    /// <summary>
    /// 已经不缺译名时追加在列表后面的那半：把本段新定下的名字报回来的格式。
    /// 列表必须整份重复，不做摘要。
    ///
    /// "必须沿用下面的译名"那半写在 <see cref="Build"/> 里；只沿用、不回收的那一遍（画面）只拿前一半。
    /// </summary>
    private const string ReuseTermsTail =
        """

        本段若出现新的专有名词，按同样方式定名，并在字幕之后另起一行输出：

        GLOSSARY: 原文 → 译名; 原文 → 译名
        """;

    /// <summary>
    /// 拼出一个窗口的请求。<paramref name="rulesOverride"/> 是档位自带的提示词模板，
    /// 给了就用它替掉按通道的那段规则；段的位置说明与术语段不受它影响。
    ///
    /// <paramref name="terms"/> 是要它沿用的已定译名，<c>null</c> 表示这一遍不谈术语（<c>--no-glossary</c>）。
    /// <paramref name="collectTerms"/> 是它要不要把本段新定下的名字报回来；不回收的那一遍（画面）就不问。
    /// </summary>
    public static string Build(
        SubtitleCueLane lane,
        MediaWindow window,
        IReadOnlyList<GlossaryTerm>? terms,
        bool collectTerms,
        string? rulesOverride = null)
    {
        var subject = lane == SubtitleCueLane.Audio ? "本段音频" : "本段画面";

        var header =
            $"{subject}是影片 {window.Describe()} 的部分。\n" +
            "时间戳请从本段的起点 00:00:00,000 开始计算，只标注本段内出现的内容。\n\n";

        var rules = string.IsNullOrWhiteSpace(rulesOverride)
            ? lane == SubtitleCueLane.Audio ? DialogueRules : VideoRules
            : rulesOverride;

        return header + rules + Terms(terms, collectTerms);
    }

    private static string Terms(IReadOnlyList<GlossaryTerm>? terms, bool collect)
    {
        if (terms is null)
        {
            return string.Empty;
        }

        if (terms.Count == 0)
        {
            // 一条既定译名都还没有：要不要它开始定名，取决于这一遍回不回收。
            return collect ? IntroduceTerms : string.Empty;
        }

        var list = string.Join("; ", terms.Select(term => $"{term.Source} → {term.Target}"));

        return "\n\n前面已经出现过的专有名词必须沿用下面的译名，不得另起新译名：\n\n"
            + list
            + (collect ? ReuseTermsTail : string.Empty);
    }
}
