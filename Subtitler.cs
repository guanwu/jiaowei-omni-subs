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

        if (!info.TrySelect(audioTrack, out var track, out var trackError))
        {
            throw new MediaException(trackError!);
        }

        var windows = MediaWindows.Plan(info.Duration);
        Log.Info($"    窗口     : {windows.Count} 段 · 每段 {Defaults.WindowLength.TotalSeconds:F0} 秒"
            + $" · 重叠 {Defaults.WindowOverlap.TotalSeconds:F0} 秒 · {track.Describe()}");

        // 画面那一条对术语只读不写：沿用对白到这一刻为止定下的译名，但不把新名字写回表 ——
        // 术语表只由对白通道增长（见 TermExchange）。
        Channel? onVideo = null;

        if (videoModel is { } pictureModel)
        {
            if (info.HasVideo)
            {
                onVideo = new Channel(
                    SubtitleCueLane.Video,
                    pictureModel,
                    windows.Count,
                    TermExchange.For(SubtitleCueLane.Video, glossary),
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
            TermExchange.For(SubtitleCueLane.Audio, glossary),
            (window, token) => ReadAudioAsync(media, input, track, window, token));

        var channels = new List<Channel> { dialogue };

        if (onVideo is not null)
        {
            channels.Add(onVideo);
        }

        // 一条通道在自己的任务里走完全部窗口。两条通道的差别都装在 Channel 里：取什么媒体、
        // 跟术语表怎么打交道。
        async Task RunLaneAsync(Channel channel)
        {
            foreach (var window in windows)
            {
                var changed = await AdvanceAsync(channel, window, cancellationToken)
                    .ConfigureAwait(false);

                if (changed)
                {
                    // 这一段有东西，立刻重写一次磁盘上的字幕（两条通道的排队由 SubtitleFile 自己管）。
                    subtitle.Publish(Srt.Compose(Combined(channels)));
                }
            }
        }

        var lanes = new List<Task> { RunLaneAsync(dialogue) };

        if (onVideo is not null)
        {
            lanes.Add(RunLaneAsync(onVideo));
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
    /// </summary>
    private async Task<bool> AdvanceAsync(
        Channel channel,
        MediaWindow window,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Log.Info($"    {channel.Label} {window.Index}/{channel.Total} · {window.Describe()}");

        if (await AskWindowAsync(channel, window, cancellationToken).ConfigureAwait(false) is not { } outcome)
        {
            return false;
        }

        channel.InputTokens += outcome.InputTokens;
        channel.OutputTokens += outcome.OutputTokens;

        if (channel.Terms.Memory is { } memory)
        {
            var added = memory.Merge(outcome.Terms);
            if (added > 0)
            {
                Log.Info($"        新术语 {added} 条，术语表共 {memory.Count} 条");
            }
        }

        if (outcome.Cues.Count == 0)
        {
            channel.Blank++;
            return false;
        }

        channel.Cues.Add(window, outcome.Cues);
        return true;
    }

    /// <summary>
    /// 取这一窗的媒体、问模型、把回答收进这个窗口自己的坐标系。
    ///
    /// 提示词在取媒体之前就拼好：要沿用的译名以"进入这一窗时"为准，取媒体花掉的那点时间不算进去
    /// （对白那一条在同一段时间里正往表里写新名字，晚一步拿到的就是另一份译名）。
    ///
    /// 一段坏掉不带走整部片子：代价是这一段内容缺失，所以要把缺在哪报清楚，作废的段数记在通道上，
    /// 返回 <c>null</c> 表示这一窗什么都没拿到。取消不是这一段的错，整次运行到此为止。
    /// </summary>
    private async Task<WindowOutcome?> AskWindowAsync(
        Channel channel,
        MediaWindow window,
        CancellationToken cancellationToken)
    {
        try
        {
            var prompt = Prompt.Build(channel.Lane, window, channel.Terms, channel.Model.PromptTemplate);
            var media = await channel.Read(window, cancellationToken).ConfigureAwait(false);
            return await AskAsync(channel, prompt, media, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            channel.Failed++;
            Log.Warn($"        这一段作废，它这一段的内容会缺失：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 一条通道走到某一窗为止的状态。
    /// </summary>
    private sealed class Channel(
        SubtitleCueLane lane,
        IMultimodalModel model,
        int total,
        TermExchange terms,
        Func<MediaWindow, CancellationToken, Task<WindowMedia>> read)
    {
        public SubtitleCueLane Lane => lane;

        public IMultimodalModel Model => model;

        public Func<MediaWindow, CancellationToken, Task<WindowMedia>> Read => read;

        /// <summary>这一条通道一共几段。</summary>
        public int Total => total;

        /// <summary>这一条通道跟术语表怎么打交道：沿用的译名从哪儿读、回不回收、并进哪一本表。</summary>
        public TermExchange Terms => terms;

        /// <summary>日志与小结里这条通道的名字。</summary>
        public string Label => lane == SubtitleCueLane.Audio ? "对白" : "画面";

        /// <summary>这一条通道收下的条目；跨窗去重与换算整片时刻都归它管。</summary>
        public LaneCues Cues { get; } = new();

        public int InputTokens { get; set; }

        public int OutputTokens { get; set; }

        /// <summary>一段内容都没有的窗口数。</summary>
        public int Blank { get; set; }

        /// <summary>作废的窗口数。</summary>
        public int Failed { get; set; }
    }

    /// <summary>两条通道合起来的那一份 —— 磁盘上写的、最后交出去的，都是它。</summary>
    private static List<SubtitleCue> Combined(IReadOnlyList<Channel> channels) =>
        [.. channels.SelectMany(channel => channel.Cues.Placed)];

    /// <summary>一条通道跑完时的两行小结：收了几条，花了多少。</summary>
    private static void DescribeChannel(Channel channel)
    {
        var placed = channel.Cues.Placed;
        var dropped = channel.Cues.Received - placed.Count;

        Log.Info($"    {channel.Label}结果 : {placed.Count} 条"
            + (dropped > 0 ? $"（跨窗重复丢掉 {dropped} 条）" : string.Empty)
            + (channel.Blank > 0 ? $" · {channel.Blank} 段没有内容" : string.Empty)
            + (channel.Failed > 0 ? $" · {channel.Failed} 段作废" : string.Empty));
        Log.Info($"    {channel.Label}用量 : 输入 {channel.InputTokens} · 输出 {channel.OutputTokens} tokens");
    }

    /// <summary>
    /// 问这一条通道的模型，把回答里的条目收进这个窗口自己的坐标系，并吸附到量出来的人声边界上 ——
    /// 那些边界量在这一段媒体上，吸附必须在段内做。平移不在这里：条目到这里还是
    /// <see cref="SubtitleWindowCue"/>。
    ///
    /// 提示词由调用方给定（见 <see cref="AskWindowAsync"/>）：它得在取媒体之前拼好。
    /// 这一条通道要回收术语，提示词里才会要新术语、这里才解析回答里那一行（见 <see cref="TermExchange"/>）。
    /// </summary>
    private async Task<WindowOutcome> AskAsync(
        Channel channel,
        string prompt,
        WindowMedia media,
        CancellationToken cancellationToken)
    {
        var reply = await channel.Model
            .CompleteAsync(new ModelRequest(prompt, media.Part), cancellationToken)
            .ConfigureAwait(false);

        var cues = Srt.Parse(Srt.StripDecorations(reply.Text), channel.Lane)
            .Select(cue => media.Speech is { } speech
                ? cue with { Start = speech.SnapStart(cue.Start), End = speech.SnapEnd(cue.End) }
                : cue)
            .ToList();

        return new WindowOutcome(
            cues,
            // 不回收术语的那一遍（画面、--no-glossary）没人收，连解析都不做。
            channel.Terms.Collects ? Srt.ParseGlossary(reply.Text) : [],
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
}
