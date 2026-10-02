namespace OmniSubs.Media;

/// <summary>
/// 一次媒体操作失败。<see cref="Detail"/> 是后端给出的原因原文，原样带给用户。
/// </summary>
internal sealed class MediaException(string message, string? detail = null) : Exception(message)
{
    public string? Detail { get; } = detail;
}
