using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SubtitleMaster.Models;

public enum OutputMode
{
    HardsubVideo, // 压制内嵌字幕视频（硬字幕烧录，默认推荐）
    SoftsubVideo, // 封装软字幕视频（无损软字幕轨封装）
    SubtitleOnly  // 仅生成字幕文件
}

public enum TranslationStyle
{
    Literal,   // 偏直译
    Balanced,  // 标准/平衡
    Free       // 偏意译
}

public enum SegmentationStyle
{
    Standard, // 标准（CJK 单行上限 23 字 / 非 CJK 45 字符，自主规范断句 - 默认推荐）
    Shorter   // 较短（CJK 单行上限 15 字 / 非 CJK 30 字符，防大字号字幕溢出屏幕）
}

public enum WebSearchMode
{
    Accurate, // 开启（深度检索验证专有名词与背景，准确无误 - 默认）
    Off       // 关闭（完全基于上下文，不进行联网搜索）
}

public partial class AppSettings : ObservableObject
{
    [ObservableProperty]
    private string _cliPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelName))]
    private string _baseModel = "gemini-4-neon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelName))]
    private string _thinkingIntensity = "high"; // "high", "medium", "low", "none" - default to high

    [ObservableProperty]
    private OutputMode _outputMode = OutputMode.HardsubVideo;

    [ObservableProperty]
    private TranslationStyle _translationStyle = TranslationStyle.Balanced;

    [ObservableProperty]
    private SegmentationStyle _segmentationStyle = SegmentationStyle.Standard;

    [ObservableProperty]
    private WebSearchMode _webSearchMode = WebSearchMode.Accurate;

    public bool EnableWebSearch => WebSearchMode != WebSearchMode.Off;

    [ObservableProperty]
    private string _selectedGlossaryCsv = string.Empty;

    [ObservableProperty]
    private bool _enableSecondarySubtitle = true;

    [ObservableProperty]
    private bool _enableVadAlignment = true;

    [ObservableProperty]
    private int _maxConcurrentTasks = 1;

    [ObservableProperty]
    private string _targetLanguage = "zh-CN";

    [ObservableProperty]
    private bool _isDebugMode = false;

    [ObservableProperty]
    private EffectSubtitleConfig _effectConfig = new();

    public string ModelName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ThinkingIntensity) || ThinkingIntensity.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return BaseModel;
            }
            if (BaseModel.EndsWith("-high", StringComparison.OrdinalIgnoreCase)
                || BaseModel.EndsWith("-medium", StringComparison.OrdinalIgnoreCase)
                || BaseModel.EndsWith("-low", StringComparison.OrdinalIgnoreCase))
            {
                return BaseModel;
            }
            string baseName = BaseModel.EndsWith("-xhigh", StringComparison.OrdinalIgnoreCase)
                ? BaseModel[..^6]
                : BaseModel;
            string effectiveIntensity = ThinkingIntensity.Equals("xhigh", StringComparison.OrdinalIgnoreCase)
                ? "high"
                : ThinkingIntensity.ToLowerInvariant();
            return $"{baseName}-{effectiveIntensity}";
        }
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            if (value.EndsWith("-xhigh", StringComparison.OrdinalIgnoreCase))
            {
                BaseModel = value[..^6];
                ThinkingIntensity = "high";
            }
            else if (value.EndsWith("-high", StringComparison.OrdinalIgnoreCase))
            {
                BaseModel = value[..^5];
                ThinkingIntensity = "high";
            }
            else if (value.EndsWith("-medium", StringComparison.OrdinalIgnoreCase))
            {
                BaseModel = value[..^7];
                ThinkingIntensity = "medium";
            }
            else if (value.EndsWith("-low", StringComparison.OrdinalIgnoreCase))
            {
                BaseModel = value[..^4];
                ThinkingIntensity = "low";
            }
            else
            {
                BaseModel = value;
                ThinkingIntensity = "medium";
            }
        }
    }

    public AppSettings()
    {
        // Auto-detect CLI path in tools, current directory, or parent directory
        string toolsCli = Path.Combine(Helpers.FilePathHelper.GetToolsDirectory(), "antigravity.exe");
        string localCli = Path.Combine(AppContext.BaseDirectory, "antigravity.exe");
        string parentCli = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "antigravity.exe"));

        if (File.Exists(toolsCli))
        {
            CliPath = toolsCli;
        }
        else if (File.Exists(localCli))
        {
            CliPath = localCli;
        }
        else if (File.Exists(parentCli))
        {
            CliPath = parentCli;
        }
        else
        {
            CliPath = "antigravity.exe";
        }
    }
}
