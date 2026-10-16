using TempScore.Core.Models;
using TempScore.Core.Services;

namespace TempScore.Core.Tests;

public class MusicPlanValidationTests
{
    private static MusicPlan PlanWith(int bpm, List<Segment> segments) =>
        new("cinematic orchestral, piano lead", bpm, "A minor", segments);

    [Fact]
    public void BpmBelowRange_ResetToDefault()
    {
        var plan = PlanWith(30, [new Segment(0, 60, 2, "soft piano")]);

        var result = MusicPlanValidation.Validate(plan, 60);

        Assert.Equal(MusicPlanValidation.DefaultBpm, result.Bpm);
    }

    [Fact]
    public void BpmAboveRange_ResetToDefault()
    {
        var plan = PlanWith(200, [new Segment(0, 60, 2, "soft piano")]);

        var result = MusicPlanValidation.Validate(plan, 60);

        Assert.Equal(MusicPlanValidation.DefaultBpm, result.Bpm);
    }

    [Fact]
    public void BpmInRange_Unchanged()
    {
        var plan = PlanWith(110, [new Segment(0, 60, 2, "soft piano")]);

        var result = MusicPlanValidation.Validate(plan, 60);

        Assert.Equal(110, result.Bpm);
    }

    [Fact]
    public void SegmentGap_IsClosed()
    {
        // 0-10, then a gap, then 15-60 — the second segment's start must snap to 10.
        // The stitched 10-60 span is 50s, so it also gets split by the >20s rule;
        // what must hold is continuity (no gap) and full coverage, not an exact count.
        var plan = PlanWith(90, [
            new Segment(0, 10, 2, "soft piano"),
            new Segment(15, 60, 3, "strings"),
        ]);

        var result = MusicPlanValidation.Validate(plan, 60);

        Assert.Equal(0, result.Segments[0].Start);
        Assert.Equal(60, result.Segments[^1].End);
        for (int i = 1; i < result.Segments.Count; i++)
        {
            Assert.Equal(result.Segments[i - 1].End, result.Segments[i].Start);
        }
        Assert.All(result.Segments, s => Assert.True(s.End - s.Start <= MusicPlanValidation.MaxSegmentLengthSeconds));
    }

    [Fact]
    public void LastSegmentEnd_SnapsToClipDuration()
    {
        var plan = PlanWith(90, [new Segment(0, 55, 2, "soft piano")]);

        var result = MusicPlanValidation.Validate(plan, 60);

        Assert.Equal(60, result.Segments[^1].End);
    }

    [Fact]
    public void SegmentOver20Seconds_IsSplit()
    {
        var plan = PlanWith(90, [new Segment(0, 45, 2, "strings")]);

        var result = MusicPlanValidation.Validate(plan, 45);

        Assert.True(result.Segments.Count > 1);
        Assert.All(result.Segments, s => Assert.True(s.End - s.Start <= MusicPlanValidation.MaxSegmentLengthSeconds));
        // Still covers the full clip with no gaps.
        Assert.Equal(0, result.Segments[0].Start);
        Assert.Equal(45, result.Segments[^1].End);
        for (int i = 1; i < result.Segments.Count; i++)
        {
            Assert.Equal(result.Segments[i - 1].End, result.Segments[i].Start);
        }
    }

    [Fact]
    public void EmptySegments_ProducesFullCoverageWithNoOversizedSegment()
    {
        // A single 0-60 covering segment would itself violate the >20s rule, so it
        // must come out split (3x20s here), while still covering the whole clip.
        var plan = PlanWith(90, []);

        var result = MusicPlanValidation.Validate(plan, 60);

        Assert.Equal(0, result.Segments[0].Start);
        Assert.Equal(60, result.Segments[^1].End);
        Assert.All(result.Segments, s => Assert.True(s.End - s.Start <= MusicPlanValidation.MaxSegmentLengthSeconds));
        for (int i = 1; i < result.Segments.Count; i++)
        {
            Assert.Equal(result.Segments[i - 1].End, result.Segments[i].Start);
        }
    }

    [Fact]
    public void NullSegments_DoesNotThrow_ProducesFullCoverage()
    {
        // System.Text.Json leaves Segments null when the model's JSON omits "segments"
        // entirely — Validate must degrade gracefully, not throw, per the guide's
        // "must never hard-stop" rule.
        var plan = new MusicPlan("cinematic", 90, "A minor", null!);

        var result = MusicPlanValidation.Validate(plan, 60);

        Assert.Equal(0, result.Segments[0].Start);
        Assert.Equal(60, result.Segments[^1].End);
    }

    [Fact]
    public void NullStyleAndKey_ReplacedWithDefaults()
    {
        var plan = new MusicPlan(null!, 90, null!, [new Segment(0, 60, 2, "piano")]);

        var result = MusicPlanValidation.Validate(plan, 60);

        Assert.False(string.IsNullOrWhiteSpace(result.Style));
        Assert.False(string.IsNullOrWhiteSpace(result.Key));
    }

    [Fact]
    public void OverlappingSegments_NeverProduceNegativeLength()
    {
        // A small model can return nested/overlapping segments, e.g. [0,30], [10,20], [20,60] —
        // the middle one is already inside the first. No result segment may have End < Start.
        var plan = PlanWith(90, [
            new Segment(0, 30, 2, "a"),
            new Segment(10, 20, 3, "b"),
            new Segment(20, 60, 4, "c"),
        ]);

        var result = MusicPlanValidation.Validate(plan, 60);

        Assert.All(result.Segments, s => Assert.True(s.End >= s.Start));
        Assert.Equal(0, result.Segments[0].Start);
        Assert.Equal(60, result.Segments[^1].End);
        for (int i = 1; i < result.Segments.Count; i++)
        {
            Assert.Equal(result.Segments[i - 1].End, result.Segments[i].Start);
        }
    }

    [Fact]
    public void PlanLongerThanClip_ClampsToClipDuration_NoNegativeLength()
    {
        // The prompt template hardcodes "0 to 60 seconds," but real clips vary — a plan
        // built for a 60s clip must still degrade safely against a shorter 30s clip.
        var plan = PlanWith(90, [
            new Segment(0, 20, 2, "a"),
            new Segment(20, 40, 3, "b"),
            new Segment(40, 60, 4, "c"),
        ]);

        var result = MusicPlanValidation.Validate(plan, 30);

        Assert.All(result.Segments, s => Assert.True(s.End >= s.Start));
        Assert.Equal(30, result.Segments[^1].End);
    }

    [Fact]
    public void EverySegmentPrompt_RestatesCorrectedStyleBpmKey()
    {
        // BPM 200 is out of range and gets reset to 90 — the prompt text must reflect the
        // corrected BPM, not the original out-of-range value the model returned.
        var plan = PlanWith(200, [
            new Segment(0, 30, 2, "soft piano"),
            new Segment(30, 60, 3, "strings"),
        ]);

        var result = MusicPlanValidation.Validate(plan, 60);

        string expectedPrefix = $"{result.Style}, {result.Bpm} bpm, {result.Key}";
        Assert.All(result.Segments, s => Assert.StartsWith(expectedPrefix, s.Prompt));
        Assert.All(result.Segments, s => Assert.DoesNotContain("200 bpm", s.Prompt));
    }
}
