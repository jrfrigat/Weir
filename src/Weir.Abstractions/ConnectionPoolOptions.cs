namespace Weir.Abstractions;

/// <summary>
/// How one data connection pools its physical database connections, stated in Weir's own terms rather
/// than in a driver's connection-string keywords. A connector translates this onto whatever its driver
/// calls the same thing.
/// <para>
/// Both shipped drivers pool by default, so leaving every property null keeps exactly the behaviour a
/// bare connection string already has. What this type adds is the ability to say so on purpose - to
/// size the pool where the endpoint's traffic warrants it, or to turn pooling off for a connection
/// whose server holds per-session state that must not outlive a request.
/// </para>
/// <para>
/// A value set here overlays the connection string, the same way the runtime settings overlay their
/// <c>appsettings.json</c> seeds: the more specific statement wins, and a property left null changes
/// nothing. So a connection string that already carries <c>Max Pool Size</c> keeps it until this says
/// otherwise.
/// </para>
/// </summary>
public sealed record ConnectionPoolOptions
{
    /// <summary>
    /// Whether physical connections are pooled and reused. Null leaves the driver default (pooled).
    /// <para>
    /// False means every request opens its own connection and closes it afterwards: a TCP connect and a
    /// login handshake per call, which is the cost pooling exists to avoid - so set it only when a
    /// connection may not be shared, not to "keep things simple".
    /// </para>
    /// </summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// Connections the pool keeps open even while idle. Null leaves the driver default. Raising it
    /// trades idle server sessions for never paying a connect on a cold path.
    /// </summary>
    public int? MinSize { get; init; }

    /// <summary>
    /// The most physical connections this data connection may hold. Null leaves the driver default.
    /// This is the real ceiling on Weir's concurrency against that database: once the pool is full,
    /// further requests wait for one to come back rather than opening another.
    /// </summary>
    public int? MaxSize { get; init; }

    /// <summary>
    /// How long a request waits for a free pooled connection before failing, in seconds. Null leaves
    /// the driver default.
    /// <para>
    /// In both shipped drivers this is the connect timeout, which covers opening a new connection and
    /// waiting for a pooled one alike - there is no separate knob for the queue. So it is also the
    /// timeout for reaching a database that is simply slow to answer.
    /// </para>
    /// </summary>
    public int? AcquireTimeoutSeconds { get; init; }

    /// <summary>Whether anything here has been set at all.</summary>
    public bool IsEmpty => Enabled is null && MinSize is null && MaxSize is null && AcquireTimeoutSeconds is null;

    /// <summary>
    /// Checks the combination for settings that cannot mean anything, so a mistake surfaces at startup
    /// instead of being silently dropped by a driver.
    /// </summary>
    /// <param name="connectionName">The connection's name, for the message.</param>
    /// <returns>The reason this configuration is impossible, or null when it is usable.</returns>
    public string? Validate(string connectionName)
    {
        if (MinSize is < 0)
        {
            return $"Data connection '{connectionName}': Pool.MinSize must not be negative.";
        }

        if (MaxSize is < 1)
        {
            return $"Data connection '{connectionName}': Pool.MaxSize must be at least 1.";
        }

        if (MinSize is { } min && MaxSize is { } max && min > max)
        {
            return $"Data connection '{connectionName}': Pool.MinSize ({min}) exceeds Pool.MaxSize ({max}).";
        }

        if (AcquireTimeoutSeconds is < 0)
        {
            return $"Data connection '{connectionName}': Pool.AcquireTimeoutSeconds must not be negative.";
        }

        // Sizing an unpooled connection is not a harmless leftover - it says the operator expects a pool
        // that will not exist, so refusing beats quietly ignoring half of what they wrote.
        if (Enabled is false && (MinSize is not null || MaxSize is not null))
        {
            return $"Data connection '{connectionName}': Pool.MinSize / Pool.MaxSize cannot apply when Pool.Enabled is false.";
        }

        return null;
    }
}
