namespace OmniSubs.Model;

/// <summary>
/// 一次识别请求：一段提示词，加上要听的音频或要看的画面。一次请求只带一段媒体。
/// 回答的格式（SRT）由提示词约定，把它解析成时间轴是排版阶段的事，不在这里。
/// </summary>
internal sealed record ModelRequest(string Prompt, ModelMediaPart Media);

/// <summary>
/// 一次识别的回答，连同用量。用量只说"进多少、出多少"，用中立的说法，由实现把自家字段对上来。
/// </summary>
internal sealed record ModelReply(string Text, int InputTokens, int OutputTokens);
