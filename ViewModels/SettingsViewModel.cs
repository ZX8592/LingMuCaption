using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing.Text;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubtitleMaster.Models;
using SubtitleMaster.Services;

namespace SubtitleMaster.ViewModels;

public class ThinkingIntensityOption
{
    public string Value { get; set; } = "high";
    public string Label { get; set; } = string.Empty;
}

public class AnimationOption
{
    public AnimationPreset Value { get; set; }
    public string Label { get; set; } = string.Empty;
}

public class ConcurrentTaskOption
{
    public int Value { get; set; }
    public string Label { get; set; } = string.Empty;
}

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private AppSettings _settings;

    [ObservableProperty]
    private bool _isCheckingCli;

    [ObservableProperty]
    private bool _isCliReady;

    [ObservableProperty]
    private string _cliStatusMessage = string.Empty;

    public ObservableCollection<string> AvailableFonts { get; } = new();

    public ObservableCollection<string> AvailableBaseModels { get; } = new()
    {
        "gemini-4-neon",
        "gemini-4-hydrogen",
        "gemini-4-argon",
        "gemini-3.8-flash"
    };

    public List<ThinkingIntensityOption> AvailableThinkingIntensities { get; }

    public List<AnimationOption> AvailableAnimations { get; }

    public List<ConcurrentTaskOption> AvailableConcurrentTasks { get; }

    public LocalizationService Loc => LocalizationService.Instance;

    public string SelectedBaseModel
    {
        get => Settings.BaseModel;
        set
        {
            if (!string.IsNullOrEmpty(value) && Settings.BaseModel != value)
            {
                Settings.BaseModel = value;
                OnPropertyChanged();
                SettingsService.Instance.SaveSettings();
            }
        }
    }

    public string SelectedThinkingIntensity
    {
        get => Settings.ThinkingIntensity;
        set
        {
            if (!string.IsNullOrEmpty(value) && Settings.ThinkingIntensity != value)
            {
                Settings.ThinkingIntensity = value;
                OnPropertyChanged();
                SettingsService.Instance.SaveSettings();
            }
        }
    }

    public string SampleMainText => Loc.IsChinese
        ? "这是生成的主字幕的效果测试"
        : "This is a test of the generated primary subtitle effect";

    public string SampleSubText => Loc.IsChinese
        ? "This is the original recognized secondary subtitle"
        : "这是自动识别的原语言副字幕对照效果";

    public string SampleTopMainText => Loc.IsChinese
        ? "这是同时说话的顶部字幕的效果测试"
        : "This is a test of the simultaneous top subtitle effect";

    public string SampleTopSubText => Loc.IsChinese
        ? "This is the simultaneous top secondary subtitle"
        : "这是同时说话的顶部原语言副字幕对照效果";

    public SettingsViewModel()
    {
        Settings = SettingsService.Instance.CurrentSettings;

        AvailableThinkingIntensities =
        [
            new() { Value = "high", Label = Loc.IntensityHigh },
            new() { Value = "medium", Label = Loc.IntensityMedium },
            new() { Value = "low", Label = Loc.IntensityLow }
        ];

        AvailableAnimations =
        [
            new() { Value = AnimationPreset.None, Label = Loc.AnimNone },
            new() { Value = AnimationPreset.FadeInOut, Label = Loc.AnimFade },
            new() { Value = AnimationPreset.PopIn, Label = Loc.AnimPopIn },
            new() { Value = AnimationPreset.SlideUp, Label = Loc.AnimSlideUp },
            new() { Value = AnimationPreset.BlurGlow, Label = Loc.AnimBlurGlow },
            new() { Value = AnimationPreset.KaraokeSweep, Label = Loc.AnimKaraokeSweep }
        ];

        AvailableConcurrentTasks =
        [
            new() { Value = 1, Label = Loc.ConcurrentOption1 },
            new() { Value = 2, Label = Loc.ConcurrentOption2 },
            new() { Value = 3, Label = Loc.ConcurrentOption3 },
            new() { Value = 4, Label = Loc.ConcurrentOption4 }
        ];

        LoadInstalledFonts();

        // Ensure current base model is a supported Gemini model in list
        if (string.Equals(Settings.ThinkingIntensity, "xhigh", StringComparison.OrdinalIgnoreCase))
        {
            Settings.ThinkingIntensity = "high";
            SettingsService.Instance.SaveSettings();
        }
        if (!CliTranscriptionService.IsSupportedGeminiModel(Settings.BaseModel))
        {
            Settings.BaseModel = "gemini-4-neon";
            SettingsService.Instance.SaveSettings();
        }
        if (!AvailableBaseModels.Contains(Settings.BaseModel))
        {
            AvailableBaseModels.Insert(0, Settings.BaseModel);
        }

        // Propagate EffectConfig changes for real-time preview & auto-save
        Settings.EffectConfig.PropertyChanged += OnEffectConfigChanged;

        _ = RefreshCliStatusAsync(forceRefresh: false);
    }

    private void LoadInstalledFonts()
    {
        try
        {
            using var fontCollection = new InstalledFontCollection();
            var fontNames = fontCollection.Families
                .Select(f => f.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name)
                .ToList();

            if (fontNames.Count > 0)
            {
                foreach (var name in fontNames)
                {
                    AvailableFonts.Add(name);
                }
            }
        }
        catch
        {
            // Fallback list if GDI+ InstalledFontCollection encounters any environment issue
            string[] fallbacks = ["Microsoft YaHei", "Segoe UI", "Arial", "SimHei", "KaiTi", "PingFang SC", "Consolas"];
            foreach (var name in fallbacks)
            {
                AvailableFonts.Add(name);
            }
        }

        if (!string.IsNullOrEmpty(Settings.EffectConfig.FontName) &&
            !AvailableFonts.Contains(Settings.EffectConfig.FontName))
        {
            AvailableFonts.Insert(0, Settings.EffectConfig.FontName);
        }
    }

    private void OnEffectConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Settings));
        SettingsService.Instance.SaveSettings();
    }

    [RelayCommand]
    public async Task RefreshCliStatusAsync(bool forceRefresh = false)
    {
        IsCheckingCli = true;
        try
        {
            var (ready, msg, models) = await CliTranscriptionService.Instance.CheckCliStatusAsync(Settings.CliPath, forceRefresh);
            IsCliReady = ready;
            CliStatusMessage = ready ? Loc.CliReadyText : $"{Loc.CliNotReadyText}: {msg}";

            if (models.Count > 0)
            {
                var desiredBases = new List<string>();
                foreach (var m in models)
                {
                    if (!CliTranscriptionService.IsSupportedGeminiModel(m))
                    {
                        continue;
                    }

                    string baseName = StripIntensitySuffix(m);
                    if (!desiredBases.Contains(baseName, StringComparer.OrdinalIgnoreCase))
                    {
                        desiredBases.Add(baseName);
                    }
                }

                if (desiredBases.Count > 0)
                {
                    for (int i = AvailableBaseModels.Count - 1; i >= 0; i--)
                    {
                        if (!desiredBases.Contains(AvailableBaseModels[i], StringComparer.OrdinalIgnoreCase))
                        {
                            AvailableBaseModels.RemoveAt(i);
                        }
                    }

                    for (int i = 0; i < desiredBases.Count; i++)
                    {
                        if (!AvailableBaseModels.Contains(desiredBases[i]))
                        {
                            AvailableBaseModels.Insert(Math.Min(i, AvailableBaseModels.Count), desiredBases[i]);
                        }
                    }

                    if (!AvailableBaseModels.Contains(Settings.BaseModel))
                    {
                        Settings.BaseModel = desiredBases[0];
                        OnPropertyChanged(nameof(SelectedBaseModel));
                        SettingsService.Instance.SaveSettings();
                    }
                }
            }
        }
        finally
        {
            IsCheckingCli = false;
        }
    }

    private static string StripIntensitySuffix(string modelId)
    {
        foreach (var suffix in new[] { "-high", "-medium", "-low" })
        {
            if (modelId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && modelId.Length > suffix.Length)
            {
                return modelId[..^suffix.Length];
            }
        }
        return modelId;
    }

    [RelayCommand]
    public void SaveSettings()
    {
        SettingsService.Instance.SaveSettings();
    }
}
