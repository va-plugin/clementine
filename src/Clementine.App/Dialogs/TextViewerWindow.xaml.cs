using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Clementine.App.Interop;
using Clementine.App.Theme;
using Clementine.Core;

namespace Clementine.App.Dialogs;

/// <summary>Shows a text result (metadata report, checksum, error details) with Copy and Save as….</summary>
public partial class TextViewerWindow : Window
{
    public TextViewerWindow(string heading, string? subheading, string text)
    {
        InitializeComponent();
        ShellTheme.ApplyWindow(this);
        Title = $"{heading} – Clementine";
        HeadingText.Text = heading;
        SubheadingText.Text = subheading ?? "";
        SubheadingText.Visibility = string.IsNullOrEmpty(subheading) ? Visibility.Collapsed : Visibility.Visible;
        Body.Text = text;

        WrapToggle.Checked += (_, _) => Body.TextWrapping = TextWrapping.Wrap;
        WrapToggle.Unchecked += (_, _) => Body.TextWrapping = TextWrapping.NoWrap;
        WrapToggle.IsChecked = text.Split('\n').Any(l => l.Length > 110);
        CopyButton.Click += (_, _) => Copy();
        SaveButton.Click += (_, _) => Save();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    /// <summary>Opens a viewer window and brings it to the front.</summary>
    public static TextViewerWindow Open(string heading, string? subheading, string text)
    {
        var window = new TextViewerWindow(heading, subheading, text);
        window.Show();
        WindowEffects.ForceActivate(window);
        return window;
    }

    void Copy()
    {
        var text = string.IsNullOrEmpty(Body.SelectedText) ? Body.Text : Body.SelectedText;
        if (!ClipboardText.TrySet(text))
            return;
        CopyButton.Content = "Copied";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopyButton.Content = "Copy";
        };
        timer.Start();
    }

    void Save()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save text",
            FileName = OutputPlanner.SanitizeName(HeadingText.Text) + ".txt",
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, Body.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't save the file: {ex.Message}", "Clementine", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
