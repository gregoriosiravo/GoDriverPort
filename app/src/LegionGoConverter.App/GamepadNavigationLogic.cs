namespace LegionGoConverter.App;

public enum Direction { Up, Down, Left, Right }

/// <summary>Raw controller sample. Stick Y is positive when pushed up (Windows.Gaming.Input convention).</summary>
public sealed record GamepadState(bool Up, bool Down, bool Left, bool Right, double StickX, double StickY, bool A, bool B);

public enum NavIntentKind { None, MoveFocus, Activate, Back }

public readonly record struct NavIntent(NavIntentKind Kind, Direction? Direction = null)
{
    public static NavIntent None => new(NavIntentKind.None);
    public static NavIntent Activate => new(NavIntentKind.Activate);
    public static NavIntent Back => new(NavIntentKind.Back);
    public static NavIntent Move(Direction d) => new(NavIntentKind.MoveFocus, d);
}

/// <summary>Pure, deterministic mapping from controller samples to UI intents. Feed it samples with a monotonic
/// timestamp; it holds only the edge/repeat state. A and B fire once per press; a held direction fires at once, again
/// after <see cref="InitialDelay"/>, then every <see cref="RepeatInterval"/>. The D-pad wins over the stick.</summary>
public sealed class GamepadNavigationLogic
{
    public const double DeadZone = 0.5;
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(120);

    bool _aDown, _bDown;
    Direction? _held;
    TimeSpan _nextRepeat;

    /// <summary>Re-syncs after the window was inactive: buttons and a held direction already down do not fire until released/changed.</summary>
    public void Reset(GamepadState s, TimeSpan now)
    {
        _aDown = s.A; _bDown = s.B;
        _held = DirectionOf(s);
        _nextRepeat = now + InitialDelay;
    }

    public NavIntent Update(GamepadState s, TimeSpan now)
    {
        bool aEdge = s.A && !_aDown, bEdge = s.B && !_bDown;
        _aDown = s.A; _bDown = s.B;

        var dir = DirectionOf(s);
        NavIntent move = NavIntent.None;
        if (dir is null) _held = null;
        else if (dir != _held) { _held = dir; _nextRepeat = now + InitialDelay; move = NavIntent.Move(dir.Value); }
        else if (now >= _nextRepeat) { _nextRepeat += RepeatInterval; if (_nextRepeat <= now) _nextRepeat = now + RepeatInterval; move = NavIntent.Move(dir.Value); }

        if (aEdge) return NavIntent.Activate;
        if (bEdge) return NavIntent.Back;
        return move;
    }

    static Direction? DirectionOf(GamepadState s)
    {
        if (s.Up) return Direction.Up;
        if (s.Down) return Direction.Down;
        if (s.Left) return Direction.Left;
        if (s.Right) return Direction.Right;
        double ax = Math.Abs(s.StickX), ay = Math.Abs(s.StickY);
        if (Math.Max(ax, ay) < DeadZone) return null;
        return ax > ay ? (s.StickX > 0 ? Direction.Right : Direction.Left) : (s.StickY > 0 ? Direction.Up : Direction.Down);
    }
}
