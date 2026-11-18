using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LegionGoConverter.App;

/// <summary>Window glue only: initial focus per step and for the prompt, Esc as Back/No, and a title bar that matches the theme.
/// No business logic.</summary>
public partial class MainWindow : Window
{
    MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => FocusDefault();
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        ThemeManager.Changed += ApplyTitleBarTheme;
        Closed += (_, _) => ThemeManager.Changed -= ApplyTitleBarTheme;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = e.NewValue as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Step) or nameof(MainViewModel.PendingPrompt)) FocusDefault();
    }

    /// <summary>After layout has applied the new visibility: No on an open prompt, otherwise the step's natural first target.</summary>
    void FocusDefault() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (_vm is null) return;
        UIElement? target = _vm.PendingPrompt is not null ? Prompt.NoButton : _vm.Step switch
        {
            WizardStep.Select => !_vm.LenovoInfo.IsValid ? SelectScreen.ChooseLenovoButton
                : !_vm.AmdInfo.IsValid ? SelectScreen.ChooseAmdButton
                : NextButton,
            WizardStep.Review => StartButton,
            WizardStep.Progress => CancelButton,
            WizardStep.Result => _vm.RebootRequired ? RestartButton : StartOverButton,
            _ => null,
        };
        if (target is { IsVisible: true, IsEnabled: true }) { target.Focus(); Keyboard.Focus(target); }
        else MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    });

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Arrow keys take exactly the D-pad path (scroll a focused reading area to its edge, otherwise move focus), so a
        // ScrollViewer around the focused control cannot swallow them.
        Direction? arrow = e.Key switch
        {
            Key.Up => Direction.Up, Key.Down => Direction.Down, Key.Left => Direction.Left, Key.Right => Direction.Right, _ => null,
        };
        if (arrow is { } d && Keyboard.Modifiers == ModifierKeys.None)
        {
            GamepadNavigator.MoveFocus(d);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape || _vm is null) return;
        AppComposition.Back(_vm);
        e.Handled = true;
    }

    // ---- title bar colour (DWMWA_USE_IMMERSIVE_DARK_MODE, Windows 10 20H1+); purely cosmetic, failures ignored
    [DllImport("dwmapi.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void ApplyTitleBarTheme()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = ThemeManager.IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { /* cosmetic */ }
    }
}
