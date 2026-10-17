// TMT-8: frame/cut extraction, wrapping the ffmpeg commands TMT-2 already
// validated against the three sample clips (notes/TempScore-Implementation-Guide.md
// Step 3). Reuses TempScore.Core.Models.Frame directly — no app/Models folder,
// per this ticket's contract — Description stays empty here; TMT-10 fills it
// in on the next stage.
//
// No WinUI/Windows App SDK types are used anywhere in this file — it's plain
// .NET (Process, regex, file I/O) and builds/runs on any platform with ffmpeg
// on PATH. That's intentional: it was written and tested on macOS against the
// real sample clips before this repo had a WinUI project to put it in. See
// notes/GUIDE_3.md for what's still open on the ThinkPad.

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using TempScore.Core.Models;

namespace TempScore.App.Services;

public sealed class FrameExtractionResult
{
    public required List<Frame> Frames { get; init; }
    public required List<double> CutTimes { get; init; }
    public required double DurationSeconds { get; init; }
    public required bool HasAudioTrack { get; init; }
}

public sealed class FrameExtractor
{
    private static readonly Regex CutTimeRegex = new(@"pts_time:([0-9.]+)", RegexOptions.Compiled);

    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;

    public FrameExtractor(string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe")
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
    }

    public async Task<FrameExtractionResult> ExtractAsync(string inputPath, string framesDir)
    {
        Directory.CreateDirectory(framesDir);

        var frames = await ExtractFramesAsync(inputPath, framesDir);
        var cutTimes = await DetectCutsAsync(inputPath);
        var duration = await GetDurationAsync(inputPath);
        var hasAudio = await HasAudioTrackAsync(inputPath);

        return new FrameExtractionResult
        {
            Frames = frames,
            CutTimes = cutTimes,
            DurationSeconds = duration,
            HasAudioTrack = hasAudio,
        };
    }

    private async Task<List<Frame>> ExtractFramesAsync(string inputPath, string framesDir)
    {
        var pattern = Path.Combine(framesDir, "f_%03d.jpg");
        await RunAsync(_ffmpegPath, ["-y", "-i", inputPath, "-vf", "fps=1/3,scale=768:-2", pattern]);

        var files = Directory.GetFiles(framesDir, "f_*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var frames = new List<Frame>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            // Frame timestamp = (index - 1) * 3 seconds, per the guide's own formula for fps=1/3.
            frames.Add(new Frame(Path: files[i], Timestamp: i * 3.0, Description: ""));
        }
        return frames;
    }

    private async Task<List<double>> DetectCutsAsync(string inputPath)
    {
        var (_, stderr) = await RunAsync(
            _ffmpegPath,
            ["-i", inputPath, "-vf", "select='gt(scene,0.3)',showinfo", "-f", "null", "-"]);
        return CutTimeRegex.Matches(stderr)
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
    }

    private async Task<double> GetDurationAsync(string inputPath)
    {
        var (stdout, _) = await RunAsync(
            _ffprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", inputPath]);
        return double.Parse(stdout.Trim(), CultureInfo.InvariantCulture);
    }

    private async Task<bool> HasAudioTrackAsync(string inputPath)
    {
        var (stdout, _) = await RunAsync(
            _ffprobePath,
            ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", inputPath]);
        return !string.IsNullOrWhiteSpace(stdout);
    }

    /// <summary>
    /// Runs a process with an argument array — never shell-string concatenation,
    /// per this ticket's security review requirement — and reads stdout/stderr
    /// concurrently to avoid the classic Process deadlock when either stream
    /// fills its buffer before the other is drained.
    /// </summary>
    private static async Task<(string Stdout, string Stderr)> RunAsync(string fileName, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited {process.ExitCode}: {stderr}");
        }
        return (stdout, stderr);
    }
}
