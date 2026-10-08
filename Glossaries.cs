namespace OmniSubs.Glossary;

/// <summary>
/// 本次运行要用到的全部术语记忆：每个输入目录一份，同目录下的视频共享同一份，于是译名跨集稳定。
///
/// 「目录 → 那一份」这个换算只在这里做，查的时候给视频路径就够了。加载时顺带把它读了哪几份
/// 报给用户（新建，还是已有几条），因为只有它知道。
/// </summary>
internal sealed class Glossaries
{
    private readonly Dictionary<string, GlossaryFile> _byDirectory = new(StringComparer.OrdinalIgnoreCase);

    private Glossaries()
    {
    }

    /// <summary>
    /// 给这些视频各读一份术语记忆。任何一份读不动都返回 <c>null</c>，原因是 <paramref name="error"/>；
    /// <paramref name="off"/>（<c>--no-glossary</c>）为真就一份都不读。
    /// </summary>
    public static Glossaries? Load(IReadOnlyList<string> targets, bool off, out string? error)
    {
        error = null;
        var glossaries = new Glossaries();

        if (off)
        {
            Log.Info("    术语记忆 : 已关闭，本次不读不写");
            return glossaries;
        }

        foreach (var directory in targets
                     .Select(Path.GetDirectoryName)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var glossary = GlossaryFile.ForDirectory(directory!);
            if (!glossary.TryLoad(out error))
            {
                return null;
            }

            glossaries._byDirectory[directory!] = glossary;

            Log.Info($"    术语记忆 : {glossary.FilePath}"
                + $"（{(glossary.Exists ? $"已有 {glossary.Count} 条" : "新建")}）· 只由对白写入");
        }

        return glossaries;
    }

    /// <summary>某个视频要用的那一份；术语记忆关掉了就是这个目录没读到，两者都是 <c>null</c>。</summary>
    public GlossaryFile? For(string videoPath) =>
        _byDirectory.GetValueOrDefault(Path.GetDirectoryName(videoPath)!);
}
