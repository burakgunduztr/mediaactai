// TMT-8: HTTP client for music_service's /health and /generate, per the
// frozen contract from TMT-7 (notes/TempScore-Implementation-Guide.md Step 7).
// Only {start, end, prompt} cross the wire — TempScore.Core.Models.Segment
// also carries Intensity, which is internal to the pipeline's plan, not part
// of this HTTP contract, so it's deliberately not serialized here rather than
// relying on the server silently ignoring an extra field.
//
// No WinUI/Windows App SDK types used — plain HttpClient, builds/runs on any
// platform. Tested against the real music_service server (MED-13's
// MusicGenEngine, not a mock) before this repo had a WinUI project to put it
// in. See notes/GUIDE_3.md for what's still open on the ThinkPad.

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TempScore.Core.Models;

namespace TempScore.App.Services;

public sealed record GenerateResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("seconds")] double Seconds);

public sealed class MusicClient : IDisposable
{
    private readonly HttpClient _http;

    public MusicClient(string baseAddress = "http://127.0.0.1:8765")
    {
        // /generate renders real audio for a whole plan's worth of segments, not just
        // one short clip — found as a real hang risk under MED-17/TMT-13, which is the
        // first caller to run this against a full multi-segment plan rather than TMT-8's
        // single short test segment. HttpClient's 100s default isn't enough; 10 minutes
        // is a generous ceiling that still eventually fails instead of hanging forever.
        _http = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            var health = await _http.GetFromJsonAsync<HealthResponse>("/health");
            return health?.Ok == true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<GenerateResult> GenerateAsync(IReadOnlyList<Segment> segments, string outPath)
    {
        var request = new GenerateRequest(
            Segments: segments.Select(s => new GenerateSegment(s.Start, s.End, s.Prompt)).ToList(),
            OutPath: outPath);

        using var response = await _http.PostAsJsonAsync("/generate", request);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"/generate returned {(int)response.StatusCode}: {detail}");
        }

        return await response.Content.ReadFromJsonAsync<GenerateResult>()
            ?? throw new InvalidOperationException("/generate returned an empty body");
    }

    public void Dispose() => _http.Dispose();

    private sealed record GenerateSegment(
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("prompt")] string Prompt);

    private sealed record GenerateRequest(
        [property: JsonPropertyName("segments")] List<GenerateSegment> Segments,
        [property: JsonPropertyName("out_path")] string OutPath);

    private sealed record HealthResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("engine")] string? Engine);
}
