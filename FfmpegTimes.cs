using System.Globalization;

namespace OmniSubs.Media.Ffmpeg;

/// <summary>
/// 时间的两种写法，都发生在与 ffmpeg 的边界上：一种写进参数，一种从它的日志里读回来。两种都必须与语言环境无关。
/// </summary>
internal static class FfmpegTimes
{
    /// <summary>把一个时刻写成 ffmpeg 收的十进制秒。</summary>
    public static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// 读出日志行里某个标记后面那个数。silencedetect 写的是
    /// <c>silence_start: 12.345</c>，标记与数字之间隔着空格，空格可有可无。
    /// </summary>
    public static TimeSpan? ReadSeconds(string line, string marker)
    {
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var rest = line.AsSpan(index + marker.Length);
        var offset = 0;
        while (offset < rest.Length && char.IsWhiteSpace(rest[offset]))
        {
            offset++;
        }

        // 跳着读（-ss 在 -i 前面）之后的时间戳可能是负的，负号要一起收进来。
        var length = 0;
        if (offset < rest.Length && rest[offset] == '-')
        {
            length++;
        }

        while (offset + length < rest.Length
               && (char.IsAsciiDigit(rest[offset + length]) || rest[offset + length] == '.'))
        {
            length++;
        }

        return length > 0
               && double.TryParse(
                   rest.Slice(offset, length),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}
