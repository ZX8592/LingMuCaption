using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SubtitleMaster.Models;

namespace SubtitleMaster.Services;

public class AppLogService
{
    private static AppLogService? _instance;
    public static AppLogService Instance => _instance ??= new AppLogService();

    private readonly object _lock = new();
    private readonly string _primaryLogPath;
    private readonly string _appDataLogPath;

    public string PrimaryLogPath => _primaryLogPath;

    public AppLogService()
    {
        string logsDir = Helpers.FilePathHelper.GetLogsDirectory();
        _primaryLogPath = Path.Combine(logsDir, "task_execution.log");

        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LingMuCaption");
        try
        {
            if (!Directory.Exists(appDataDir)) Directory.CreateDirectory(appDataDir);
        }
        catch { }
        _appDataLogPath = Path.Combine(appDataDir, "task_execution.log");
    }

    public void LogInfo(string message)
    {
        WriteEntry("INFO", message);
    }

    public void LogWarning(string message)
    {
        WriteEntry("WARN", message);
    }

    public void LogError(string message, Exception? ex = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(message);
        if (ex != null)
        {
            sb.AppendLine($"[Exception] {ex.GetType().FullName}: {ex.Message}");
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                sb.AppendLine(ex.StackTrace);
            }
        }
        WriteEntry("ERROR", sb.ToString().TrimEnd());
    }

    public void LogTaskStart(
        string fileName, 
        TimeSpan duration, 
        long fileSize, 
        string model, 
        string thinking, 
        OutputMode outputMode, 
        bool hasNvenc,
        bool webSearch,
        string targetLang)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($"[任务启动] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"  • 目标视频: {fileName} (时长: {(int)duration.TotalMinutes:D2}:{duration.Seconds:D2}, 大小: {fileSize / (1024.0 * 1024.0):F2} MB)");
        sb.AppendLine($"  • 模型配置: {model} (思考强度: {thinking})");
        sb.AppendLine($"  • 翻译目标: {targetLang} | 联网搜索: {(webSearch ? "开启" : "关闭")}");
        sb.AppendLine($"  • 输出模式: {outputMode} | NVENC GPU硬件加速: {(hasNvenc ? "已启用 (NVIDIA NVENC)" : "不可用 (使用CPU软编)")}");
        sb.AppendLine("--------------------------------------------------------------------------------");
        AppendRaw(sb.ToString());
    }

    public void LogStage(string stageName, double elapsedSeconds, string details)
    {
        string entry = $"  [{stageName}] 耗时: {elapsedSeconds:F2}秒 ({elapsedSeconds * 1000.0:F0}ms) | {details}";
        WriteEntry("STAGE", entry);
    }

    public void LogTaskComplete(
        string fileName, 
        double totalSeconds, 
        Dictionary<string, double> stageTimings, 
        bool isSuccess, 
        string? outputPath = null, 
        string? error = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--------------------------------------------------------------------------------");
        if (isSuccess)
        {
            sb.AppendLine($"[任务完成] 视频: {fileName} | 状态: 成功");
            sb.AppendLine($"  • 总耗时: {totalSeconds:F2}秒 (约 {(int)(totalSeconds / 60):D2}:{(int)(totalSeconds % 60):D2})");
            sb.AppendLine("  • 各阶段耗时占比明细:");
            foreach (var kv in stageTimings)
            {
                double pct = totalSeconds > 0 ? (kv.Value / totalSeconds) * 100.0 : 0.0;
                sb.AppendLine($"     - {kv.Key}: {kv.Value:F2}s ({pct:F1}%)");
            }
            if (!string.IsNullOrEmpty(outputPath))
            {
                sb.AppendLine($"  • 最终产物: {outputPath}");
            }
        }
        else
        {
            sb.AppendLine($"[任务失败] 视频: {fileName} | 总运行耗时: {totalSeconds:F2}秒");
            if (!string.IsNullOrEmpty(error))
            {
                sb.AppendLine($"  • 错误详情: {error}");
            }
        }
        sb.AppendLine("================================================================================\n");
        AppendRaw(sb.ToString());
    }

    private void WriteEntry(string level, string message)
    {
        string formatted = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}\n";
        AppendRaw(formatted);
    }

    private void AppendRaw(string content)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_primaryLogPath, content, Encoding.UTF8);
            }
            catch { }

            try
            {
                if (!string.Equals(_primaryLogPath, _appDataLogPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.AppendAllText(_appDataLogPath, content, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
