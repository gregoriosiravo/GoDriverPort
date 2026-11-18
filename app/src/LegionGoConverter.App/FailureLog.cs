namespace LegionGoConverter.App;

/// <summary>Reports each distinct failure message once to a sink and counts all occurrences, so a persistent fault in a 60 Hz loop
/// leaves one line, not thousands.</summary>
public sealed class FailureLog(Action<string> sink)
{
    readonly HashSet<string> _seen = new();
    public int Count { get; private set; }

    public void Report(string message)
    {
        Count++;
        if (!_seen.Add(message)) return;
        try { sink(message); } catch { /* the sink must never break the caller */ }
    }
}
