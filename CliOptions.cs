using OmniSubs.Config;

namespace OmniSubs.Cli;

/// <summary>
/// 命令行参数。
///
/// 这里没有「开某个功能」这一类参数：要开的功能由给出它所用的模型档来表达，
/// 例如 <c>--model-video video</c> 既指定画面用哪个模型，也就是开启了视频字幕。
/// </summary>
internal sealed class CliOptions
{
    public List<string> Inputs { get; } = [];

    /// <summary>音频字幕使用的档位 id。省略时取 <see cref="Defaults.DefaultProfileId"/>。</summary>
    public string? AudioProfileId { get; set; }

    /// <summary>视频字幕使用的档位 id。给了就是开启视频字幕；没给就是不做画面通道。</summary>
    public string? VideoProfileId { get; set; }

    /// <summary>音频字幕强制开启，所以只有「关掉术语记忆」这一个否定式开关。</summary>
    public bool NoGlossary { get; set; }

    /// <summary>音轨序号或语言代码；省略时用第一条音轨。</summary>
    public string? AudioTrack { get; set; }

    public bool ShowHelp { get; set; }

    /// <summary>机器可读的进度写到这里，给前端（mpv 插件）看；不给就是纯命令行用法，什么都不写。</summary>
    public string? ProgressFile { get; set; }

    public const string Usage =
        """
        omnisubs - 用多模态大模型为视频生成中文字幕

        用法：
          omnisubs <视频文件或目录> [更多输入...] [选项]

        说明：
          音频字幕始终生成：音频按 30 秒窗口切片（相邻窗口重叠 5 秒），逐窗交给音频模型
          听写并翻译成简体中文，再用从音频里测出的人声边界修正时间戳，最后合并回一条时间轴。
          跨窗口重复译出的句子只保留离窗口中心更近的那一份。

          视频字幕按需开启：加上 --model-video <档位id> 就会同时读画面上的文字，并与音频字幕
          合并进同一个 .srt —— 画面文字置顶，音频字幕置底。不加这个参数就只有音频字幕。

          术语记忆默认开启：人名、地名、专有名词第一次出现时定名，之后强制沿用同一译名。
          只有音频字幕会往表里写；画面字幕沿用对白定下的译名，但自己不写回。
          累计表存在视频同目录的 omnisubs.glossary.json，同目录下的其他剧集自动读到同一份，
          于是译名跨集稳定。

          .srt 写在视频旁边、同名 .srt 直接覆盖，而且每走完一个窗口就整份更新一次 ——
          第一个窗口之后就能挂上看，不必等整部片子跑完；输入是目录时递归处理其中所有视频；
          一个视频失败不影响其余的继续。

        模型档：
          omnisubs.json 与 omnisubs.exe 放在一起，只记录静态的模型信息：

            {
              "models": {
                "default": { "baseUrl": "https://.../compatible-mode/v1", "apiKey": "sk-...", "model": "..." },
                "video":   { "baseUrl": "https://.../v1",                    "apiKey": "sk-...", "model": "..." }
              }
            }

          要开哪个功能就在命令行上给出对应的档位 id。配置里不记录功能开关，也不记录角色绑定。

        选项：
          --model-audio <档位id>    音频字幕使用的模型档，默认 default
          --model-video <档位id>    开启视频字幕识别，并使用该模型档
          --no-glossary             关闭术语自动记忆（默认开启）
          --audio-track <序号|语言> 指定音轨：第几条（从 1 数起）或语言代码，默认第一条
          --progress-file <路径>    把这次运行的结论写给前端看（mpv 插件用）：成没成、失败的原因
          -h, --help                显示本帮助
        """;

    /// <summary>不带值的选项。</summary>
    private static readonly Dictionary<string, Action<CliOptions>> Flags = new(StringComparer.Ordinal)
    {
        ["-h"] = options => options.ShowHelp = true,
        ["--help"] = options => options.ShowHelp = true,
        ["--no-glossary"] = options => options.NoGlossary = true,
    };

    /// <summary>
    /// 要接一个值的选项。值原样收下：档位是否存在、凭据有没有配齐，是配置加载之后的事
    /// （见 <see cref="OpenAiBinding"/>）。
    /// </summary>
    private static readonly Dictionary<string, Action<CliOptions, string>> Valued = new(StringComparer.Ordinal)
    {
        ["--model-audio"] = (options, value) => options.AudioProfileId = value,
        ["--model-video"] = (options, value) => options.VideoProfileId = value,
        ["--audio-track"] = (options, value) => options.AudioTrack = value,
        ["--progress-file"] = (options, value) => options.ProgressFile = value,
    };

    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        options = new CliOptions();
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // 不以横线开头的都是输入，可以出现在选项之间。
            if (!arg.StartsWith('-'))
            {
                options.Inputs.Add(arg);
                continue;
            }

            if (Flags.TryGetValue(arg, out var flag))
            {
                flag(options);
                continue;
            }

            if (Valued.TryGetValue(arg, out var apply))
            {
                // 缺值不拿下一个参数顶替：写清是哪个选项要值就退出。
                if (i + 1 >= args.Length)
                {
                    error = $"选项 {arg} 需要一个值。";
                    return false;
                }

                apply(options, args[++i]);
                continue;
            }

            error = $"无法识别的选项：{arg}";
            return false;
        }

        if (options.ShowHelp)
        {
            return true;
        }

        error = options.Validate();
        return error is null;
    }

    /// <summary>检查选项之间必须成立的约束。凭据与档位不在这里查，见 <see cref="OpenAiBinding"/>。</summary>
    private string? Validate() =>
        Inputs.Count == 0 ? "至少要给一个视频文件或目录。" : null;
}
