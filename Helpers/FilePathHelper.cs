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
}
