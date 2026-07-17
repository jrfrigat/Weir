using Microsoft.Extensions.Caching.Memory;
using Weir.Core;

namespace Weir.Host.Security;

/// <summary>
/// Caps how many unresolved API keys a single caller address may present in a short window, so a flood
/// of random keys cannot turn each request into a control-plane database lookup.
/// </summary>
public interface IApiKeyFloodGuard
{
    /// <summary>
    /// Whether the caller has already spent its budget of unresolved keys in the current window and
    /// should be refused before the next database lookup.
    /// </summary>
    /// <param name="caller">The caller's address.</param>
    /// <returns>True when the caller is over budget.</returns>
    bool ShouldBlock(string caller);

    /// <summary>Records that the caller presented one key that did not resolve.</summary>
    /// <param name="caller">The caller's address.</param>
    void RecordFailure(string caller);
}

/// <summary>
/// Default <see cref="IApiKeyFloodGuard"/>. Counts unresolved-key attempts per caller address in a
/// fixed one-minute window and reports a caller over the runtime <see
/// cref="Weir.Contracts.WeirSystemSettings.ApiKeyFailureThreshold"/> as blocked.
/// <para>
/// The counter is keyed by caller address, not by key, and that is the whole point. A resolved key is
/// held in the authenticator's short-lived cache, so a legitimate client hits the database once and
/// then not again; an unknown key is never cached, so without this guard every one of a million random
/// keys would query the store. Keying by key would not help - each fresh random key misses a
/// per-key negative cache too - and would itself be an unbounded allocation. The set of caller
/// addresses is bounded by the real TCP peers reaching the node, and is bounded here again by a hard
/// cap on tracked callers.
/// </para>
/// <para>
/// It is per-instance and in-memory on purpose: it protects this node's database connections, needs no
/// coordination to do that, and fails open. When the tracked-caller cap is reached a new caller is
/// simply not tracked (it falls through to a lookup, exactly as before the guard existed) rather than
/// blocked, so the guard can never deny a caller it has no record of. A blocked caller costs nothing:
/// the check is a dictionary read that happens before the lookup it replaces.
/// </para>
/// <para>
/// "Per caller address" is only "per client" when Weir sees the real client address. Behind a reverse
/// proxy the socket belongs to the proxy, so <c>Weir:Network:TrustedProxies</c> must be set or every
/// caller shares one budget - the same condition the sign-in throttle depends on.
/// </para>
/// </summary>
public sealed class ApiKeyFloodGuard : IApiKeyFloodGuard, IDisposable
{
    /// <summary>The window a caller's unresolved-key count accumulates over before it resets.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The most caller addresses tracked at once. A backstop against a widely distributed flood filling
    /// the map: past it, new callers are untracked (and so never blocked), which is the pre-guard
    /// behaviour rather than a new failure mode.
    /// </summary>
    private const long MaxTrackedCallers = 100_000;

    /// <summary>Runtime settings, read live so a changed threshold takes effect without a restart.</summary>
    private readonly IRuntimeSettings _settings;

    /// <summary>Per-address counters, each expiring one window after the caller's first unresolved key.</summary>
    private readonly MemoryCache _cache;

    /// <summary>Creates the guard.</summary>
    /// <param name="settings">The runtime settings supplying the threshold.</param>
    public ApiKeyFloodGuard(IRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxTrackedCallers });
    }

    /// <inheritdoc />
    public bool ShouldBlock(string caller)
    {
        var threshold = _settings.Current.ApiKeyFailureThreshold;
        return threshold > 0
            && _cache.TryGetValue(caller, out Counter? counter)
            && counter is not null
            && Volatile.Read(ref counter.Count) >= threshold;
    }

    /// <inheritdoc />
    public void RecordFailure(string caller)
    {
        // Nothing to count against when the guard is off; skip the allocation a new entry would cost.
        if (_settings.Current.ApiKeyFailureThreshold <= 0)
        {
            return;
        }

        // The window is anchored to the first failure: the entry expires one window later regardless of
        // later increments, so a caller cannot hold its own bucket open by continuing to fail. A racing
        // pair on a brand-new caller may each build a counter and one is dropped; the slight undercount
        // is immaterial to a flood guard and costs less than locking the hot path.
        var counter = _cache.GetOrCreate(caller, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Window;
            entry.Size = 1;
            return new Counter();
        });

        if (counter is not null)
        {
            Interlocked.Increment(ref counter.Count);
        }
    }

    /// <summary>Disposes the backing cache.</summary>
    public void Dispose() => _cache.Dispose();

    /// <summary>A caller's unresolved-key count within one window. A class so the entry can be mutated in place.</summary>
    private sealed class Counter
    {
        /// <summary>The number of unresolved keys the caller has presented in the current window.</summary>
        public int Count;
    }
}
