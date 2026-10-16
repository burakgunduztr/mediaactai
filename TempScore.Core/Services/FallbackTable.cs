using TempScore.Core.Models;

namespace TempScore.Core.Services;

/// <summary>
/// The rule-based mood-to-prompt fallback from TempScore-Implementation-Guide.md, used when Phi Silica's
/// JSON is unusable after one retry. Must never hard-stop the pipeline — every lookup here is null-safe,
/// since the scene data this reads from can itself be the product of a partially-malformed parse.
/// </summary>
public static class FallbackTable
{
    private const string DefaultPrompt = "soft piano and pads";
    private const int MinIntensity = 1;
    private const int MaxIntensity = 5;

    private static readonly Dictionary<string, string> MoodToPrompt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["calm"] = "soft piano and pads",
        ["tense"] = "low strings ostinato, pulsing synth",
        ["happy"] = "bright acoustic guitar and light percussion",
        ["action"] = "driving drums, brass hits",
    };

    public static string PromptFor(string? mood) =>
        !string.IsNullOrWhiteSpace(mood) && MoodToPrompt.TryGetValue(mood, out var prompt) ? prompt : DefaultPrompt;

    /// <summary>
    /// Builds a plan directly from a scene summary, skipping the Phi Silica music-plan call
    /// entirely, then routes it through <see cref="MusicPlanValidation.Validate"/> — the same
    /// gate a real plan goes through — so the result has exactly the shape every downstream
    /// stage (music_service, Muxer, UI) already expects, including the restated prompts.
    /// </summary>
    public static MusicPlan BuildFallbackPlan(
        SceneSummary? scenes,
        double clipDurationSeconds,
        string style = "cinematic orchestral, piano lead",
        int bpm = MusicPlanValidation.DefaultBpm,
        string key = "A minor")
    {
        var sceneList = scenes?.Scenes ?? [];
        var segments = sceneList
            .Select(scene => new Segment(
                scene.Start,
                scene.End,
                Math.Clamp(scene.Energy, MinIntensity, MaxIntensity),
                PromptFor(scene.Mood)))
            .ToList();

        var plan = new MusicPlan(style, bpm, key, segments);
        return MusicPlanValidation.Validate(plan, clipDurationSeconds);
    }
}
