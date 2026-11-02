using System.Windows;
using Clementine.App.Interop;
using Clementine.App.Theme;

namespace Clementine.App.Dialogs;

/// <summary>Friendly "something went wrong" dialog with copyable details (used by the global exception handlers).</summary>
public partial class ErrorDialog : Window
{
    public ErrorDialog(string message, string? details)
    {
        InitializeComponent();
        ShellTheme.ApplyWindow(this);
        MessageText.Text = message;
        DetailsBox.Text = details ?? "";
        DetailsButton.Visibility = string.IsNullOrEmpty(details) ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.Visibility = DetailsButton.Visibility;
        DetailsButton.Click += (_, _) =>
        {
            var show = DetailsBox.Visibility != Visibility.Visible;
            DetailsBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            DetailsButton.Content = show ? "Hide details" : "Show details";
        };
        CopyButton.Click += (_, _) => ClipboardText.TrySet($"{message}{Environment.NewLine}{Environment.NewLine}{details}");
        CloseButton.Click += (_, _) => Close();
    }

    /// <summary>Expands the details box (snapshots).</summary>
    internal void ExpandDetails()
    {
        DetailsBox.Visibility = Visibility.Visible;
        DetailsButton.Content = "Hide details";
    }
}
