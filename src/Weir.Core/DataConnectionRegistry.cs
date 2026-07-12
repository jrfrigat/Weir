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
            _map[name] = new DataConnectionDescriptor
            {
                Name = name,
                Provider = entry.Provider,
                ConnectionString = entry.ConnectionString,
                DefaultCommandTimeoutSeconds = entry.DefaultCommandTimeoutSeconds,
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
