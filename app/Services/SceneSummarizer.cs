// TMT-11: scene summarization. Phi Silica returned NotSupportedOnCurrentSystem
// on the real ThinkPad, so this goes through Foundry Local's phi-4-mini instead
// — see notes/switchingToFoundryLocal.md. ITextGenerator is the seam for a
// future LanguageModel-based implementation if that path ever starts working;
// not written here since it needs real Windows AI APIs this repo can't build
// against on Mac.
//
// No WinUI/Windows App SDK types used — plain HttpClient, so this was written
// and tested for real against the actual Foundry Local server and real frame
// descriptions from TMT-10, before this repo had a WinUI project to put it in.
// See notes/GUIDE_5.md for what's still open on the ThinkPad.

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TempScore.Core.Models;
using TempScore.Core.Services;

namespace TempScore.App.Services;

public interface ITextGenerator
{
    Task<string> GenerateAsync(string prompt);
}

/// <summary>
/// Calls Foundry Local's local OpenAI-compatible chat endpoint. Shared by
/// both SceneSummarizer and MusicPlanner (TMT-11) — one instance, reused
/// across both calls; the resolved model id is cached after the first call.
/// </summary>
public sealed class FoundryLocalTextGenerator : ITextGenerator, IDisposable
{
    // Confirmed working with phi-4-mini on Mac in PR #7's foundry_local/smoke_test.py.
    private const string SystemPrompt = "You output only valid JSON, no prose, no markdown fences.";

    private readonly HttpClient _http;
    private readonly string _aliasPrefix;
    private string? _resolvedModelId;

    /// <param name="aliasPrefix">
    /// The CLI alias used by `foundry model load` (e.g. "phi-4-mini"). The
    /// chat endpoint needs the exact catalog model id, not this alias —
    /// resolved from /v1/models at first use, not hardcoded, since the
    /// version suffix (e.g. ":5") can change between catalog updates.
    /// </param>
    public FoundryLocalTextGenerator(string baseAddress = "http://127.0.0.1:56343", string aliasPrefix = "phi-4-mini")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseAddress) };
        _aliasPrefix = aliasPrefix;
    }

    public async Task<string> GenerateAsync(string prompt)
    {
        var modelId = await ResolveModelIdAsync();
        var request = new ChatRequest(
            Model: modelId,
            Messages:
            [
                new ChatMessage("system", SystemPrompt),
                new ChatMessage("user", prompt),
            ],
            Temperature: 0.2,
            MaxTokens: 600);

        using var response = await _http.PostAsJsonAsync("/v1/chat/completions", request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>();
        return body?.Choices.FirstOrDefault()?.Message.Content?.Trim() ?? "";
    }

    private async Task<string> ResolveModelIdAsync()
    {
        if (_resolvedModelId is not null)
        {
            return _resolvedModelId;
        }

        var models = await _http.GetFromJsonAsync<ModelsResponse>("/v1/models");
        var match = models?.Data.FirstOrDefault(m => m.Id.StartsWith(_aliasPrefix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No loaded model id starts with '{_aliasPrefix}' — run `foundry model load {_aliasPrefix}` first.");
        _resolvedModelId = match.Id;
        return _resolvedModelId;
    }

    public void Dispose() => _http.Dispose();

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens);

    private sealed record ChatResponseMessage([property: JsonPropertyName("content")] string? Content);

    private sealed record ChatChoice([property: JsonPropertyName("message")] ChatResponseMessage Message);

    private sealed record ChatResponse([property: JsonPropertyName("choices")] List<ChatChoice> Choices);

    private sealed record ModelEntry([property: JsonPropertyName("id")] string Id);

    private sealed record ModelsResponse([property: JsonPropertyName("data")] List<ModelEntry> Data);
}

/// <summary>
/// Groups timed frame descriptions and cut times into 3-6 scenes via one
/// Phi-silica-shaped call (now Foundry Local), per
/// notes/TempScore-Implementation-Guide.md Step 5. Retries once on bad
/// JSON using TMT-1's JsonParsing.ParseJson, then falls back to one scene
/// per cut segment with the frame descriptions joined — "the demo must
/// never stop at this step."
/// </summary>
public sealed class SceneSummarizer
{
    private readonly ITextGenerator _textGenerator;

    public SceneSummarizer(ITextGenerator textGenerator) => _textGenerator = textGenerator;

    public async Task<SceneSummary> SummarizeAsync(List<Frame> frames, List<double> cutTimes, double durationSeconds)
    {
        var prompt = BuildPrompt(frames, cutTimes, durationSeconds);

        var response = await _textGenerator.GenerateAsync(prompt);
        var summary = JsonParsing.ParseJson<SceneSummary>(response);

        if (summary is null)
        {
            var retryResponse = await _textGenerator.GenerateAsync(
                prompt + "\n\nYour last answer was not valid JSON. Return only the JSON.");
            summary = JsonParsing.ParseJson<SceneSummary>(retryResponse);
        }

        return summary ?? BuildFallbackSummary(frames, cutTimes, durationSeconds);
    }

    private static string BuildPrompt(List<Frame> frames, List<double> cutTimes, double durationSeconds)
    {
        var cuts = string.Join(", ", cutTimes.Select(c => c.ToString("0.0", CultureInfo.InvariantCulture)));
        var frameLines = string.Join("\n", frames.Select(f => $"{(int)f.Timestamp}s: {f.Description}"));

        return
            $"You are a film editor. Below are frame descriptions of a {durationSeconds:0}-second video,\n" +
            "with timestamps in seconds, and the times of hard cuts.\n" +
            "Group them into 3 to 6 scenes. Return ONLY JSON in this shape:\n" +
            "{\"scenes\":[{\"start\":0,\"end\":12,\"summary\":\"...\",\"mood\":\"calm\",\"energy\":2}]}\n" +
            "mood is one word. energy is 1 (still) to 5 (intense).\n\n" +
            $"CUTS: {cuts}\n" +
            "FRAMES:\n" +
            frameLines;
    }

    /// <summary>
    /// "fall back to one scene per cut segment with the frame descriptions
    /// joined" — one scene between each consecutive cut (plus clip start/
    /// end), mood/energy defaulted since there's no model output to read
    /// them from. Never throws: cutTimes/frames can themselves be the
    /// product of a partially-malformed upstream step.
    /// </summary>
    private static SceneSummary BuildFallbackSummary(List<Frame> frames, List<double> cutTimes, double durationSeconds)
    {
        var boundaries = new List<double> { 0 };
        boundaries.AddRange(cutTimes.Where(c => c > 0 && c < durationSeconds));
        boundaries.Add(durationSeconds);
        boundaries = boundaries.Distinct().OrderBy(b => b).ToList();

        var scenes = new List<Scene>();
        for (int i = 0; i < boundaries.Count - 1; i++)
        {
            double start = boundaries[i], end = boundaries[i + 1];
            if (end <= start)
            {
                continue;
            }

            var descriptions = frames
                .Where(f => f.Timestamp >= start && f.Timestamp < end)
                .Select(f => f.Description)
                .Where(d => !string.IsNullOrWhiteSpace(d));
            var summary = string.Join(" ", descriptions);

            scenes.Add(new Scene(start, end, summary.Length == 0 ? "no description available" : summary, "calm", 3));
        }

        if (scenes.Count == 0)
        {
            scenes.Add(new Scene(0, durationSeconds, "no description available", "calm", 3));
        }

        return new SceneSummary(scenes);
    }
}
