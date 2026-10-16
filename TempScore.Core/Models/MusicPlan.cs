namespace TempScore.Core.Models;

/// <summary>One music segment: {"start","end","intensity","prompt"}.</summary>
public record Segment(double Start, double End, int Intensity, string Prompt);

/// <summary>The Phi Silica music-plan JSON: {"style","bpm","key","segments":[...]}.</summary>
public record MusicPlan(string Style, int Bpm, string Key, List<Segment> Segments);
