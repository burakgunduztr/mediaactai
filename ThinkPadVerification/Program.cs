// Throwaway ThinkPad verification tests for GUIDE_3.md (MED-12/TMT-8) and
// GUIDE_4.md (MED-14/TMT-10). NOT meant to be permanent or committed to the
// repo -- same discipline as the Mac-side scratch harnesses used throughout
// this project ("the Mac-side validation harness isn't part of the repo --
// scratch, not a deliverable"). Delete this file once every result below is
// recorded in both guides' "Record the results" tables -- it's placed here,
// in the project's existing TempScore.Core.Tests folder, purely so it's a
// real file you can copy straight into the matching ThinkPad folder; it
// cannot build on Mac (no `app`/TempScore.App project exists here to
// reference) and must not be committed.
//
// Prerequisites before running these on the ThinkPad:
//   1. TempScore.Core.Tests must have a ProjectReference to `app`
//      (the TempScore.App project) -- right-click TempScore.Core.Tests in
//      Solution Explorer -> Add -> Project Reference... -> check app.
//   2. For FrameExtractor_RealWindows_MatchesExpected: ffmpeg/ffprobe on PATH,
//      and a real copy of samples/action.mp4 -- fix RepoRoot below.
//   3. For MusicClient_RealServer_HealthyAndGenerates: music_service running
//      in its own terminal first (cd music_service, .venv\Scripts\activate,
//      python server.py) -- per installForWindows.md Step 3.
//   4. For FrameDescriber_RealWindows_20Frames_WithinBudget: Foundry Local
//      running with the chosen vision model loaded (qwen3.5-2b, confirmed
//      working per GUIDE_4.md) -- fix FoundryPort below from
//      `foundry server status`.
//
// Run via Test -> Test Explorer after building the project -- all three
// show up as discovered [Fact]s under ThinkPadVerification.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TempScore.App.Services;
using TempScore.Core.Models;
using Xunit;

public class ThinkPadVerification
{
    // ------------------------------------------------------------------
    // FIX THESE THREE BEFORE RUNNING -- every placeholder below reads from
    // these, so there's only one place to edit per machine/run.
    // ------------------------------------------------------------------
    private const string RepoRoot = @"C:\path\to\MediaActAI";       // your actual clone path
    private const string FoundryPort = "56343";                     // from `foundry server status`
    private const string VisionModelAlias = "qwen3.5-2b-generic-cpu";           // whatever GUIDE_4.md settled on

    private static string SamplesClip(string name) => Path.Combine(RepoRoot, "samples", name);

    // ------------------------------------------------------------------
    // GUIDE_3.md (MED-12/TMT-8) -- FrameExtractor against a real sample clip.
    // Expected values are TMT-2's recorded numbers for action.mp4, re-checked
    // on real Windows Process calls, not assumed from the Mac run.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FrameExtractor_RealWindows_MatchesExpected()
    {
        var extractor = new FrameExtractor();
        var result = await extractor.ExtractAsync(
            SamplesClip("action.mp4"),
            Path.Combine(Path.GetTempPath(), "tmt8_frames_action"));

        Assert.Equal(20, result.Frames.Count);
        Assert.Equal(4, result.CutTimes.Count);
        Assert.Equal(60.0, result.DurationSeconds);
        Assert.True(result.HasAudioTrack);
    }

    // ------------------------------------------------------------------
    // GUIDE_3.md (MED-12/TMT-8) -- MusicClient against the real running
    // music_service. Start music_service manually first -- see prereqs above.
    // ------------------------------------------------------------------
    [Fact]
    public async Task MusicClient_RealServer_HealthyAndGenerates()
    {
        using var client = new MusicClient();

        var healthy = await client.IsHealthyAsync();
        Assert.True(healthy, "music_service not reachable at http://127.0.0.1:8765 -- is it running?");

        var runsDir = Path.Combine(
            Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(),
            "TempScore", "runs");
        Directory.CreateDirectory(runsDir);
        var outPath = Path.Combine(runsDir, "thinkpad_verify.wav");

        var segments = new List<Segment> { new(Start: 0, End: 5, Intensity: 3, Prompt: "soft piano") };
        var result = await client.GenerateAsync(segments, outPath);

        Assert.True(File.Exists(result.Path));
        Assert.True(result.Seconds > 0);
    }

    // ------------------------------------------------------------------
    // GUIDE_4.md (MED-14/TMT-10) -- FrameDescriber timing against all 20
    // frames of a real extraction, compared to the ~2s/frame budget.
    // Needs Foundry Local running with VisionModelAlias already loaded.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FrameDescriber_RealWindows_20Frames_WithinBudget()
    {
        var extractor = new FrameExtractor();
        var result = await extractor.ExtractAsync(
            SamplesClip("action.mp4"),
            Path.Combine(Path.GetTempPath(), "tmt10_frames_action"));

        using var describer = new FoundryLocalImageDescriber(
            baseAddress: $"http://127.0.0.1:{FoundryPort}",
            aliasPrefix: VisionModelAlias);
        var frameDescriber = new FrameDescriber(describer);

        var sw = Stopwatch.StartNew();
        var described = await frameDescriber.DescribeAllAsync(result.Frames);  // all 20, not Take(3)
        sw.Stop();

        var perFrame = sw.Elapsed.TotalSeconds / described.Count;
        foreach (var f in described)
        {
            Console.WriteLine($"  t={f.Timestamp}s: \"{f.Description}\"");
        }
        Console.WriteLine($"{described.Count} frames, {sw.Elapsed.TotalSeconds:F1}s total, {perFrame:F2}s/frame");

        Assert.True(perFrame < 2.0, $"got {perFrame:F2}s/frame, budget is ~2s/frame");
    }
}
