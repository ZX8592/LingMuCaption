# 免责声明 / Disclaimer

[English version below](#disclaimer-in-english)

---

## 中文版免责声明

### 1. 仅供学习与交流使用
**灵幕助手（LingMu Caption）** 是一款开源的本地多媒体辅助工具，旨在为多语言学习、影视文化交流以及无障碍视听提供便利。本软件及通过本软件生成的所有字幕、音视频衍生内容，**仅供个人学习、研究、交流以及非营利性用途使用，严禁用于任何未经授权的商业盈利活动**。

### 2. 知识产权与版权合规
- 用户使用本软件处理任何视频、音频素材时，应确保已获得原作品版权所有者的合法授权，或符合相关法律法规所规定的合理使用（Fair Use）范畴。
- 软件开发者不对用户因导入、转录、翻译、分发含有受版权保护素材而引发的任何侵权争议、纠纷或法律责任承担责任。

### 3. AI 模型生成内容的局限性
- 本软件调用的多模态大语言模型（如 Gemini / Antigravity CLI 等）基于概率与上下文生成字幕与翻译结果。
- 尽管系统内置了专有名词智能搜索与对齐校验机制，但开发者无法对生成结果的准确性、完整性、合法性或时效性做出百分之百保证。用户应自行审查生成字幕的真实性与合规性。

### 4. 账号隐私与数据安全（零凭据收集）
- **绝不记录用户凭证**：灵幕助手为纯本地开源软件，不设任何云端中转服务器。本软件**绝不读取、截获、记录或上传**用户的任何账号、密码、Token 或认证密钥。所有的登录授权均由官方 CLI 客户端（`antigravity.exe`）端对端处理并由操作系统底层安全机制加密存储。
- **沙盒隔离与零残留**：在执行大模型调用时默认启用严格的工作区沙盒隔离机制，禁止触碰工作区外文件；任务结束后自动清理所有临时音视频分块、会话数据库及分析缓存，除输出结果外不在系统残留垃圾。
- **凭据自主保管**：用户需自行妥善保管本地模型 CLI 的官方登录凭证，因用户自身操作导致的凭证泄露由用户自行承担。

### 5. 第三方组件与独立工具版权声明 (FFmpeg & CLI)
- **FFmpeg**：
  - 本软件依赖外部 FFmpeg 多媒体框架进行音频提取、波形探测及视频压制。
  - FFmpeg 是 Fabrice Bellard 及 FFmpeg 团队的商标及著作权财产，基于 **LGPL v2.1+ / GPL v2+** 许可协议分发。
  - 本项目源代码遵循 MIT 协议，未对 FFmpeg 进行源码修改；任何随附或引用的 FFmpeg 二进制程序均遵循其各自的开源许可证。FFmpeg 的完整源代码及许可证详情可在其官方网站 [ffmpeg.org](https://ffmpeg.org/) 获取。
- **模型推理 CLI (Antigravity CLI / Gemini)**：
  - 本软件调用的模型命令行工具（如 `antigravity.exe`）及相关云端大模型服务为独立第三方产品，其知识产权、品牌商标与著作权归其所属权利人（Google LLC 等）所有。
  - 本软件仅作为本地宿主界面与外部调度包装器，未篡改该 CLI 核心程序，亦不对该第三方服务的可用性、配额策略或服务条款变更承担担保责任。用户使用该 CLI 需遵守对应提供商的服务条款与使用准则。

### 6. 免责限制
在法律允许的最大范围内，本软件以“现状”（AS IS）形式提供，开发者明确声明不提供任何形式的明示或默示担保。在任何情况下，开发者均不对因使用或无法使用本软件而导致的任何直接、间接、偶然、特殊或惩罚性损害承担责任。

---

<a name="disclaimer-in-english"></a>
## Disclaimer in English

### 1. For Educational and Communication Purposes Only
**LingMu Caption (灵幕助手)** is an open-source multimedia desktop utility developed to facilitate language learning, cross-cultural communication, and accessibility. This software and any derived subtitles or synthesized video materials are **intended solely for personal study, research, and non-commercial communication purposes**. They must not be used for unauthorized commercial distribution or commercial monetization.

### 2. Intellectual Property and Content Rights
- Users must ensure they possess the necessary rights, licenses, or legal permissions (such as Fair Use) before processing copyrighted audio and video content with this software.
- The developers assume no responsibility or legal liability for any copyright infringement, claims, or damages resulting from unauthorized content processing or distribution by users.

### 3. AI Generated Content Notice
- The multimodal AI models integrated or called via CLI (e.g., Gemini / Antigravity CLI) produce transcriptions and translations probabilistically.
- While the software incorporates terminology validation and context alignment, no guarantee is made regarding absolute accuracy, completeness, or suitability for any specific purpose. Users are advised to review and verify all generated subtitle output.

### 4. Account Privacy and Zero Credential Logging
- **No Credential Interception**: LingMu Caption is a strictly local open-source utility with zero middleman servers. The application **never reads, intercepts, logs, caches, or uploads** user account credentials, passwords, API keys, or authentication tokens. All authentication is managed end-to-end directly by the official CLI (`antigravity.exe`) and encrypted by the operating system.
- **Sandboxing & Zero-Trash**: External inference runs under strict sandbox isolation, prohibiting access to external system directories. All transient audio/video chunks and session caches are automatically destroyed upon task completion.
- **Credential Safeguarding**: Users are solely responsible for protecting their local CLI authentication tokens.

### 5. Third-Party Components and Standalone Tools (FFmpeg & CLI)
- **FFmpeg**:
  - This application relies on the FFmpeg multimedia framework for audio demuxing, waveform probing, and video subtitle burning.
  - FFmpeg is a trademark and copyright of Fabrice Bellard and the FFmpeg developers, distributed under the **LGPL v2.1+ / GPL v2+**.
  - LingMu Caption is licensed under MIT and does not modify FFmpeg source code. Any packaged or referenced FFmpeg binaries remain subject to their respective licenses. FFmpeg source code and licensing information can be obtained from [ffmpeg.org](https://ffmpeg.org/).
- **Model Inference CLI (Antigravity CLI / Gemini)**:
  - The external CLI tool (e.g., `antigravity.exe`) and connected foundation model services are independent third-party proprietary products. All trademarks, copyrights, and intellectual property belong to their respective owners (e.g., Google LLC).
  - LingMu Caption acts solely as a client-side wrapper and workflow orchestrator without modifying the CLI binary. Users must comply with the applicable terms of service and acceptable use policies of the respective service providers.

### 6. Limitation of Liability
TO THE MAXIMUM EXTENT PERMITTED BY APPLICABLE LAW, THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES, OR OTHER LIABILITY ARISING FROM OR IN CONNECTION WITH THE SOFTWARE.
