using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniSubs.Subtitles;

namespace OmniSubs.Glossary;

/// <summary>术语累计表里的一条：原文 → 译名。</summary>
internal sealed class GlossaryTerm
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;
}

/// <summary>记忆文件的形状；<c>terms</c> 用数组以保住先后顺序。</summary>
internal sealed class GlossaryDocument
{
    [JsonPropertyName("terms")]
    public List<GlossaryTerm> Terms { get; set; } = [];
}

[JsonSourceGenerationOptions(
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    WriteIndented = true)]
[JsonSerializable(typeof(GlossaryDocument))]
internal sealed partial class GlossaryJsonContext : JsonSerializerContext;

/// <summary>
/// 写盘专用的上下文：不转义非 ASCII，换行固定为 <see cref="Defaults.NewLine"/>。
/// 读仍用 <see cref="GlossaryJsonContext.Default"/>。
/// </summary>
internal static class GlossaryJson
{
    public static readonly GlossaryJsonContext ForWriting =
        new(new JsonSerializerOptions(GlossaryJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NewLine = Defaults.NewLine,
        });
}

/// <summary>
/// 术语记忆文件：落在视频所在目录、文件名固定（见 <see cref="FileName"/>），同目录下的其他视频自动读到同一份。
///
/// 合并规则是首译优先：后到的不同译法不推翻已经定下的那个。每定下新译名就落盘一次
/// （同一个窗口里的几条并作一次写，见 <see cref="Merge"/>）。
///
/// 已知限制：同一目录被两个进程同时处理时，后落盘的覆盖先落盘的。
/// </summary>
internal sealed class GlossaryFile
{
    /// <summary>固定文件名。</summary>
    public const string FileName = "omnisubs.glossary.json";

    private readonly List<GlossaryTerm> _terms = [];
    private readonly HashSet<string> _sources = new(StringComparer.Ordinal);

    /// <summary>
    /// 写和读各自进来时都要过这道门，落盘也在门里 —— 于是「表长了」与「盘上的是新的」不会被分开看到。
    /// </summary>
    private readonly Lock _gate = new();

    private GlossaryFile(string path) => FilePath = path;

    public string FilePath { get; }

    public int Count => _terms.Count;

    /// <summary>当前的全部术语，最先进来的排在最前。</summary>
    public IReadOnlyList<GlossaryTerm> Terms => _terms;

    /// <summary>本次打开时文件是否已经存在，仅用于把「新建」和「已有 0 条」区分开。</summary>
    public bool Exists { get; private set; }

    /// <summary>
    /// 最后一次落盘失败的原因；写成过、或者还没有新译名可写，都是 <c>null</c>。
    /// 写不动只记下来（见 <see cref="Save"/>），跑完之后由调用方报出去。
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>某个目录所用的那一份：目录就是它出现的位置，文件名固定。</summary>
    public static GlossaryFile ForDirectory(string directory) =>
        new(Path.Combine(Path.GetFullPath(directory), FileName));

    /// <summary>
    /// 读入已有的术语。文件不存在是正常的（第一次处理这个目录），读不动或读不懂是错误。
    /// </summary>
    public bool TryLoad(out string? error)
    {
        error = null;
        Exists = File.Exists(FilePath);

        if (!Exists)
        {
            return true;
        }

        string text;
        try
        {
            text = File.ReadAllText(FilePath);
        }
        catch (Exception ex)
        {
            error = $"无法读取术语记忆文件 {FilePath}：{ex.Message}";
            return false;
        }

        GlossaryDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(text, GlossaryJsonContext.Default.GlossaryDocument);
        }
        catch (JsonException ex)
        {
            error = $"术语记忆文件 {FilePath} 不是有效的 JSON：{ex.Message}";
            return false;
        }

        foreach (var term in document?.Terms ?? [])
        {
            Add(term);
        }

        // 读进来只填表，不落盘。
        return true;
    }

    /// <summary>
    /// 合并模型报来的术语，返回真正新增的条数；有新增就当场落盘。
    ///
    /// 写盘在锁里做（<see cref="Save"/> 自己不上锁）。
    /// </summary>
    public int Merge(IEnumerable<GlossaryTerm> terms)
    {
        var added = 0;

        lock (_gate)
        {
            foreach (var term in terms)
            {
                if (Add(term))
                {
                    added++;
                }
            }

            if (added > 0)
            {
                Save();
            }
        }

        return added;
    }

    /// <summary>
    /// 此刻已经定下的译名的一份拷贝 —— 读的那一边要的是一份不会再变的东西。
    /// </summary>
    public IReadOnlyList<GlossaryTerm> Snapshot()
    {
        lock (_gate)
        {
            return [.. _terms];
        }
    }

    /// <summary>
    /// 把表写成文件：先写临时文件再替换，中途出错不会截断已有的表。
    ///
    /// 只由 <see cref="Merge"/> 在长出新条目之后调用，调用时已经持有 <c>_gate</c>，所以这里不再上锁。
    /// 写不动只记进 <see cref="LastError"/>、不往外抛，由调用方在跑完后报出。
    /// </summary>
    private void Save()
    {
        if (_terms.Count == 0)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(
                new GlossaryDocument { Terms = _terms },
                GlossaryJson.ForWriting.GlossaryDocument);

            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, FilePath, overwrite: true);

            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 只在第一次写不动时报一行，之后一直写不动也不再重复。
            if (LastError is null)
            {
                Log.Warn($"        术语表暂时写不出去，下个窗口再试：{ex.Message}");
            }

            LastError = ex.Message;
        }
    }

    private bool Add(GlossaryTerm term)
    {
        if (term.Source.Length == 0 || term.Target.Length == 0 || !_sources.Add(term.Source))
        {
            return false;
        }

        _terms.Add(term);
        return true;
    }
}
