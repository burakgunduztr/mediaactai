namespace TempScore.Core.Models;

/// <summary>One extracted video frame with its NPU-generated description.</summary>
public record Frame(string Path, double Timestamp, string Description);
