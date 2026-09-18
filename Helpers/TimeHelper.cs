using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SubtitleMaster.Helpers;

public static class TimeHelper
{
    private static readonly Regex TimeRegex = new(
        @"(?:(?:(?<hours>\d{1,2}):)?(?<minutes>\d{1,2}):)?(?<seconds>\d{1,2})(?:[\.,](?<millis>\d{1,3}))?",
        RegexOptions.Compiled);

    public static TimeSpan ParseTime(string raw) => ParseTimestamp(raw);

    public static TimeSpan ParseTimestamp(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return TimeSpan.Zero;

        raw = raw.Trim();

        // Check if raw is a pure number (seconds or milliseconds)
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double numSecs))
        {
            if (numSecs > 100000) // likely milliseconds
                return TimeSpan.FromMilliseconds(numSecs);
            return TimeSpan.FromSeconds(numSecs);
        }

        var match = TimeRegex.Match(raw);
        if (!match.Success)
            return TimeSpan.Zero;

        int hours = match.Groups["hours"].Success ? int.Parse(match.Groups["hours"].Value) : 0;
        int minutes = match.Groups["minutes"].Success ? int.Parse(match.Groups["minutes"].Value) : 0;
        int seconds = match.Groups["seconds"].Success ? int.Parse(match.Groups["seconds"].Value) : 0;
        int millis = 0;

        if (match.Groups["millis"].Success)
        {
            string mStr = match.Groups["millis"].Value.PadRight(3, '0');
            if (mStr.Length > 3) mStr = mStr.Substring(0, 3);
            millis = int.Parse(mStr);
        }

        return new TimeSpan(0, hours, minutes, seconds, millis);
    }

    /// <summary>
    /// Formats TimeSpan to standard SRT format: 00:00:00,000
    /// </summary>
    public static string ToSrtTime(TimeSpan time)
    {
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
    }

    /// <summary>
    /// Formats TimeSpan to standard ASS format: 0:00:00.00 (centiseconds)
    /// </summary>
    public static string ToAssTime(TimeSpan time)
    {
        int centis = (int)Math.Floor(time.Milliseconds / 10.0);
        return $"{(int)time.TotalHours:0}:{time.Minutes:00}:{time.Seconds:00}.{centis:00}";
    }

    public static string FormatDuration(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
        return $"{time.Minutes:00}:{time.Seconds:00}";
    }
}
