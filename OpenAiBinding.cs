using OmniSubs.Cli;

namespace OmniSubs.Config;

/// <summary>
/// 解析失败的两类原因。它们各自对应一个退出码，所以必须分开。
///
/// 这里没有"成功"这个值 —— 成功由 <see cref="OpenAiBinding.Resolve"/> 返回非空表示。
/// </summary>
internal enum OpenAiBindingFailure
{
    /// <summary>档位不存在，或档位写残了 —— 是配置本身的问题。</summary>
    Config,

    /// <summary>缺少 apiKey —— 缺的是凭据，不是别处写错。</summary>
    MissingCredential,
}

/// <summary>
/// 本次调用要用的档位：音频一个，画面一个（可选）。
///
/// <see cref="Video"/> 有没有值，就是视频字幕的开关 —— 不另立一份开关状态，
/// 也就不会出现"开关说开、档位没给"这种半开着的状态。
/// </summary>
internal sealed record OpenAiBinding(OpenAiResolvedProfile Audio, OpenAiResolvedProfile? Video)
{
    /// <summary>
    /// 把命令行给的档位 id 与配置里的档位对上，并补齐凭据。
    ///
    /// 成功时返回绑定；失败时返回 <c>null</c>，<paramref name="error"/> 是给人看的那一句、
    /// <paramref name="failure"/> 决定调用方退哪个码 —— 两者都只在失败时有意义。
    /// </summary>
    public static OpenAiBinding? Resolve(
        ConfigDocument config,
        CliOptions options,
        out OpenAiBindingFailure failure,
        out string? error)
    {
        failure = OpenAiBindingFailure.Config;
        error = null;

        var audioId = options.AudioProfileId ?? Defaults.DefaultProfileId;

        if (!config.Models.TryGetValue(audioId, out var audioProfile))
        {
            error = UnknownProfile(audioId, config);
            return null;
        }

        if (ResolveRole(audioId, audioProfile, out failure, out error) is not { } audio)
        {
            return null;
        }

        if (options.VideoProfileId is not { } videoId)
        {
            return new OpenAiBinding(audio, null);
        }

        if (!config.Models.TryGetValue(videoId, out var videoProfile))
        {
            error = UnknownProfile(videoId, config);
            return null;
        }

        var video = ResolveRole(videoId, videoProfile, out failure, out error);
        return video is null ? null : new OpenAiBinding(audio, video);
    }

    /// <summary>
    /// 把档位的字段补齐成确定值。返回 null 时 <paramref name="error"/> 是给人看的那一句、
    /// <paramref name="failure"/> 决定调用方退哪个码 —— 两者都只在失败时有意义。
    /// </summary>
    private static OpenAiResolvedProfile? ResolveRole(
        string id,
        OpenAiProfile profile,
        out OpenAiBindingFailure failure,
        out string? error)
    {
        failure = OpenAiBindingFailure.Config;

        if (string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            error = $"档位 {id} 缺少 baseUrl。";
            return null;
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            error = $"档位 {id} 缺少 model。";
            return null;
        }

        // 密钥只从这份配置里取，没有第二处来源。
        if (string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            failure = OpenAiBindingFailure.MissingCredential;
            error = $"档位 {id} 没有 API key：{ConfigFile.Location} 里这个档位的 apiKey 是空的。";
            return null;
        }

        // 三个检查都过，没有错要说。
        error = null;

        return new OpenAiResolvedProfile(
            id,
            profile.BaseUrl.TrimEnd('/'),
            profile.ApiKey.Trim(),
            profile.Model,
            profile.MaxTokens ?? Defaults.MaxTokens,
            profile.Temperature ?? Defaults.Temperature,
            profile.ThinkingBudget ?? Defaults.ThinkingBudget,
            profile.Prompt);
    }


    /// <summary>报出没找到的档位，并顺手列出可用的，两者都点明程序去哪个文件里找。</summary>
    private static string UnknownProfile(string id, ConfigDocument config) =>
        config.Models.Count == 0
            ? $"{ConfigFile.Location} 不存在，或里面没有 models。没有可用档位，无法使用 {id}。"
            : $"{ConfigFile.Location} 里没有档位 {id}，可用："
              + $"{string.Join("、", config.Models.Keys.Order(StringComparer.Ordinal))}。";
}
