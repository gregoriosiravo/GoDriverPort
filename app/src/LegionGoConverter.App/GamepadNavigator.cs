using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Windows.Gaming.Input;

namespace LegionGoConverter.App;

/// <summary>Polls the first connected gamepad every ~16 ms on the UI thread and turns <see cref="GamepadNavigationLogic"/>
/// intents into focus moves, button activations and a Back action. Tolerates no controller and hot-plugging; never throws
/// into the dispatcher.</summary>
public sealed class GamepadNavigator
{
    readonly Dispatcher _dispatcher;
    readonly Action _back;
    readonly GamepadNavigationLogic _logic = new();
    readonly Func<bool> _isActive;
    readonly FailureLog _failures;
    bool _wasActive;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    DispatcherTimer? _timer;

    public GamepadNavigator(Dispatcher dispatcher, Action back, Func<bool>? isWindowActive = null, Action<string>? log = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _back = back ?? throw new ArgumentNullException(nameof(back));
        _isActive = isWindowActive ?? (() => Application.Current?.MainWindow?.IsActive == true);
        _failures = new FailureLog(log ?? (m => Debug.WriteLine("GamepadNavigator: " + m)));
    }

    /// <summary>Total failures seen (including repeats of an already logged message).</summary>
    public int FailureCount => _failures.Count;

    public bool IsRunning => _timer is { IsEnabled: true };

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => Tick(), _dispatcher);
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>Pure: the raw reading as the logic's input. Stick Y is positive up, as in the logic.</summary>
    public static GamepadState ToState(GamepadReading r) => new(
        Up: r.Buttons.HasFlag(GamepadButtons.DPadUp),
        Down: r.Buttons.HasFlag(GamepadButtons.DPadDown),
        Left: r.Buttons.HasFlag(GamepadButtons.DPadLeft),
        Right: r.Buttons.HasFlag(GamepadButtons.DPadRight),
        StickX: r.LeftThumbstickX,
        StickY: r.LeftThumbstickY,
        A: r.Buttons.HasFlag(GamepadButtons.A),
        B: r.Buttons.HasFlag(GamepadButtons.B));

    void Tick() => Poll(ReadFirstPad, Apply);

    static GamepadState ReadFirstPad()
    {
        var pad = Gamepad.Gamepads.FirstOrDefault();
        return pad is null ? new GamepadState(false, false, false, false, 0, 0, false, false) : ToState(pad.GetCurrentReading());
    }

    /// <summary>One poll. While the window is inactive nothing is read or applied, and the logic is re-synced on reactivation so no
    /// held button or direction fires. Failures (reading, or applying an intent such as Back) are logged once per distinct message
    /// and counted; nothing is thrown. Public so the gating and logging can be tested without WPF.</summary>
    public void Poll(Func<GamepadState> read, Action<NavIntent> apply)
    {
        bool active;
        try { active = _isActive(); } catch (Exception e) { _failures.Report("Window activity check failed: " + e.Message); active = false; }
        if (!active) { _wasActive = false; return; }
        GamepadState state;
        try { state = read(); }
        catch (Exception e) { _failures.Report("Reading the controller failed: " + e.Message); return; }
        var now = _clock.Elapsed;
        if (!_wasActive) { _wasActive = true; _logic.Reset(state, now); return; }
        var intent = _logic.Update(state, now);
        if (intent.Kind == NavIntentKind.None) return;
        try { apply(intent); }
        catch (Exception e) { _failures.Report($"Applying {intent.Kind} failed: " + e); }
    }

    void Apply(NavIntent intent)
    {
        switch (intent.Kind)
        {
            case NavIntentKind.MoveFocus when intent.Direction is { } d: MoveFocus(d); break;
            case NavIntentKind.Activate: Activate(); break;
            case NavIntentKind.Back: _back(); break;
        }
    }

    /// <summary>When the focused element is a scrollable reading area (a focusable <see cref="ScrollViewer"/>), Up/Down scroll it
    /// one step (see <see cref="ReadingScroll.Decide"/>). Returns false when focus should move instead.</summary>
    public static bool TryScrollFocused(Direction d)
    {
        if (Keyboard.FocusedElement is not ScrollViewer sv) return false;
        if (ReadingScroll.Decide(d, sv.VerticalOffset, sv.ExtentHeight, sv.ViewportHeight) is not { } delta) return false;
        sv.ScrollToVerticalOffset(sv.VerticalOffset + delta);
        return true;
    }

    /// <summary>One D-pad/stick step: scroll a focused reading area, otherwise move focus spatially. The window routes the
    /// arrow keys here too, so keyboard and controller behave identically.</summary>
    public static void MoveFocus(Direction d)
    {
        if (TryScrollFocused(d)) return;
        var request = new TraversalRequest(d switch
        {
            Direction.Up => FocusNavigationDirection.Up,
            Direction.Down => FocusNavigationDirection.Down,
            Direction.Left => FocusNavigationDirection.Left,
            _ => FocusNavigationDirection.Right,
        });
        if (Keyboard.FocusedElement is UIElement focused && focused.MoveFocus(request)) return;
        if (Keyboard.FocusedElement is null) FocusFirst();
    }

    static void FocusFirst()
    {
        var window = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                     ?? Application.Current?.MainWindow;
        window?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    static void Activate()
    {
        if (Keyboard.FocusedElement is not UIElement el) return;
        if (el is ToggleButton tb)
        {
            if (UiaPeer(tb) is IToggleProvider tp) tp.Toggle(); else tb.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        else if (el is ButtonBase bb)
        {
            if (UiaPeer(bb) is IInvokeProvider ip) ip.Invoke(); else bb.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
    }

    static object? UiaPeer(UIElement el) => UIElementAutomationPeer.CreatePeerForElement(el) switch
    {
        ToggleButtonAutomationPeer t => t.GetPattern(PatternInterface.Toggle),
        ButtonBaseAutomationPeer b => b.GetPattern(PatternInterface.Invoke),
        _ => null,
    };
}
