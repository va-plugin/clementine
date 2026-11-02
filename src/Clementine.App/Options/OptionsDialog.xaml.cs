using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Clementine.App.Interop;
using Clementine.App.Theme;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.App.Options;

/// <summary>
/// Options dialog generated from an <see cref="OptionSchema"/>: a borderless popover that opens next to the drop point.
/// Enter confirms, Esc cancels. Use <see cref="ShowAsync"/>.
/// </summary>
public partial class OptionsDialog : Window
{
    readonly OptionFormModel _model;
    readonly IReadOnlyList<InputFile> _inputs;
    readonly IOptionsPreview? _preview;
    readonly List<FieldEditor> _editors = [];
    readonly TaskCompletionSource<OptionValues?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    ScreenPoint? _anchor;

    public OptionsDialog(OptionSchema schema, IReadOnlyList<InputFile> inputs, OptionValues initial, string? iconKey = null, IOptionsPreview? preview = null)
    {
        InitializeComponent();
        ShellTheme.ApplyWindow(this, framed: false);
        _model = new OptionFormModel(schema, initial);
        _inputs = inputs;
        _preview = preview;

        Title = schema.Title;
        TitleText.Text = schema.Title;
        SubtitleText.Text = InputSummary.Describe(inputs);
        IconGlyph.Text = Glyphs.ForIconKey(iconKey);
        if (schema.Description is { Length: > 0 } description)
        {
            DescriptionText.Text = description;
            DescriptionText.Visibility = Visibility.Visible;
        }
        ConfirmButton.Content = schema.ConfirmLabel is { Length: > 0 } confirm ? confirm : "Convert";

        var index = 0;
        foreach (var field in schema.Fields)
        {
            var editor = FieldEditor.Create(field, _model, inputs, index++);
            _editors.Add(editor);
            FieldsPanel.Children.Add(editor.View);
        }

        _model.ValueChanged += (_, _) => UpdateState();
        UpdateState();

        Header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        CloseButton.Click += (_, _) => Finish(null);
        CancelButton.Click += (_, _) => Finish(null);
        ConfirmButton.Click += (_, _) => Confirm();
        ResetButton.Click += (_, _) =>
        {
            _model.Reset();
            foreach (var editor in _editors)
                editor.Refresh();
            UpdateState();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => OnLoaded();
        Closed += (_, _) => _result.TrySetResult(null);
    }

    /// <summary>The form state (exposed for tests and snapshots).</summary>
    internal OptionFormModel Model => _model;

    /// <summary>Types into a field's box as a user would (tests and snapshots).</summary>
    internal bool TypeInto(string key, string text) =>
        _editors.FirstOrDefault(e => string.Equals(e.Field.Key, key, StringComparison.OrdinalIgnoreCase))?.TypeText(text) == true;

    /// <summary>Whether the confirm button is enabled.</summary>
    internal bool CanConfirm => ConfirmButton.IsEnabled;

    /// <summary>Shows the dialog next to <paramref name="anchor"/> and completes with the values, or null when cancelled.</summary>
    public Task<OptionValues?> ShowAsync(ScreenPoint? anchor)
    {
        _anchor = anchor;
        Left = -32000;
        Top = -32000;
        Opacity = 0;
        Show();
        return _result.Task;
    }

    void OnLoaded()
    {
        PlaceNearAnchor();
        WindowEffects.ForceActivate(this);
        _editors.FirstOrDefault(e => _model.IsVisible(e.Field))?.FocusEditor();

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(OpacityProperty, fade);
    }

    void PlaceNearAnchor()
    {
        var anchor = _anchor ?? ScreenGeometry.CursorPosition();
        var monitor = ScreenGeometry.MonitorAt(anchor);
        // Keep the whole dialog on screen: long forms scroll instead of growing past the work area.
        FieldsScroller.MaxHeight = Math.Clamp(monitor.WorkArea.Height / monitor.Scale - 280, 180, 640);
        // First move onto the target monitor so WPF adopts its DPI, then place exactly.
        ScreenGeometry.MoveTo(this, monitor.WorkArea.X + 10, monitor.WorkArea.Y + 10, topmost: true);
        UpdateLayout();
        var (width, height) = ScreenGeometry.PixelSize(this);
        var (x, y) = _anchor is null
            ? (monitor.WorkArea.X + (monitor.WorkArea.Width - width) / 2, monitor.WorkArea.Y + (monitor.WorkArea.Height - height) / 3)
            : ScreenGeometry.PlaceNear(anchor, width, height, monitor.WorkArea);
        ScreenGeometry.MoveTo(this, x, y, topmost: true);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Finish(null);
                break;
            case Key.Enter when Keyboard.FocusedElement is not TextBox { AcceptsReturn: true } && Keyboard.FocusedElement is not Button:
                e.Handled = true;
                Confirm();
                break;
        }
    }

    void UpdateState()
    {
        foreach (var editor in _editors)
            editor.View.Visibility = _model.IsVisible(editor.Field) ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.IsEnabled = _model.IsValid;

        if (_preview is not null)
        {
            string? text;
            try
            {
                text = _preview.Preview(_inputs, _model.Values);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
            {
                text = ex.Message;
            }
            PreviewText.Text = text ?? "";
            PreviewBox.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    void Confirm()
    {
        var ok = true;
        foreach (var editor in _editors.Where(e => _model.IsVisible(e.Field)))
            ok &= editor.Commit();
        UpdateState();
        if (!ok || !_model.IsValid)
        {
            _editors.FirstOrDefault(e => _model.Validate(e.Field) is not null)?.FocusEditor();
            return;
        }
        Finish(_model.Result());
    }

    void Finish(OptionValues? values)
    {
        _result.TrySetResult(values);
        Close();
    }
}
