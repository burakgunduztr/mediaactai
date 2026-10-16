using System.Text.Json;

namespace TempScore.Core.Services;

/// <summary>Defensive JSON parsing for small-model output that sometimes wraps JSON in extra text.</summary>
public static class JsonParsing
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Extracts the first-'{'-to-last-'}' span and deserializes it. Returns default on any failure.</summary>
    public static T? ParseJson<T>(string text)
    {
        int a = text.IndexOf('{'), b = text.LastIndexOf('}');
        if (a < 0 || b <= a) return default;
        try { return JsonSerializer.Deserialize<T>(text[a..(b + 1)], Options); }
        catch (JsonException) { return default; }
    }
}
