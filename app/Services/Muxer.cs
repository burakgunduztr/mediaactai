// TMT-12: final mux. `-c:v copy` always (video stream is never re-encoded,
// which is what keeps this to ~1s per notes/TempScore-Implementation-Guide.md
// Step 8); sidechain-duck when the clip has an audio track (the pipeline's
// only available signal for "has dialogue to preserve" — nothing upstream
// classifies audio content beyond presence/absence, per TMT-8's
// FrameExtractionResult.HasAudioTrack), plain replace otherwise.
//
// No WinUI/Windows App SDK types used — plain Process/ArgumentList, same
// pattern as FrameExtractor.cs (TMT-8), so this was written and tested for
// real against all three sample clips on Mac before this repo had a WinUI
// project to put it in. See notes/GUIDE_6.md for what's still open on the
// ThinkPad.

using System.Diagnostics;

namespace TempScore.App.Services;

public sealed class Muxer
{
    private readonly string _ffmpegPath;

    public Muxer(string ffmpegPath = "ffmpeg") => _ffmpegPath = ffmpegPath;

    /// <param name="hasAudioTrack">
    /// From TMT-8's FrameExtractionResult.HasAudioTrack — the pipeline's only
    /// signal for which ffmpeg command form applies. True: sidechain-duck the
    /// score under the clip's existing audio. False: replace the audio
    /// entirely with the score (also covers "no dialogue" per the guide,
    /// since nothing upstream distinguishes dialogue from any other audio).
    /// </param>
    public async Task MuxAsync(string inputVideoPath, string scoreWavPath, string outputPath, bool hasAudioTrack)
    {
        var args = hasAudioTrack
            ? BuildDuckArgs(inputVideoPath, scoreWavPath, outputPath)
            : BuildReplaceArgs(inputVideoPath, scoreWavPath, outputPath);
        await RunAsync(_ffmpegPath, args);
    }

    private static List<string> BuildReplaceArgs(string input, string score, string output) =>
    [
        "-y", "-i", input, "-i", score,
        "-map", "0:v", "-map", "1:a",
        "-c:v", "copy", "-c:a", "aac", "-shortest",
        output,
    ];

    private static List<string> BuildDuckArgs(string input, string score, string output) =>
    [
        "-y", "-i", input, "-i", score,
        "-filter_complex",
        "[0:a]asplit=2[v1][v2];[1:a][v1]sidechaincompress=threshold=0.05:ratio=8:release=300[m];[v2][m]amix=inputs=2:duration=first:normalize=0[a]",
        "-map", "0:v", "-map", "[a]",
        "-c:v", "copy", "-c:a", "aac",
        output,
    ];

    /// <summary>
    /// Runs ffmpeg with an argument array — never shell-string concatenation,
    /// same security requirement as TMT-8 — reading stdout/stderr
    /// concurrently to avoid the classic Process deadlock. Duplicated from
    /// FrameExtractor.cs's private helper rather than shared: small, and
    /// each ticket's file stays self-contained per this project's
    /// file-disjoint parallel-safety convention.
    /// </summary>
    private static async Task RunAsync(string fileName, IEnumerable<string> arguments)
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
    }
}
