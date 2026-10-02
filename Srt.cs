using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OmniSubs.Glossary;

namespace OmniSubs.Subtitles;

/// <summary>一条字幕属于哪条通道，决定它在双轨布局里显示在哪。</summary>
internal enum SubtitleCueLane
{
    /// <summary>对白。走播放器默认的底部位置，不加任何覆盖标签。</summary>
    Audio,

    /// <summary>画面上的文字。置顶显示，见 <see cref="Defaults.TopOverrideTag"/>。</summary>
    Video,
}

/// <summary>一条字幕，时刻落在整部影片的时间轴上（从文件开头算起）。</summary>
internal sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text, SubtitleCueLane Lane);

/// <summary>
/// 一条字幕在它自己那个窗口里的位置（时刻从这一段媒体自己的开头算起），与 <see cref="SubtitleCue"/>
/// 是两个坐标系，不可混用。
/// </summary>
internal sealed record SubtitleWindowCue(TimeSpan Start, TimeSpan End, string Text, SubtitleCueLane Lane);

/// <summary>
/// 字幕的读写：从模型返回的任意文本里抠出 SRT，修成播放器接受的形状，再把两条通道的条目合成一份文档。
/// </summary>
internal static partial class Srt
{
    /// <summary>字幕文件的扩展名。</summary>
    public const string Extension = ".srt";

    /// <summary>一个时间戳，小时位可有可无：<c>00:12,340</c> 与 <c>01:00,160</c> 都接受。</summary>
    private const string StampStart = @"(?:(?<h0>\d{1,3}):)?(?<m0>\d{1,2}):(?<s0>\d{1,2})[,.:](?<f0>\d{1,3})";
    private const string StampEnd = @"(?:(?<h1>\d{1,3}):)?(?<m1>\d{1,2}):(?<s1>\d{1,2})[,.:](?<f1>\d{1,3})";

    [GeneratedRegex(@"^\s*" + StampStart + @"\s*-->\s*" + StampEnd)]
    private static partial Regex CueLineRegex();

    [GeneratedRegex(@"^\s*GLOSSARY\s*[:：]\s*(?<body>.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GlossaryLineRegex();

    /// <summary>
    /// 从返回的文本里抠出字幕条目，全部打上 <paramref name="lane"/> 指定的通道记号（模型说什么都不改变它）。
    /// 回来的时刻是段内的（从这一段媒体的起点算起），调用方须自行按所属窗口换算成整片时刻。
    /// </summary>
    public static List<SubtitleWindowCue> Parse(string rawText, SubtitleCueLane lane)
    {
        var lines = SplitLines(rawText);
        var cues = new List<SubtitleWindowCue>();
        var index = 0;

        while (index < lines.Length)
        {
            if (!TryReadCueHeader(lines[index], out var start, out var end))
            {
                index++;
                continue;
            }

            index++;

            var text = new StringBuilder();
            while (index < lines.Length)
            {
                var line = lines[index];

                // 一条条目要么结束在空行，要么结束在下一个条目头上，谁先到算谁。
                if (string.IsNullOrWhiteSpace(line))
                {
                    index++;
                    break;
                }

                if (TryReadCueHeader(line, out _, out _))
                {
                    break;
                }

                if (text.Length > 0)
                {
                    text.Append('\n');
                }

                text.Append(line.Trim());
                index++;
            }

            var body = CleanupText(text.ToString());
            if (body.Length > 0)
            {
                cues.Add(new SubtitleWindowCue(start, end, body, lane));
            }
        }

        return cues;
    }

    /// <summary>
    /// 读模型追加在末尾的那行 <c>GLOSSARY:</c>，格式是 <c>原文 → 译名; 原文 → 译名</c>；拆不成这个形状的一律丢掉。
    /// </summary>
    public static List<GlossaryTerm> ParseGlossary(string rawText)
    {
        var terms = new List<GlossaryTerm>();

        var match = GlossaryLineRegex().Match(rawText);
        if (!match.Success)
        {
            return terms;
        }

        foreach (var item in match.Groups["body"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split(["→", "->"], 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            {
                terms.Add(new GlossaryTerm { Source = parts[0], Target = parts[1] });
            }
        }

        return terms;
    }

    /// <summary>把两条通道各自的条目分别规整，再按时间交叉合并成一份时间轴；通道之间允许重叠，同一通道内不允许。</summary>
    public static List<SubtitleCue> Compose(IReadOnlyList<SubtitleCue> cues) =>
        cues.GroupBy(cue => cue.Lane)
            .SelectMany(group => Normalize(group))
            .OrderBy(cue => cue.Start)
            .ThenBy(cue => cue.End)
            .ToList();

    /// <summary>
    /// 把同一通道的条目按给定顺序修成一条合法时间轴：起点推到前一条说完之后，零长条目补足最短时长，
    /// 保证严格递增、互不重叠。
    /// </summary>
    public static List<SubtitleCue> Normalize(IEnumerable<SubtitleCue> cues)
    {
        var result = new List<SubtitleCue>();

        foreach (var cue in cues)
        {
            var start = cue.Start < TimeSpan.Zero ? TimeSpan.Zero : cue.Start;
            var end = cue.End;

            if (end <= start)
            {
                end = start + Defaults.MinimumCueLength;
            }

            if (result.Count > 0)
            {
                var previous = result[^1];

                if (start < previous.End)
                {
                    start = previous.End;
                    if (end <= start)
                    {
                        end = start + Defaults.MinimumCueLength;
                    }
                }
            }

            result.Add(cue with { Start = start, End = end });
        }

        return result;
    }

    /// <summary>
    /// 写成一份合规的 SRT：序号从 1 起、时间戳严格递增；换行与编码见 <see cref="Defaults"/>。置顶标签在这里加。
    /// </summary>
    public static string Serialize(IReadOnlyList<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        var number = 1;

        foreach (var cue in cues)
        {
            builder.Append(number++.ToString(CultureInfo.InvariantCulture)).Append(Defaults.NewLine);
            builder.Append(Format(cue.Start))
                .Append(" --> ")
                .Append(Format(cue.End))
                .Append(Defaults.NewLine);

            var text = cue.Lane == SubtitleCueLane.Video ? Defaults.TopOverrideTag + cue.Text : cue.Text;
            builder.Append(text.Replace("\n", Defaults.NewLine)).Append(Defaults.NewLine).Append(Defaults.NewLine);
        }

        return builder.ToString();
    }

    /// <summary>落盘：UTF-8 无 BOM；换行由 <see cref="Serialize"/> 定为 LF。</summary>
    public static void Save(string path, IReadOnlyList<SubtitleCue> cues) =>
        File.WriteAllText(path, Serialize(cues), new UTF8Encoding(false));

    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}");
    }

    /// <summary>去掉 Markdown 围栏行，其余行原样保留。</summary>
    public static string StripDecorations(string rawText) =>
        string.Join(
            '\n',
            SplitLines(rawText).Where(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal)));

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static bool TryReadCueHeader(string line, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;

        var match = CueLineRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        start = ReadStamp(match, 0);
        end = ReadStamp(match, 1);
        return true;
    }

    private static TimeSpan ReadStamp(Match match, int slot)
    {
        var hours = match.Groups[$"h{slot}"].Value;
        var minutes = match.Groups[$"m{slot}"].Value;
        var seconds = match.Groups[$"s{slot}"].Value;

        // 一位或两位小数是"十分之一、百分之一秒"，不是毫秒。
        var fraction = match.Groups[$"f{slot}"].Value;
        fraction = fraction.Length switch
        {
            1 => fraction + "00",
            2 => fraction + "0",
            _ => fraction,
        };

        return new TimeSpan(
            0,
            hours.Length > 0 ? int.Parse(hours, CultureInfo.InvariantCulture) : 0,
            int.Parse(minutes, CultureInfo.InvariantCulture),
            int.Parse(seconds, CultureInfo.InvariantCulture),
            int.Parse(fraction, CultureInfo.InvariantCulture));
    }

    /// <summary>把一条条目的多行并起来，顺手去掉模型偶尔撒进正文的 Markdown 强调符。</summary>
    private static string CleanupText(string text) =>
        string.Join(
            '\n',
            text.Split('\n')
                .Select(line => line.Replace("**", string.Empty).Trim())
                .Where(line => line.Length > 0));
}
