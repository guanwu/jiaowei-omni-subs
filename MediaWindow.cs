using System.Globalization;

namespace OmniSubs.Media;

/// <summary>
/// 时间上的一段媒体，也是交给模型的一次请求所覆盖的范围。序号从 1 数起，与报给用户时说的"第几段"一致。
/// 声音与画面共用同一套分窗。注意两个坐标系：<see cref="Start"/> 相对文件开头，而量出来的人声边界以段的起点为 0。
/// </summary>
internal sealed record MediaWindow(int Index, TimeSpan Start, TimeSpan Duration)
{
    public TimeSpan End => Start + Duration;

    /// <summary>
    /// 这一段在整部片子里的位置，报给人看的样子：<c>5:15–6:15</c>。日志与提示词都取自它，两处必须说同一句话。
    /// </summary>
    public string Describe() => $"{Stamp(Start)}–{Stamp(End)}";

    private static string Stamp(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}

internal static class MediaWindows
{
    /// <summary>
    /// 把整条时间轴切成互相重叠的窗口。声音与画面共用这一套分法，两条通道的条目最后要合并进同一份字幕。
    /// </summary>
    public static List<MediaWindow> Plan(TimeSpan duration)
    {
        var step = Defaults.WindowLength - Defaults.WindowOverlap;
        var windows = new List<MediaWindow>();
        var index = 1;

        for (var start = TimeSpan.Zero; start < duration; start += step)
        {
            var length = duration - start;
            if (length > Defaults.WindowLength)
            {
                length = Defaults.WindowLength;
            }

            // 结尾的零头已经落在前一个窗口的重叠里：尾段短于重叠长度就丢掉，不再请求。
            if (index > 1 && length < Defaults.WindowOverlap)
            {
                break;
            }

            windows.Add(new MediaWindow(index++, start, length));
        }

        return windows;
    }
}
