namespace Weir.Abstractions;

/// <summary>
/// Resolves a logical data-connection name to its provider and connection string. Populated from
/// configuration (<c>Weir:DataConnections</c>); lets one Weir instance target many servers at once.
/// </summary>
public interface IDataConnectionRegistry
{
    /// <summary>Attempts to resolve a connection by name.</summary>
    bool TryGet(string name, out DataConnectionDescriptor descriptor);

    /// <summary>Resolves a connection by name, throwing if it is not registered.</summary>
    DataConnectionDescriptor Resolve(string name);

    /// <summary>All registered connections.</summary>
    IReadOnlyCollection<DataConnectionDescriptor> All { get; }
}

/// <summary>Describes one named data connection.</summary>
public sealed record DataConnectionDescriptor
{
    /// <summary>Logical connection name referenced by endpoints.</summary>
    public required string Name { get; init; }

    /// <summary>Provider key, e.g. <c>SqlServer</c>; selects the matching <see cref="IDbConnector"/>.</summary>
    public required string Provider { get; init; }

    /// <summary>ADO.NET connection string.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Optional default command timeout, in seconds, applied when an endpoint sets none.</summary>
    public int? DefaultCommandTimeoutSeconds { get; init; }
}
