using TempScore.Core.Models;
using TempScore.Core.Services;

namespace TempScore.Core.Tests;

public class JsonParsingTests
{
    [Fact]
    public void WellFormedJson_Parses()
    {
        var plan = JsonParsing.ParseJson<MusicPlan>(
            """{"style":"cinematic","bpm":90,"key":"A minor","segments":[{"start":0,"end":12,"intensity":2,"prompt":"soft piano"}]}""");

        Assert.NotNull(plan);
        Assert.Equal("cinematic", plan!.Style);
        Assert.Equal(90, plan.Bpm);
        Assert.Single(plan.Segments);
    }

    [Fact]
    public void TextWrappedJson_Parses()
    {
        var plan = JsonParsing.ParseJson<MusicPlan>(
            """Sure, here is the plan: {"style":"cinematic","bpm":72,"key":"A minor","segments":[]} Hope that helps!""");

        Assert.NotNull(plan);
        Assert.Equal(72, plan!.Bpm);
    }

    [Fact]
    public void TruncatedJson_ReturnsDefault()
    {
        var plan = JsonParsing.ParseJson<MusicPlan>("""{"style":"cinematic","bpm":72,"key":"A minor","segments":[""");

        Assert.Null(plan);
    }

    [Fact]
    public void MissingBraces_ReturnsDefault()
    {
        var plan = JsonParsing.ParseJson<MusicPlan>("not json at all");

        Assert.Null(plan);
    }

    [Fact]
    public void TruncatedAfterClosedInnerObject_HitsJsonExceptionPath_ReturnsDefault()
    {
        // Unlike TruncatedJson_ReturnsDefault (no closing '}' at all, caught by the a/b
        // index guard), this has a balanced-looking brace count but invalid trailing
        // content, so it must be the JsonException catch — not the index guard — that
        // returns default.
        var plan = JsonParsing.ParseJson<MusicPlan>(
            """{"style":"cinematic","bpm":72,"key":"A minor","segments":[{"start":0}""");

        Assert.Null(plan);
    }

    [Fact]
    public void MissingSegmentsKey_ParsesWithNullSegments_NotAFailure()
    {
        // A missing key is not malformed JSON — System.Text.Json fills the constructor
        // parameter with null rather than throwing. ParseJson correctly reports this as a
        // "successful" parse; it's MusicPlanValidation's job (tested separately) to treat
        // a null Segments list safely, not JsonParsing's.
        var plan = JsonParsing.ParseJson<MusicPlan>("""{"style":"cinematic","bpm":90,"key":"A minor"}""");

        Assert.NotNull(plan);
        Assert.Null(plan!.Segments);
    }
}
