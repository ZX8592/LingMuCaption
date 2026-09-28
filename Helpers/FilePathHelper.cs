using System;
using System.IO;

namespace SubtitleMaster.Helpers;

public static class FilePathHelper
{
    /// <summary>
    /// Computes destination directory for a video file based on whether it was imported as a single file or part of a folder.
    /// </summary>
    public static string GetOutputDirectory(string sourceVideoPath, string? importedRootDirectory = null)
    {
        string videoDir = Path.GetDirectoryName(sourceVideoPath) ?? AppContext.BaseDirectory;

        if (string.IsNullOrEmpty(importedRootDirectory))
        {
            // Single video file import -> original video directory
            return videoDir;
        }

        // Folder import -> sibling folder at same level with _Subtitled suffix
        string normalizedRoot = Path.GetFullPath(importedRootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string parentDir = Path.GetDirectoryName(normalizedRoot) ?? AppContext.BaseDirectory;
        string rootFolderName = Path.GetFileName(normalizedRoot);
        string newRootFolderName = $"{rootFolderName}_Subtitled";
        string targetBaseDir = Path.Combine(parentDir, newRootFolderName);

        // Compute relative subfolder if any
        string relativeSubDir = Path.GetRelativePath(normalizedRoot, videoDir);
        if (relativeSubDir == ".")
        {
            return targetBaseDir;
        }

        return Path.Combine(targetBaseDir, relativeSubDir);
    }

    /// <summary>
    /// Gets formatted file size string (e.g. 15.4 MB, 1.2 GB)
    /// </summary>
    public static string FormatFileSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024m) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024m;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }

    public static bool IsVideoFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".flv" or ".webm" or ".m4v" or ".ts" or ".mts" => true,
            _ => false
        };
    }

    /// <summary>
    /// Locates or creates the tools directory for antigravity.exe, ffmpeg.exe, etc.
    /// </summary>
    public static string GetToolsDirectory()
    {
        string localTools = Path.Combine(AppContext.BaseDirectory, "tools");
        if (Directory.Exists(localTools)) return localTools;

        string parentTools = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools"));
        if (Directory.Exists(parentTools)) return parentTools;

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Locates or creates the logs directory for clean logging output.
    /// </summary>
    public static string GetLogsDirectory()
    {
        try
        {
            string localLogs = Path.Combine(AppContext.BaseDirectory, "logs");
            if (Directory.Exists(localLogs)) return localLogs;

            string parentLogs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs"));
            if (Directory.Exists(parentLogs)) return parentLogs;

            Directory.CreateDirectory(localLogs);
            return localLogs;
        }
        catch
        {
            return AppContext.BaseDirectory;
        }
    }

    /// <summary>
    /// Locates the root directory where 灵幕助手.exe resides (parent of app/ when published, or BaseDirectory in dev).
    /// </summary>
    public static string GetAppRootDirectory()
    {
        try
        {
            string parentDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            if (File.Exists(Path.Combine(parentDir, "灵幕助手.exe")) ||
                Directory.Exists(Path.Combine(parentDir, "tools")))
            {
                return parentDir;
            }
        }
        catch { }

        return AppContext.BaseDirectory;
    }

    public const string SampleAssTemplateFileName = "用户字幕样式(示例).ass";

    /// <summary>
    /// Ensures that 用户字幕样式(示例).ass exists in the 灵幕助手.exe root directory.
    /// </summary>
    public static string EnsureSampleAssTemplateExists()
    {
        string rootDir = GetAppRootDirectory();
        string samplePath = Path.Combine(rootDir, SampleAssTemplateFileName);
        try
        {
            if (!File.Exists(samplePath))
            {
                File.WriteAllText(samplePath, GetDefaultSampleAssTemplateContent(), System.Text.Encoding.UTF8);
            }
        }
        catch { }
        return samplePath;
    }

    /// <summary>
    /// Returns all .ass filenames located in the 灵幕助手.exe sibling directory (with 用户字幕样式(示例).ass first).
    /// </summary>
    public static System.Collections.Generic.List<string> GetAvailableAssTemplateFiles()
    {
        EnsureSampleAssTemplateExists();
        var result = new System.Collections.Generic.List<string>();
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ScanDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var file in Directory.GetFiles(dir, "*.ass", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(file);
                    if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                    {
                        result.Add(name);
                    }
                }
            }
            catch { }
        }

        string rootDir = GetAppRootDirectory();
        ScanDir(rootDir);
        if (!string.Equals(rootDir, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            ScanDir(AppContext.BaseDirectory);
        }

        result.Sort((a, b) =>
        {
            bool aIsSample = string.Equals(a, SampleAssTemplateFileName, StringComparison.OrdinalIgnoreCase);
            bool bIsSample = string.Equals(b, SampleAssTemplateFileName, StringComparison.OrdinalIgnoreCase);
            if (aIsSample && !bIsSample) return -1;
            if (!aIsSample && bIsSample) return 1;
            return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
        });

        return result;
    }

    /// <summary>
    /// Resolves the full file path for a given custom .ass template file name.
    /// </summary>
    public static string? ResolveAssTemplatePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (Path.IsPathRooted(fileName) && File.Exists(fileName)) return fileName;

        string rootCandidate = Path.Combine(GetAppRootDirectory(), fileName);
        if (File.Exists(rootCandidate)) return rootCandidate;

        string baseCandidate = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(baseCandidate)) return baseCandidate;

        return null;
    }

    public static string GetDefaultSampleAssTemplateContent()
    {
        return """
[Script Info]
; ============================================================================
; 灵幕助手 - 自定义 ASS 字幕样式模板文件 (用户字幕样式(示例).ass)
; ============================================================================
; 使用说明：
; 1. 将任意 .ass 字幕文件放置在「灵幕助手.exe」同级文件夹下，即可在软件「设置 -> 3. 特效字幕样式」
;    最下方的「自定义样式配置」下拉框中选择该文件，直接完整覆盖软件内置的样式设置。
; 2. 您可以直接使用记事本修改本文件，也可以使用专业字幕软件 Aegisub 打开本文件进行可视化调色、
;    排版、添加高级特效标签并保存。
; 3. 颜色格式说明 (ASS 标准 &HAABBGGRR 十六进制格式，注意红蓝通道顺序为 BBGGRR)：
;    - AA: 透明度 (00=完全不透明, 40=25%透明, 80=50%透明, FF=完全透明)
;    - BB: 蓝色通道 (00~FF) | GG: 绿色通道 (00~FF) | RR: 红色通道 (00~FF)
;    常用颜色代码参考：
;    * 影视暖金黄: &H0082E0FF   (对应网页色 #FFE082)
;    * 经典柔和白: &H00EDEDED   (对应网页色 #EDEDED)
;    * 纯白:       &H00FFFFFF   (对应网页色 #FFFFFF)
;    * 冰川浅蓝白: &H00F8E8D8   (对应网页色 #D8E8F8)
;    * 纯黑描边:   &H00000000   (对应网页色 #000000)
;    * 半透明黑底: &H66000000   (约60%不透明度的黑色阴影/背景框)
; 4. 背景色块盒子技巧：
;    将下方 Style 行中的 BorderStyle 字段从 1 改为 3，即可将描边模式切换为「不透明/半透明矩形背景底框」！
; ============================================================================
Title: LingMu Caption Custom Style Template
ScriptType: v4.00+
WrapStyle: 0
ScaledBorderAndShadow: yes
YCbCr Matrix: TV.709
PlayResX: 1920
PlayResY: 1080

[V4+ Styles]
; 字段顺序说明：
; Name(样式名), Fontname(字体), Fontsize(字号), PrimaryColour(主体色), SecondaryColour(次要色), OutlineColour(描边色), BackColour(阴影/背景框色), Bold(粗体:-1是/0否), Italic(斜体:-1是/0否), Underline(下划线), StrikeOut(删除线), ScaleX(横向缩放%), ScaleY(纵向缩放%), Spacing(字间距), Angle(旋转角), BorderStyle(边框样式:1=描边+阴影,3=矩形背景色块盒), Outline(描边粗细), Shadow(阴影深度), Alignment(对齐:2=底部居中,8=顶部居中), MarginL(左边距), MarginR(右边距), MarginV(垂直边距), Encoding(编码:1)
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Microsoft YaHei,70,&H0082E0FF,&H000000FF,&H00101820,&H70000000,-1,0,0,0,100,100,1,0,1,2.6,1.8,2,30,30,48,1
Style: Secondary,Microsoft YaHei,48,&H00F5EEE8,&H000000FF,&H00101820,&H70000000,0,0,0,0,100,100,0,0,1,1.8,1.2,2,30,30,28,1
Style: TopDefault,Microsoft YaHei,70,&H0082E0FF,&H000000FF,&H00101820,&H70000000,-1,0,0,0,100,100,1,0,1,2.6,1.8,8,30,30,48,1
Style: TopSecondary,Microsoft YaHei,48,&H00F5EEE8,&H000000FF,&H00101820,&H70000000,0,0,0,0,100,100,0,0,1,1.8,1.2,8,30,30,28,1
Style: Watermark,Microsoft YaHei,26,&H20FFFFFF,&H000000FF,&H30000000,&H60000000,0,0,0,0,100,100,0,0,1,1.2,0.8,8,30,30,24,1

[Events]
; 行内高级特效标签配置说明：
; 灵幕助手会自动提取下方第一条示例 Dialogue 中开头的 {...} 特效标签作为主字幕特效，
; 并提取 \N{...} 中的特效标签作为双语副字幕特效（例如 \fad(180,180) 淡入淡出、\be1 边缘柔化、\blur1.2 高斯模糊等）。
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,{\fad(180,180)\be1}这是生成的主字幕的效果测试\N{\rSecondary\be1}This is the original recognized secondary subtitle
""";
    }
}

