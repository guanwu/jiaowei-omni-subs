namespace OmniSubs.Media.Ffmpeg;

/// <summary>
/// 找 ffmpeg 与 ffprobe。查找范围固定为三条，按顺序：PATH、可执行文件所在目录、其下的 <c>tools/</c>。
/// 两个工具各找各的；找 ffprobe 时先看 ffmpeg 旁边。
/// </summary>
internal static class FfmpegTools
{
    private static readonly string FfmpegName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    private static readonly string FfprobeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    private const string ToolsDirectoryName = "tools";

    /// <summary>找 ffmpeg；找不到时 <c>error</c> 里写清该把它放到哪儿。</summary>
    public static bool TryLocateFfmpeg(out string path, out string? error)
    {
        if (Search(FfmpegName) is { } ffmpeg)
        {
            path = ffmpeg;
            error = null;
            return true;
        }

        path = string.Empty;
        error = $"没有找到 {FfmpegName}：把它放进 PATH，或放到本程序旁边、或旁边的 {ToolsDirectoryName}/ 里。";
        return false;
    }

    public static bool TryLocateFfprobe(out string path, out string? error)
    {
        if (TryLocateFfmpeg(out var ffmpeg, out _))
        {
            var besideFfmpeg = Path.Combine(Path.GetDirectoryName(ffmpeg)!, FfprobeName);
            if (File.Exists(besideFfmpeg))
            {
                path = besideFfmpeg;
                error = null;
                return true;
            }
        }

        if (Search(FfprobeName) is { } ffprobe)
        {
            path = ffprobe;
            error = null;
            return true;
        }

        path = string.Empty;
        error = $"没有找到 {FfprobeName}：它和 {FfmpegName} 在同一个包里，"
                + $"应当与它放在一起，也可以单独放进 PATH、或本程序旁边的 {ToolsDirectoryName}/ 里。";
        return false;
    }

    private static string? Search(string executableName)
    {
        foreach (var directory in SearchDirectories())
        {
            try
            {
                var candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // PATH 里可能有写坏的条目，跳过它继续找下一个。
            }
        }

        return null;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return directory.Trim();
        }

        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, ToolsDirectoryName);
    }
}
