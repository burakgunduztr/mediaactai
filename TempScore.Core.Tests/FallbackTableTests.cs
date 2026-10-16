using TempScore.Core.Models;
using TempScore.Core.Services;

namespace TempScore.Core.Tests;

public class FallbackTableTests
{
    [Theory]
    [InlineData("calm", "soft piano and pads")]
    [InlineData("tense", "low strings ostinato, pulsing synth")]
    [InlineData("happy", "bright acoustic guitar and light percussion")]
    [InlineData("action", "driving drums, brass hits")]
    [InlineData("CALM", "soft piano and pads")] // case-insensitive
    public void PromptFor_KnownMood_ReturnsMappedPrompt(string mood, string expected)
    {
        Assert.Equal(expected, FallbackTable.PromptFor(mood));
    }

    [Fact]
    public void PromptFor_UnknownMood_ReturnsDefault()
    {
        Assert.Equal("soft piano and pads", FallbackTable.PromptFor("melancholic"));
    }

    [Fact]
    public void PromptFor_NullMood_ReturnsDefault_DoesNotThrow()
    {
        // A partially-malformed scene (missing "mood") must not crash the very fallback
        // path that's supposed to be the pipeline's safety net.
        Assert.Equal("soft piano and pads", FallbackTable.PromptFor(null));
    }

    [Fact]
    public void BuildFallbackPlan_ProducesValidMusicPlanShape()
    {
        var scenes = new SceneSummary([
            new Scene(0, 20, "woman at bus stop", "calm", 2),
            new Scene(20, 60, "car chase", "action", 5),
        ]);

        var plan = FallbackTable.BuildFallbackPlan(scenes, clipDurationSeconds: 60);

        // Must be consumable by every downstream stage exactly like a real Phi Silica plan:
        // valid BPM, no gaps, covers the full clip, every prompt restates style/bpm/key
        // at the front (per the guide), not merely contains them somewhere.
        Assert.InRange(plan.Bpm, MusicPlanValidation.MinBpm, MusicPlanValidation.MaxBpm);
        Assert.Equal(0, plan.Segments[0].Start);
        Assert.Equal(60, plan.Segments[^1].End);
        string expectedPrefix = $"{plan.Style}, {plan.Bpm} bpm, {plan.Key}";
        Assert.All(plan.Segments, s => Assert.StartsWith(expectedPrefix, s.Prompt));
        Assert.Contains(FallbackTable.PromptFor("action"), plan.Segments[^1].Prompt);
    }

    [Fact]
    public void BuildFallbackPlan_ClampsIntensityToValidRange()
    {
        // Scene.Energy comes from the same potentially-malformed source as everything
        // else here; a garbage value (0 or 99) must not propagate into Segment.Intensity.
        var scenes = new SceneSummary([
            new Scene(0, 30, "a", "calm", 0),
            new Scene(30, 60, "b", "action", 99),
        ]);

        var plan = FallbackTable.BuildFallbackPlan(scenes, clipDurationSeconds: 60);

        Assert.All(plan.Segments, s => Assert.InRange(s.Intensity, 1, 5));
    }

    [Fact]
    public void BuildFallbackPlan_NullScenes_DoesNotThrow_ProducesFullCoverage()
    {
        var plan = FallbackTable.BuildFallbackPlan(null, clipDurationSeconds: 60);

        Assert.Equal(0, plan.Segments[0].Start);
        Assert.Equal(60, plan.Segments[^1].End);
    }
}
