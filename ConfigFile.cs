using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniSubs.Config;

/// <summary>
/// omnisubs.json 的内容：只有静态的模型档定义。
/// </summary>
internal sealed class ConfigDocument
{
    [JsonPropertyName("models")]
    public Dictionary<string, OpenAiProfile> Models { get; set; } = [];
}

// 手写的文件应当容得下注释和多余的逗号。
[JsonSourceGenerationOptions(
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(ConfigDocument))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

internal static class ConfigFile
{
    public const string FileName = "omnisubs.json";

    /// <summary>配置文件在可执行文件旁边。</summary>
    public static string Location => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// 读配置。文件不存在不算失败 —— 缺哪个档位由 <see cref="OpenAiBinding"/> 在解析那一步报；
    /// 存在却读不动或读不懂才算失败。
    /// </summary>
    public static bool TryLoad(out ConfigDocument config, out string? error)
    {
        config = new ConfigDocument();
        error = null;

        var path = Location;
        if (!File.Exists(path))
        {
            return true;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            error = $"无法读取 {path}：{ex.Message}";
            return false;
        }

        try
        {
            config = JsonSerializer.Deserialize(text, ConfigJsonContext.Default.ConfigDocument)
                ?? new ConfigDocument();
        }
        catch (JsonException ex)
        {
            error = $"{path} 不是有效的配置：{ex.Message}";
            return false;
        }

        return true;
    }
}
