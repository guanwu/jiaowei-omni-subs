using OmniSubs.Glossary;
using OmniSubs.Media;
using OmniSubs.Media.Ffmpeg;
using OmniSubs.Model;
using OmniSubs.Subtitles;

namespace OmniSubs.Recognition;

/// <summary>
/// 把一部影片变成一份字幕。对白与画面两条通道并发各跑各的，谁也不等谁。
/// <paramref name="videoModel"/> 有值、且探测到有画面时，才开画面通道。
/// </summary>
internal sealed class Subtitler(
    FfmpegMedia media,
    IMultimodalModel audioModel,
    IMultimodalModel? videoModel,
    string? audioTrack)
{
    /// <summary>两段内容挨得多近才算同一段被两个窗口各译了一遍。</summary>
    private static readonly TimeSpan DuplicateTolerance = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// 一条译好的条目，连同是哪个窗口产出它的。条目本身还是段内时刻
    /// （<see cref="SubtitleWindowCue"/>），换算成整片时刻只在 <see cref="Place"/> 里做。
    /// </summary>
    private sealed record Candidate(SubtitleWindowCue Cue, MediaWindow Window)
    {
        /// <summary>这条条目在整部影片上的起点。</summary>
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

    /// <summary>一个窗口要交给模型的东西：一段媒体，以及（只有对白才有）量出来的人声边界。</summary>
    private sealed record WindowMedia(ModelMediaPart Part, MediaSpeechMap? Speech);

    /// <summary>一个窗口的回答。条目还在段内坐标系里，出了循环才换算成影片上的时刻。</summary>
    private sealed record WindowOutcome(
        IReadOnlyList<SubtitleWindowCue> Cues,
        IReadOnlyList<GlossaryTerm> Terms,
        int InputTokens,
        int OutputTokens);

    /// <summary>
    /// 整部影片的字幕，已按时间排好。每走完一段都重写一遍磁盘上的字幕，所以正常跑完时磁盘上的
    /// 内容与返回值是同一份。对白是强制的，读不出音轨就当场抛出 <see cref="MediaException"/>；
    /// 画面是按需的，没有画面只是跳过。
    /// </summary>
    public async Task<List<SubtitleCue>> RunAsync(
        string input,
        SubtitleFile subtitle,
        GlossaryFile? glossary,
        CancellationToken cancellationToken)
    {
        // 时长、音轨、有没有画面都是同一次 ffprobe 的结论，只探一次。
        var info = await media.ProbeAsync(input, cancellationToken).ConfigureAwait(false);

        if (info.Select(audioTrack, out var trackError) is not { } track)
        {
            throw new MediaException(trackError!);
        }

        var windows = MediaWindows.Plan(info.Duration);
        Log.Info($"    窗口     : {windows.Count} 段 · 每段 {Defaults.WindowLength.TotalSeconds:F0} 秒"
            + $" · 重叠 {Defaults.WindowOverlap.TotalSeconds:F0} 秒 · {track.Describe()}");

        // 画面那一条对术语只读不写：沿用对白到这一刻为止定下的译名，但不把新名字写回表 ——
        // 术语表只由对白通道增长。
        Channel? onVideo = null;

        if (videoModel is { } pictureModel)
        {
            if (info.HasVideo)
            {
                onVideo = new Channel(
                    SubtitleCueLane.Video,
                    pictureModel,
                    windows.Count,
                    null,
                    (window, token) => ReadVideoAsync(media, input, window, token));
            }
            else
            {
                Log.Warn($"{Path.GetFileName(input)} 没有画面，跳过视频字幕。");
            }
        }

        // 对白这一条两样都做：沿用已定下的译名，并把这一段新定下的名字并回表。
        var dialogue = new Channel(
            SubtitleCueLane.Audio,
            audioModel,
            windows.Count,
            glossary,
            (window, token) => ReadAudioAsync(media, input, track, window, token));

        var channels = new List<Channel> { dialogue };

        if (onVideo is not null)
        {
            channels.Add(onVideo);
        }

        // 两条通道各跑各的，但都往同一份 .srt 上写，所以写这一下要排队。
        var publishing = new Lock();

        // 一条通道在自己的任务里走完全部窗口。两条通道的差别只剩两处：取媒体，
        // 以及沿用的译名从哪儿来 —— 后者按窗给。
        async Task RunLaneAsync(Channel channel, Func<IReadOnlyList<GlossaryTerm>?> terms)
        {
            foreach (var window in windows)
            {
                var changed = await AdvanceAsync(channel, window, terms(), cancellationToken)
                    .ConfigureAwait(false);

                if (changed)
                {
                    // 这一段有东西，立刻重写一次磁盘上的字幕。
                    lock (publishing)
                    {
                        subtitle.Publish(Srt.Compose(Combined(channels)));
                    }
                }
            }
        }

        var lanes = new List<Task>
        {
            // 对白读的就是那本活的表：它就是写表的那一条。
            RunLaneAsync(dialogue, () => glossary?.Terms),
        };

        if (onVideo is not null)
        {
            // 表还在长，所以每窗都要一份新的拷贝。
            lanes.Add(RunLaneAsync(onVideo, () => glossary?.Snapshot()));
        }

        await Task.WhenAll(lanes).ConfigureAwait(false);

        foreach (var channel in channels)
        {
            DescribeChannel(channel);
        }

        return Srt.Compose(Combined(channels));
    }

    /// <summary>
    /// 一条通道走一窗，把这一窗的条目并进它自己那一份里；返回这一份有没有变。
    /// <paramref name="terms"/> 是要模型沿用的已定译名，<see cref="Channel.Memory"/> 是这一段新定下的
    /// 名字并进哪里；两者为 <c>null</c> 都表示这一条不谈术语。
    /// </summary>
    private async Task<bool> AdvanceAsync(
        Channel channel,
        MediaWindow window,
        IReadOnlyList<GlossaryTerm>? terms,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Log.Info($"    {channel.Label} {window.Index}/{channel.Total} · {window.Describe()}");

        WindowOutcome outcome;

        try
        {
            var media = await channel.Read(window, cancellationToken).ConfigureAwait(false);
            outcome = await AskAsync(
                channel.Lane, channel.Model, window, media, terms, channel.Memory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消不是这一段的错，整次运行到此为止。
            throw;
        }
        catch (Exception ex)
        {
            // 一段坏掉不带走整部片子。代价是这一段内容缺失，所以要把缺在哪报清楚。
            channel.Failed++;
            Log.Warn($"        这一段作废，它这一段的内容会缺失：{ex.Message}");
            return false;
        }

        channel.InputTokens += outcome.InputTokens;
        channel.OutputTokens += outcome.OutputTokens;

        if (channel.Memory is not null)
        {
            var added = channel.Memory.Merge(outcome.Terms);
            if (added > 0)
            {
                Log.Info($"        新术语 {added} 条，术语表共 {channel.Memory.Count} 条");
            }
        }

        if (outcome.Cues.Count == 0)
        {
            channel.Blank++;
            return false;
        }

        channel.Candidates.AddRange(outcome.Cues.Select(cue => new Candidate(cue, window)));

        // 去重是对整个候选列表算的，交出去的始终是"到目前为止完整的那一份"。
        channel.Published = DropDuplicates(channel.Candidates);
        return true;
    }

    /// <summary>
    /// 一条通道走到某一窗为止的状态。术语只留"往里收"这一头（<see cref="Memory"/>）。
    /// </summary>
    private sealed class Channel(
        SubtitleCueLane lane,
        IMultimodalModel model,
        int total,
        GlossaryFile? memory,
        Func<MediaWindow, CancellationToken, Task<WindowMedia>> read)
    {
        public SubtitleCueLane Lane => lane;

        public IMultimodalModel Model => model;

        public Func<MediaWindow, CancellationToken, Task<WindowMedia>> Read => read;

        /// <summary>这一条通道一共几段。</summary>
        public int Total => total;

        /// <summary>这一段新定下的名字并进哪里；<c>null</c> 表示不回收，也就不问它要。</summary>
        public GlossaryFile? Memory => memory;

        /// <summary>日志与小结里这条通道的名字。</summary>
        public string Label => lane == SubtitleCueLane.Audio ? "对白" : "画面";

        /// <summary>这一条通道收下的全部候选；跨窗去重是对整个列表算的。</summary>
        public List<Candidate> Candidates { get; } = [];

        /// <summary>到这一刻为止交出去的那一份。</summary>
        public List<SubtitleCue> Published { get; set; } = [];

        public int InputTokens { get; set; }

        public int OutputTokens { get; set; }

        /// <summary>一段内容都没有的窗口数。</summary>
        public int Blank { get; set; }

        /// <summary>作废的窗口数。</summary>
        public int Failed { get; set; }
    }

    /// <summary>两条通道合起来的那一份 —— 磁盘上写的、最后交出去的，都是它。</summary>
    private static List<SubtitleCue> Combined(IReadOnlyList<Channel> channels) =>
        [.. channels.SelectMany(channel => channel.Published)];

    /// <summary>一条通道跑完时的两行小结：收了几条，花了多少。</summary>
    private static void DescribeChannel(Channel channel)
    {
        Log.Info($"    {channel.Label}结果 : {channel.Published.Count} 条"
            + (channel.Candidates.Count > channel.Published.Count
                ? $"（跨窗重复丢掉 {channel.Candidates.Count - channel.Published.Count} 条）"
                : string.Empty)
            + (channel.Blank > 0 ? $" · {channel.Blank} 段没有内容" : string.Empty)
            + (channel.Failed > 0 ? $" · {channel.Failed} 段作废" : string.Empty));
        Log.Info($"    {channel.Label}用量 : 输入 {channel.InputTokens} · 输出 {channel.OutputTokens} tokens");
    }

    /// <summary>
    /// 问一个窗口，把回答里的条目收进这个窗口自己的坐标系，并吸附到量出来的人声边界上 ——
    /// 那些边界量在这一段音频上，吸附必须在段内做。平移不在这里：条目到这里还是
    /// <see cref="SubtitleWindowCue"/>。
    ///
    /// <paramref name="memory"/> 不为 <c>null</c> 才在提示词里要新术语、才解析回答里那一行。
    /// </summary>
    private async Task<WindowOutcome> AskAsync(
        SubtitleCueLane lane,
        IMultimodalModel model,
        MediaWindow window,
        WindowMedia media,
        IReadOnlyList<GlossaryTerm>? terms,
        GlossaryFile? memory,
        CancellationToken cancellationToken)
    {
        var prompt = Prompt.Build(lane, window, terms, collectTerms: memory is not null, model.PromptTemplate);
        var reply = await model
            .CompleteAsync(new ModelRequest(prompt, media.Part), cancellationToken)
            .ConfigureAwait(false);

        var cues = Srt.Parse(Srt.StripDecorations(reply.Text), lane)
            .Select(cue => media.Speech is { } speech
                ? cue with { Start = speech.SnapStart(cue.Start), End = speech.SnapEnd(cue.End) }
                : cue)
            .ToList();

        return new WindowOutcome(
            cues,
            // 不回收术语的那一遍（画面、--no-glossary）没人收，连解析都不做。
            memory is null ? [] : Srt.ParseGlossary(reply.Text),
            reply.InputTokens,
            reply.OutputTokens);
    }

    /// <summary>对白那一段：一段音频，加上量出来的人声边界（条目要往它上面吸）。</summary>
    private static async Task<WindowMedia> ReadAudioAsync(
        FfmpegMedia media,
        string input,
        MediaAudioTrack track,
        MediaWindow window,
        CancellationToken cancellationToken)
    {
        var part = await media.ReadAudioAsync(input, track, window, cancellationToken).ConfigureAwait(false);
        var speech = await media.DetectSpeechAsync(input, track, window, cancellationToken).ConfigureAwait(false);

        return new WindowMedia(part, speech);
    }

    /// <summary>画面那一段：一段按固定间隔重采样过的画面，没有人声边界要吸附。</summary>
    private static async Task<WindowMedia> ReadVideoAsync(
        FfmpegMedia media,
        string input,
        MediaWindow window,
        CancellationToken cancellationToken)
    {
        var part = await media.ReadVideoAsync(input, window, cancellationToken).ConfigureAwait(false);
        return new WindowMedia(part, null);
    }

    /// <summary>
    /// 丢掉两个窗口把同一段内容各译了一遍时多出来的那一份，留离自己窗口中心更近的那一份。
    /// 配对只按时间、且只在同一条通道之内做。
    /// </summary>
    private static List<SubtitleCue> DropDuplicates(List<Candidate> candidates)
    {
        var ordered = candidates.OrderBy(candidate => candidate.AbsoluteStart).ToList();
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
}
