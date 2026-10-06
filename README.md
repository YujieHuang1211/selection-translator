# HuaCi 划词翻译

Windows 全局划词翻译工具。浏览网页、在 Chrome 里看 PDF、或者在任意软件里，选中看不懂的词或句子，点一下旁边的「译」图标就能看到翻译、重点词汇讲解，还能听发音。

## 功能

- **划词即译**：选中文字（拖选 / 双击 / 三击 / Shift+单击）后光标旁出现「译」图标，点击才翻译，不打扰正常操作
- **快捷键**：选中后按快捷键直接翻译；没选中时打开输入框，可以手动输入或粘贴
- **流式翻译**：译文逐字出现，一般 1 秒左右出第一个字
- **重点词汇**：按你的词汇水平（四级 / 六级 / 考研 / 雅思 / 托福 / GRE）挑出生词，给出美英音标、本句中的含义、多条释义、例句、词形和常用搭配
- **词典模式**：只选一个单词或短语时，显示完整词条
- **发音**：原句、译文、每个单词（美音 / 英音分开）、每条例句都能点击朗读；使用微软 Edge 神经网络语音，断网时自动改用 Windows 离线语音
- **翻译方向可调**：默认外文→中文、中文→英文；弹窗顶部可一键切换，例如改成外文→日语
- **固定 / 拖动**：点 📌 固定弹窗，或直接拖动标题栏（拖过就会自动固定），方便对照阅读
- **历史记录与生词本**：自动保存翻译历史（相同内容再次翻译时秒出），单词可收藏到生词本，支持导出 CSV 和 Anki
- **模型可选**：使用 OpenRouter，默认 `deepseek/deepseek-v4.1-flash`，设置里可搜索切换任意模型
- 浅色 / 深色主题跟随系统，支持多显示器和高 DPI，开机自启

## 下载与安装

不需要安装 .NET，也不需要自己编译。

1. 打开 [Releases 页面](https://github.com/YujieHuang1211/selection-translator/releases/latest)，下载 `HuaCi-v1.0.0-win-x64.exe`。
2. 把它放到一个固定的文件夹，例如 `D:\Tools\HuaCi\`，双击运行。
   - 如果 Windows 弹出「Windows 已保护你的电脑」：点 **更多信息 → 仍要运行**。软件没有购买代码签名证书，所以会有这个提示，只在第一次出现。
3. 第一次运行会自动打开设置窗口：
   - 到 <https://openrouter.ai/keys> 注册并创建一个 API Key（需要先在 OpenRouter 充一点值；默认模型 DeepSeek V4.1 Flash 很便宜，一次划词（含重点词汇讲解）大约 0.003–0.005 美元，充 5 美元可以用一千次以上）
   - 把 Key 粘贴到「OpenRouter API Key」，点「测试连接」，显示 ✓ 后点「保存」
4. 想开机自动运行：设置里打开「开机自动启动」。

> 系统要求：Windows 10 1809 或更新版本、Windows 11，64 位。翻译和在线发音需要能访问 openrouter.ai 和微软语音服务。

## 使用

1. 运行后程序在右下角托盘（可能收在 ∧ 里面），没有主窗口。
2. API Key 只保存在你自己的电脑上，并用 Windows 账户加密。
3. 程序在右下角托盘运行：
   - **左键单击托盘图标**：打开翻译输入框
   - **右键**：历史记录与生词本、暂停划词、设置、退出
4. 选中文字后点「译」图标，或按快捷键（默认 Alt+Q；被别的软件占用时会自动换成 Ctrl+Alt+D 并弹通知提示）。

弹窗里的小技巧：

- 原文可以直接修改，按 Enter 重新翻译（Shift+Enter 换行）
- 译文可以选中复制，也可以点 📋 复制整段
- 点单词卡片右上角的 ☆ 收藏到生词本
- Esc 或点击窗口外关闭（固定后点外面不会关）

## 常见问题

**某个软件里选中文字后不出图标？**
为了避免拖窗口、拖文件时误弹，默认只在文本光标（I 形）处弹出图标。个别软件用的是自定义光标，可以在设置里关闭「只在文本光标处弹出」，或者直接用快捷键。

**快捷键没反应？**
快捷键可能被其他软件占了。默认的 Alt+Q 被占用时，程序会自动换成 Ctrl+Alt+D 等空闲组合，并弹通知告诉你。也可以在设置里自己录一个。

**在以管理员身份运行的程序里取不到字？**
这是 Windows 的权限隔离（UIPI）限制，需要把本软件也以管理员身份运行。

**会不会弄乱我的剪贴板？**
不会。取词时优先用 UI Automation 直接读取；需要模拟复制的程序（Chrome、Edge、VS Code 等）会先备份剪贴板，读完立即恢复。如果剪贴板正被别的程序占用、没能备份，就不会去覆盖它。

**终端里取词会不会触发 Ctrl+C 中断程序？**
不会。Windows Terminal 和控制台默认改用 Ctrl+Shift+C。

**数据存在哪？**
`%APPDATA%\HuaCi\`：`settings.json`（API Key 用 Windows 账户加密）、`huaci.db`（历史和生词本）、`log.txt`（错误日志）。

## 开发

```powershell
dotnet build                 # 构建
dotnet test                  # 单元测试
# 不弹界面的端到端自检：翻译 + 词汇解析 + 两种语音（需要 OPENROUTER_API_KEY 环境变量或已保存的 Key）
src\HuaCi\bin\Debug\net8.0-windows10.0.19041.0\HuaCi.exe --selftest "text to translate" --out report.txt
# 把弹窗 / 设置 / 历史窗口离屏渲染成 PNG，用于检查布局
src\HuaCi\bin\Debug\net8.0-windows10.0.19041.0\HuaCi.exe --snapshot <输出目录> [dark]
# 发布
dotnet publish src/HuaCi/HuaCi.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/HuaCi
dotnet publish src/HuaCi/HuaCi.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/HuaCi-portable
```

技术栈：C# .NET 8 + WPF + WPF-UI，NAudio 播放，SQLite 存储。

| 目录 | 内容 |
|---|---|
| `src/HuaCi/Selection` | 全局鼠标/键盘钩子、选择手势判断、取词（UIA + 剪贴板）、热键 |
| `src/HuaCi/Translation` | OpenRouter 客户端（HTTP/2 长连接、SSE 流式）、提示词与 JSON Schema、增量解析 |
| `src/HuaCi/Speech` | Edge 在线语音（WebSocket 长连接）、Windows 离线语音、播放与预取 |
| `src/HuaCi/UI` | 「译」图标、翻译弹窗、设置、历史与生词本 |
| `src/HuaCi/Data` | 历史 / 生词本 SQLite 存储与导出 |
