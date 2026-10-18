using TempScore.Core.Models;

namespace TempScore.Core.Services;

/// <summary>
/// BPM clamping, segment gap/overlap/length correction, and prompt-restatement, per
/// notes/TempScore-Implementation-Guide.md's MusicPlanner step. This is the single gate every
/// plan — real or fallback — passes through, so it must never throw on malformed input;
/// the whole point of the "never hard-stop" pipeline is that this is the last line of defense.
/// </summary>
public static class MusicPlanValidation
{
    public const int MinBpm = 50;
    public const int MaxBpm = 160;
    public const int DefaultBpm = 90;
    public const double MaxSegmentLengthSeconds = 20.0;

    // MED-19/TMT-15 review finding (architect): the prompt asks the model for Intensity
    // 1-5, but nothing enforced that range before now -- a real phi-4-mini response hit
    // Intensity 10 during MED-15's validation. Clamped, not reset, since an out-of-range
    // value still points the right way ("very intense"); resetting to a default would
    // throw that signal away. Mirrors FallbackTable's own clamp so every path -- real,
    // fallback, or user-edited -- ends up in the same range.
    private const int MinIntensity = 1;
    private const int MaxIntensity = 5;

    // Matches music_service/server.py's MAX_PROMPT_CHARS exactly -- duplicated rather than
    // shared across the C#/Python boundary, same as every other cross-language constant in
    // this pipeline (BPM range, segment length cap). A restated prompt over this length
    // would otherwise 400 at music_service, not here, turning a cosmetic model verbosity
    // issue into a run that fails on stage.
    private const int MaxPromptChars = 500;

    private const string DefaultStyle = "cinematic orchestral, piano lead";
    private const string DefaultKey = "A minor";

    /// <summary>
    /// Returns a corrected copy of <paramref name="plan"/>: BPM/style/key reset to defaults when
    /// missing or out of range, segments sorted and stitched so there are no gaps or overlaps and
    /// the last segment ends at <paramref name="clipDurationSeconds"/>, any segment longer than 20s
    /// split into equal pieces, and every prompt rebuilt to lead with the corrected style/bpm/key.
    /// </summary>
    public static MusicPlan Validate(MusicPlan plan, double clipDurationSeconds)
    {
        string style = string.IsNullOrWhiteSpace(plan.Style) ? DefaultStyle : plan.Style;
        string key = string.IsNullOrWhiteSpace(plan.Key) ? DefaultKey : plan.Key;
        int bpm = plan.Bpm is < MinBpm or > MaxBpm ? DefaultBpm : plan.Bpm;

        var stitched = CloseGaps(plan.Segments, clipDurationSeconds);
        var split = SplitLongSegments(stitched);
        var restated = RestatePrompts(split, style, bpm, key);

        return plan with { Style = style, Bpm = bpm, Key = key, Segments = restated };
    }

    private static List<Segment> CloseGaps(List<Segment>? segments, double clipDurationSeconds)
    {
        var sorted = (segments ?? []).OrderBy(s => s.Start).ToList();
        if (sorted.Count == 0)
        {
            return [new Segment(0, clipDurationSeconds, 2, "instrumental")];
        }

        var result = new List<Segment>(sorted.Count);
        double cursor = 0;
        foreach (var s in sorted)
        {
            // Clamp End into [cursor, clipDuration] so an overlapping, nested, or
            // beyond-the-clip segment from the model can never produce a negative-length
            // segment or run past the actual clip length.
            double end = Math.Min(Math.Max(cursor, s.End), clipDurationSeconds);
            if (end > cursor)
            {
                result.Add(s with { Start = cursor, End = end });
                cursor = end;
            }
            // else: zero/negative length after clamping (fully overlapped by an earlier
            // segment, or starts beyond the clip already) — drop it rather than emit a
            // degenerate segment.
        }

        if (result.Count == 0)
        {
            return [new Segment(0, clipDurationSeconds, 2, "instrumental")];
        }

        int lastIndex = result.Count - 1;
        result[lastIndex] = result[lastIndex] with { End = clipDurationSeconds };
        return result;
    }

    private static List<Segment> SplitLongSegments(List<Segment> segments)
    {
        var result = new List<Segment>();
        foreach (var s in segments)
        {
            double length = s.End - s.Start;
            if (length <= MaxSegmentLengthSeconds)
            {
                result.Add(s);
                continue;
            }

            int pieces = (int)Math.Ceiling(length / MaxSegmentLengthSeconds);
            double pieceLength = length / pieces;
            for (int i = 0; i < pieces; i++)
            {
                double start = s.Start + i * pieceLength;
                double end = i == pieces - 1 ? s.End : start + pieceLength;
                result.Add(s with { Start = start, End = end });
            }
        }
        return result;
    }

    /// <summary>
    /// Rebuilds every segment's prompt to lead with the corrected global style, BPM, and key,
    /// per the guide's "every segment prompt must restate the global style/bpm/key" rule — this
    /// is what keeps the music sounding like one piece instead of disconnected clips, and it
    /// must use the corrected BPM so the text never disagrees with the plan.
    ///
    /// Idempotent by construction (MED-19/TMT-15 review finding, architect): a body that
    /// already starts with the current prefix is left alone rather than re-prefixed, so
    /// calling Validate twice on the same plan — e.g. once on the real/fallback output, then
    /// again after Orchestrator's onPlanReady edit hook — never compounds the prefix into
    /// "style, bpm, key, style, bpm, key, ...". Also clamps Intensity and truncates the final
    /// prompt to MaxPromptChars here, since this is the one place every segment's final shape
    /// is assembled.
    /// </summary>
    private static List<Segment> RestatePrompts(List<Segment> segments, string style, int bpm, string key)
    {
        string prefix = $"{style}, {bpm} bpm, {key}";
        return segments
            .Select(s =>
            {
                string body = s.Prompt ?? "";
                string restated = body.Length == 0
                    ? prefix
                    : body.StartsWith(prefix, StringComparison.Ordinal)
                        ? body
                        : $"{prefix}, {body}";
                if (restated.Length > MaxPromptChars)
                {
                    restated = restated[..MaxPromptChars];
                }
                return s with
                {
                    Prompt = restated,
                    Intensity = Math.Clamp(s.Intensity, MinIntensity, MaxIntensity),
                };
            })
            .ToList();
    }
}
