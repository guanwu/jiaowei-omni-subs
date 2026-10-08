namespace OmniSubs.Cli;

/// <summary>
/// 命令行给的输入展开成待处理的视频清单：文件原样收下，目录递归展开，按扩展名认视频、去掉重复的。
/// 目录递归是固定行为，没有开关。
/// </summary>
internal static class Targets
{
    /// <summary>
    /// 展开输入；返回 <c>false</c> 表示有输入用不了，原因是 <paramref name="error"/>，
    /// 此时清单是空的。空清单本身是合法结果 —— 目录里一个视频都没有。
    /// </summary>
    public static bool TryResolve(IReadOnlyList<string> inputs, out List<string> targets, out string? error)
    {
        error = null;
        targets = [];

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            if (File.Exists(input))
            {
                var full = Path.GetFullPath(input);
                if (!IsVideo(full))
                {
                    error = $"不支持的文件类型：{input}";
                    return false;
                }

                if (seen.Add(full))
                {
                    found.Add(full);
                }

                continue;
            }

            if (Directory.Exists(input))
            {
                foreach (var file in Directory
                             .EnumerateFiles(input, "*", SearchOption.AllDirectories)
                             .Where(IsVideo)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var full = Path.GetFullPath(file);
                    if (seen.Add(full))
                    {
                        found.Add(full);
                    }
                }

                continue;
            }

            error = $"输入路径不存在：{input}";
            return false;
        }

        targets = found;
        return true;
    }

    private static bool IsVideo(string path) =>
        Defaults.VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
