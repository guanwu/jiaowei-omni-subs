namespace OmniSubs;

/// <summary>
/// 全局可调数值与输出格式契约。这些值都不作为命令行参数暴露，要调就改这里。
/// </summary>
internal static class Defaults
{
    /// <summary>
    /// 没写 <c>--model-audio</c> 时用的档位 id。它必须存在于 omnisubs.json 的 models 里。
    /// </summary>
    public const string DefaultProfileId = "default";

    /// <summary>按扩展名判断输入是不是视频。大小写不敏感。</summary>
    public static readonly string[] VideoExtensions =
    [
        ".mp4", ".mkv", ".mov", ".avi", ".flv", ".wmv", ".webm", ".m4v",
        ".ts", ".mts", ".mpg", ".mpeg", ".rmvb", ".rm", ".3gp", ".vob", ".ogv"
    ];

    // ------------------------------------------------------------------ 音频字幕识别

    /// <summary>一次请求覆盖的时长。</summary>
    public static readonly TimeSpan WindowLength = TimeSpan.FromSeconds(30);

    /// <summary>相邻窗口共享的时长。</summary>
    public static readonly TimeSpan WindowOverlap = TimeSpan.FromSeconds(5);

    /// <summary>静音判定的门限（dB）与最短时长（秒）。</summary>
    public const int SilenceThresholdDb = -30;

    public const double MinimumSilenceSeconds = 0.2;

    /// <summary>时间戳与测得的边界相距在这个范围内才吸附过去。</summary>
    public const double SnapToleranceSeconds = 0.6;

    /// <summary>时间轴接受的最短条目，用于模型给出零长度时间戳时兜底。</summary>
    public static readonly TimeSpan MinimumCueLength = TimeSpan.FromSeconds(0.8);

    /// <summary>
    /// 交给模型的音频形状。这是与模型之间的约定，媒体后端必须交出这个形状的音频。
    /// </summary>
    public const string AudioFormat = "mp3";

    public const int AudioSampleRate = 16000;

    public const int AudioChannels = 1;

    public const int AudioKbps = 32;

    // ------------------------------------------------------------------ 视频字幕识别

    /// <summary>画面重采样的间隔，也是交给模型的那段画面自己的帧率。</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 交给模型的画面形状。与 <see cref="AudioFormat"/> 同理，媒体后端必须交出一段这个容器格式、不高于这个高度的画面。
    /// </summary>
    public const string FrameFormat = "mp4";

    public const int FrameHeight = 1080;

    /// <summary>画面这一段的画质档（编码器的质量目标）。</summary>
    public const int FrameQuality = 24;

    // ------------------------------------------------------------------ 模型调用

    /// <summary>一次窗口回答的上限。</summary>
    public const int MaxTokens = 8192;

    public const int ThinkingBudget = 1024;

    public const double Temperature = 0.2;

    /// <summary>一次请求的 HTTP 超时，给得宽一些。</summary>
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(10);

    // ------------------------------------------------------------------ 输出格式契约

    /// <summary>换行固定为 LF，编码固定为 UTF-8 无 BOM。</summary>
    public const string NewLine = "\n";

    /// <summary>
    /// 视频字幕的置顶标记（ASS 覆盖标签）。音频条目不添加任何标记，走播放器默认的底部位置；
    /// 视频字幕关闭时整份字幕不带这个标签。
    /// </summary>
    public const string TopOverrideTag = @"{\an8}";
}
