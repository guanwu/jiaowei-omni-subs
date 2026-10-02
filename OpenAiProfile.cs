using System.Text.Json.Serialization;

namespace OmniSubs.Config;

/// <summary>
/// omnisubs.json 里的一个模型档：静态的连接信息与调用参数。这些字段是这一族端点才有的说法，
/// 不进 <c>OmniSubs.Model</c> 下的契约。
///
/// 它留在 <c>OmniSubs.Config</c> 而不与兑现该契约的 <see cref="OpenAiModel"/> 同住：
/// 分开会让 <c>OmniSubs.Config</c> 与 <c>OmniSubs.Model.OpenAi</c> 互相依赖成一个环。
/// 这是本项目"实现挂在契约之下"的一处显式例外。
/// </summary>
internal sealed class OpenAiProfile
{
    /// <summary>OpenAI 兼容端点，结尾不带斜杠。</summary>
    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    /// <summary>密钥。只从这份配置里取，没有环境变量这一路（见 <see cref="OpenAiBinding"/>）。</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>下面几个调用参数留空时取 <see cref="Defaults"/> 里的值。</summary>
    [JsonPropertyName("maxTokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("thinkingBudget")]
    public int? ThinkingBudget { get; set; }

    /// <summary>该档位的提示词模板。</summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }
}

/// <summary>
/// 档位解析 + 凭据补齐之后的结果：每个字段都已确定，调用方不必再判空。
/// </summary>
internal sealed record OpenAiResolvedProfile(
    string Id,
    string BaseUrl,
    string ApiKey,
    string Model,
    int MaxTokens,
    double Temperature,
    int ThinkingBudget,
    string? Prompt);
