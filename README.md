<div align="center">

# 灵幕助手 (LingMu Caption)

**基于多模态大语言模型的现代 Windows 智能字幕制作与视频压制工作站**

[![Platform](https://img.shields.io/badge/平台-Windows%2010%2F11%20x64-0078D7.svg)](https://microsoft.com/windows)
[![Framework](https://img.shields.io/badge/框架-.NET%208.0%20%7C%20WinUI%203-512BD4.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/开源协议-MIT-green.svg)](LICENSE)
[![Disclaimer](https://img.shields.io/badge/使用声明-仅限交流学习%20非商用-orange.svg)](DISCLAIMER.md)

</div>

---

## 项目简介

**灵幕助手（LingMu Caption）** 是一款专为 Windows 平台打造的高颜值、全流程极简智能字幕处理工具。

本项目深度结合 **WinUI 3** 的现代 Fluent 设计语言与 **多模态大语言模型（Gemini）** 的音频多模态理解能力，借助调用本地的ffmpeg与Antigravity CLI，能够全自动完成：
- **音频高清提取与时基校准**
- **上下文深度语音听辨与长短句分词**
- **多风格双语智能翻译与专有名词网络检索核验**
- **ASS / SRT 特效动画排版与实时预览**
- **基于 NVIDIA NVENC 硬件加速的视频硬字幕压制**

---

## 核心特性

### 1. 多模态大模型智能听辨与上下文翻译
- **上下文感知理解**：依托大模型的多模态理解能力，直接以原生音频信息为上下文输出字幕，告别传统工具不会识别背景，断句破碎、词义脱节的问题。
- **三档翻译风格调教**：提供「直译优先」（忠实原句结构）、「自然平衡」（准确通顺）与「意译润色」（影视台词本地化风格）。
- **专有名词智能搜索**：内置网络检索校验机制，自动校对专业术语、品牌词、人名缩写及最新时事热词，提供“精准核查 / 快速核对 / 关闭”三档强度。
- **语种自适应**：智能探测原音频语种，支持不同语言字幕输出。

### 2. 专业级特效字幕与动画动效（ASS / SRT）
- **双语智能排版**：支持双语模式，提供完整的特效字幕自定义支持。
- **多格式灵活输出**：支持仅导出 `.srt` / `.ass` 字幕、外挂软字幕视频（Softsub）或硬字幕压制视频（Hardsub）。

### 3. 安全沙盒与零残留清理
- **严格工作区沙盒**：调用推理引擎时开启沙盒隔离参数，禁止任何越权访问工作区外文件的行为。
- **运行后自动清理**：任务执行结束后，自动清理本次产生的临时分块、会话数据库及无用缓存，电脑上除目标字幕/视频外**零残留垃圾**。

### 4. 隐私与账号安全
- **零凭据收集**：灵幕助手**绝不记录、收集、缓存或上传**您的任何账号、密码、Token 或凭证信息。
- **官方凭据隔离**：所有登录态由官方 CLI（`antigravity.exe`）依据其自身安全机制加密保存在系统安全位置，本软件**无法接触、也无权读取任何账号敏感数据**。
---

## 便携发布包结构

项目发布后的便携包位于 `Publish\灵幕助手\`：

```text
灵幕助手/
├── 灵幕助手.exe           # 主执行文件
├── tools/                 # 外部运行依赖统一存放
│   ├── antigravity.exe    # 大模型推理 CLI
│   └── ffmpeg.exe         # 音视频编解码引擎
├── logs/                  # 运行日志归集目录
└── app/                   # 应用程序主程序与依赖 DLL 隔离存放
    ├── SubtitleMaster.exe
    ├── SubtitleMaster.dll
    └── ...
```

---

## 🚀 快速上手

### 账号与使用前提
- **账号要求**：使用本软件**需要您拥有具备 Antigravity CLI / Gemini 权限的个人账号**。
- **首次授权**：首次使用前，请前往 `tools/` 文件夹双击启动 `antigravity.exe`，按照官方引导完成登录（若系统已有登录态则无需重复登录）。

### 方式一：使用Releases便携包
1. 从 Releases 下载并解压打包好的 `灵幕助手` 完整版压缩包。
2. 在`tools/` 文件夹中找到并启动antigravity.exe，按引导完成登录。
3. 双击根目录下的 **`灵幕助手.exe`** 即可启动。
4. 设置所需字幕语言与样式，选择字幕规则偏好。
5. 将视频文件或包含视频的文件夹拖入主界面，视频自动开始处理。

### 方式二：从源码编译构建

#### 1. 环境要求
- 操作系统：Windows 10 (Build 19041+) 或 Windows 11 (推荐 24H2)
- 开发环境：[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022（需勾选“.NET 桌面开发”工作负载）

#### 2. 克隆仓库
```bash
git clone https://github.com/your-username/LingMuCaption.git
cd LingMuCaption
```

#### 3. 编译发布
在项目根目录下（`LingMuCaption/`）运行以下命令，即可一步到位编译并输出完整的绿色发布包：
```cmd
dotnet publish SubtitleMaster.csproj -c Release -r win-x64 -o Publish\灵幕助手\app
```
编译完成后，进入 `Publish\灵幕助手\` 即可直接运行与打包。

---

## ❓ 常见问题 (FAQ)

<details>
<summary><b>Q1: 首次启动提示 CLI 未登录或模型不可用？</b></summary>
若 tools/antigravity.exe 尚未登录，可手动打开终端进入 tools 目录运行一次 `antigravity.exe auth` 或按提示完成授权登录，登录凭据将保存在系统标准位置。
</details>

<details>
<summary><b>Q2: 视频压制是否支持 AMD / Intel 显卡硬件加速？</b></summary>
当前版本优先对普及度最高、支持最稳定的 NVIDIA NVENC 进行专用底层适配。若系统中未检测到可用 NVENC，程序会自动无缝降级为画质更细腻的 CPU 多线程 libx264 编码。
</details>

<details>
<summary><b>Q3: 软件是否会记录或泄露我的个人账号与密码？</b></summary>
绝对不会。灵幕助手为 100% 纯本地开源工具，不设任何私人中转服务器。软件仅通过本地系统进程管道向 tools/antigravity.exe 派发音视频听辨任务，所有账号登录与鉴权均由官方 CLI 客户端自行处理并加密保存在系统安全目录下，软件本身绝不读取、不记录、也不上传任何账号或 Token 凭据。
</details>

<details>
<summary><b>Q4: 为什么软件运行完后临时文件都找不到了？</b></summary>
灵幕助手默认启用了“零残留”策略，任务结束后会自动回收所有临时音视频切片和对话会话。如需排查问题，可在主界面连续右键点击三次“设置”图标开启「调试模式」，调试模式下将完整保留中间文件。
</details>

---

## ⚖️ 第三方组件与开源许可声明

灵幕助手依赖并调用了以下优秀的外部独立项目与框架，特此致以谢意：

| 组件名称 | 许可协议 | 作用与说明 | 官方来源 / 源码地址 |
| :--- | :--- | :--- | :--- |
| **FFmpeg** | LGPL v2.1+ / GPL v2+ | 高性能音视频提取、时基校准与硬字幕压制引擎 | [ffmpeg.org](https://ffmpeg.org/) |
| **Antigravity CLI** | 第三方服务条款 | 多模态大语言模型推理执行引擎 (Google LLC) | 遵循其官方服务准则与分发条款 |
| **Windows App SDK (WinUI 3)** | MIT License | 微软官方 Windows 11 现代 Fluent UI 界面系统 | [github.com/microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) |
| **CommunityToolkit.Mvvm** | MIT License | 高性能现代化 MVVM 架构基础库 | [github.com/CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) |

> **注意**：灵幕助手自身源码遵循 MIT 协议开源。本项目未对 FFmpeg 或 CLI 程序进行源码级修改或静态嵌入，两者作为外部独立进程被调用，各自遵守原作者所规定的开源许可证与服务协议。

---

## ⚠️ 免责声明

1. **非商业用途**：灵幕助手为开源学习与技术交流工具，旨在为多语言学习、文化交流及无障碍视听提供便利。**严禁用于任何未经授权的商业牟利行为**。
2. **版权合规**：用户在使用本工具处理媒体资源时，需确保拥有原作品合法授权或符合法律规定的合理使用范畴。开发者不对任何素材版权侵权争议承担法律责任。
3. **第三方工具版权**：外部调用的 FFmpeg 与模型 CLI 著作权归属于各自官方团队，用户需自行遵守其对应使用规范。
4. **AI 生成内容**：字幕文本均由多模态大语言模型概率生成，开发者不对生成内容的准确性与完整性提供绝对担保，请在重要场合自行审查核实。
5. 完整法律条款请参阅 [DISCLAIMER.md](DISCLAIMER.md)。

---

## 📄 开源许可证

本项目遵循 [MIT License](LICENSE) 开源协议。
