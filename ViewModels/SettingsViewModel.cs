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
        "gemini-3.8-flash",
        "gemini-3.7-flash",
        "gemini-3.6-flash",
        "gemini-3.1-pro",
        "claude-sonnet-4-6",
        "claude-opus-4-6",
        "gpt-oss-120b"
    };

    public List<ThinkingIntensityOption> AvailableThinkingIntensities { get; }

    public List<AnimationOption> AvailableAnimations { get; }

    public LocalizationService Loc => LocalizationService.Instance;

    public List<string> AvailableLanguages { get; } =
    [
        "zh-CN",
        "en-US",
        "ja-JP",
        "ko-KR",
        "es-ES",
        "fr-FR",
        "de-DE",
        "ru-RU"
    ];

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

        LoadInstalledFonts();

        // Ensure current base model is in list
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

            // Incorporate any extra models found by CLI without clearing or wiping user's selection
            foreach (var m in models)
            {
                string baseName = m;
                var parts = m.Split('-');
                if (parts.Length >= 2 && (parts[^1] == "high" || parts[^1] == "medium" || parts[^1] == "low"))
                {
                    baseName = string.Join('-', parts.Take(parts.Length - 1));
                }

                if (!AvailableBaseModels.Contains(baseName))
                {
                    AvailableBaseModels.Add(baseName);
                }
            }
        }
        finally
        {
            IsCheckingCli = false;
        }
    }

    [RelayCommand]
    public void SaveSettings()
    {
        SettingsService.Instance.SaveSettings();
    }
}
