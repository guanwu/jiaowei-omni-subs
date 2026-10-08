using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OmniSubs.Media;

/// <summary>
/// 文件里的一条音轨。<see cref="Number"/> 是给人看的序号，从 1 数起，也是 <c>--audio-track</c> 收的那个数字。
/// </summary>
internal sealed record MediaAudioTrack(int Number, string? Language, string? Title)
{
    /// <summary>报给用户的样子：序号加上能认出来的标签。</summary>
    public string Describe()
    {
        var tags = new[] { Language, Title }.Where(tag => !string.IsNullOrWhiteSpace(tag)).ToList();
        return tags.Count == 0
            ? $"第 {Number} 条"
            : $"第 {Number} 条（{string.Join("、", tags)}）";
    }
}

/// <summary>
/// 探测的结论，也是识别流程真正要用到的全部事实：文件有多长、有哪些音轨、有没有画面。
/// </summary>
internal sealed record MediaInfo(TimeSpan Duration, IReadOnlyList<MediaAudioTrack> AudioTracks, bool HasVideo)
{
    /// <summary>
    /// 把探测到的三样事实收成一份结论。"没有音轨"在这里就是终点：音频字幕是强制开启的，
    /// 没有可听的内容就直接抛出，不把空音轨清单交给调用方。
    /// </summary>
    public static MediaInfo From(
        string input,
        TimeSpan duration,
        IReadOnlyList<MediaAudioTrack> audioTracks,
        bool hasVideo)
    {
        if (audioTracks.Count == 0)
        {
            throw new MediaException($"{Path.GetFileName(input)} 没有音轨，没有可听的内容。");
        }

        return new MediaInfo(duration, audioTracks, hasVideo);
    }

    /// <summary>
    /// 把 <c>--audio-track</c> 给的写法兑现成一条音轨。全数字按序号认，其余按语言代码认（大小写不敏感）；
    /// 没给就是第一条。认不出来不猜：返回 <c>false</c>，并报出可用的有哪些。
    /// </summary>
    public bool TrySelect(
        string? spec,
        [NotNullWhen(true)] out MediaAudioTrack? track,
        out string? error)
    {
        track = null;
        error = null;

        // 探测时没有音轨就直接抛了，能走到这里至少有一条。
        if (string.IsNullOrWhiteSpace(spec))
        {
            track = AudioTracks[0];
            return true;
        }

        var wanted = spec.Trim();

        if (int.TryParse(wanted, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            track = AudioTracks.FirstOrDefault(item => item.Number == number);
            if (track is not null)
            {
                return true;
            }

            error = $"这个文件没有第 {number} 条音轨。可用：{Available()}";
            return false;
        }

        track = AudioTracks.FirstOrDefault(item =>
            string.Equals(item.Language, wanted, StringComparison.OrdinalIgnoreCase));

        if (track is not null)
        {
            return true;
        }

        error = $"这个文件没有语言为 {wanted} 的音轨。可用：{Available()}";
        return false;
    }

    private string Available() => string.Join("、", AudioTracks.Select(track => track.Describe()));
}
