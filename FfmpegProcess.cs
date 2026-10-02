using System.Diagnostics;
using System.Text;

namespace OmniSubs.Media.Ffmpeg;

internal sealed record FfmpegProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// 跑一个子进程，收回它的输出。参数走参数表而不是拼一条命令行，所以不涉及引号转义。
/// </summary>
internal static class FfmpegProcess
{
    /// <summary>stdout / stderr 各自保留的字符上限，只留开头一段。</summary>
    private const int OutputLimitChars = 32 * 1024;

    public static async Task<FfmpegProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

        try
        {
            if (!process.Start())
            {
                throw new MediaException($"无法启动 {executable}。");
            }
        }
        catch (MediaException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MediaException($"无法启动 {executable}：{ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消时把子进程一起带走，否则它会在后台继续编码。
            TryKill(process);
            throw;
        }

        // 等读取线程把尾巴上的行交出来，再取字符串。
        process.WaitForExit();

        return new FfmpegProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void Append(StringBuilder sink, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sink)
        {
            if (sink.Length < OutputLimitChars)
            {
                sink.AppendLine(line);
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 已经退出了。
        }
    }
}
