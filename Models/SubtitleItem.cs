using System;
using System.Text.Json.Serialization;

namespace SubtitleMaster.Models;

public class SubtitleItem
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("start")]
    public string Start { get; set; } = string.Empty;

    [JsonPropertyName("end")]
    public string End { get; set; } = string.Empty;

    [JsonPropertyName("source_text")]
    public string SourceText { get; set; } = string.Empty;

    [JsonPropertyName("target_text")]
    public string TargetText { get; set; } = string.Empty;

    [JsonIgnore]
    public TimeSpan StartTime { get; set; }

    [JsonIgnore]
    public TimeSpan EndTime { get; set; }
}
