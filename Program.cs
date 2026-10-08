using OmniSubs.Cli;
using OmniSubs.Config;
using OmniSubs.Glossary;
using OmniSubs.Media;
using OmniSubs.Media.Ffmpeg;
using OmniSubs.Model.OpenAi;
using OmniSubs.Recognition;
using OmniSubs.Subtitles;

namespace OmniSubs;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitPartialFailure = 1;
    private const int ExitUsageError = 2;
    private const int ExitMissingComponent = 3;
    private const int ExitMissingCredential = 4;
    private const int ExitConfigError = 5;
    private const int ExitCancelled = 130;

    private static async Task<int> Main(string[] args)
    {
        Log.UseUtf8Console();

        if (!CliOptions.TryParse(args, out var options, out var parseError))
        {
            Log.Error(parseError!);
            Log.Info(string.Empty);
            Log.Info(CliOptions.Usage);
            return ExitUsageError;
        }

        if (options.ShowHelp)
        {
            Log.Info(CliOptions.Usage);
            return ExitSuccess;
        }

        // 进度文件在读配置之前开：配置、凭据失败的那几次运行也要留下终态记录。
        var progress = new ProgressFile();
        if (!progress.TryOpen(options.ProgressFile, out var progressError))
        {
            Log.Error(progressError!);
            return ExitUsageError;
        }

        try
        {
            return await RunAsync(options).ConfigureAwait(false);
        }
        finally
        {
            // 终态只写这一处，任何返回点都经过它；没过成时，原因就是最后报出去的那一条错误。
            progress.Finish(Log.LastError);
        }
    }

    private static async Task<int> RunAsync(CliOptions options)
    {
        // 配置问题单独给退出码，不打用法。
        if (!ConfigFile.TryLoad(out var config, out var configError))
        {
            Log.Error(configError!);
            return ExitConfigError;
        }

        // 档位与凭据在碰媒体之前查完。
        var binding = OpenAiBinding.Resolve(config, options, out var bindingFailure, out var bindingError);
        if (binding is null)
        {
            Log.Error(bindingError!);
            return bindingFailure == OpenAiBindingFailure.MissingCredential
                ? ExitMissingCredential
                : ExitConfigError;
        }

        var targets = ResolveTargets(options, out var listingError);
        if (listingError is not null)
        {
            Log.Error(listingError);
            return ExitUsageError;
        }

        if (targets.Count == 0)
        {
            Log.Warn("没有找到可处理的视频。");
            return ExitSuccess;
        }

        // 组件在碰任何媒体之前先找齐；ffmpeg 与 ffprobe 都是必需的。
        if (!FfmpegMedia.TryCreate(out var media, out var componentError))
        {
            Log.Error(componentError!);
            return ExitMissingComponent;
        }

        Log.Step("本次执行");
        DescribeBinding(binding, options);

        var glossaries = new Dictionary<string, GlossaryFile>(StringComparer.OrdinalIgnoreCase);
        if (!LoadGlossaries(options, targets, glossaries, out var glossaryError))
        {
            Log.Error(glossaryError!);
            return ExitConfigError;
        }

        Log.Info($"    待处理   : {targets.Count} 个视频");
        Log.Info(string.Empty);

        var subtitler = new Subtitler(
            media,
            new OpenAiModel(binding.Audio),
            binding.Video is { } videoProfile ? new OpenAiModel(videoProfile) : null,
            options.AudioTrack);

        // 取消由我们自己收尾，让各层 finally 有机会清理临时文件。
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var produced = 0;

        foreach (var target in targets)
        {
            var name = Path.GetFileName(target);
            Log.Step(name);

            var glossary = glossaries.GetValueOrDefault(Path.GetDirectoryName(target)!);
            var subtitle = SubtitleFile.ForVideo(target);

            try
            {
                var cues = await subtitler
                    .RunAsync(target, subtitle, glossary, cancellation.Token)
                    .ConfigureAwait(false);

                if (cues.Count == 0)
                {
                    Log.Error($"{name} 没有识别出任何内容，未写出字幕。");
                    continue;
                }

                // 字幕已随识别逐窗口写出（见 SubtitleFile），这里只看落盘有没有失败。
                if (subtitle.LastError is { } writeError)
                {
                    Log.Error($"{name} 的字幕没能落盘：{writeError}");
                    continue;
                }

                ReportGlossary(glossary);

                Log.Info($"    写出     : {subtitle.FilePath}（{cues.Count} 条）");
                produced++;
            }
            catch (OperationCanceledException)
            {
                ReportGlossary(glossary);
                Log.Warn($"{name} 已取消。");
                return ExitCancelled;
            }
            catch (Exception ex)
            {
                ReportGlossary(glossary);
                Log.Error($"{name} 处理失败：{ex.Message}");

                // 后端给出的原因原文单独再报一行。
                if (ex is MediaException { Detail: { } detail })
                {
                    Log.Error(detail);
                }
            }
        }

        Log.Info(string.Empty);

        if (produced < targets.Count)
        {
            Log.Warn($"{targets.Count - produced} / {targets.Count} 个视频未产出字幕。");
            return ExitPartialFailure;
        }

        Log.Info($"全部完成：{produced} 个视频都写出了字幕。");
        return ExitSuccess;
    }

    /// <summary>
    /// 报告术语记忆有没有落盘失败。术语由 <see cref="GlossaryFile.Merge"/> 随识别过程按段写出，
    /// 这里只看结果；没落盘不影响这个视频的字幕。
    /// </summary>
    private static void ReportGlossary(GlossaryFile? glossary)
    {
        if (glossary?.LastError is { } error)
        {
            Log.Warn($"    术语记忆 : 没能落盘（{error}）—— 这一次定下的译名不会留给同目录的下一集");
        }
    }

    private static void DescribeBinding(OpenAiBinding binding, CliOptions options)
    {
        var track = string.IsNullOrWhiteSpace(options.AudioTrack) ? "第一条" : options.AudioTrack;

        Log.Info($"    音频字幕 : 档位 {binding.Audio.Id} · {binding.Audio.Model} · 音轨 {track}");

        Log.Info(binding.Video is { } video
            ? $"    视频字幕 : 已启用 · 档位 {video.Id} · {video.Model}"
            : "    视频字幕 : 未启用（加 --model-video <档位id> 开启）");
    }

    /// <summary>
    /// 每个输入目录各读一份术语记忆并逐个报出；任何一份读不动都返回 <c>false</c> 并给出 <paramref name="error"/>。
    /// </summary>
    private static bool LoadGlossaries(
        CliOptions options,
        List<string> targets,
        Dictionary<string, GlossaryFile> glossaries,
        out string? error)
    {
        error = null;

        if (options.NoGlossary)
        {
            Log.Info("    术语记忆 : 已关闭，本次不读不写");
            return true;
        }

        foreach (var directory in targets
                     .Select(Path.GetDirectoryName)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var glossary = GlossaryFile.ForDirectory(directory!);
            if (!glossary.TryLoad(out error))
            {
                return false;
            }

            glossaries[directory!] = glossary;

            Log.Info($"    术语记忆 : {glossary.FilePath}"
                + $"（{(glossary.Exists ? $"已有 {glossary.Count} 条" : "新建")}）· 只由对白写入");
        }

        return true;
    }

    private static List<string> ResolveTargets(CliOptions options, out string? error)
    {
        error = null;
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in options.Inputs)
        {
            if (File.Exists(input))
            {
                var full = Path.GetFullPath(input);
                if (!IsVideoFile(full))
                {
                    error = $"不支持的文件类型：{input}";
                    return [];
                }

                if (seen.Add(full))
                {
                    results.Add(full);
                }

                continue;
            }

            if (Directory.Exists(input))
            {
                // 目录递归是固定行为，没有开关。
                foreach (var file in Directory
                             .EnumerateFiles(input, "*", SearchOption.AllDirectories)
                             .Where(IsVideoFile)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var full = Path.GetFullPath(file);
                    if (seen.Add(full))
                    {
                        results.Add(full);
                    }
                }

                continue;
            }

            error = $"输入路径不存在：{input}";
            return [];
        }

        return results;
    }

    private static bool IsVideoFile(string path) =>
        Defaults.VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
