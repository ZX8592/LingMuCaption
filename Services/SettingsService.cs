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
        GlossaryService.EnsureSampleGlossaryExists();
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

                    // Migrate legacy default EffectConfig values (56 / 0.65 / 12 / Bold=false) to new defaults (68 / 0.70 / 0 / Bold=true)
                    var ec = loaded.EffectConfig;
                    bool needsSave = false;
                    if (ec.FontSize == 56 && Math.Abs(ec.SecondaryScale - 0.65) < 0.01 && ec.SubSpacing == 12 && !ec.Bold)
                    {
                        ec.FontSize = 68;
                        ec.SecondaryScale = 0.70;
                        ec.SubSpacing = 0;
                        ec.Bold = true;
                        needsSave = true;
                    }

                    if (!ec.ColorMigratedToEdeded)
                    {
                        if (string.Equals(ec.PrimaryColor, "#FFFFFF", StringComparison.OrdinalIgnoreCase))
                            ec.PrimaryColor = "#EDEDED";
                        if (string.Equals(ec.SecondaryColor, "#FFFFFF", StringComparison.OrdinalIgnoreCase))
                            ec.SecondaryColor = "#EDEDED";
                        ec.ColorMigratedToEdeded = true;
                        needsSave = true;
                    }

                    if (needsSave)
                    {
                        try
                        {
                            var options = new JsonSerializerOptions { WriteIndented = true };
                            File.WriteAllText(_settingsFilePath, JsonSerializer.Serialize(loaded, options));
                        }
                        catch { }
                    }

                    return loaded;
                }
            }
        }
        catch
        {
            // Fallback to default
        }

        var defaults = new AppSettings();
        // Dedicated to Chinese translation and transcription
        defaults.TargetLanguage = "zh-CN";
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
