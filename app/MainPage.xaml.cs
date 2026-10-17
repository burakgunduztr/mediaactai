// TMT-13: MainPage code-behind. Deliberately thin — every call here either
// reacts to a UI event or forwards into Orchestrator.cs, which owns all six
// stages' sequencing. Nothing here decides *how* a stage runs; that's the
// "no pipeline logic lives in MainPage.xaml.cs" AC this ticket is checked
// against.
//
// UNVERIFIED ON MAC: there is no WinUI 3 / Windows App SDK on macOS, so this
// file has never been through the C#/XAML compiler and has never run against
// a real Window. It assumes the WinUI 3 project created per notes/GUIDE_3.md
// exposes a static App.MainWindow (the usual WinUI 3 template shape, needed
// here only for FileOpenPicker's HWND interop) — add that to App.xaml.cs if
// the ThinkPad project doesn't already have it. See notes/GUIDE_7.md for the
// full list of things to check/fix before this compiles and runs.

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
/// One row in the editable plan list. Edits here are display-only for this
/// ticket's AC (a single "Score it" press runs all six stages and plays the
/// result) — Orchestrator's onPlanReady seam exists for feeding edits back
/// into generation, but wiring that up is additional scope, not required
/// here, so OnPlanReady below only populates this list, never reads it back.
/// </summary>
public sealed class PlanSegmentViewModel : INotifyPropertyChanged
{
    private double _intensity;
    private string _prompt;

    public PlanSegmentViewModel(Segment segment)
    {
        RangeLabel = $"{segment.Start:0}-{segment.End:0}s";
        _intensity = segment.Intensity;
        _prompt = segment.Prompt;
    }

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class MainPage : Page
{
    private readonly Orchestrator _orchestrator;
    private readonly MusicClient _engineHealthClient;
    private StorageFile? _selectedFile;

    public ObservableCollection<string> StepLabels { get; } = new();

    public ObservableCollection<PlanSegmentViewModel> PlanSegments { get; } = new();

    public MainPage()
    {
        InitializeComponent();

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

            Player.Source = MediaSource.CreateFromUri(new Uri(result.OutputVideoPath));
            Player.MediaPlayer.Play();
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

    private Task<MusicPlan> OnPlanReady(MusicPlan plan)
    {
        foreach (var segment in plan.Segments)
        {
            PlanSegments.Add(new PlanSegmentViewModel(segment));
        }
        return Task.FromResult(plan);
    }
}
