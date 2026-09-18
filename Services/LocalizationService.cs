using System;
using System.Globalization;

namespace SubtitleMaster.Services;

public class LocalizationService
{
    private static LocalizationService? _instance;
    public static LocalizationService Instance => _instance ??= new LocalizationService();

    public bool IsChinese { get; private set; }

    public LocalizationService()
    {
        string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        IsChinese = lang.StartsWith("zh");
    }

    public string AppTitle => IsChinese ? "灵幕助手" : "LingMu Caption";
    public string DebugModeEnabled => IsChinese ? "调试模式已开启：任务完成后将保留临时音频与过程文件" : "Debug mode enabled: temporary files will be preserved after tasks";
    public string DebugModeDisabled => IsChinese ? "调试模式已关闭：任务完成后将自动彻底清理所有临时文件" : "Debug mode disabled: temporary files will be cleaned automatically";
    public string DebugModeBadge => IsChinese ? "调试模式" : "DEBUG";
    public string DropHintTitle => IsChinese ? "拖拽视频或文件夹到此处" : "Drop videos or folders here";
    public string DropHintSub => IsChinese ? "拖入或选择后立即开始制作，无需多余确认" : "Processing starts immediately without confirmation";
    public string ImportVideosBtn => IsChinese ? "导入视频" : "Import Videos";
    public string ImportFolderBtn => IsChinese ? "导入文件夹" : "Import Folder";
    public string ClearCompletedBtn => IsChinese ? "清除已完成" : "Clear Completed";
    public string SettingsBtn => IsChinese ? "设置" : "Settings";
    public string QueueTitle => IsChinese ? "处理队列" : "Task Queue";
    public string EmptyQueueHint => IsChinese ? "暂无任务，请点击上方按钮或直接拖入视频" : "No active tasks. Drop files or use import buttons.";

    public string StatusWaiting => IsChinese ? "等待中..." : "Waiting...";
    public string StatusExtractingAudio => IsChinese ? "正在提取音频..." : "Extracting audio...";
    public string StatusTranscribing => IsChinese ? "AI 转录与多语言翻译中..." : "AI transcribing & translating...";
    public string StatusFormatting => IsChinese ? "正在校验与生成字幕..." : "Validating & generating subtitles...";
    public string StatusRenderingVideo => IsChinese ? "正在合成/压制视频..." : "Synthesizing video...";
    public string StatusCompleted => IsChinese ? "处理完成" : "Completed";
    public string StatusFailed => IsChinese ? "处理失败" : "Failed";

    public string OpenOutputFolder => IsChinese ? "打开所在目录" : "Open Folder";
    public string Retry => IsChinese ? "重试" : "Retry";

    // Settings
    public string SettingsTitle => IsChinese ? "设置中心" : "Settings";
    public string CliSectionTitle => IsChinese ? "1. Antigravity CLI 状态与模型" : "1. Antigravity CLI & Model";
    public string CliReadyText => IsChinese ? "CLI 服务正常可用" : "CLI is ready";
    public string CliNotReadyText => IsChinese ? "CLI 未就绪或未检测到" : "CLI not ready";
    public string RefreshCliBtn => IsChinese ? "重新检测" : "Check CLI";
    public string BrowseBtn => IsChinese ? "浏览..." : "Browse...";
    public string PlayAnimationBtn => IsChinese ? "播放动效" : "Play Animation";
    public string FullscreenPreviewBtn => IsChinese ? "全屏预览" : "Fullscreen";
    public string ExitFullscreenBtn => IsChinese ? "退出全屏 (Esc)" : "Exit Fullscreen (Esc)";
    public string ModelLabel => IsChinese ? "AI 转录模型：" : "AI Model:";
    public string CliPathLabel => IsChinese ? "CLI 程序路径：" : "CLI Executable Path:";

    public string OutputSectionTitle => IsChinese ? "2. 输出与字幕规则" : "2. Output & Subtitle Options";
    public string OutputModeLabel => IsChinese ? "输出模式：" : "Output Mode:";
    public string ModeHardsub => IsChinese ? "压制内嵌字幕视频 (硬字幕直接烧录进画面，全平台直接可见 - 推荐)" : "Hardsub Video (Burn-in subtitles into video frames - Recommended)";
    public string ModeSoftsub => IsChinese ? "封装软字幕视频 (无损软字幕轨封装，秒级完成，播放器可开关)" : "Softsub Video (Muxed subtitle track, instant lossless packaging)";
    public string ModeSubtitleOnly => IsChinese ? "仅生成字幕文件 (.ass / .srt)" : "Subtitle Files Only (.ass / .srt)";

    public string SubtitleFormatLabel => IsChinese ? "字幕格式：" : "Subtitle Format:";
    public string FormatAss => IsChinese ? "ASS 特效字幕 (支持样式与动效)" : "ASS Styled Subtitle (Styles & Animations)";
    public string FormatSrt => IsChinese ? "SRT 标准字幕" : "SRT Standard Subtitle";

    public string TranslationStyleLabel => IsChinese ? "翻译风格偏好：" : "Translation Style:";
    public string StyleLiteral => IsChinese ? "偏直译 (忠实原句结构与精确术语)" : "Literal (Faithful to syntax & terms)";
    public string StyleBalanced => IsChinese ? "标准平衡 (兼顾准确性与自然表达)" : "Balanced (Natural & accurate)";
    public string StyleFree => IsChinese ? "偏意译 (影视口语自然本地化)" : "Free / Localized (Idiomatic subtitle phrasing)";

    public string WebSearchModeLabel => IsChinese ? "联网搜索强度：" : "Web Search Intensity:";
    public string WebSearchAccurate => IsChinese ? "精确 (深度检索验证专有名词与背景，准确无误 - 默认)" : "Accurate (Deep verification of proper nouns & terms - Default)";
    public string WebSearchFast => IsChinese ? "快速 (仅检索必要核心专有名词，减少搜索强度与耗时)" : "Fast (Only search critical entities, reduce search intensity & time)";
    public string WebSearchOff => IsChinese ? "关闭 (完全基于上下文，不进行联网搜索)" : "Off (Context-based only, no web searches)";

    public string SecondarySubtitleLabel => IsChinese ? "启用副字幕 (自动将原语言作为第二行对照)：" : "Enable Secondary Subtitle (Original recognized speech as secondary line):";
    public string TargetLanguageLabel => IsChinese ? "字幕翻译目标语言：" : "Target Subtitle Language:";

    public string ThinkingIntensityLabel => IsChinese ? "思考强度 (Thinking Intensity)：" : "Thinking Intensity:";
    public string IntensityHigh => IsChinese ? "高 (深度语义分析与精细断句)" : "High (Deep semantic analysis & precise segmentation)";
    public string IntensityMedium => IsChinese ? "中 (速度与深度平衡 - 默认)" : "Medium (Balanced speed & depth - Default)";
    public string IntensityLow => IsChinese ? "低 (快速生成，减少思考时间)" : "Low (Fast generation, minimal thinking)";

    public string SegmentationStyleLabel => IsChinese ? "断句分割风格偏好：" : "Segmentation Style:";
    public string DenseShortStyle => IsChinese ? "多断句 (短句独立拆分，节奏紧凑，适合演讲与短视频 - 推荐)" : "Dense Short Sentences (Short split, brisk pace - Recommended)";
    public string SparseLongStyle => IsChinese ? "少断句 (长句完整合并，语义连贯)" : "Sparse Long Sentences (Merged long sentences)";

    public string EffectSectionTitle => IsChinese ? "3. 特效字幕样式设计与实时预览" : "3. Subtitle Style Designer & Live Preview";
    public string FontLabel => IsChinese ? "字体选择 (自动读取系统安装字体)：" : "Font Family (System installed fonts):";
    public string FontSizeLabel => IsChinese ? "主字体大小：" : "Font Size:";
    public string FontStyleLabel => IsChinese ? "字体样式：" : "Font Style:";
    public string BoldLabel => IsChinese ? "粗体" : "Bold";
    public string ItalicLabel => IsChinese ? "斜体" : "Italic";
    public string AlignmentLabel => IsChinese ? "字幕对齐方式：" : "Alignment:";
    public string AlignBottomCenter => IsChinese ? "底部居中 (默认)" : "Bottom Center (Default)";
    public string AlignTopCenter => IsChinese ? "顶部居中" : "Top Center";
    public string AlignBottomLeft => IsChinese ? "底部靠左" : "Bottom Left";
    public string AlignBottomRight => IsChinese ? "底部靠右" : "Bottom Right";
    public string SecondaryScaleLabel => IsChinese ? "副字幕缩放比例：" : "Secondary Subtitle Scale:";
    public string LetterSpacingLabel => IsChinese ? "字间距：" : "Letter Spacing:";
    public string MarginVLabel => IsChinese ? "垂直底边距：" : "Bottom Margin:";
    public string PrimaryColorLabel => IsChinese ? "主字幕颜色：" : "Primary Color:";
    public string SecondaryColorLabel => IsChinese ? "副字幕颜色：" : "Secondary Color:";
    public string OutlineColorLabel => IsChinese ? "描边颜色：" : "Outline Color:";
    public string OutlineWidthLabel => IsChinese ? "描边粗细：" : "Outline Width:";
    public string ShadowDepthLabel => IsChinese ? "投影深度：" : "Shadow Depth:";
    public string ShadowColorLabel => IsChinese ? "投影颜色：" : "Shadow Color:";
    public string AnimationPresetLabel => IsChinese ? "动态效果预设：" : "Animation Preset:";
    public string AnimNone => IsChinese ? "无动效 (静态居中)" : "None (Static)";
    public string AnimFade => IsChinese ? "平滑淡入淡出 (推荐)" : "Smooth Fade In/Out (Recommended)";
    public string AnimPopIn => IsChinese ? "弹性缩放弹出 (醒目吸睛)" : "Elastic Pop/Zoom In";
    public string AnimSlideUp => IsChinese ? "向上平滑滑入 (自然连贯)" : "Smooth Slide Up";
    public string AnimBlurGlow => IsChinese ? "柔光呼吸动效 (电影质感)" : "Cinematic Blur & Glow";
    public string AnimKaraokeSweep => IsChinese ? "卡拉OK高亮变色" : "Karaoke Color Sweep";
    public string AnimSpeedLabel => IsChinese ? "动画速度：" : "Animation Speed:";

    public string PreviewTitle => IsChinese ? "实时视频画面预览效果" : "Live Subtitle Preview";
    public string NotificationCompletedTitle => IsChinese ? "字幕制作全部完成" : "Subtitle Processing Completed";
    public string NotificationCompletedBody => IsChinese ? "所有待处理视频已成功生成字幕！" : "All video tasks have been processed successfully!";
    public string OpenFolderBtn => IsChinese ? "打开" : "Open";
    public string RetryBtn => IsChinese ? "重试" : "Retry";
}
