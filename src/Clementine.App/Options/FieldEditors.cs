using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Clementine.App.Theme;
using Clementine.Core;

namespace Clementine.App.Options;

/// <summary>One field's editor in the options dialog. Editors read and write the shared <see cref="OptionFormModel"/>.</summary>
internal abstract class FieldEditor
{
    protected FieldEditor(OptionField field, OptionFormModel model)
    {
        Field = field;
        Model = model;
        Error = new TextBlock
        {
            Style = (Style)Application.Current.FindResource("Shell.Caption"),
            Foreground = ShellTheme.Brush("Clementine.Error"),
            Margin = new Thickness(2, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        Help = field.Help is { Length: > 0 } help
            ? new TextBlock { Text = help, Style = (Style)Application.Current.FindResource("Shell.Caption"), Margin = new Thickness(2, 4, 0, 0) }
            : null;
    }

    public OptionField Field { get; }

    protected OptionFormModel Model { get; }

    protected TextBlock Error { get; }

    protected TextBlock? Help { get; }

    /// <summary>The complete block (label, control, help, error) placed in the dialog.</summary>
    public FrameworkElement View { get; protected set; } = null!;

    /// <summary>Re-reads the model (after Reset) and refreshes the error line.</summary>
    public abstract void Refresh();

    /// <summary>Applies any text still being typed. Returns false when it isn't valid.</summary>
    public virtual bool Commit() => true;

    public abstract void FocusEditor();

    /// <summary>Types <paramref name="text"/> into the editor's text box, as a user would (text-based editors only).</summary>
    public virtual bool TypeText(string text) => false;

    protected static bool Type(TextBox box, string text)
    {
        box.Text = text;
        return true;
    }

    public void ShowError(string? message)
    {
        Error.Text = message ?? "";
        Error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    protected FrameworkElement Stack(params FrameworkElement?[] parts)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        foreach (var part in parts)
        {
            if (part is not null)
                panel.Children.Add(part);
        }
        if (Help is not null)
            panel.Children.Add(Help);
        panel.Children.Add(Error);
        return panel;
    }

    protected static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = ShellTheme.Brush("Clementine.Text"),
        Margin = new Thickness(2, 0, 0, 6),
        VerticalAlignment = VerticalAlignment.Center,
    };

    protected static void SetErrorBorder(Control control, bool hasError) =>
        control.BorderBrush = hasError ? ShellTheme.Brush("Clementine.Error") : ShellTheme.Brush("Clementine.Stroke");

    public static FieldEditor Create(OptionField field, OptionFormModel model, IReadOnlyList<InputFile> inputs, int index) => field switch
    {
        NumberField n => new NumberEditor(n, model),
        ChoiceField c => new ChoiceEditor(c, model, index),
        BoolField b => new BoolEditor(b, model),
        TextField t => new TextEditor(t, model),
        TimeField t => new TimeEditor(t, model),
        FolderField f => new FolderEditor(f, model, inputs),
        _ => new TextEditor(new TextField(field.Key, field.Label, model.Values.GetString(field.Key, "")), model),
    };
}

/// <summary>Slider plus an exact-value box with the unit.</summary>
internal sealed class NumberEditor : FieldEditor
{
    readonly NumberField _field;
    readonly TextBox _box;
    readonly Slider _slider;
    bool _updating;

    public NumberEditor(NumberField field, OptionFormModel model)
        : base(field, model)
    {
        _field = field;
        var range = Math.Max(field.Max - field.Min, double.Epsilon);
        var step = field.Step > 0 ? field.Step : Math.Pow(10, -field.Decimals);
        _slider = new Slider
        {
            Minimum = field.Min,
            Maximum = field.Max,
            SmallChange = step,
            LargeChange = Math.Max(step, range / 10),
            TickFrequency = step,
            IsSnapToTickEnabled = range / step <= 2000,
            Margin = new Thickness(0, 2, 0, 0),
            IsTabStop = false,
        };
        AutomationName(_slider, field.Label);
        _box = new TextBox
        {
            Width = 86,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(8, 5, 8, 5),
        };
        AutomationName(_box, field.Label);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Label(field.Label);
        label.Margin = new Thickness(2, 0, 8, 0);
        header.Children.Add(label);
        Grid.SetColumn(_box, 1);
        header.Children.Add(_box);
        if (field.Unit is { Length: > 0 } unit)
        {
            var unitText = new TextBlock
            {
                Text = unit,
                Foreground = ShellTheme.Brush("Clementine.TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 22,
            };
            Grid.SetColumn(unitText, 2);
            header.Children.Add(unitText);
        }

        // A slider only helps when its steps are coarse enough to hit a value; huge ranges get the box alone.
        if (range / step > 1000)
        {
            _slider.Visibility = Visibility.Collapsed;
            _box.Width = 120;
        }
        View = Stack(header, _slider);
        Refresh();

        _slider.ValueChanged += (_, e) =>
        {
            if (_updating)
                return;
            var value = Math.Round(e.NewValue, Math.Clamp(field.Decimals, 0, 6));
            Model.Set(field.Key, value);
            _updating = true;
            _box.Text = OptionFormModel.FormatNumber(field, value);
            _updating = false;
            Validate();
        };
        _box.TextChanged += (_, _) =>
        {
            if (_updating)
                return;
            ApplyText(updateSlider: true);
        };
        _box.LostKeyboardFocus += (_, _) => Commit();
        _box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down)
            {
                var current = Model.Values.GetDouble(field.Key, field.Default);
                var multiplier = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                var next = Math.Clamp(current + (e.Key == Key.Up ? step : -step) * multiplier, field.Min, field.Max);
                Model.Set(field.Key, next);
                Refresh();
                _box.SelectAll();
                e.Handled = true;
            }
        };
        _box.GotKeyboardFocus += (_, _) => _box.SelectAll();
    }

    public override void Refresh()
    {
        _updating = true;
        var value = Model.Values.GetDouble(_field.Key, _field.Default);
        _slider.Value = value;
        _box.Text = OptionFormModel.FormatNumber(_field, value);
        _updating = false;
        Validate();
    }

    public override bool Commit()
    {
        if (!ApplyText(updateSlider: true))
            return false;
        _updating = true;
        _box.Text = OptionFormModel.FormatNumber(_field, Model.Values.GetDouble(_field.Key, _field.Default));
        _updating = false;
        return true;
    }

    public override void FocusEditor()
    {
        _box.Focus();
        _box.SelectAll();
    }

    public override bool TypeText(string text) => Type(_box, text);

    bool ApplyText(bool updateSlider)
    {
        var error = Model.SetFromText(_field, _box.Text);
        ShowError(error);
        SetErrorBorder(_box, error is not null);
        if (error is null && updateSlider)
        {
            _updating = true;
            _slider.Value = Model.Values.GetDouble(_field.Key, _field.Default);
            _updating = false;
        }
        return error is null;
    }

    void Validate()
    {
        var error = Model.Validate(_field);
        ShowError(error);
        SetErrorBorder(_box, error is not null);
    }

    static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}

/// <summary>Segmented buttons for a few short choices, a drop-down otherwise.</summary>
internal sealed class ChoiceEditor : FieldEditor
{
    readonly ChoiceField _field;
    readonly List<RadioButton> _segments = [];
    readonly ComboBox? _combo;
    bool _updating;

    public ChoiceEditor(ChoiceField field, OptionFormModel model, int index)
        : base(field, model)
    {
        _field = field;
        if (UseSegments(field))
        {
            var grid = new UniformGrid { Rows = 1 };
            var group = $"choice-{index}-{field.Key}";
            foreach (var choice in field.Choices)
            {
                var segment = new RadioButton
                {
                    Content = choice.Label,
                    GroupName = group,
                    Style = (Style)Application.Current.FindResource("Shell.Segment"),
                    Tag = choice.Value,
                };
                segment.Checked += (_, _) =>
                {
                    if (!_updating)
                        Model.Set(field.Key, choice.Value);
                };
                _segments.Add(segment);
                grid.Children.Add(segment);
            }
            var host = new Border { Style = (Style)Application.Current.FindResource("Shell.SegmentHost"), Child = grid };
            View = Stack(Label(field.Label), host);
        }
        else
        {
            _combo = new ComboBox { MaxDropDownHeight = 320, Padding = new Thickness(10, 6, 30, 6) };
            foreach (var choice in field.Choices)
                _combo.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
            System.Windows.Automation.AutomationProperties.SetName(_combo, field.Label);
            _combo.SelectionChanged += (_, _) =>
            {
                if (!_updating && _combo.SelectedItem is ComboBoxItem { Tag: string value })
                    Model.Set(field.Key, value);
            };
            View = Stack(Label(field.Label), _combo);
        }
        Refresh();
    }

    /// <summary>Width the dialog gives a segmented control (DIP).</summary>
    const double SegmentHostWidth = 350;

    /// <summary>
    /// Segments when there are 2–4 choices whose longest label fits in an equal-width segment
    /// (about 7 DIP per character plus padding); a drop-down otherwise.
    /// </summary>
    public static bool UseSegments(ChoiceField field) =>
        field.Choices.Count is >= 2 and <= 4
        && field.Choices.Max(c => c.Label.Length) * 7.2 + 26 <= SegmentHostWidth / field.Choices.Count;

    public override void Refresh()
    {
        _updating = true;
        var value = Model.Values.GetString(_field.Key, _field.Default);
        foreach (var segment in _segments)
            segment.IsChecked = string.Equals((string)segment.Tag, value, StringComparison.OrdinalIgnoreCase);
        if (_combo is not null)
        {
            _combo.SelectedItem = _combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string)i.Tag, value, StringComparison.OrdinalIgnoreCase));
        }
        _updating = false;
    }

    public override void FocusEditor()
    {
        if (_combo is not null)
            _combo.Focus();
        else
            (_segments.FirstOrDefault(s => s.IsChecked == true) ?? _segments.FirstOrDefault())?.Focus();
    }
}

/// <summary>Toggle switch.</summary>
internal sealed class BoolEditor : FieldEditor
{
    readonly BoolField _field;
    readonly CheckBox _toggle;
    bool _updating;

    public BoolEditor(BoolField field, OptionFormModel model)
        : base(field, model)
    {
        _field = field;
        _toggle = new CheckBox
        {
            Content = field.Label,
            Style = (Style)Application.Current.FindResource("Shell.Toggle"),
            Margin = new Thickness(0, 2, 0, 0),
        };
        _toggle.Checked += (_, _) => Changed();
        _toggle.Unchecked += (_, _) => Changed();
        View = Stack(_toggle);
        if (Help is not null)
            Help.Margin = new Thickness(50, 2, 0, 0);
        Refresh();
    }

    void Changed()
    {
        if (!_updating)
            Model.Set(_field.Key, _toggle.IsChecked == true);
    }

    public override void Refresh()
    {
        _updating = true;
        _toggle.IsChecked = Model.Values.GetBool(_field.Key, _field.Default);
        _updating = false;
    }

    public override void FocusEditor() => _toggle.Focus();
}

/// <summary>Single-line, multi-line or password text.</summary>
internal sealed class TextEditor : FieldEditor
{
    readonly TextField _field;
    readonly TextBox? _box;
    readonly PasswordBox? _password;
    bool _updating;

    public TextEditor(TextField field, OptionFormModel model)
        : base(field, model)
    {
        _field = field;
        Control control;
        if (field.IsPassword)
        {
            _password = new PasswordBox();
            _password.PasswordChanged += (_, _) => Model.Set(field.Key, _password.Password);
            control = _password;
        }
        else
        {
            _box = new TextBox { Tag = field.Placeholder };
            if (field.Multiline)
            {
                _box.AcceptsReturn = true;
                _box.TextWrapping = TextWrapping.Wrap;
                _box.MinHeight = 76;
                _box.MaxHeight = 180;
                _box.VerticalContentAlignment = VerticalAlignment.Top;
                _box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            _box.TextChanged += (_, _) =>
            {
                if (!_updating)
                    Model.Set(field.Key, _box.Text);
            };
            control = _box;
        }
        System.Windows.Automation.AutomationProperties.SetName(control, field.Label);
        View = Stack(Label(field.Label), control);
        Refresh();
    }

    public override void Refresh()
    {
        _updating = true;
        var text = Model.Values.GetString(_field.Key, _field.Default);
        if (_box is not null)
            _box.Text = text;
        if (_password is not null && _password.Password != text)
            _password.Password = text;
        _updating = false;
    }

    public override void FocusEditor()
    {
        if (_password is not null)
        {
            _password.Focus();
            return;
        }
        _box?.Focus();
        _box?.SelectAll();
    }

    public override bool TypeText(string text) => _box is not null && Type(_box, text);
}

/// <summary>"mm:ss.fff" box with live validation; ↑/↓ nudge by a second (Shift: 0.1 s, Ctrl: 10 s).</summary>
internal sealed class TimeEditor : FieldEditor
{
    readonly TimeField _field;
    readonly TextBox _box;
    bool _updating;

    public TimeEditor(TimeField field, OptionFormModel model)
        : base(field, model)
    {
        _field = field;
        _box = new TextBox { Tag = "mm:ss.fff", FontFamily = (FontFamily)Application.Current.FindResource("Shell.MonoFont"), FontSize = 13 };
        System.Windows.Automation.AutomationProperties.SetName(_box, field.Label);
        _box.TextChanged += (_, _) =>
        {
            if (!_updating)
                Apply();
        };
        _box.LostKeyboardFocus += (_, _) => Commit();
        _box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down))
                return;
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? TimeSpan.FromMilliseconds(100)
                : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? TimeSpan.FromSeconds(10)
                : TimeSpan.FromSeconds(1);
            var current = Model.Values.GetTime(field.Key, field.Default);
            var next = e.Key == Key.Up ? current + step : current - step;
            Model.Set(field.Key, next < TimeSpan.Zero ? TimeSpan.Zero : next);
            Refresh();
            e.Handled = true;
        };
        View = Stack(Label(field.Label), _box);
        Refresh();
    }

    public override void Refresh()
    {
        _updating = true;
        _box.Text = OptionValues.FormatTime(Model.Values.GetTime(_field.Key, _field.Default));
        _updating = false;
        ShowError(null);
        SetErrorBorder(_box, false);
    }

    public override bool Commit()
    {
        if (!Apply())
            return false;
        _updating = true;
        _box.Text = OptionValues.FormatTime(Model.Values.GetTime(_field.Key, _field.Default));
        _updating = false;
        return true;
    }

    public override void FocusEditor()
    {
        _box.Focus();
        _box.SelectAll();
    }

    public override bool TypeText(string text) => Type(_box, text);

    bool Apply()
    {
        var error = Model.SetFromText(_field, _box.Text);
        ShowError(error);
        SetErrorBorder(_box, error is not null);
        return error is null;
    }
}

/// <summary>Path box plus a Browse… button (system folder picker).</summary>
internal sealed class FolderEditor : FieldEditor
{
    readonly FolderField _field;
    readonly TextBox _box;
    readonly IReadOnlyList<InputFile> _inputs;
    bool _updating;
    bool _touched;

    public FolderEditor(FolderField field, OptionFormModel model, IReadOnlyList<InputFile> inputs)
        : base(field, model)
    {
        _field = field;
        _inputs = inputs;
        _box = new TextBox { Tag = "Choose a folder…" };
        System.Windows.Automation.AutomationProperties.SetName(_box, field.Label);
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) => Browse();
        _box.TextChanged += (_, _) =>
        {
            if (_updating)
                return;
            _touched = true;
            var error = Model.SetFromText(_field, _box.Text);
            ShowError(error);
            SetErrorBorder(_box, error is not null);
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_box);
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        View = Stack(Label(field.Label), row);
        Refresh();
    }

    public override void Refresh()
    {
        _updating = true;
        _box.Text = Model.Values.GetString(_field.Key, "");
        _updating = false;
        // Don't greet the user with "Choose a folder." before they've done anything.
        var error = _touched ? Model.Validate(_field) : null;
        ShowError(error);
        SetErrorBorder(_box, error is not null);
    }

    public override bool Commit()
    {
        _touched = true;
        var error = Model.Validate(_field);
        ShowError(error);
        SetErrorBorder(_box, error is not null);
        return error is null;
    }

    public override void FocusEditor() => _box.Focus();

    public override bool TypeText(string text) => Type(_box, text);

    void Browse()
    {
        var current = Model.Values.GetStringOrNull(_field.Key);
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _field.Label,
            InitialDirectory = Directory.Exists(current) ? current : _inputs.FirstOrDefault()?.DirectoryPath ?? "",
        };
        if (dialog.ShowDialog(Window.GetWindow(View)) == true)
        {
            _touched = true;
            Model.Set(_field.Key, dialog.FolderName);
            Refresh();
        }
    }
}

/// <summary>Formats the "what am I converting" line under a dialog's title.</summary>
internal static class InputSummary
{
    public static string Describe(IReadOnlyList<InputFile> inputs)
    {
        if (inputs.Count == 0)
            return "";
        if (inputs.Count == 1)
            return inputs[0].Name;
        var kinds = inputs.GroupBy(i => i.Kind).ToList();
        var noun = kinds.Count == 1 ? KindNoun(kinds[0].Key, inputs.Count) : "files";
        return string.Create(CultureInfo.CurrentCulture, $"{inputs.Count} {noun}");
    }

    public static string KindNoun(FileKind kind, int count)
    {
        var singular = kind switch
        {
            FileKind.Folder => "folder",
            FileKind.Image => "image",
            FileKind.Video => "video",
            FileKind.Audio => "audio file",
            FileKind.Pdf => "PDF",
            FileKind.Document => "document",
            FileKind.Spreadsheet => "spreadsheet",
            FileKind.Presentation => "presentation",
            FileKind.Ebook => "e-book",
            FileKind.Text => "text file",
            FileKind.Subtitle => "subtitle file",
            FileKind.Archive => "archive",
            _ => "file",
        };
        return count == 1 ? singular : singular + "s";
    }
}
