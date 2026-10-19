// TMT-10: frame description. The NPU ImageDescriptionGenerator path
// returned NotSupportedOnCurrentSystem on the real ThinkPad, so this goes
// through Foundry Local's vision-capable model instead — see
// notes/switchingToFoundryLocal.md and notes/GUIDE_foundrylocalsetup.md
// for the pivot and the open questions this still carries (exact model
// choice on the ThinkPad, content-safety equivalent).
//
// No WinUI/Windows App SDK types used — plain HttpClient/File I/O, so this
// was written and tested for real against the actual Foundry Local server
// and a real extracted frame on Mac, before this repo had a WinUI project
// to put it in. See notes/GUIDE_4.md for what's still open on the ThinkPad.
//
// IImageDescriber is the seam for the "kept as the unused alternate
// implementation" ImageDescriptionGenerator path this ticket's description
// mentions — if Phi Silica/Image Description ever starts reporting Ready
// after further troubleshooting (notes/enableWindowsAIAPI.md), a
// WindowsAiImageDescriber : IImageDescriber can be added without touching
// FrameDescriber or this interface. Not written here: it needs the real
// Windows AI APIs to write against, which don't exist on Mac.

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TempScore.Core.Models;

namespace TempScore.App.Services;

public interface IImageDescriber
{
    Task<string> DescribeAsync(string framePath);
}

/// <summary>
/// Describes one frame at a time by calling Foundry Local's local
/// OpenAI-compatible vision endpoint. Reused across the whole run — the
/// resolved model id is cached after the first call, matching this
/// ticket's "reuse one client across the whole run" requirement.
/// </summary>
public sealed class FoundryLocalImageDescriber : IImageDescriber, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _aliasPrefix;
    private string? _resolvedModelId;

    /// <param name="aliasPrefix">
    /// The CLI alias used by `foundry model load` (e.g. "qwen3.5-2b",
    /// confirmed working on Mac). The chat endpoint needs the exact catalog
    /// model id, not this alias — same gotcha as MED-9/MED-15's text path —
    /// so this is resolved from /v1/models at first use, not hardcoded.
    /// The ThinkPad may end up loading a different (GPU-targeted) model from
    /// its own catalog; pass that alias here instead of changing this file.
    /// </param>
    public FoundryLocalImageDescriber(
        string baseAddress = "http://127.0.0.1:53430", 
        string aliasPrefix = "qwen3.5-2b-generic-cpu")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseAddress) };
        _aliasPrefix = aliasPrefix;
    }

    public async Task<string> DescribeAsync(string framePath)
    {
        var modelId = await ResolveModelIdAsync();
        var bytes = await File.ReadAllBytesAsync(framePath);
        var base64 = Convert.ToBase64String(bytes);
        var mime = Path.GetExtension(framePath).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";

        var request = new ChatRequest(
            Model: modelId,
            Messages:
            [
                new ChatMessage(
                    Role: "user",
                    Content:
                    [
                        new TextPart("Describe this video frame in one short sentence, under 40 words."),
                        new ImageUrlPart($"data:{mime};base64,{base64}"),
                    ])
            ],
            Temperature: 0.2,
            MaxTokens: 2048);

        using var response = await _http.PostAsJsonAsync("/v1/chat/completions", request);
        // .EnsureSuccessStatusCode();
        if (!response.IsSuccessStatusCode) 
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Foundry {(int)response.StatusCode}: {err}");
        }
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>();
        var text = body?.Choices.FirstOrDefault()?.Message.Content?.Trim() ?? "";
        Debug.WriteLine($"FRAME {Path.GetFileName(framePath)}: {text}");
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

    private sealed record TextPart([property: JsonPropertyName("text")] string Text)
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "text";
    }

    private sealed record ImageUrlPart([property: JsonPropertyName("image_url")] ImageUrlValue ImageUrl)
    {
        public ImageUrlPart(string dataUri) : this(new ImageUrlValue(dataUri)) { }

        [JsonPropertyName("type")]
        public string Type { get; } = "image_url";
    }

    private sealed record ImageUrlValue([property: JsonPropertyName("url")] string Url);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] List<object> Content);

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
/// Orchestrates description across every frame, shortening each result to
/// ~40 words regardless of which IImageDescriber backs it.
/// </summary>
public sealed class FrameDescriber
{
    private readonly IImageDescriber _describer;

    public FrameDescriber(IImageDescriber describer) => _describer = describer;

    public async Task<List<Frame>> DescribeAllAsync(List<Frame> frames)
    {
        var described = new List<Frame>(frames.Count);
        foreach (var frame in frames)
        {
            string raw;
            try
            {
                raw = await _describer.DescribeAsync(frame.Path);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                // MED-19/TMT-15 review finding (architect): a transport failure on one
                // frame (Foundry Local hiccup/unloaded mid-run) previously killed the
                // whole pipeline here, even though SceneSummarizer's own fallback already
                // copes with empty descriptions (BuildFallbackSummary joins whatever
                // non-empty descriptions exist, defaulting to "no description available"
                // if none do). Degrading one frame to "" is strictly better than stopping
                // the run over it.
                raw = "";
            }
            described.Add(frame with { Description = Truncate(raw, maxWords: 40) });
        }
        return described;
    }

    private static string Truncate(string text, int maxWords)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= maxWords ? text.Trim() : string.Join(' ', words.Take(maxWords)) + "...";
    }
}
