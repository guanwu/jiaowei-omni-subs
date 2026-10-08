using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniSubs.Model;

namespace OmniSubs.Media.Ffmpeg;

// ffprobe 报的形状：只在这个文件里用，不属于契约层。

/// <summary>ffprobe 报的一条流。只取用得到的字段，其余忽略。</summary>
internal sealed class ProbeStream
{
    [JsonPropertyName("codec_type")]
    public string? CodecType { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; set; }
}

internal sealed class ProbeFormat
{
    [JsonPropertyName("duration")]
    public string? Duration { get; set; }
}

internal sealed class ProbeResult
{
    [JsonPropertyName("streams")]
    public List<ProbeStream>? Streams { get; set; }

    [JsonPropertyName("format")]
    public ProbeFormat? Format { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ProbeResult))]
internal sealed partial class ProbeJsonContext : JsonSerializerContext;

/// <summary>
/// 媒体这一层唯一碰 ffmpeg / ffprobe 的地方：问一个文件是什么形状，从它里面切出音频、
/// 量出人声、取出画面。调用方只跟 <see cref="MediaInfo"/>、<see cref="ModelMediaPart"/>、
/// <see cref="MediaSpeechMap"/> 打交道。
/// </summary>
internal sealed class FfmpegMedia(string ffmpegPath, string ffprobePath)
{
    /// <summary>ffmpeg 与 ffprobe 缺一个就返回 <c>false</c>，原因写进 <paramref name="error"/>。</summary>
    public static bool TryCreate([NotNullWhen(true)] out FfmpegMedia? media, out string? error)
    {
        if (!FfmpegTools.TryLocateFfmpeg(out var ffmpeg, out error)
            || !FfmpegTools.TryLocateFfprobe(out var ffprobe, out error))
        {
            media = null;
            return false;
        }

        media = new FfmpegMedia(ffmpeg, ffprobe);
        error = null;
        return true;
    }

    /// <summary>
    /// 读时长、音轨与有没有画面。三样取自同一次调用，所以音轨序号按文件里的顺序数、从 1 起
    /// —— <c>--audio-track</c> 的序号就是这么来的。量不出时长就抛。
    /// </summary>
    public async Task<MediaInfo> ProbeAsync(string input, CancellationToken cancellationToken)
    {
        string[] arguments =
        [
            "-hide_banner",
            "-loglevel", "error",
            "-show_entries", "format=duration:stream=codec_type:stream_tags=language,title",
            "-of", "json",
            input,
        ];

        var result = await RunToolAsync(
                ffprobePath,
                $"读不了 {Path.GetFileName(input)}",
                arguments,
                cancellationToken)
            .ConfigureAwait(false);

        ProbeResult? probe;
        try
        {
            probe = JsonSerializer.Deserialize(result.StandardOutput, ProbeJsonContext.Default.ProbeResult);
        }
        catch (JsonException ex)
        {
            throw new MediaException($"看不懂 ffprobe 的输出：{ex.Message}", result.StandardOutput);
        }

        if (ParseDuration(probe?.Format?.Duration) is not { } duration)
        {
            throw new MediaException($"量不出 {Path.GetFileName(input)} 的时长。");
        }

        var streams = probe?.Streams ?? [];

        var audioTracks = streams
            .Where(stream => stream.CodecType == "audio")
            .Select((stream, index) => new MediaAudioTrack(
                index + 1,
                ReadTag(stream, "language"),
                ReadTag(stream, "title")))
            .ToList();

        return MediaInfo.From(
            input,
            duration,
            audioTracks,
            streams.Any(stream => stream.CodecType == "video"));
    }

    /// <summary>
    /// 把一条音轨上的一个窗口切成字节。编码参数见 <see cref="Defaults.AudioFormat"/> 一带的常量；
    /// 音量一律不动。
    /// </summary>
    public async Task<ModelMediaPart> ReadAudioAsync(
        string input,
        MediaAudioTrack track,
        MediaWindow window,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        Seek(arguments, window, input);
        SelectAudio(arguments, track);

        arguments.AddRange(
        [
            "-ac", Defaults.AudioChannels.ToString(CultureInfo.InvariantCulture),
            "-ar", Defaults.AudioSampleRate.ToString(CultureInfo.InvariantCulture),
            "-c:a", "libmp3lame",
            "-b:a", $"{Defaults.AudioKbps}k",
        ]);

        return await RenderAsync(
                $"切不出 {Path.GetFileName(input)} 的第 {window.Index} 个窗口",
                ModelMediaKind.Audio,
                arguments,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 在一个窗口里找出静音，把它们的反面当作人声区间报出来；时刻以窗口起点为 0。
    /// 结论只是参考：人声一直不够安静时只是少一次吸附机会，不会多出错的边界。
    /// </summary>
    public async Task<MediaSpeechMap> DetectSpeechAsync(
        string input,
        MediaAudioTrack track,
        MediaWindow window,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostats", "-loglevel", "info" };
        Seek(arguments, window, input);
        SelectAudio(arguments, track);

        arguments.AddRange(
        [
            "-af", $"silencedetect=noise={Defaults.SilenceThresholdDb}dB"
                    + $":d={Defaults.MinimumSilenceSeconds.ToString(CultureInfo.InvariantCulture)}",
            "-f", "null",
            "-",
        ]);

        var result = await RunToolAsync(
                ffmpegPath,
                $"分析不了 {Path.GetFileName(input)} 的第 {window.Index} 个窗口",
                arguments,
                cancellationToken)
            .ConfigureAwait(false);

        return new MediaSpeechMap(Invert(ParseSilences(result.StandardError), window.Duration));
    }

    /// <summary>
    /// 把这一段画面按固定间隔重采样成交给模型的字节。
    /// 只缩不放：源比约定高就缩到约定高度，比约定矮就原样交出去。
    /// </summary>
    public async Task<ModelMediaPart> ReadVideoAsync(
        string input,
        MediaWindow window,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        Seek(arguments, window, input);

        // 只送画面；声音由音频那一半单独送。
        arguments.AddRange(["-an", "-map", "0:v:0"]);

        // fps 的分子分母按毫秒写，间隔小于 1 秒时也说得出来（250 毫秒 → fps=1000/250）。
        // scale 的逗号要括住整个式子，否则会被当成两个滤镜的连接符。
        arguments.AddRange(
        [
            "-vf", $"fps=1000/{Defaults.FrameInterval.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}"
                    + $",scale=-2:'min({Defaults.FrameHeight},ih)'",
            "-c:v", "libx265",
            "-preset", "medium",
            "-crf", Defaults.FrameQuality.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-tag:v", "hvc1",
        ]);

        return await RenderAsync(
                $"取不出 {Path.GetFileName(input)} 的第 {window.Index} 段画面",
                ModelMediaKind.Video,
                arguments,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 跑一次 ffmpeg / ffprobe 并把失败变成异常：退出码非 0 时把 <paramref name="action"/>
    /// （"切不出 x.mkv 的第 2 个窗口"这样的人话）和它自己报的原因一起抛出去；成功则把输出交回调用方。
    /// </summary>
    private static async Task<FfmpegProcessResult> RunToolAsync(
        string executable,
        string action,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await FfmpegProcess
            .RunAsync(executable, arguments, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new MediaException(
                $"{Path.GetFileNameWithoutExtension(executable)} {action}（退出码 {result.ExitCode}）。",
                result.StandardError.Trim());
        }

        return result;
    }

    /// <summary>
    /// 渲染一段媒体：<paramref name="arguments"/> 是给 ffmpeg 的参数，输出文件由这里补在最后 ——
    /// 于是临时文件一定落在临时目录、无论成败都被删掉。交给模型的那段字节用这个模态自己的
    /// 容器格式（<see cref="Defaults.AudioFormat"/> / <see cref="Defaults.FrameFormat"/>）。
    /// </summary>
    private async Task<ModelMediaPart> RenderAsync(
        string action,
        ModelMediaKind kind,
        List<string> arguments,
        CancellationToken cancellationToken)
    {
        var format = kind == ModelMediaKind.Audio ? Defaults.AudioFormat : Defaults.FrameFormat;
        var output = NewTempPath(format);
        arguments.Add(output);

        try
        {
            await RunToolAsync(ffmpegPath, action, arguments, cancellationToken).ConfigureAwait(false);

            var bytes = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
            return new ModelMediaPart(kind, bytes, format);
        }
        finally
        {
            TryDelete(output);
        }
    }

    /// <summary>
    /// 定位到一个窗口的起点：<c>-ss</c> 放在 <c>-i</c> 之前是跳着读，输出这一段的时刻
    /// 因此相对窗口起点，而不是相对整部片子。
    /// </summary>
    private static void Seek(List<string> arguments, MediaWindow window, string input) =>
        arguments.AddRange(
        [
            "-ss", FfmpegTimes.Seconds(window.Start),
            "-t", FfmpegTimes.Seconds(window.Duration),
            "-i", input,
        ]);

    /// <summary>只要这一条音轨，不取画面。</summary>
    private static void SelectAudio(List<string> arguments, MediaAudioTrack track) =>
        arguments.AddRange(["-vn", "-map", Specifier(track)]);

    /// <summary>把给人看的序号换算成 ffmpeg 的说法：它从 0 数起。</summary>
    private static string Specifier(MediaAudioTrack track) => $"0:a:{track.Number - 1}";

    /// <summary>
    /// 从 silencedetect 的日志里读出 <c>silence_start</c> / <c>silence_end</c> 配对。
    /// 窗口结束时还没结束的静音只报 start，留下一个开着的 start，由 <see cref="Invert"/> 收口。
    /// </summary>
    private static List<(TimeSpan Start, TimeSpan End)> ParseSilences(string log)
    {
        var silences = new List<(TimeSpan Start, TimeSpan End)>();
        TimeSpan? open = null;

        foreach (var line in log.Split('\n'))
        {
            if (FfmpegTimes.ReadSeconds(line, "silence_start:") is { } start)
            {
                open = start;
                continue;
            }

            if (FfmpegTimes.ReadSeconds(line, "silence_end:") is { } end && open is not null)
            {
                silences.Add((open.Value, end));
                open = null;
            }
        }

        return silences;
    }

    /// <summary>
    /// 把静音区间取反成人声区间：<see cref="MediaSpeechMap"/> 只认人声区间，取反只在这里做。
    /// </summary>
    private static List<(TimeSpan Start, TimeSpan End)> Invert(
        IReadOnlyList<(TimeSpan Start, TimeSpan End)> silences,
        TimeSpan duration)
    {
        var speech = new List<(TimeSpan Start, TimeSpan End)>();
        var cursor = TimeSpan.Zero;

        foreach (var silence in silences)
        {
            if (silence.Start > cursor)
            {
                speech.Add((cursor, silence.Start));
            }

            cursor = silence.End > cursor ? silence.End : cursor;
        }

        if (cursor < duration)
        {
            speech.Add((cursor, duration));
        }

        return speech;
    }

    private static string? ReadTag(ProbeStream stream, string name) =>
        stream.Tags?.GetValueOrDefault(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static TimeSpan? ParseDuration(string? raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    /// <summary>切出的音频与编出的画面先落在这一层的临时文件上，再读成字节。</summary>
    private static string NewTempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"omnisubs-{Guid.NewGuid():N}.{extension}");

    /// <summary>删不掉就留着：临时目录自己会清，这里的失败也不该盖掉上一次真正的错误。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
