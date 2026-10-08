using OmniSubs.Media;
using OmniSubs.Subtitles;

namespace OmniSubs.Recognition;

/// <summary>
/// 一条通道收下的条目：窗口一个一个交进来，随时可以要一份到此刻为止的完整时间轴。
///
/// 它管的是跨窗口才有的那两件事：相邻两个窗口会把重叠那几秒里的同一段内容各译一遍，这里按时间
/// 把重复的配对、只留离自己窗口中心更近的那一份；以及把段内时刻换算成整片时刻 ——
/// <see cref="SubtitleWindowCue"/> 与 <see cref="SubtitleCue"/> 是两个坐标系，换算只在这里做。
///
/// <see cref="Placed"/> 每次都是一份完整的时间轴，且交出去之后不再改动。另一条通道因此可以直接读它，
/// 不必加锁：读到的要么是上一份完整的、要么是这一份完整的。
/// </summary>
internal sealed class LaneCues
{
    /// <summary>两段内容挨得多近才算同一段被两个窗口各译了一遍。</summary>
    private static readonly TimeSpan DuplicateTolerance = TimeSpan.FromSeconds(2.5);

    private readonly List<Candidate> _received = [];

    private IReadOnlyList<SubtitleCue> _placed = [];

    /// <summary>收下一个窗口的全部条目；没有内容的窗口不必交进来。</summary>
    public void Add(MediaWindow window, IReadOnlyList<SubtitleWindowCue> cues)
    {
        if (cues.Count == 0)
        {
            return;
        }

        _received.AddRange(cues.Select(cue => new Candidate(cue, window)));

        // 去重是对整个列表算的，所以交出去的始终是"到目前为止完整的那一份"。
        _placed = DropDuplicates();
    }

    /// <summary>到这一刻为止的那一份时间轴，已按整片时刻排好；还没有内容就是空的。</summary>
    public IReadOnlyList<SubtitleCue> Placed => _placed;

    /// <summary>一共收下多少条，含后来被当作跨窗重复丢掉的。</summary>
    public int Received => _received.Count;

    /// <summary>
    /// 丢掉两个窗口把同一段内容各译了一遍时多出来的那一份，留离自己窗口中心更近的那一份。
    /// 配对只按时间、且只在同一条通道之内做。
    /// </summary>
    private List<SubtitleCue> DropDuplicates()
    {
        var ordered = _received.OrderBy(candidate => candidate.AbsoluteStart).ToList();
        var dropped = new HashSet<Candidate>();

        for (var i = 0; i < ordered.Count; i++)
        {
            if (dropped.Contains(ordered[i]))
            {
                continue;
            }

            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (ordered[j].AbsoluteStart - ordered[i].AbsoluteStart > DuplicateTolerance)
                {
                    break;
                }

                // 同一个窗口里的两条不是重复；已挑掉的那条也不再参与。
                if (ordered[j].Window.Index == ordered[i].Window.Index || dropped.Contains(ordered[j]))
                {
                    continue;
                }

                dropped.Add(
                    ordered[j].DistanceFromCentre < ordered[i].DistanceFromCentre
                        ? ordered[i]
                        : ordered[j]);
                break;
            }
        }

        return ordered
            .Where(candidate => !dropped.Contains(candidate))
            .Select(candidate => candidate.Place())
            .ToList();
    }

    /// <summary>
    /// 一条收下的条目，连同是哪个窗口产出它的；条目本身还在段内坐标系里
    /// （<see cref="SubtitleWindowCue"/>），换算成整片时刻只在 <see cref="Place"/> 里做。
    /// </summary>
    private sealed record Candidate(SubtitleWindowCue Cue, MediaWindow Window)
    {
        /// <summary>它在整部影片上的起点。</summary>
        public TimeSpan AbsoluteStart => Place().Start;

        /// <summary>它离产出它的那个窗口的中心有多远；越远说明那一窗看它看得越不完整。</summary>
        public TimeSpan DistanceFromCentre => ((Cue.Start + Cue.End) / 2 - Window.Duration / 2).Duration();

        /// <summary>把它放回整部影片的时间轴上。</summary>
        public SubtitleCue Place() => new(
            Window.Start + Cue.Start,
            Window.Start + Cue.End,
            Cue.Text,
            Cue.Lane);
    }
}
