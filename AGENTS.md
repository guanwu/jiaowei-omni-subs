# AGENTS.md

给 AI 编码代理看的项目说明。假设读者对本项目一无所知。

## 项目概览

OmniSubs 是一个 Windows x64 命令行程序，用多模态大模型给视频生成**简体中文字幕**。
入口是可执行文件 `omnisubs.exe`（在版本库里叫 `omnisubs`，见 `OmniSubs.csproj` 的 `AssemblyName`）。

两条字幕通道：

- **音频字幕（对白）**：始终生成。把视频按 30 秒窗口切片（相邻窗口重叠 5 秒）交给多模态模型
  听写并翻译，再用 ffmpeg 的 `silencedetect` 从音频里量出人声边界修正时间戳。
- **视频字幕（画面文字）**：用 `--model-video <档位id>` 开启。按固定 2 秒间隔重采样画面交给模型
  抄写画面上的**大字**（标题、字卡、招牌），与音频字幕合并进同一个 `.srt`：画面文字置顶
  （ASS 标签 `{\an8}`），对白置底。

`.srt` 写在视频旁边、同名直接覆盖，**每走完一个窗口就整份重写一次**，所以第一个窗口之后
就能挂上看。另外还有一份 mpv 插件（`mpv/main.lua`），右键菜单触发识别、边跑边把 `.srt` 挂上。

项目的**注释、日志、提示词、README、CI 注释全部用简体中文**，代码标识符用英文。
写代码/注释时沿用这个分工。

## 技术栈

- **.NET 10 / C# latest**，`Nullable` 与 `ImplicitUsings` 全开（`OmniSubs.csproj`）。
- **Native AOT**：`PublishAot=true`，`RuntimeIdentifier=win-x64`，`OptimizationPreference=Size`，
  `StripSymbols=true`。因此：
  - **只能发布 win-x64**，跨平台不是目标（虽然 `FfmpegTools` 内有非 Windows 分支的兜底）。
  - `EnableAotAnalyzer` / `EnableTrimAnalyzer` / `TrimmerSingleWarn=false` 全开，
    且 `JsonSerializerIsReflectionEnabledByDefault=false`。
  - **JSON 一律用 `System.Text.Json` 的源生成器**（`JsonSerializerContext` 的 partial class），
    不能写反射式序列化，否则 AOT 分析器会报错、运行期会失效。
- **零 NuGet 依赖**：`OmniSubs.csproj` 没有任何 `PackageReference`，只用 BCL。
  不要为了小功能引入第三方包。
- **外部依赖是 ffmpeg / ffprobe**（不是 NuGet，是随包分发的 exe）。
- 使用 .NET 10 的 `System.Threading.Lock`（`lock (someLock)` 的锁对象类型）、
  `[GeneratedRegex]` 源生成正则、集合表达式 `[]`、file-scoped namespace、record、
  primary constructor。代码风格是"扁平放在项目根、靠 namespace 分组"；
  **一个文件承载一个概念**，同一份上下文里的几个小类型刻意住在一起
  （`OpenAiModel.cs` 的 `Wire*` 报文、`FfmpegMedia.cs` 的 `Probe*`、`MediaInfo.cs` 的 `MediaAudioTrack`），
  不追求一类型一文件。

## 构建、运行与验证

```bash
dotnet build                # 编译：产物在 bin/Debug/net10.0/win-x64/
dotnet run -- --help        # 看用法（dotnet run -- <参数>）
dotnet publish -c Release   # Native AOT 单文件产物：bin/Release/net10.0/win-x64/publish/omnisubs.exe
```

要点：

- **配置 `omnisubs.json` 必须与 `omnisubs.exe` 放在一起**（`ConfigFile.Location` =
  `AppContext.BaseDirectory` 拼 `omnisubs.json`）。`OmniSubs.csproj` 里有一条带
  `Condition="Exists(...)"` 的 `None` 项，**只要构建时项目根有 `omnisubs.json`，就会自动拷到输出目录**；
  所以本地开发通常不必手工拷贝，但**新建/修改配置后要重新 build** 才会被带过去。
- 本地验证：直接跑 `dotnet run -- <某个视频> --progress-file <临时文件>`。
- **本项目没有测试工程**。验证素材（`.gitignore` 里的 `Video.mkv`）不进版本库，本机要跑端到端
  得自备一段样片。改动后请用真实的视频跑一遍，不要只跑编译。
- 退出码是明确契约（`Program.cs` 顶部常量）：`0` 成功 · `1` 部分失败 · `2` 用法错误 ·
  `3` 缺组件（ffmpeg/ffprobe）· `4` 缺凭据 · `5` 配置错误 · `130` 取消（Ctrl+C）。

## 代码组织

所有 `.cs` 都在项目根目录，**没有按 namespace 建子目录**。分组靠 namespace：

| namespace | 文件 | 职责 |
|---|---|---|
| `OmniSubs` | `Program.cs`, `Defaults.cs`, `Log.cs`, `ProgressFile.cs`, `MediaException.cs` | 入口编排、全局可调常量、控制台输出、给前端的进度文件 |
| `OmniSubs.Cli` | `CliOptions.cs`, `Targets.cs` | 命令行解析与用法文本；输入展开成待处理清单 |
| `OmniSubs.Config` | `ConfigFile.cs`, `OpenAiProfile.cs`, `OpenAiBinding.cs` | 读 `omnisubs.json`，把档位 id 解析成确定值 + 凭据 |
| `OmniSubs.Media` | `MediaInfo.cs`, `MediaWindow.cs`, `MediaSpeechMap.cs` | 领域模型：探测结论（含音轨选择）、窗口切分、人声边界 |
| `OmniSubs.Media.Ffmpeg` | `FfmpegMedia.cs`, `FfmpegTools.cs`, `FfmpegProcess.cs`, `FfmpegTimes.cs` | 唯一碰 ffmpeg/ffprobe 的地方 |
| `OmniSubs.Model` | `IMultimodalModel.cs`, `ModelRequest.cs`, `ModelMediaPart.cs` | 与模型之间的**契约层** |
| `OmniSubs.Model.OpenAi` | `OpenAiModel.cs` | 契约的一个实现：OpenAI 兼容端点 |
| `OmniSubs.Recognition` | `Subtitler.cs`, `Prompt.cs`, `LaneCues.cs`, `TermExchange.cs` | 把一部影片变成字幕；提示词拼装；一条通道的条目累积（含跨窗去重）；两条通道共用的术语记忆 |
| `OmniSubs.Subtitles` | `Srt.cs`, `SubtitleFile.cs` | SRT 读写、双通道合成、字幕落盘（写盘排队归落盘那一处） |
| `OmniSubs.Glossary` | `GlossaryFile.cs`, `Glossaries.cs` | 术语记忆文件的读写与合并；本次运行要用哪几份 |

### 主流程（`Program.cs` → `Subtitler.RunAsync`）

1. `CliOptions.TryParse` 解析参数（`--model-audio` / `--model-video` / `--no-glossary` /
   `--audio-track` / `--progress-file` / `-h`）。
2. `ProgressFile.TryOpen` 打开进度文件（**在读配置之前**，好让失败那几次也留下终态记录）。
3. `ConfigFile.TryLoad` 读 `omnisubs.json`；`OpenAiBinding.Resolve` 把档位 id 对上、补齐凭据。
4. `Targets.TryResolve` 展开输入（文件或**递归目录**），按 `Defaults.VideoExtensions` 判类型。
5. `FfmpegMedia.TryCreate` 定位 ffmpeg/ffprobe（查找顺序：`PATH` → exe 同目录 → exe 同目录 `tools/`）。
6. `Glossaries.Load` 给每个输入目录各读一份 `GlossaryFile`（同目录所有视频共享一份术语表），
   之后"这个视频用哪一份"由它回答。
7. 对每个视频构造 `Subtitler`，对白通道与画面通道**各跑一个 Task 并发执行**；
   每个窗口完成后把整份 `.srt` 重写一遍（写盘由 `SubtitleFile` 自己排队）。

## 关键设计约定（改动时必须遵守）

- **没有独立的"功能开关"**。要开画面字幕就给出它用的档位（`--model-video <id>`）；
  配置里不记开关。这样不存在"配置说开、命令行说不要"的第三种状态。
- **契约与实现分离**：`IMultimodalModel` 是唯一契约，只承诺"给一段媒体、拿回一段文本 + 用量"。
  `OpenAiModel` 是唯一实现。**`OpenAiProfile`（配置里的档位）刻意留在 `OmniSubs.Config` 而非
  `OmniSubs.Model.OpenAi`**，是为了避免 `Config` 与 `Model.OpenAi` 循环依赖 ——
  这是"实现挂在契约之下"的**显式例外**，`OpenAiProfile.cs` 的注释里写明了理由。
- **两个坐标系不可混用**：`SubtitleWindowCue` 的时刻以窗口起点为 0；
  `SubtitleCue` 的时刻相对整片开头。换算归 `LaneCues` 管：窗口时刻进、整片时刻出，
  跨窗口去重也在那里（`Candidate.Place()`）。
  人声边界的吸附**必须在段内做**（`MediaSpeechMap`）。
- **失败隔离**：单个窗口超时/报错/回答读不动就跳过并报出缺了哪一段，不拖垮整部片子；
  `OpenAiModel.CompleteAsync` 对可重发的失败（超时、网络错误、5xx、限流）**只重试一次**，
  重试策略只在这一处。用量（token）如实累计并报出。
- **术语表只由音频通道增长**：对白通道既读又写；画面通道只读快照（`GlossaryFile.Snapshot()`），不写回。
  合并规则是**首译优先**（已存在的原文不覆盖）。每有新译名就落盘（先写 `.tmp` 再 `File.Move` 替换）。
- **落盘错误不抛异常**：`SubtitleFile.LastError` / `GlossaryFile.LastError` 记录原因，由 `Program` 在跑完后
  报出；一次写不动下个窗口还会再写。
- **可调旋钮都在 `Defaults.cs`**（窗口长度、重叠、静音门限、音频/画面格式、token 上限、
  HTTP 超时、换行与置顶标签……）。这些**不作为命令行参数暴露，要调就改这里**。
  与之相对，某个模块自己的实现常量就地留着，不往上搬（`OpenAiModel.RetryDelay`、
  `FfmpegProcess.OutputLimitChars`、`GlossaryFile` 落盘用的 `.tmp` 后缀……）。
  提示词里引用的采样间隔（`FrameInterval`）必须与 ffmpeg 实际重采样间隔一致。
- **输出格式契约**：`UTF-8 无 BOM` + `LF` 换行（`Defaults.NewLine`）。
  字幕与术语表的 JSON 都用源生成器，写盘时 `UnsafeRelaxedJsonEscaping`（不转义非 ASCII）。
- **模型回答的解析是容错的**：`Srt.Parse` 从任意文本里抠 SRT（时间戳小时位可省、`.` 与 `,` 都收），
  去掉 Markdown 围栏与 `**` 强调；`GLOSSARY:` 行拆不成 `原文 → 译名; ...` 的形状就整行丢掉。
- **进度文件是程序与 mpv 插件之间的接口**：一行一条 `key\tvalue`，每次整份重写；
  终态只写 `exit`（0/1）与失败时的 `error`。插件**从不解析控制台文字**（那是随时可改的散文）。
- **用户可见的错误信息要带得走一句人能看懂的原因**；后端原文通过 `MediaException.Detail` 原样再报一行。
  错误走 stderr，日志/进度走 stdout。

## 配置与数据文件

- `omnisubs.json`（**含 API key，不进版本库**）：只有 `models` 字典，每个档位是
  `baseUrl` / `apiKey` / `model`（必填）与 `maxTokens` / `temperature` / `thinkingBudget` / `prompt`（可选）。
  `apiKey` **只从这份配置里取，不看环境变量**。可提交的模板是 `omnisubs.json.example`。
- `omnisubs.glossary.json`：术语记忆文件，路径固定为 `<视频所在目录>/omnisubs.glossary.json`，
  同目录所有视频共享。纯文本、LF、UTF-8 无 BOM，可手改可删。
- `mpv/main.lua`：mpv 插件。识别在 `omnisubs.exe` 子进程里做，插件靠轮询 `.srt` **文件大小**的变化
  来重挂字幕（程序每窗口整份重写、只会变长），并读进度文件判断成败。菜单项用
  `menu-data` 属性插入；子进程走 `command_native_async`。

## 部署

发布包不在本地手工打。`.github/workflows/release.yml`：

- 触发：Release `published`，或手动 `workflow_dispatch`（只组包不上传）。
- 在 `windows-latest` 上 `dotnet publish -c Release`，现取一份 ffmpeg/ffprobe
  （gyan.dev 的 essentials 包），把 `mpv/main.lua` + `omnisubs.exe` + `tools/ffmpeg.exe`、
  `tools/ffprobe.exe` + **密钥留空**的 `omnisubs.json` 装进 `omnisubs/`，打成
  `omnisubs-mpv-win-x64.zip` 挂到该 Release。
- zip 条目名**一律正斜杠**（自己用 `System.IO.Compression` 写，不用 `Compress-Archive`，
  后者会写反斜杠导致 unzip / macOS 解不开）。

## 安全与版本控制

`.gitignore` 的忽略项及原因：

- `bin/`、`obj/`：构建产物。
- `omnisubs.json`：**运行期配置，里面有 API key**，绝不提交；可提交的模板是 `omnisubs.json.example`。
- `omnisubs.glossary.json`：术语记忆跟着视频走，不属于仓库。
- `tools/`：随程序分发的 ffmpeg（几百 MB，本身是构建产物）。
- `Video.mkv` / `Video.srt`：本地验证素材与它的产物。

不要把真实密钥写进任何会进版本库的文件；CI 里给用户的那份 `omnisubs.json` 用占位密钥替换成空串。
