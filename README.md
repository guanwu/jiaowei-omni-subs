# OmniSubs

用多模态大模型为视频生成中文字幕。音频字幕与画面文字合并进同一个 `.srt`：**画面文字置顶，对白置底**。

- **音频字幕**：始终生成。按 30 秒窗口切片（相邻窗口重叠 5 秒）交给模型听写并译成简体中文，
  再用从音频里测出的人声边界修正时间戳。
- **视频字幕**：`--model-video <档位id>` 开启。按固定 2 秒间隔读画面上的文字，与音频字幕合并。
  承诺的是**大字**（标题、字卡、招牌），小字读不出。
- **术语记忆**：默认开启。人名、地名、专有名词首见定名，之后强制沿用同一译名；累计表存在
  视频同目录的 `omnisubs.glossary.json`，同目录的其他剧集自动共享。
- `.srt` 写在视频旁边、同名直接覆盖，**每走完一个窗口就整份重写一次** ——
  第一个窗口之后就能挂上看，不必等整部片子跑完。

## 需要什么

- Windows x64。用 Release 里的包不必自备运行时；自建需要 .NET 10 SDK。
- `ffmpeg` / `ffprobe`：按 PATH → 程序旁边 → 程序旁边 `tools/` 的顺序找。
- 一份填好密钥的 `omnisubs.json`，**与 `omnisubs.exe` 放在一起**。

## 快速开始

```bash
# 用发布包（推荐）：下载 Release 里的 omnisubs-mpv-win-x64.zip，
# 解压出的 omnisubs/ 里已经带了 omnisubs.exe、插件与 ffmpeg，填好 omnisubs.json 即可：

omnisubs 剧集E01.mkv                          # 只出音频字幕
omnisubs 剧集E01.mkv --model-video video      # 音频字幕 + 画面字幕
omnisubs "D:/剧集/第一季" --model-video video  # 目录递归处理
```

```bash
# 从源码跑
cp omnisubs.json.example omnisubs.json            # 填上 baseUrl / apiKey / model
dotnet build
cp omnisubs.json bin/Debug/net10.0/               # 配置必须待在 exe 旁边
dotnet run -- 剧集E01.mkv
dotnet publish -c Release                         # Native AOT 产物
```

## 命令行

```
omnisubs <视频文件或目录> [更多输入...] [选项]

  --model-audio <档位id>     音频字幕使用的模型档，默认 default
  --model-video <档位id>     开启视频字幕识别，并使用该模型档
  --no-glossary              关闭术语自动记忆（默认开启）
  --audio-track <序号|语言>  指定音轨：第几条（从 1 数起）或语言代码，默认第一条
  --progress-file <路径>     把这次运行的结论写给前端看（mpv 插件用）
  -h, --help                 显示本帮助
```

输入是目录时递归处理其中所有视频；一个视频失败不影响其余的继续。

退出码：`0` 成功 · `1` 部分失败（有视频没产出字幕）· `2` 用法错误 · `3` 缺组件（找不到
ffmpeg/ffprobe）· `4` 缺凭据 · `5` 配置错误 · `130` 取消（Ctrl+C）。

**要开的功能由给出它所用的模型档来表达**：`--model-video video` 既指定画面用哪个模型，
也就是开启了画面通道。配置文件里不记录功能开关，于是不存在"配置说要开、命令行说不要"的第三种状态。

## 配置：`omnisubs.json`

放在 `omnisubs.exe` 旁边，只记录静态的模型信息。**密钥写在里面，这份文件要自己保管**；
能提交的只有 `omnisubs.json.example`。

```jsonc
{
  "models": {
    "default": {
      "baseUrl": "https://<host>/compatible-mode/v1",
      "apiKey": "sk-...",
      "model": "qwen3.8-omni-flash"
    },
    "video": {
      "baseUrl": "https://<host>/v1",
      "apiKey": "sk-...",
      "model": "<vision-model>"
    }
  }
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `baseUrl` | 是 | OpenAI 兼容端点，结尾不带斜杠 |
| `model` | 是 | 模型名 |
| `apiKey` | 是 | 只从这份配置里取，不看环境变量 |
| `maxTokens` / `temperature` / `thinkingBudget` | 否 | 留空取内核默认值 |
| `prompt` | 否 | 该档位的提示词模板（只换得掉"按通道的规则"那一段） |

音频与画面可以是同一个模型（`--model-video default`），也可以是两个不同的档位。

## 术语记忆文件

路径 `<视频所在目录>/omnisubs.glossary.json`，同目录下所有视频共用这一份
（一个文件夹装一部剧 → 恰好一份）：

```jsonc
{ "terms": [ { "source": "東京", "target": "东京" } ] }
```

- **只有音频字幕会往里写**；画面通道只沿用对白到那一刻为止定下的译名，不写回。
- 首译优先：已存在的原文一律不覆盖。
- 每定下新译名就落一次盘，中途取消也不丢。
- 纯文本、LF 换行、UTF-8 无 BOM，可手改、可直接删掉重来。

## mpv 插件

`mpv/` 是给 mpv 用的那一份：右键 → `OmniSubs` → 选一项，就给**正在播的这一部**出字幕。
识别在另一个进程里做，播放不受影响；字幕文件一被程序写出来就挂上，随后跟着它一段段更新。

1. 从 Release 下载 `omnisubs-mpv-win-x64.zip`，把里面的 `omnisubs` 目录整个放进 mpv 的
   `scripts/`（便携版是 `portable_config/scripts/`）。
2. 编辑同目录的 `omnisubs.json`，填上 `baseUrl` / `apiKey` / `model`。包里那份密钥是空的，
   没填就点菜单会退 `4`，并报出这个文件的完整路径。
3. 重启 mpv，对着正在播的片子右键 → `OmniSubs`。

菜单三项：`翻译音轨`、`翻译音轨和画面`、`停止翻译`。插件没有可配置项，也没有查询进度的入口 ——
字幕一段段长出来就是进度。

## 已知边界

- **画面通道只承诺大字**：标题、字卡、招牌读得准，海报正文一类的小字读不准。
- **内容审核**：画面通道偶尔会被端点按"整批媒体"回 `400 data_inspection_failed`，
  那一段就当没拿到字幕（跳过、其余照跑）。
- **单窗失败不拖垮整片**：某个窗口超时/报错/回答读不动就跳过并报明缺了哪一段，
  其余窗口照常，整片跑完退 `1`。
- **同一目录被两个进程同时处理时**，术语表后落盘的会覆盖先落盘的（本版不加锁）。

## 构建

```bash
dotnet build              # 编译
dotnet run -- --help      # 用法
dotnet publish -c Release # Native AOT 产物
```

发布包不在本地手工打：`.github/workflows/release.yml` 在 Release 发布后跑
`dotnet publish -c Release`，把 `mpv/main.lua`、产物 `omnisubs.exe`、现取的一份 ffmpeg/ffprobe
放进同一个 `omnisubs/` 目录，配一份**密钥留空**的 `omnisubs.json`，打成 zip 挂上去。

发一版的过程（**关键是"发布 Release"这一步，只推 tag、或只存成草稿都不算**）：

1. GitHub 上 `Releases` → `Draft a new release` → 新建 tag（例如 `v1.0.0`）→ 点 **`Publish release`**。
2. 这次发布触发 workflow，两分钟左右 Release 上就多出一个资产 `omnisubs-mpv-win-x64.zip`；
   从那以后用户下载的就是它。

Actions 页里手动 `Run workflow` 那一路**只组包、不上传** —— 包留在 run 的 artifact 里（要登录才能下），
它只用来验证这一步。**所以手动跑出一片绿色并不代表 Release 上有包**；要重新生成某个版本的包，
从那次 release 触发的 run 上点 `Re-run all jobs`。

本项目没有测试工程；验证素材是根目录的 `Video.mkv`（未进版本库）。
