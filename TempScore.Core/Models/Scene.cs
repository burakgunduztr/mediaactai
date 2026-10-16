namespace TempScore.Core.Models;

/// <summary>One scene in the Phi Silica scene-summary JSON: {"scenes":[{"start","end","summary","mood","energy"}]}.</summary>
public record Scene(double Start, double End, string Summary, string Mood, int Energy);

public record SceneSummary(List<Scene> Scenes);
