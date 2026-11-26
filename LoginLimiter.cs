namespace NvrVideoLan;

/// <summary>Small, per-client in-memory password failure throttle.</summary>
public sealed class LoginLimiter
{
    private sealed record Failure(int Count, DateTime WindowEndUtc, DateTime LockedUntilUtc);
    private readonly Dictionary<string, Failure> _entries = new();
    private readonly object _gate = new();
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Lockout = TimeSpan.FromSeconds(90);

    public bool CanAttempt(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var value)) return true;
            return value.LockedUntilUtc <= DateTime.UtcNow;
        }
    }

    public void Failed(string key)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_entries.Count > 2000)
                foreach (var old in _entries.Where(x => x.Value.WindowEndUtc < now &&
                    x.Value.LockedUntilUtc < now).Select(x => x.Key).ToArray())
                    _entries.Remove(old);
            if (!_entries.TryGetValue(key, out var current) || current.WindowEndUtc < now)
                current = new Failure(0, now + Window, DateTime.MinValue);
            int count = current.Count + 1;
            _entries[key] = new Failure(count, current.WindowEndUtc,
                count >= MaxFailures ? now + Lockout : DateTime.MinValue);
        }
    }

    public void Succeeded(string key)
    {
        lock (_gate) _entries.Remove(key);
    }
}
