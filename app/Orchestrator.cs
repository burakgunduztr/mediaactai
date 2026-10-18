// TMT-13: pipeline orchestration. Runs all six stages in order — extract,
// describe, summarize, plan, generate, mux — reporting progress through
// IProgress<StepUpdate> so MainPage.xaml.cs can stay a pure listener per
// this ticket's "no pipeline logic lives in MainPage.xaml.cs" AC. Nothing
// here decides *how* a stage works; it only sequences the six classes
// TMT-8/9/10/11/12 already built and validated independently.
//
// No WinUI/Windows App SDK types used — plain .NET, so this was written
// and run for real end-to-end (all six stages, real Foundry Local models,
// real music_service, real sample clip, real output.mp4) on Mac before
// this repo had a WinUI project to put it or MainPage in. See
// notes/GUIDE_7.md for what's still open on the ThinkPad — mainly
// MainPage.xaml/.xaml.cs themselves, which need the actual WinUI project
// to even compile against.

using System.Text.Json;
using TempScore.App.Services;
using TempScore.Core.Models;
using TempScore.Core.Services;

namespace TempScore.App;

public sealed record StepUpdate(string StepName, int StepIndex, int TotalSteps);

public sealed record OrchestratorRunResult(string OutputVideoPath, string WorkDir);

/// <summary>
/// Sequences the six pipeline stages against one input video. Writes to a
/// fresh <c>&lt;runs-base&gt;\&lt;timestamp&gt;\</c> folder it resolves itself
/// (frames/, descriptions.json, summary.json, plan.json, score.wav,
/// output.mp4) per notes/TempScore-Implementation-Guide.md's run-folder
/// contract — deliberately not a caller-supplied directory, since
/// music_service's /generate independently validates out_path against that
/// same runs base (TEMPSCORE_RUNS_DIR, else %LOCALAPPDATA%\TempScore\runs,
/// else ~/.tempscore/runs — see server.py's RUNS_BASE_DIR) and a mismatched
/// workDir fails with 400 at the generation step, as this file's own
/// end-to-end validation hit before this precedence was added here.
///
/// <paramref name="onPlanReady"/> is the seam for MainPage's "editable
/// plan" panel (Behavior section of MED-17/TMT-13): it receives the
/// validated plan after MusicPlanner and can hand back an edited one
/// before generation proceeds. The AC only requires a single "Score it"
/// press to run all six stages and play the result, so a no-op callback
/// (the default) is enough to satisfy it — MainPage wiring edited values
/// back in is additional, not required here.
/// </summary>
public sealed class Orchestrator
{
    private const int TotalSteps = 6;

    private static readonly JsonSerializerOptions DebugJsonOptions = new() { WriteIndented = true };

    private readonly FrameExtractor _frameExtractor;
    private readonly FrameDescriber _frameDescriber;
    private readonly SceneSummarizer _sceneSummarizer;
    private readonly MusicPlanner _musicPlanner;
    private readonly MusicClient _musicClient;
    private readonly Muxer _muxer;

    public Orchestrator(
        FrameExtractor frameExtractor,
        FrameDescriber frameDescriber,
        SceneSummarizer sceneSummarizer,
        MusicPlanner musicPlanner,
        MusicClient musicClient,
        Muxer muxer)
    {
        _frameExtractor = frameExtractor;
        _frameDescriber = frameDescriber;
        _sceneSummarizer = sceneSummarizer;
        _musicPlanner = musicPlanner;
        _musicClient = musicClient;
        _muxer = muxer;
    }

    public async Task<OrchestratorRunResult> RunAsync(
        string inputVideoPath,
        IProgress<StepUpdate>? progress = null,
        Func<MusicPlan, Task<MusicPlan>>? onPlanReady = null)
    {
        var workDir = Path.Combine(ResolveRunsBaseDir(), DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(workDir);

        progress?.Report(new StepUpdate("Extracting frames", 1, TotalSteps));
        var framesDir = Path.Combine(workDir, "frames");
        var extraction = await _frameExtractor.ExtractAsync(inputVideoPath, framesDir);

        progress?.Report(new StepUpdate("Describing frames", 2, TotalSteps));
        var described = await _frameDescriber.DescribeAllAsync(extraction.Frames);
        await WriteDebugJsonAsync(workDir, "descriptions.json", described);

        progress?.Report(new StepUpdate("Summarizing scenes", 3, TotalSteps));
        var sceneSummary = await _sceneSummarizer.SummarizeAsync(described, extraction.CutTimes, extraction.DurationSeconds);
        await WriteDebugJsonAsync(workDir, "summary.json", sceneSummary);

        progress?.Report(new StepUpdate("Planning music", 4, TotalSteps));
        var plan = await _musicPlanner.PlanAsync(sceneSummary, extraction.DurationSeconds);
        if (onPlanReady is not null)
        {
            // MED-19/TMT-15 review finding (architect): an edited plan must re-pass
            // MusicPlanValidation -- otherwise a hand-edited segment (prompt too long,
            // intensity out of range, a gap introduced by the edit) reaches /generate
            // unchecked and the run 400s instead of degrading. Validate is idempotent
            // (see MusicPlanValidationTests.Validate_IsIdempotent), so re-running it on
            // an already-valid, unedited plan is a safe no-op.
            plan = MusicPlanValidation.Validate(await onPlanReady(plan), extraction.DurationSeconds);
        }
        await WriteDebugJsonAsync(workDir, "plan.json", plan);

        progress?.Report(new StepUpdate("Generating music", 5, TotalSteps));
        var scoreWavPath = Path.Combine(workDir, "score.wav");
        await _musicClient.GenerateAsync(plan.Segments, scoreWavPath);

        progress?.Report(new StepUpdate("Muxing output", 6, TotalSteps));
        var outputPath = Path.Combine(workDir, "output.mp4");
        // Mux the path this run itself built and validated (scoreWavPath), not
        // generated.Path from the HTTP response (MED-19/TMT-15 security finding) --
        // whatever answers on music_service's port should never be able to hand the
        // app an arbitrary path/URL for ffmpeg to open.
        await _muxer.MuxAsync(inputVideoPath, scoreWavPath, outputPath, extraction.HasAudioTrack);

        return new OrchestratorRunResult(outputPath, workDir);
    }

    /// <summary>
    /// Same precedence as music_service's RUNS_BASE_DIR (server.py) so a
    /// run written here is always a valid /generate out_path target without
    /// either side needing to know about the other's resolution.
    /// </summary>
    private static string ResolveRunsBaseDir()
    {
        var envOverride = Environment.GetEnvironmentVariable("TEMPSCORE_RUNS_DIR");
        if (!string.IsNullOrEmpty(envOverride))
        {
            return envOverride;
        }

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(localAppData))
        {
            return Path.Combine(localAppData, "TempScore", "runs");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tempscore", "runs");
    }

    /// <summary>
    /// "This makes debugging easy and lets you replay a good run if live
    /// generation fails on stage" (the guide's run-folder contract) — best
    /// effort only, never lets a debug-artifact write failure take down an
    /// otherwise-successful stage. Catches broadly, not just IOException:
    /// MED-19/TMT-15 review finding (code-reviewer) — UnauthorizedAccessException
    /// (realistic on Windows with AV scanning a fresh LOCALAPPDATA folder) is not
    /// an IOException subclass and would otherwise escape and kill an
    /// otherwise-fully-successful run over a non-essential debug artifact.
    /// </summary>
    private static async Task WriteDebugJsonAsync<T>(string workDir, string fileName, T value)
    {
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, fileName), JsonSerializer.Serialize(value, DebugJsonOptions));
        }
        catch (Exception)
        {
        }
    }
}
