using System.Collections.Concurrent;

namespace Weir.Abstractions;

/// <summary>
/// Decides how often a "last used" timestamp is actually written for a given API key or admin token.
/// Every authenticated request touches one, and persisting each touch would put a write on the hot path
/// for a column nobody reads more than once a session - so a store records at most one write per key per
/// window and skips the rest.
/// <para>
/// The state that makes that decision is one timestamp per key, which is why it has to be pruned: a key
/// used once leaves an entry behind forever, and a long-lived process that has seen many keys accumulates
/// them without limit. Entries older than twice the window cannot influence a decision - the next touch
/// for that key passes on age alone - so dropping them changes no behaviour. The sweep runs on a write,
/// only once the map has grown past <see cref="PruneThreshold"/> entries, and only one caller at a time;
/// a map that stays small never pays for it.
/// </para>
/// </summary>
public sealed class TouchThrottleCache
{
    /// <summary>Size the map must exceed before a write considers sweeping it.</summary>
    public const int PruneThreshold = 1024;

    /// <summary>Last time each key's timestamp was actually persisted.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _entries = new();

    /// <summary>Minimum interval between persisted updates for one key.</summary>
    private readonly TimeSpan _window;

    /// <summary>1 while a sweep is running, so concurrent writers do not all sweep at once.</summary>
    private int _pruning;

    /// <summary>Creates the cache over the throttle window.</summary>
    /// <param name="window">Minimum interval between persisted updates for a single key.</param>
    public TouchThrottleCache(TimeSpan window) => _window = window;

    /// <summary>How many keys are currently remembered. Exposed for tests and diagnostics.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Records a touch and reports whether it should be persisted. False means the key was written within
    /// the window and this touch can be dropped.
    /// </summary>
    /// <param name="id">The API key or admin token being touched.</param>
    /// <param name="usedAt">When it was used.</param>
    /// <returns>True when the caller should write the timestamp.</returns>
    public bool ShouldPersist(Guid id, DateTimeOffset usedAt)
    {
        if (_entries.TryGetValue(id, out var last) && usedAt - last < _window)
        {
            return false;
        }

        _entries[id] = usedAt;
        if (_entries.Count > PruneThreshold)
        {
            Prune(usedAt);
        }

        return true;
    }

    /// <summary>Drops a key's entry, for a key or token that has just been deleted or revoked.</summary>
    /// <param name="id">The key to forget.</param>
    public void Forget(Guid id) => _entries.TryRemove(id, out _);

    /// <summary>
    /// Removes entries that can no longer suppress a write. Best-effort: a caller that finds a sweep
    /// already running returns immediately rather than queueing behind it, since the map being slightly
    /// oversized for a moment costs nothing.
    /// </summary>
    /// <param name="now">The current time, taken from the touch that triggered the sweep.</param>
    private void Prune(DateTimeOffset now)
    {
        if (Interlocked.Exchange(ref _pruning, 1) == 1)
        {
            return;
        }

        try
        {
            var cutoff = now - (_window + _window);
            foreach (var entry in _entries)
            {
                if (entry.Value <= cutoff)
                {
                    _entries.TryRemove(entry);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _pruning, 0);
        }
    }
}
