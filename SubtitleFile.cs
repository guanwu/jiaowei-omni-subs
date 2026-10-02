using OmniSubs.Glossary;

namespace OmniSubs.Subtitles;

/// <summary>
/// 字幕的落点：此刻已经定下来的全部条目，写到哪里去。与 <see cref="GlossaryFile"/> 对称，落在视频旁边。
/// 每走完一个窗口就重写一遍，写出去的一直是识别到那一刻为止的全部条目。
/// </summary>
internal sealed class SubtitleFile(string path)
{
    public string FilePath { get; } = path;

    /// <summary>
    /// 最后一次落盘失败的原因；写成过、或者还没写过，都是 <c>null</c>。
    /// 识别跑完后由调用方检查：不为 <c>null</c> 就是这份字幕没能留在磁盘上，得当成"这个视频没产出"。
    /// </summary>
    public string? LastError { get; private set; }

    private bool _announced;

    /// <summary>某个视频所用的那一份：同名、换成 <see cref="Srt.Extension"/>。</summary>
    public static SubtitleFile ForVideo(string videoPath) =>
        new(Path.ChangeExtension(videoPath, Srt.Extension));

    /// <summary>
    /// 把当前完整的条目写出去；空的什么都不写。写不动只记入 <see cref="LastError"/>、不往外抛，
    /// 下个窗口还会再写一遍；日志只在从"写得成"变成"写不动"那一次报一行。
    /// </summary>
    public void Publish(IReadOnlyList<SubtitleCue> cues)
    {
        if (cues.Count == 0)
        {
            return;
        }

        try
        {
            Srt.Save(FilePath, cues);

            // 第一次写成时说一声：从这一刻起用户就有字幕可看了。后面每段重写不再重复报。
            if (!_announced)
            {
                _announced = true;
                Log.Info($"    字幕可用 : {FilePath}（此后每走完一个窗口重写一次）");
            }

            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (LastError is null)
            {
                Log.Warn($"        字幕暂时写不出去，下个窗口再试：{ex.Message}");
            }

            LastError = ex.Message;
        }
    }
}
