using Microsoft.Extensions.Options;
using Weir.Abstractions;

namespace Weir.Core;

/// <summary>Configuration entry for one named data connection (bound from <c>Weir:DataConnections</c>).</summary>
public sealed class DataConnectionEntry
{
    /// <summary>Provider key, e.g. <c>SqlServer</c>.</summary>
    public string Provider { get; set; } = "SqlServer";

    /// <summary>ADO.NET connection string.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Optional default command timeout, in seconds.</summary>
    public int? DefaultCommandTimeoutSeconds { get; set; }

    /// <summary>
    /// Optional connection-pool settings for this connection. Bound from
    /// <c>Weir:DataConnections:&lt;name&gt;:Pool</c>; every property is optional and an omitted one
    /// leaves the driver's own default in place.
    /// </summary>
    public DataConnectionPoolEntry Pool { get; set; } = new();
}

/// <summary>
/// Configuration entry for a connection's pool. A mutable, settable mirror of
/// <see cref="ConnectionPoolOptions"/>, because the configuration binder needs public setters and the
/// descriptor the rest of the system reads should stay immutable.
/// </summary>
public sealed class DataConnectionPoolEntry
{
    /// <summary>Whether physical connections are pooled and reused. Null keeps the driver default.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Connections kept open while idle. Null keeps the driver default.</summary>
    public int? MinSize { get; set; }

    /// <summary>Ceiling on physical connections. Null keeps the driver default.</summary>
    public int? MaxSize { get; set; }

    /// <summary>Seconds a request waits for a free pooled connection. Null keeps the driver default.</summary>
    public int? AcquireTimeoutSeconds { get; set; }

    /// <summary>Converts the bound entry into the immutable form the descriptor carries.</summary>
    /// <returns>The equivalent options record.</returns>
    public ConnectionPoolOptions ToOptions() => new()
    {
        Enabled = Enabled,
        MinSize = MinSize,
        MaxSize = MaxSize,
        AcquireTimeoutSeconds = AcquireTimeoutSeconds,
    };
}

/// <summary>Options carrying the set of named data connections.</summary>
public sealed class WeirDataConnectionsOptions
{
    /// <summary>Named connections keyed by logical name.</summary>
    public IDictionary<string, DataConnectionEntry> Connections { get; } =
        new Dictionary<string, DataConnectionEntry>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Default <see cref="IDataConnectionRegistry"/> built from bound options.</summary>
public sealed class DataConnectionRegistry : IDataConnectionRegistry
{
    private readonly Dictionary<string, DataConnectionDescriptor> _map;

    /// <summary>Builds the registry from configured connections.</summary>
    public DataConnectionRegistry(IOptions<WeirDataConnectionsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _map = new Dictionary<string, DataConnectionDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in options.Value.Connections)
        {
            var pool = (entry.Pool ?? new DataConnectionPoolEntry()).ToOptions();

            // A pool that cannot exist as described is a startup failure, not a runtime surprise: the
            // registry is built while the host starts, so the process refuses to come up rather than
            // serving traffic against a connection whose configuration was half ignored.
            if (pool.Validate(name) is { } reason)
            {
                throw new WeirConfigurationException(reason);
            }

            _map[name] = new DataConnectionDescriptor
            {
                Name = name,
                Provider = entry.Provider,
                ConnectionString = entry.ConnectionString,
                DefaultCommandTimeoutSeconds = entry.DefaultCommandTimeoutSeconds,
                Pool = pool,
            };
        }
    }

    /// <inheritdoc />
    public bool TryGet(string name, out DataConnectionDescriptor descriptor)
    {
        if (_map.TryGetValue(name, out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = null!;
        return false;
    }

    /// <inheritdoc />
    public DataConnectionDescriptor Resolve(string name) =>
        _map.TryGetValue(name, out var found)
            ? found
            : throw new WeirConfigurationException($"Data connection '{name}' is not configured.");

    /// <inheritdoc />
    public IReadOnlyCollection<DataConnectionDescriptor> All => _map.Values;
}
