namespace Weir.Caching.Redis;

/// <summary>Restart-required Redis settings independent of the rate limiter's connection.</summary>
public sealed class RedisResponseCacheOptions
{
    /// <summary>The configuration section bound by the in-box host.</summary>
    public const string SectionName = "Weir:ResponseCache:Redis";
    /// <summary>Secret connection settings; never include these in diagnostics.</summary>
    public string ConnectionString { get; set; } = string.Empty;
    /// <summary>Required installation identity that isolates this cache from other deployments.</summary>
    public string Namespace { get; set; } = string.Empty;
    /// <summary>Logical Redis database, or -1 for the connection's default.</summary>
    public int Database { get; set; } = -1;
    /// <summary>Driver operation timeout, independent of database execution timeouts.</summary>
    public int OperationTimeoutMilliseconds { get; set; } = 1000;
    /// <summary>Maximum retained snapshot lifetime; shorter endpoint TTLs remain unchanged.</summary>
    public int MaximumTtlSeconds { get; set; } = 86_400;

    /// <summary>Checks non-secret ranges before creating any connection.</summary>
    /// <returns>Whether the configuration is usable.</returns>
    public bool IsValid() => !string.IsNullOrWhiteSpace(ConnectionString) &&
        !string.IsNullOrWhiteSpace(Namespace) && Namespace.Length <= 128 &&
        Namespace.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.') &&
        Database >= -1 && OperationTimeoutMilliseconds is >= 100 and <= 30_000 && MaximumTtlSeconds is >= 1 and <= 604_800;
}
