using OmniSubs.Glossary;
using OmniSubs.Subtitles;

namespace OmniSubs.Recognition;

/// <summary>
/// 一条通道跟术语表打交道的全部内容：要沿用的译名从哪儿读，以及这一段新定下的名字要不要报回来、
/// 并进哪一本表。
///
/// 三种做法都从 <see cref="For"/> 出来，于是不存在"要回收却没有表可并"这种半开着的状态，调用方
/// 也不必同时记住两个必须彼此吻合的说法：
///   * 对白两样都做 —— 术语表只由对白增长；
///   * 画面只读 —— 它沿用对白到那一刻为止定下的译名，但不把新名字写回去；
///   * 没给表（<c>--no-glossary</c>）就两样都不做，提示词里连术语段也没有。
/// </summary>
internal sealed class TermExchange
{
    private TermExchange(GlossaryFile? memory, bool collects)
    {
        Memory = memory;
        Collects = collects;
    }

    /// <summary>要回收时并进哪一本表；不谈术语就是 <c>null</c>。</summary>
    public GlossaryFile? Memory { get; }

    /// <summary>要不要在提示词里问本段新定下的名字，并在回答里解析那一行。</summary>
    public bool Collects { get; }

    /// <summary>
    /// 此刻已经定下的译名，交给模型沿用；不谈术语时是 <c>null</c>。
    /// 取的是当下一刻的一份拷贝：另一条通道正往表里加名字，也改不到它。
    /// </summary>
    public IReadOnlyList<GlossaryTerm>? Reuse() => Memory?.Snapshot();

    /// <summary>这条通道的做法。</summary>
    public static TermExchange For(SubtitleCueLane lane, GlossaryFile? glossary)
    {
        if (glossary is null)
        {
            return new TermExchange(null, false);
        }

        // 对白读写两样都做，画面只读。
        return new TermExchange(glossary, lane == SubtitleCueLane.Audio);
    }
}
