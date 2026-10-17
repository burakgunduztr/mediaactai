// TMT-11: music plan generation. Same Foundry Local pivot as
// SceneSummarizer.cs — see notes/switchingToFoundryLocal.md and
// notes/GUIDE_5.md for what's still open on the ThinkPad.

using System.Text.Json;
using TempScore.Core.Models;
using TempScore.Core.Services;

namespace TempScore.App.Services;

/// <summary>
/// Turns a SceneSummary into a validated MusicPlan via one Phi-silica-shaped
/// call (now Foundry Local), per notes/TempScore-Implementation-Guide.md
/// Step 6. Retries once on bad JSON, then falls back to TMT-1's
/// FallbackTable. Every path — real or fallback — goes through
/// MusicPlanValidation.Validate before returning, since that's the single
/// gate every downstream stage (music_service, Muxer, UI) depends on.
/// </summary>
public sealed class MusicPlanner
{
    // camelCase == lowercase for these single-word property names, matching
    // the guide's own JSON example exactly — JsonParsing.Options alone (used
    // for parsing model output) has no naming policy set for *writing*, so
    // embedding scenes with it would send PascalCase keys back to the model.
    private static readonly JsonSerializerOptions PromptJsonOptions = new(JsonParsing.Options)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ITextGenerator _textGenerator;

    public MusicPlanner(ITextGenerator textGenerator) => _textGenerator = textGenerator;

    public async Task<MusicPlan> PlanAsync(SceneSummary scenes, double durationSeconds)
    {
        var prompt = BuildPrompt(scenes);

        var response = await _textGenerator.GenerateAsync(prompt);
        var plan = JsonParsing.ParseJson<MusicPlan>(response);

        if (plan is null)
        {
            var retryResponse = await _textGenerator.GenerateAsync(
                prompt + "\n\nYour last answer was not valid JSON. Return only the JSON.");
            plan = JsonParsing.ParseJson<MusicPlan>(retryResponse);
        }

        return plan is not null
            ? MusicPlanValidation.Validate(plan, durationSeconds)
            : FallbackTable.BuildFallbackPlan(scenes, durationSeconds);
    }

    private static string BuildPrompt(SceneSummary scenes)
    {
        var scenesJson = JsonSerializer.Serialize(scenes, PromptJsonOptions);

        return
            "You are a film composer. Write a music plan for these scenes.\n" +
            "Keep ONE style for the whole piece, and change only intensity and texture.\n" +
            "No segment longer than 20 seconds. Segments must cover the full clip duration.\n" +
            "Return ONLY JSON:\n" +
            "{\"style\":\"cinematic orchestral, piano lead\",\"bpm\":72,\"key\":\"A minor\",\n" +
            " \"segments\":[{\"start\":0,\"end\":12,\"intensity\":2,\n" +
            "   \"prompt\":\"soft solo piano, rain ambience, melancholic, 72 bpm, A minor\"}]}\n\n" +
            $"SCENES: {scenesJson}";
    }
}
