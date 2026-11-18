namespace LegionGoConverter.App;

/// <summary>Pure decision for a focused, scrollable reading area (consent list, result details, prompt message): Up/Down
/// scroll it one step until its edge, and only then let focus move on. Used by the gamepad and the arrow keys alike.</summary>
public static class ReadingScroll
{
    public const double DefaultStep = 48;
    const double Epsilon = 0.5;

    /// <summary>Returns the vertical offset change to apply, or null when focus should move instead (sideways, not
    /// scrollable, already at the edge in that direction, or nonsensical numbers).</summary>
    public static double? Decide(Direction direction, double offset, double extent, double viewport, double step = DefaultStep)
    {
        if (direction is not (Direction.Up or Direction.Down)) return null;
        if (!double.IsFinite(offset) || !double.IsFinite(extent) || !double.IsFinite(viewport) || step <= 0) return null;
        double max = extent - viewport;
        if (max <= Epsilon) return null;
        if (direction == Direction.Up)
            return offset <= Epsilon ? null : -Math.Min(step, offset);
        double room = max - offset;
        return room <= Epsilon ? null : Math.Min(step, room);
    }
}
