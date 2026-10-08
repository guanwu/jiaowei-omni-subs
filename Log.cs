using System.Text;

namespace OmniSubs;

/// <summary>
/// 控制台输出。
///
/// 约定：日志走 stdout、错误走 stderr（重定向 stdout 取结果时不会把错误混进结果里），
/// 带前缀的那几种在能读颜色的场合上色。输出都过同一把锁，于是并发写出的两行不会互相插进对方中间。
/// </summary>
internal static class Log
{
    private static readonly Lock Gate = new();

    private static string? _lastError;

    /// <summary>
    /// 最后报出去的那一条错误；没报过错就是 <c>null</c>。收尾时给进度文件当失败原因，
    /// 于是插件看到的那一句与用户看到的是同一句。
    /// </summary>
    public static string? LastError => _lastError;

    /// <summary>
    /// 控制台按 UTF-8 输出，中文日志与字幕才不会变成乱码。控制台被重定向或受限时用默认编码 ——
    /// 这件事拦不住整次运行，所以失败只当没做到。
    /// </summary>
    public static void UseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // 控制台被重定向或受限，用默认编码即可。
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
    /// 同时记下最后一条，见 <see cref="LastError"/>。
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
}
