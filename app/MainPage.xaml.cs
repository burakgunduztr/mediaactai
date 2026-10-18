// TMT-13: MainPage code-behind. Deliberately thin — every call here either
// reacts to a UI event or forwards into Orchestrator.cs, which owns all six
// stages' sequencing. Nothing here decides *how* a stage runs; that's the
// "no pipeline logic lives in MainPage.xaml.cs" AC this ticket is checked
// against.
//
// MED-18/TMT-14 addition: regenerate-from-plan and the mood slider. Both
// still only forward into Orchestrator (RegenerateAsync, ApplyMoodWord) --
// MainPage's own job is just remembering _basePlan/_lastRun between clicks
// and converting the plan panel's view models back into real Segments.
// The offline badge, the third item in TMT-14's scope, was explicitly
// dropped from this pass by the user -- not implemented here.
//
// UNVERIFIED ON MAC: there is no WinUI 3 / Windows App SDK on macOS, so this
// file has never been through the C#/XAML compiler and has never run against
// a real Window. It assumes the WinUI 3 project created per notes/GUIDE_3.md
// exposes a static App.MainWindow (the usual WinUI 3 template shape, needed
// here only for FileOpenPicker's HWND interop) — add that to App.xaml.cs if
// the ThinkPad project doesn't already have it. See notes/GUIDE_7.md and
// notes/GUIDE_9.md for the full list of things to check/fix on the ThinkPad.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TempScore.App.Services;
using TempScore.Core.Models;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TempScore.App;

/// <summary>
/// One row in the editable plan list. Start/End/Intensity/Prompt are kept as
/// the real editable fields (not collapsed into a display-only RangeLabel
/// string) so this can round-trip back into a real Segment for MED-18/TMT-14's
/// regenerate-from-plan — a gap code-reviewer flagged during MED-19/TMT-15's
/// review before that feature existed to need it.
/// </summary>
public sealed class PlanSegmentViewModel : INotifyPropertyChanged
{
    private double _intensity;
    private string _prompt;

    public PlanSegmentViewModel(Segment segment)
    {
        Start = segment.Start;
        End = segment.End;
        RangeLabel = $"{segment.Start:0}-{segment.End:0}s";
        _intensity = segment.Intensity;
        _prompt = segment.Prompt;
    }

    public double Start { get; }

    public double End { get; }

    public string RangeLabel { get; }

    public double Intensity
    {
        get => _intensity;
        set { _intensity = value; OnPropertyChanged(); }
    }

    public string Prompt
    {
        get => _prompt;
        set { _prompt = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Intensity is clamped again inside Orchestrator.RegenerateAsync (via
    /// MusicPlanValidation.Validate) regardless — rounding here is just to
    /// turn the Slider's double back into Segment's int cleanly, not to
    /// enforce the [1,5] range itself.
    /// </summary>
    public Segment ToSegment() => new(Start, End, (int)Math.Round(Intensity), Prompt);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class MainPage : Page
{
    private static readonly string[] MoodWords = ["somber", "calm", "warm", "energetic", "triumphant"];

    private readonly Orchestrator _orchestrator;
    private readonly MusicClient _engineHealthClient;
    private StorageFile? _selectedFile;

    // MED-18/TMT-14: _basePlan is the plan exactly as MusicPlanner/the fallback
    // produced it for the current video -- never mutated by the mood slider or
    // manual edits, so each mood-slider move starts fresh from the same
    // baseline instead of compounding onto whatever word was applied last.
    // _lastRun carries the duration/audio-track/input-path RegenerateAsync
    // needs, so regenerating never has to re-run frame extraction just to
    // recover them.
    private MusicPlan? _basePlan;
    private OrchestratorRunResult? _lastRun;

    public ObservableCollection<string> StepLabels { get; } = new();

    public ObservableCollection<PlanSegmentViewModel> PlanSegments { get; } = new();

    public MainPage()
    {
        InitializeComponent();

        // MED-19/TMT-15 review finding (code-reviewer): none of the IDisposables built
        // below (_engineHealthClient, the FoundryLocal* clients inside Orchestrator) are
        // ever disposed -- accepted as-is, not an oversight: this is a single-window demo
        // app whose process lifetime equals these HttpClients' useful lifetime, so there's
        // no Unloaded/reconstruction path that would actually leak a connection.
        _engineHealthClient = new MusicClient();
        _orchestrator = new Orchestrator(
            new FrameExtractor(),
            new FrameDescriber(new FoundryLocalImageDescriber()),
            new SceneSummarizer(new FoundryLocalTextGenerator()),
            new MusicPlanner(new FoundryLocalTextGenerator()),
            _engineHealthClient,
            new Muxer());

        _ = RefreshEngineStatusAsync();
    }

    private async Task RefreshEngineStatusAsync()
    {
        bool healthy;
        try
        {
            healthy = await _engineHealthClient.IsHealthyAsync();
        }
        catch
        {
            healthy = false;
        }
        EngineStatusText.Text = healthy ? "Engine: ready" : "Engine: unreachable";
    }

    private async void OnOpenVideoClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".mp4");

        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        _selectedFile = file;
        SelectedFileText.Text = file.Name;
        ScoreItButton.IsEnabled = true;
    }

    private async void OnScoreItClick(object sender, RoutedEventArgs e)
    {
        if (_selectedFile is null)
        {
            return;
        }

        ScoreItButton.IsEnabled = false;
        StepLabels.Clear();
        PlanSegments.Clear();

        var progress = new Progress<StepUpdate>(update =>
            StepLabels.Add($"[{update.StepIndex}/{update.TotalSteps}] {update.StepName}"));

        try
        {
            var result = await _orchestrator.RunAsync(_selectedFile.Path, progress, OnPlanReady);
            _lastRun = result;
            _basePlan = result.Plan; // the final, validated plan this run actually used -- the mood slider/regenerate baseline
            RefreshPlanPanel(result.Plan);

            Player.Source = MediaSource.CreateFromUri(new Uri(result.OutputVideoPath));
            Player.MediaPlayer.Play();

            RegenerateButton.IsEnabled = true;
            MoodSlider.IsEnabled = true;
            ApplyMoodButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StepLabels.Add($"FAILED: {ex.Message}");
        }
        finally
        {
            ScoreItButton.IsEnabled = true;
        }
    }

    private Task<MusicPlan> OnPlanReady(MusicPlan plan) => Task.FromResult(plan);

    private void RefreshPlanPanel(MusicPlan plan)
    {
        PlanSegments.Clear();
        foreach (var segment in plan.Segments)
        {
            PlanSegments.Add(new PlanSegmentViewModel(segment));
        }
    }

    /// <summary>
    /// MED-18/TMT-14: "edit a segment prompt, rerun only music+mux." Takes
    /// whatever the plan panel currently shows -- including any manual edits
    /// to Prompt/Intensity via the panel's bound TextBox/Slider -- converts it
    /// back into real Segments, and reruns only steps 5-6 via RegenerateAsync.
    /// Style/Bpm/Key come from _basePlan since the panel only edits
    /// per-segment fields, not the plan-level ones.
    /// </summary>
    private async void OnRegenerateClick(object sender, RoutedEventArgs e)
    {
        if (_lastRun is null || _basePlan is null)
        {
            return;
        }

        var editedPlan = _basePlan with { Segments = PlanSegments.Select(vm => vm.ToSegment()).ToList() };
        await RunRegenerateAsync(editedPlan);
    }

    /// <summary>
    /// MED-18/TMT-14's mood slider: always applies the selected word to
    /// _basePlan (never to whatever's currently in the panel, including a
    /// previous mood-word application), then regenerates and refreshes the
    /// panel to show the result -- so repeated slider moves don't compound
    /// words onto each other, and a prior manual panel edit is intentionally
    /// superseded by picking a mood (one or the other per attempt, not both
    /// merged, since there's no sensible way to merge a hand-edited prompt
    /// with a mood word without guessing which part of the text was which).
    /// </summary>
    private async void OnApplyMoodClick(object sender, RoutedEventArgs e)
    {
        if (_lastRun is null || _basePlan is null)
        {
            return;
        }

        var moodWord = MoodWords[Math.Clamp((int)MoodSlider.Value - 1, 0, MoodWords.Length - 1)];
        var editedPlan = Orchestrator.ApplyMoodWord(_basePlan, moodWord);
        await RunRegenerateAsync(editedPlan);
    }

    private async Task RunRegenerateAsync(MusicPlan editedPlan)
    {
        var run = _lastRun!;

        RegenerateButton.IsEnabled = false;
        ApplyMoodButton.IsEnabled = false;
        StepLabels.Clear();

        var progress = new Progress<StepUpdate>(update =>
            StepLabels.Add($"[{update.StepIndex}/{update.TotalSteps}] {update.StepName}"));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _orchestrator.RegenerateAsync(run.InputVideoPath, run.DurationSeconds, run.HasAudioTrack, editedPlan, progress);
            sw.Stop();
            _lastRun = result;
            RefreshPlanPanel(result.Plan);
            StepLabels.Add($"Regenerated in {sw.Elapsed.TotalSeconds:F1}s");

            Player.Source = MediaSource.CreateFromUri(new Uri(result.OutputVideoPath));
            Player.MediaPlayer.Play();
        }
        catch (Exception ex)
        {
            StepLabels.Add($"FAILED: {ex.Message}");
        }
        finally
        {
            RegenerateButton.IsEnabled = true;
            ApplyMoodButton.IsEnabled = true;
        }
    }
}
