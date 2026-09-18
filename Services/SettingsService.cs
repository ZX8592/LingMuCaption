using System;
using System.IO;
using System.Text.Json;
using SubtitleMaster.Models;

namespace SubtitleMaster.Services;

public class SettingsService
{
    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

    private readonly string _settingsFolder;
    private readonly string _settingsFilePath;

    public AppSettings CurrentSettings { get; private set; }

    public SettingsService()
    {
        _settingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SubtitleMaster");
        _settingsFilePath = Path.Combine(_settingsFolder, "settings.json");

        CurrentSettings = LoadSettings();
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    // Ensure EffectConfig exists
                    loaded.EffectConfig ??= new EffectSubtitleConfig();
                    return loaded;
                }
            }
        }
        catch
        {
            // Fallback to default
        }

        var defaults = new AppSettings();
        // If Chinese system, default to zh-CN, otherwise en-US
        defaults.TargetLanguage = LocalizationService.Instance.IsChinese ? "zh-CN" : "en-US";
        return defaults;
    }

    public void SaveSettings()
    {
        try
        {
            if (!Directory.Exists(_settingsFolder))
            {
                Directory.CreateDirectory(_settingsFolder);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(CurrentSettings, options);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // Ignore write failures in read-only environments
        }
    }
}
