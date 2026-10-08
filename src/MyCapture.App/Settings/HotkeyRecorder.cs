using System.Windows;
using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MyCapture.Core.Settings;

namespace MyCapture.App.Settings;

public sealed class HotkeyRecorder : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(HotkeyRecorder), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => { ((HotkeyRecorder)d).Refresh(); ((HotkeyRecorder)d).CheckConflict(); }));
    public static readonly DependencyProperty DefaultTextProperty = DependencyProperty.Register(nameof(DefaultText), typeof(string),
        typeof(HotkeyRecorder), new PropertyMetadata(string.Empty));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string DefaultText { get => (string)GetValue(DefaultTextProperty); set => SetValue(DefaultTextProperty, value); }
    internal static HotkeyRecorder? Active { get; private set; }
    internal static Func<Hotkey, bool>? CanRegister { get; set; }
    private readonly Button _record = new() { MinWidth = 210, MinHeight = 38, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _conflict = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 4, 0, 0) };

    public HotkeyRecorder()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_record);
        foreach ((string key, Action action) in new[]
        {
            ("Hotkey.Clear", (Action)(() => { Stop(); SetCurrentValue(TextProperty, string.Empty); })),
            ("Hotkey.Reset", (Action)(() => { Stop(); SetCurrentValue(TextProperty, DefaultText); CheckConflict(); })),
        })
        {
            var button = new Button { Content = UiText.Get(key), MinHeight = 38, Margin = new Thickness(6, 0, 0, 0) };
            button.Click += (_, _) => action();
            AutomationProperties.SetName(button, UiText.Get(key));
            row.Children.Add(button);
        }
        Children.Add(row);
        _conflict.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetLiveSetting(_conflict, AutomationLiveSetting.Assertive);
        Children.Add(_conflict);
        _record.Click += (_, _) => { Active?.Stop(); Active = this; Refresh(); _record.Focus(); };
        _record.PreviewKeyDown += (_, e) =>
        {
            if (Active != this) return;
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { Stop(); return; }
            Hotkey? chord = FromKey(key, Keyboard.Modifiers);
            if (chord is not null) Accept(chord);
        };
        _record.LostKeyboardFocus += (_, _) => Stop();
        Unloaded += (_, _) => Stop();
        Loaded += (_, _) => { Refresh(); CheckConflict(); };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsDraft oldDraft) oldDraft.ErrorsChanged -= OnErrorsChanged;
            if (e.NewValue is SettingsDraft newDraft) newDraft.ErrorsChanged += OnErrorsChanged;
            CheckConflict();
        };
        Refresh();
    }

    internal static Hotkey? FromKey(Key key, ModifierKeys modifiers)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System) return null;
        HotkeyModifiers flags = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) flags |= HotkeyModifiers.Windows;
        return new Hotkey(flags, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    internal void Accept(Hotkey chord)
    {
        SetCurrentValue(TextProperty, chord.ToString());
        GetBindingExpression(TextProperty)?.UpdateSource();
        Stop();
        CheckConflict();
    }

    private void Stop() { if (Active == this) Active = null; Refresh(); }

    private void OnErrorsChanged(object? sender, DataErrorsChangedEventArgs e) => CheckConflict();

    private void CheckConflict()
    {
        string message = string.Empty;
        if (Hotkey.TryParse(Text, out Hotkey chord) && chord.IsAssigned && CanRegister?.Invoke(chord) == false)
            message = UiText.Get("Hotkey.Unavailable");
        BindingExpression? binding = GetBindingExpression(TextProperty);
        if (DataContext is SettingsDraft draft && binding is not null)
        {
            string? error = draft.GetErrors(binding.ParentBinding.Path.Path).Cast<string>().FirstOrDefault();
            if (error is not null) message = error;
        }
        _conflict.Text = message;
    }

    private void Refresh()
    {
        if (_record is null) return;
        if (Active == this) _record.Content = UiText.Get("Hotkey.Waiting");
        else
        {
            var chips = new WrapPanel();
            foreach (string key in string.IsNullOrEmpty(Text) ? new[] { UiText.Get("Hotkey.Unassigned") } : Text.Split('+'))
            {
                var chip = new Border { CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
                    Margin = new Thickness(2), Padding = new Thickness(5, 2, 5, 2), Child = new TextBlock { Text = key } };
                chip.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
                chips.Children.Add(chip);
            }
            _record.Content = chips;
        }
        AutomationProperties.SetName(_record, $"{AutomationProperties.GetName(this)} {Text} {UiText.Get("Hotkey.Record")}");
    }
}
