namespace OmniSubs.Model;

/// <summary>
/// 请求里一段媒体的模态：听或看。
/// </summary>
internal enum ModelMediaKind
{
    Audio,
    Video,
}

/// <summary>
/// 请求里的一段媒体：这些字节，以及它们的容器格式。音频与画面共用这一个载荷类型。
/// <see cref="Format"/> 是这段字节自己的格式（<c>mp3</c> / <c>mp4</c>），由产出它的媒体实现
/// 连同字节一起带出来；它没有默认值，省掉它就会省出一个说不清的载荷。
/// </summary>
internal sealed record ModelMediaPart(ModelMediaKind Kind, byte[] Data, string Format);
