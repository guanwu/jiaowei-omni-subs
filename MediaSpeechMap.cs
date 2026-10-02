namespace OmniSubs.Media;

/// <summary>
/// 一个窗口里人声真正从哪里起、到哪里落（量出来的，不是问模型要的）。
/// 模型的时间戳会被吸附到这些边界上；时刻以窗口起点为 0，与模型给的时间戳同处一个坐标系。
/// </summary>
internal sealed class MediaSpeechMap(IReadOnlyList<(TimeSpan Start, TimeSpan End)> speech)
{
    /// <summary>把一句话的开始吸附到最近的、量出来的人声起点上 —— 够近才吸。</summary>
    public TimeSpan SnapStart(TimeSpan value) => Snap(Onsets, value);

    /// <summary>把一句话的结束吸附到最近的、量出来的人声落点上 —— 够近才吸。</summary>
    public TimeSpan SnapEnd(TimeSpan value) => Snap(Offsets, value);

    private IEnumerable<TimeSpan> Onsets => speech.Select(segment => segment.Start);

    private IEnumerable<TimeSpan> Offsets => speech.Select(segment => segment.End);

    private static TimeSpan Snap(IEnumerable<TimeSpan> boundaries, TimeSpan value)
    {
        var best = value;
        var bestDistance = TimeSpan.FromSeconds(Defaults.SnapToleranceSeconds);

        foreach (var boundary in boundaries)
        {
            var distance = (boundary - value).Duration();
            if (distance < bestDistance)
            {
                best = boundary;
                bestDistance = distance;
            }
        }

        return best;
    }
}
