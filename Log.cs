namespace OmniSubs;

/// <summary>
/// 控制台输出，外加一份给前端看的进度文件。
///
/// 进度文件是程序与 mpv 插件之间的接口：开跑时清空，结束时写下结论（成没成、失败的原因）。
/// 每次都整份重写，于是不会无界增长，读者也永远读不到半条记录。
/// </summary>
internal static class Log
{
    private static readonly Lock Gate = new();

    private static string? _progressFile;
    private static bool? _progressSucceeded;
    private static string? _lastError;

    /// <summary>
    /// 打开进度文件；<paramref name="path"/> 为 null 就是纯命令行用法，什么都不写。
    /// 路径不可用算用法错误、返回 false。
    /// </summary>
    public static bool OpenProgressFile(string? path)
    {
        lock (Gate)
        {
            _progressFile = null;

            if (path is null)
            {
                return true;
            }

            try
            {
                var full = Path.GetFullPath(path);

                if (Path.GetDirectoryName(full) is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                }

                _progressFile = full;
                Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
            {
                Error($"无法写入进度文件 {path}：{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 收尾：写下这次运行成没成。失败的原因是这次运行最后一次报的错（见 <see cref="Error"/>）。
    /// </summary>
    public static void ProgressEnd()
    {
        lock (Gate)
        {
            _progressSucceeded = _lastError is null;
            Flush();
        }
    }

    public static void Step(string message)
    {
        lock (Gate)
        {
            Write(Console.Out, Console.IsOutputRedirected, ConsoleColor.Cyan, "==> ", message);
        }
    }

    public static void Info(string message)
    {
        lock (Gate)
        {
            Console.Out.WriteLine(message);
        }
    }

    public static void Warn(string message)
    {
        lock (Gate)
        {
            Write(Console.Out, Console.IsOutputRedirected, ConsoleColor.Yellow, "warn: ", message);
        }
    }

    /// <summary>
    /// 错误走 stderr，于是重定向 stdout 取结果时不会把错误混进结果里；前缀与正文必须写在同一个流上。
    /// 同时记下最后一条，供 <see cref="ProgressEnd"/> 当失败原因。
    /// </summary>
    public static void Error(string message)
    {
        lock (Gate)
        {
            _lastError = message;
            Write(Console.Error, Console.IsErrorRedirected, ConsoleColor.Red, "error: ", message);
        }
    }

    /// <summary>
    /// 写一行带色前缀的消息。调用方已持有 <see cref="Gate"/>，所以这里不再上锁。
    /// 不能读颜色的场合（输出被重定向、没有控制台）只写前缀。
    /// </summary>
    private static void Write(
        TextWriter writer,
        bool redirected,
        ConsoleColor color,
        string prefix,
        string message)
    {
        if (redirected)
        {
            writer.Write(prefix);
        }
        else
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            writer.Write(prefix);
            Console.ForegroundColor = previous;
        }

        writer.WriteLine(message);
    }

    /// <summary>
    /// 整份重写。读者按行读，所以每一行都得是完整的记录：值里的制表符与换行先换成空格。
    /// </summary>
    private static void Flush()
    {
        if (_progressFile is null)
        {
            return;
        }

        var records = new List<string>();

        // 终态最后写，写完这份文件不再变。
        if (_progressSucceeded is { } succeeded)
        {
            records.Add(Record("exit", succeeded ? "0" : "1"));

            if (!succeeded && _lastError is { Length: > 0 })
            {
                records.Add(Record("error", _lastError));
            }
        }

        // 还没有结论（开跑时那一次）就写成一份空文件 —— 清掉上一次跑剩下的内容。
        File.WriteAllText(
            _progressFile,
            records.Count == 0 ? string.Empty : string.Join('\n', records) + "\n");
    }

    private static string Record(string key, string value) =>
        key + "\t" + value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
