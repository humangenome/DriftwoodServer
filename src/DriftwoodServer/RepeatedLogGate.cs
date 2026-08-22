namespace DriftwoodServer;

internal readonly record struct RepeatedLogDecision(bool Emit, int Suppressed);

internal sealed class RepeatedLogGate
{
    private readonly long _windowMilliseconds;
    private readonly object _sync = new();
    private string? _lastMessage;
    private long _lastEmissionAt;
    private int _suppressed;

    public RepeatedLogGate(TimeSpan window)
    {
        if (window <= TimeSpan.Zero || window.TotalMilliseconds > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }
        _windowMilliseconds = checked((long)window.TotalMilliseconds);
    }

    public RepeatedLogDecision Observe(string message, long nowMilliseconds)
    {
        lock (_sync)
        {
            if (string.Equals(message, _lastMessage, StringComparison.Ordinal) &&
                nowMilliseconds - _lastEmissionAt < _windowMilliseconds)
            {
                _suppressed++;
                return new RepeatedLogDecision(false, 0);
            }

            int suppressed = _suppressed;
            _lastMessage = message;
            _lastEmissionAt = nowMilliseconds;
            _suppressed = 0;
            return new RepeatedLogDecision(true, suppressed);
        }
    }
}
