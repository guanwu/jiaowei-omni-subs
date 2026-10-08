using System.Text;

namespace OmniSubs;

/// <summary>
/// 程序与前端（mpv 插件）之间的那一份接口：一行一条 <c>key\tvalue</c> 的进度文件。
///
/// 开跑时先写成一份空文件（清掉上一次跑剩下的内容），收尾时写下结论：<c>exit</c>（0 成 / 1 没成），
/// 没成再写一行 <c>error</c>。每次都整份重写，于是不会无界增长，读者也永远读不到半条记录。
/// 插件从不解析控制台文字 —— 那是随时可改的散文；它只从这份文件里读成没成、原因是什么。
/// </summary>
internal sealed class ProgressFile
{
    private string? _path;

    /// <summary>
    /// 打开进度文件；<paramref name="path"/> 为 <c>null</c> 就是纯命令行用法，什么都不写。
    /// 路径不可用算用法错误：返回 <c>false</c> 并把原因交给调用方去报。
    /// </summary>
    public bool TryOpen(string? path, out string? error)
    {
        error = null;

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

            _path = full;
            Write(string.Empty);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            error = $"无法写入进度文件 {path}：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 收尾：写下这次运行成没成 —— <paramref name="failure"/> 为 <c>null</c> 就是成了，
    /// 否则没成，并把这一句当失败的原因。写完这份文件不再变。
    /// </summary>
    public void Finish(string? failure)
    {
        var records = new List<string> { Record("exit", failure is null ? "0" : "1") };

        if (failure is { Length: > 0 })
        {
            records.Add(Record("error", failure));
        }

        Write(string.Join('\n', records) + "\n");
    }

    /// <summary>整份重写。没给路径就什么都不写。</summary>
    private void Write(string content)
    {
        if (_path is not null)
        {
            File.WriteAllText(_path, content, new UTF8Encoding(false));
        }
    }

    /// <summary>读者按行读，所以每一行都得是完整的记录：值里的制表符与换行先换成空格。</summary>
    private static string Record(string key, string value) =>
        key + "\t" + value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
