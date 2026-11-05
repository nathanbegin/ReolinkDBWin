using System.Globalization;
using System.Text.Json.Nodes;
using ReolinkDBWin.Core.Models;

namespace ReolinkDBWin.Core.Frigate;

/// <summary>Événement Frigate (/api/events).</summary>
public sealed record FrigateEvent
{
    public required string Id { get; init; }
    public required string Camera { get; init; }
    public required string Label { get; init; }
    public string? SubLabel { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public double? Score { get; init; }
    public bool HasClip { get; init; }
    public bool HasSnapshot { get; init; }
    public IReadOnlyList<string> Zones { get; init; } = [];

    public bool InProgress => EndTime is null;
    public TimeSpan? Duration => EndTime - StartTime;
    public DetectionType Type => DetectionTypeInfo.FromLabel(Label);

    public static IReadOnlyList<FrigateEvent> ParseList(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is not JsonArray arr) return [];
        var list = new List<FrigateEvent>(arr.Count);
        foreach (var item in arr.OfType<JsonObject>())
        {
            var e = Parse(item);
            if (e is not null) list.Add(e);
        }
        return list;
    }

    public static FrigateEvent? Parse(JsonObject o)
    {
        var id = o["id"]?.ToString();
        if (string.IsNullOrEmpty(id)) return null;
        var start = ReadDouble(o["start_time"]);
        var end = ReadDouble(o["end_time"]);
        double? score = ReadDouble(o["data"]?["top_score"]) ?? ReadDouble(o["top_score"]) ?? ReadDouble(o["data"]?["score"]) ?? ReadDouble(o["score"]);

        return new FrigateEvent
        {
            Id = id,
            Camera = o["camera"]?.ToString() ?? "",
            Label = o["label"]?.ToString() ?? "",
            SubLabel = ReadSubLabel(o["sub_label"]),
            StartTime = start is null ? DateTimeOffset.MinValue : FromUnix(start.Value),
            EndTime = end is null ? null : FromUnix(end.Value),
            Score = score,
            HasClip = ReadBool(o["has_clip"]),
            HasSnapshot = ReadBool(o["has_snapshot"]),
            Zones = o["zones"] is JsonArray z ? z.Select(x => x?.ToString()).OfType<string>().ToList() : [],
        };
    }

    private static DateTimeOffset FromUnix(double seconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000)).ToLocalTime();

    // Frigate 0.13+ : sub_label peut être ["nom", score] ; avant : chaîne.
    private static string? ReadSubLabel(JsonNode? n) => n switch
    {
        null => null,
        JsonArray a when a.Count > 0 => a[0]?.ToString(),
        JsonValue v when v.TryGetValue(out string? s) => string.IsNullOrWhiteSpace(s) ? null : s,
        _ => null,
    };

    private static double? ReadDouble(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    private static bool ReadBool(JsonNode? n)
    {
        if (n is not JsonValue v) return false;
        if (v.TryGetValue(out bool b)) return b;
        if (v.TryGetValue(out int i)) return i != 0;
        return false;
    }
}
