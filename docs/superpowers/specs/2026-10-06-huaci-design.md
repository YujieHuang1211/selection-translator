# HuaCi 划词翻译（Windows）— 设计与实施计划

## Context
用户要一个在 Windows 全局可用的划词翻译工具：网页、Chrome 里打开的 PDF、普通桌面软件中选中文字 → 光标旁出现小图标 → 点击才翻译。翻译走 OpenRouter API，模型可选，默认 `deepseek/deepseek-v4.1-flash`（已通过 OpenRouter 公开 `/api/v1/models` 核实该 ID 存在，支持 `structured_outputs`、`reasoning` 参数）。除翻译外需要：重点词汇词典式详解、整句与单词发音、优秀的使用体验。

用户已确认：
- 技术栈：**C# .NET 8 + WPF**（本机已装 .NET 8 SDK）
- 翻译方向：默认 **外文→中文、中文→英文**，可在设置/弹窗里改，例如 外文→日语
- 发音：**微软 Edge 在线神经语音 + Windows 离线语音兜底**
- 附加功能：**快捷键直接翻译、固定/拖动翻译窗、历史记录 & 生词本、开机自启**

项目目录 `E:\14_claude_code\04_划词翻译软件` 目前为空，从零搭建。

---

## 一、用户体验（核心流程）

1. **选中**：左键拖选松开 / 双击选词 / 三击选段 / Shift+单击扩选 → 光标右下 (+12,+12) 处淡入一个 24px 圆形小图标（120ms 动画）。
   - 只在**按下时光标是 I 形（IDC_IBEAM）**的情况下触发，避免拖窗口、拖文件、画图时乱弹。
   - 图标**不抢焦点**（`WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` + 对 `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`），原程序的选区不会丢。
   - 图标 4 秒后自动消失；鼠标悬停时暂停计时；在别处按鼠标、按键或滚轮时立即消失。
2. **点击图标** → 取选中文本 → 在原位置展开翻译弹窗（位置限制在当前显示器工作区内，支持多显示器和 Per-Monitor DPI）。
3. **快捷键**（默认 `Alt+Q`，可改）：不经图标，直接翻译当前选中内容。
4. **翻译弹窗**（不抢焦点地弹出；点进去后才激活；点击外部/Esc 关闭，📌 固定后不关闭、可拖动）：
```
┌──────────────────────────────────────────────┐
│ [自动 → 中文 ▾]                    📌  ⚙  ✕  │  ← 语言下拉可临时切换（如 →日语）
│ The quick brown fox jumps…  🔊               │  ← 原文（可折叠），读原句
│──────────────────────────────────────────────│
│ 敏捷的棕色狐狸跳过……（流式逐字出现）  🔊 📋  │  ← 译文、朗读译文、复制
│──────────────────────────────────────────────│
│ 重点词汇                                      │
│ ☆ ubiquitous   美 /juːˈbɪkwɪtəs/ 🔊  英 /…/ 🔊 │
│   adj. 无处不在的；普遍存在的                 │
│   本句中：指……                                │
│   例：Smartphones are ubiquitous… 🔊          │
│       智能手机无处不在……                      │
│   ▸ 更多释义 / 词形 / 搭配                    │
│ ☆ …                                           │
└──────────────────────────────────────────────┘
```
   - 选中**单个词/短语**时切换为**词典模式**：大号词头 + 美/英音标及发音 + 多义项（词性、释义、例句）+ 词形变化 + 常用搭配。
   - ☆ 一键加入生词本。
   - 加载时显示骨架屏动画；译文先流式出现（约 1 秒内出首字），词汇卡片随后淡入。
   - 错误状态直接在弹窗里给出可点击的处理方式：未配置 API Key → 「去设置」按钮；网络/限流 → 「重试」；401/402 → 用中文说明 Key 无效或余额不足。
5. **托盘图标**：暂停/恢复划词、历史 & 生词本、设置、退出。首次启动若没有 API Key，自动打开设置页并给出引导。
6. 浅色/深色主题跟随系统；字号可调。

## 二、架构

单进程 WPF 应用，单实例（命名 Mutex）。

```
HuaCi.sln
src/HuaCi/
  App.xaml(.cs)               启动、单实例、托盘、全局异常兜底
  Core/
    AppSettings.cs            设置模型
    SettingsStore.cs          %APPDATA%\HuaCi\settings.json；API Key 用 DPAPI(ProtectedData) 加密
    LanguageInfo.cs           语言表（代码/中文名/默认语音）+ 文字脚本检测（汉字/假名/谚文/拉丁…）
    AutoStart.cs              HKCU\...\Run 开机自启
  Selection/
    NativeMethods.cs          P/Invoke：SetWindowsHookEx、SendInput、GetCursorInfo、剪贴板序号等
    MouseHook.cs              WH_MOUSE_LL 全局鼠标钩子（运行在专用线程的消息循环上）
    SelectionGestureDetector.cs  纯逻辑：根据按下/松开/双击/位移/I 形光标判断“发生了选择”→ 可单测
    HotkeyManager.cs          RegisterHotKey
    TextGrabber.cs            取选中文本：UIA TextPattern.GetSelection（150ms 超时）→ 失败则模拟复制
    ClipboardGuard.cs         备份剪贴板（文本/HTML/RTF/图片/文件列表）→ 发送复制 → 等剪贴板序号变化 → 读取 → 还原
  Translation/
    OpenRouterClient.cs       /chat/completions（SSE 流式 + 非流式 structured outputs）、/models 列表
    Prompts.cs                翻译提示词、词汇分析提示词、JSON Schema
    TranslationService.cs     并行发起两路请求；结果缓存（同文本+同设置命中历史即秒出）；取消上一次请求
    Models.cs                 AnalysisResult / WordEntry / Sense / Example 等
  Speech/
    EdgeTtsClient.cs          Edge 朗读 WebSocket 协议（含 Sec-MS-GEC 令牌），输出 MP3
    WindowsTts.cs             Windows.Media.SpeechSynthesis 离线兜底
    SpeechService.cs          按语言选语音、内存 LRU 缓存音频、NAudio 播放；再次点击即停止
  Data/
    HistoryStore.cs           SQLite（Microsoft.Data.Sqlite）：history、vocab 两张表；导出 CSV / Anki TSV
  UI/
    FloatingIcon.xaml         选中后的小图标
    TranslatePopup.xaml + TranslatePopupViewModel.cs
    Controls/SpeakButton.xaml 发音按钮（空闲/加载/播放中三种状态）
    Controls/WordCard.xaml    重点词汇卡片
    SettingsWindow.xaml       设置
    HistoryWindow.xaml        历史 & 生词本（搜索、星标、导出）
    Themes/Light.xaml, Dark.xaml
tests/HuaCi.Tests/            xUnit
```

**NuGet**：CommunityToolkit.Mvvm、H.NotifyIcon.Wpf（托盘）、NAudio（播放 MP3）、Microsoft.Data.Sqlite、WPF-UI（Win11 Fluent 风格的设置/历史窗口）。TFM：`net8.0-windows10.0.19041.0`（为了用 WinRT 语音 API）。

## 三、关键实现细节

### 取词（最容易出问题的部分）
- 拖选或双击时**只显示图标，不读文本**，所以平时不会碰剪贴板，也不会给其他程序造成负担。点击图标时才真正取文本。
- `TextGrabber` 的取词顺序：
  1. 前台进程是 Chromium 系浏览器（chrome/msedge/brave）时：直接模拟复制。Chrome 的网页和 PDF 查看器用复制最可靠；查询 UIA 反而会打开 Chrome 的无障碍树，拖慢浏览器。
  2. 其他进程：先试 UIA `TextPattern.GetSelection()`，超过 150ms 或结果为空时再模拟复制。
  3. 复制按键可按进程配置：终端类进程（WindowsTerminal、conhost）用 `Ctrl+Shift+C`，因为在终端里 `Ctrl+C` 会中断正在运行的程序。
- 模拟复制前先等用户松开所有修饰键（防止快捷键 Alt 还按着变成 Alt+C），用 `SendInput` 发送，然后轮询 `GetClipboardSequenceNumber`，最多等 400ms；读到文本后 300ms 内还原原剪贴板内容。
- 进程黑名单（设置里可编辑）：在这些程序里不弹图标，默认包含游戏全屏等。
- 已知限制：以管理员权限运行的程序，由于 UIPI 限制无法模拟按键（UIA 有时仍可取到）。设置里提供“以管理员身份运行”的说明。

### 翻译（OpenRouter）
- `POST https://openrouter.ai/api/v1/chat/completions`，请求头带 `HTTP-Referer` 和 `X-Title: HuaCi`；关闭推理 `reasoning: { enabled: false }` 以降低延迟。
- **两路请求并行**：
  1. **译文**：`stream: true`，`temperature 0.3`，只输出译文。提示词规则：把文本译成 {目标语言}；如果文本本身已是 {目标语言}，则译成 {备选语言}（默认 目标=中文、备选=英文，满足“外文→中文、中文→英文”）。
  2. **分析**：`response_format: json_schema`（structured outputs）返回：
     `{ source_lang, mode: "word"|"text", keywords: [{ word, lemma, phonetic_us, phonetic_uk, pos, brief, context_meaning, senses:[{pos, meaning, example, example_translation}], forms, collocations }] }`
     句子取 2–6 个重点词，按设置的“词汇水平”（默认 CET-6，可选 CET-4/考研/雅思/托福/GRE）挑选超出该水平的词；单词/短语走词典模式，给出完整词条。讲解语言默认中文（可设）。
- 模型不支持 structured outputs 时，降级为“提示词要求输出 JSON + 容错解析”（去掉 ```json 包裹、截取第一个 `{…}`）。
- 新划词会取消上一次未完成的请求（CancellationToken）；单次文本上限 5000 字符，超出则截断并提示。
- 设置里的模型下拉框从 `/api/v1/models` 拉取，支持搜索，显示单价，并可一键恢复默认模型。

### 发音
- **Edge 在线语音**：`wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=…&Sec-MS-GEC=…&Sec-MS-GEC-Version=…`。Sec-MS-GEC = SHA256(按 5 分钟取整的 Windows FileTime + TrustedClientToken) 的大写十六进制，参照开源 `edge-tts` 的实现。输出 `audio-24khz-48kbitrate-mono-mp3`。
- 语音选择：英文 美音 `en-US-AvaNeural` / 英音 `en-GB-SoniaNeural`，中文 `zh-CN-XiaoxiaoNeural`，日语 `ja-JP-NanamiNeural`，韩语 `ko-KR-SunHiNeural`，其他语言用 `en-US-AvaMultilingualNeural`。语言优先采用分析结果里的 `source_lang`，还没返回时用本地文字脚本检测。设置里可改语音和语速。
- 在线失败（断网、协议变更）时 1.5 秒内自动切换到 Windows 离线语音，不打扰用户；托盘气泡只提示一次。
- 音频按 (文本, 语音, 语速) 做 LRU 缓存，重复点击立即播放；播放中再点一次即停止。

### 历史 & 生词本
- SQLite 存于 `%APPDATA%\HuaCi\huaci.db`：`history(id, text, translation, analysis_json, source_lang, target_lang, model, created_at)`、`vocab(id, word, phonetic, brief, entry_json, source_sentence, created_at)`。
- 历史窗口：搜索、点击重新打开弹窗；生词本：删除、导出 CSV / Anki 可导入的 TSV。

### 设置项
API Key（含“测试连接”按钮）、模型、目标语言 / 备选语言 / 讲解语言、词汇水平、触发方式（显示图标 / 关闭）、快捷键、图标自动隐藏时间、发音语音（美/英）和语速、离线兜底开关、主题、字号、进程黑名单、按进程配置复制键、开机自启。

## 四、实施步骤
0. 计划批准后：`git init`，把本设计保存为 `docs/superpowers/specs/2026-10-06-huaci-design.md` 并提交。
1. 脚手架：sln、WPF 项目、测试项目、NuGet、主题资源、托盘、单实例。
2. Core：设置读写 + DPAPI、语言表与脚本检测（先写测试）。
3. Translation：SSE 解析、提示词与 Schema、JSON 容错解析（先写测试）→ OpenRouterClient → TranslationService。
4. Speech：Sec-MS-GEC 令牌（测试）→ EdgeTtsClient → WindowsTts → SpeechService。
5. Selection：手势判定逻辑（先写测试）→ 鼠标钩子 → 剪贴板守护 → TextGrabber → 热键。
6. UI：FloatingIcon → TranslatePopup（流式文本、骨架屏、词卡、发音按钮、固定/拖动、错误态）→ Settings → History。
7. Data：HistoryStore + 生词本 + 导出；接入缓存。
8. 开机自启、首次启动引导、打磨（动画、DPI、多显示器、深色模式）。
9. 发布：`dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true`（依赖 .NET 8 桌面运行时，约 10MB）；另提供 `--self-contained` 版本，免装运行时，体积较大。

## 五、验证
- **单元测试**（`dotnet test`）：手势判定、脚本检测、SSE 分块解析、JSON 容错解析、Sec-MS-GEC 令牌、设置读写。
- **端到端自检**：`HuaCi.exe --selftest "Ubiquitous computing is reshaping how we live."` 不启动界面，直接调用 OpenRouter（读取环境变量 `OPENROUTER_API_KEY` 或已保存的 Key）并输出译文和分析 JSON；再分别调用 Edge 与离线 TTS 合成音频文件，检查非空 MP3/WAV。这一步需要你提供 OpenRouter API Key。
- **手工测试清单**（我先跑一遍，你再体验）：
  - Chrome 网页中拖选、双击、三击；Chrome 打开的 PDF；记事本 / Word / VS Code / 微信 / Windows Terminal 中划词。
  - 取词后剪贴板内容与之前一致；拖动窗口或文件时不弹图标；黑名单程序中不弹图标。
  - 快捷键 Alt+Q；固定后拖动弹窗；点击外部关闭；Esc 关闭。
  - 发音：整句、美/英音、例句；断网时自动切到离线语音。
  - API Key 错误、断网、切换模型、目标语言改成日语。
  - 历史、生词本导出、开机自启开关、深浅色主题、125%/150% 缩放和多显示器。
