using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Clementine.App.Interop;
using Clementine.App.Theme;
using Clementine.Core;

namespace Clementine.App.Main;

/// <summary>A row in the main window's "Recent jobs" list. Refresh with <see cref="Update"/> on the UI thread.</summary>
public sealed class JobRow : INotifyPropertyChanged
{
    public JobRow(Job job)
    {
        Job = job;
        Icon = ShellIcons.For(job.Inputs[0].Path, 28);
        Update();
    }

    public Job Job { get; }

    public ImageSource? Icon { get; }

    public string Name => Job.DisplayName;

    public string Operation => Job.Operation.Info.Title;

    public string StatusText { get; private set; } = "";

    public Brush StatusBrush { get; private set; } = Brushes.Gray;

    public double Progress { get; private set; }

    public bool IsIndeterminate { get; private set; }

    public Visibility ProgressVisibility { get; private set; }

    public Visibility RevealVisibility { get; private set; }

    public Visibility CancelVisibility { get; private set; }

    public IReadOnlyList<string> Outputs => Job.Result?.Outputs ?? [];

    public string Tooltip { get; private set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update()
    {
        var running = Job.State is JobState.Running or JobState.Queued;
        StatusText = Job.State switch
        {
            JobState.Queued => "Waiting…",
            JobState.Running => Job.Progress is { } p ? $"{(p * 100).ToString("0", CultureInfo.CurrentCulture)}%{(Job.Status is { } s ? " · " + s : "")}" : Job.Status ?? "Working…",
            JobState.Succeeded => Outputs.Count == 1 ? Path.GetFileName(Outputs[0].TrimEnd('\\'))
                : Outputs.Count > 1 ? $"{Outputs.Count} outputs" : Job.Result?.TextTitle ?? "Done",
            JobState.Failed => Job.ErrorMessage ?? "Failed",
            _ => "Canceled",
        };
        StatusBrush = Job.State switch
        {
            JobState.Succeeded => ShellTheme.Brush("Clementine.Success"),
            JobState.Failed => ShellTheme.Brush("Clementine.Error"),
            _ => ShellTheme.Brush("Clementine.TextSecondary"),
        };
        Progress = Job.Progress ?? 0;
        IsIndeterminate = Job.State == JobState.Running && Job.Progress is null;
        ProgressVisibility = running ? Visibility.Visible : Visibility.Collapsed;
        RevealVisibility = Job.State == JobState.Succeeded && Outputs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelVisibility = running ? Visibility.Visible : Visibility.Collapsed;
        Tooltip = Job.State == JobState.Failed ? $"{Job.ErrorMessage}" : string.Join(Environment.NewLine, Outputs.DefaultIfEmpty(Job.Inputs[0].Path));
        OnChanged(null);
    }

    /// <summary>What screen readers announce for the list item ("clip.mov, ProRes 4444 · alpha, 40% · Encoding…").</summary>
    public override string ToString() => $"{Name}, {Operation}, {StatusText}";

    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
