using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniSubs.Config;

namespace OmniSubs.Model.OpenAi;

// 这一组类型只在这个文件里活着：描述 OpenAI 兼容端点的报文形状，契约层没有它们的位置。

/// <summary>内容数组里的一项。三种媒体的字段并列摆着，用不到的写成 null 因而不出现。</summary>
internal sealed class WirePart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("input_audio")]
    public WireAudio? InputAudio { get; set; }

    [JsonPropertyName("video_url")]
    public WireVideo? VideoUrl { get; set; }
}

/// <summary>
/// 内联音频。<see cref="Data"/> 必须带上 <c>data:audio/…;base64,</c> 前缀 ——
/// 漏掉它服务端会把值当成一个裸 URL 去解析。前缀在拼装处生成。
/// </summary>
internal sealed class WireAudio
{
    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;

    /// <summary>
    /// 载荷自己的格式（<c>mp3</c> 之类），原样来自 <see cref="ModelMediaPart.Format"/>，没有默认值。
    /// </summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;
}

/// <summary>内联画面，同样是带 <c>data:</c> 前缀的整段视频。</summary>
internal sealed class WireVideo
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

internal sealed class WireMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public List<WirePart> Content { get; set; } = [];
}

internal sealed class WireRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<WireMessage> Messages { get; set; } = [];

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    /// <summary>流式读：非流式的调用要等服务端把整段回答生成完才回，长回答会被响应超时从中间截断。</summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    /// <summary>
    /// 思维链的开关与额度上限。这两个字段是这一族端点的扩展，不在 OpenAI 标准里；
    /// 额度为 0 时两个都不发，严格的端点也能用。
    /// </summary>
    [JsonPropertyName("enable_thinking")]
    public bool? EnableThinking { get; set; }

    [JsonPropertyName("thinking_budget")]
    public int? ThinkingBudget { get; set; }
}

internal sealed class WireDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

internal sealed class WireChoice
{
    [JsonPropertyName("delta")]
    public WireDelta? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal sealed class WireUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }
}

/// <summary>流里的一帧。用量通常挂在最后一帧上，所以每帧都要看一眼。</summary>
internal sealed class WireFrame
{
    [JsonPropertyName("choices")]
    public List<WireChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public WireUsage? Usage { get; set; }
}

internal sealed class WireErrorEnvelope
{
    [JsonPropertyName("error")]
    public WireError? Error { get; set; }
}

internal sealed class WireError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireRequest))]
[JsonSerializable(typeof(WireFrame))]
[JsonSerializable(typeof(WireErrorEnvelope))]
internal sealed partial class ModelJsonContext : JsonSerializerContext;

/// <summary>
/// 用 OpenAI 兼容的 chat-completions 兑现 <see cref="IMultimodalModel"/>。不缓存、不批量：
/// 一条通道之内的窗口按顺序一个一个问，对白窗口把新术语写回表、画面窗口只读不写；
/// 两条通道各跑各的（见 <see cref="Subtitler"/>）。限速、网络错误、5xx、超时都先重试一次，
/// 仍失败就抛给调用方：那一段作废，内容缺失当场报出，不带走整部片子。
/// </summary>
internal sealed class OpenAiModel(OpenAiResolvedProfile profile) : IMultimodalModel
{
    /// <summary>所有实例共用一个以复用连接池；超时不设在这里，按请求给（见 <see cref="CompleteAsync"/>）。</summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    public string? PromptTemplate => profile.Prompt;

    public async Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request);

        try
        {
            return await SendAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRetryable(ex) && !cancellationToken.IsCancellationRequested)
        {
            // 只重试一次，且只重试重发可能成功的失败（网络错误、5xx、限流、超时）。
            Log.Warn($"        模型请求失败，重试一次：{ex.Message}");
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        // 就再问一遍；这一遍无论成是败都交给调用方，重试策略到此为止。
        return await SendAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把一次请求拼成一整份 JSON；媒体以 base64 内联在请求里（data URI）。</summary>
    private string BuildPayload(ModelRequest request)
    {
        var media = request.Media;
        var base64 = Convert.ToBase64String(media.Data);

        // 抽象只承诺"这段字节是这个格式"，这里把它当 MIME 子类型拼进 data URI。
        var mime = media.Kind == ModelMediaKind.Audio ? "audio" : "video";

        var part = media.Kind == ModelMediaKind.Audio
            ? new WirePart
            {
                Type = "input_audio",
                InputAudio = new WireAudio
                {
                    Data = $"data:{mime}/{media.Format};base64,{base64}",
                    Format = media.Format,
                },
            }
            : new WirePart
            {
                Type = "video_url",
                VideoUrl = new WireVideo { Url = $"data:{mime}/{media.Format};base64,{base64}" },
            };

        // 媒体在前、问题在后。
        var parts = new List<WirePart>
        {
            part,
            new() { Type = "text", Text = request.Prompt },
        };

        var wire = new WireRequest
        {
            Model = profile.Model,
            MaxTokens = profile.MaxTokens,
            Temperature = profile.Temperature,
            Stream = true,
            Messages = [new WireMessage { Role = "user", Content = parts }],
        };

        if (profile.ThinkingBudget > 0)
        {
            wire.EnableThinking = true;
            wire.ThinkingBudget = profile.ThinkingBudget;
        }

        return JsonSerializer.Serialize(wire, ModelJsonContext.Default.WireRequest);
    }

    private async Task<ModelReply> SendAsync(string payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Defaults.HttpTimeout);

        Log.Info($"        POST {payload.Length / 1024.0 / 1024.0:F2} MB · 档位 {profile.Id}");

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{profile.BaseUrl}/chat/completions")
        {
            Content = new StringContent(payload, new UTF8Encoding(false), "application/json"),
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await Http
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 用户没取消，那就是我们自己设的超时到了：报成超时，别报成取消。
            throw new TimeoutException(
                $"模型在 {Defaults.HttpTimeout.TotalMinutes:F0} 分钟内没有答复，已放弃。");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"模型端点回了 HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）：{Explain(body)}",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            return await ReadStreamAsync(response, timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 读 SSE 流并把它拼回一整个回答：只认 <c>data:</c> 开头的行，注释、空行、<c>[DONE]</c> 哨兵都不是内容；
    /// 思维链走另一个字段，不会被拼进来。
    /// </summary>
    private static async Task<ModelReply> ReadStreamAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var text = new StringBuilder();
        var inputTokens = 0;
        var outputTokens = 0;
        var truncated = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var frame = line[5..].Trim();
            if (frame.Length == 0 || frame == "[DONE]")
            {
                continue;
            }

            WireFrame? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize(frame, ModelJsonContext.Default.WireFrame);
            }
            catch (JsonException ex)
            {
                throw new HttpRequestException(
                    $"读不懂流里的一帧：{ex.Message}\n帧：{(frame.Length <= 300 ? frame : frame[..300] + "…")}");
            }

            if (chunk?.Usage is { } usage)
            {
                inputTokens = usage.PromptTokens;
                outputTokens = usage.CompletionTokens;
            }

            foreach (var choice in chunk?.Choices ?? [])
            {
                // finish_reason="length" 表示撞上输出上限；只用来给"没有内容"一个更准的原因。
                if (choice.FinishReason == "length")
                {
                    truncated = true;
                }

                if (choice.Delta?.Content is { Length: > 0 } delta)
                {
                    text.Append(delta);
                }
            }
        }

        var result = text.ToString();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new HttpRequestException(truncated
                ? "模型这次没有返回任何内容：回答一开始就被输出上限截断了。"
                : "模型这次没有返回任何内容。");
        }

        return new ModelReply(result, inputTokens, outputTokens);
    }

    /// <summary>这次失败是否值得再问一遍：只对重发同样请求可能成功的失败（超时、网络错误、5xx、限流）返回 true。</summary>
    private static bool IsRetryable(Exception exception) => exception switch
    {
        TimeoutException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: var status } =>
            (int)status! >= 500
            || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        _ => false,
    };

    /// <summary>
    /// 从端点回的错误体里取出原因：JSON 取 <c>error.message</c>，否则整段带回并截断。
    /// </summary>
    private static string Explain(string body)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(body, ModelJsonContext.Default.WireErrorEnvelope);
            if (!string.IsNullOrWhiteSpace(envelope?.Error?.Message))
            {
                return envelope.Error.Message;
            }
        }
        catch (JsonException)
        {
            // 不是 JSON 就整段带回去。
        }

        return body.Length <= 500 ? body : body[..500] + "…";
    }
}
