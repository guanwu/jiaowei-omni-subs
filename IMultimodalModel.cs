namespace OmniSubs.Model;

/// <summary>
/// 本系统与模型之间的唯一契约：把一段媒体交给多模态模型，拿回一段文本。
///
/// 要让 OmniSubs 认识一家新模型：实现这个接口，在 omnisubs.json 里加一个档位，
/// 然后用 --model-audio / --model-video 指过去。
///
/// 实现约定（由实现方保证，调用方依赖）：
///   * 单次请求失败重试一次，仍失败则抛出 —— 重试策略只有这一处；
///   * 用量信息如实带回，服务端没给就按 0 记；
///   * 抛出的异常要带得走一句人能看懂的原因，它会被原样打给用户。
/// </summary>
internal interface IMultimodalModel
{
    /// <summary>
    /// 这个档位自带的提示词模板；档位没写就是 <c>null</c>，用内核按通道给的那段规则。
    /// </summary>
    string? PromptTemplate { get; }

    Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);
}
