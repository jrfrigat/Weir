using System.Text.Json;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>Supplies string values for non-body parameter sources (query, route, header, claim).</summary>
public interface IValueSource
{
    /// <summary>Attempts to read a value by key.</summary>
    bool TryGet(string key, out string? value);
}

/// <summary>An empty value source that never resolves a key.</summary>
public sealed class EmptyValueSource : IValueSource
{
    /// <summary>Shared instance.</summary>
    public static readonly EmptyValueSource Instance = new();

    private EmptyValueSource()
    {
    }

    /// <inheritdoc />
    public bool TryGet(string key, out string? value)
    {
        value = null;
        return false;
    }
}

/// <summary>
/// One data-plane invocation: the resolved endpoint plus the raw request inputs. The host builds
/// this from the ASP.NET Core <c>HttpContext</c> without the engine depending on ASP.NET.
/// </summary>
public sealed class WeirInvocation
{
    /// <summary>The resolved endpoint definition.</summary>
    public required EndpointDefinition Endpoint { get; init; }

    /// <summary>The parsed JSON request body (an object), when present.</summary>
    public JsonElement Body { get; init; }

    /// <summary>Whether <see cref="Body"/> holds a value.</summary>
    public bool HasBody { get; init; }

    /// <summary>Query-string values.</summary>
    public IValueSource Query { get; init; } = EmptyValueSource.Instance;

    /// <summary>Route-template values.</summary>
    public IValueSource Route { get; init; } = EmptyValueSource.Instance;

    /// <summary>Request headers.</summary>
    public IValueSource Header { get; init; } = EmptyValueSource.Instance;

    /// <summary>Authenticated principal claims.</summary>
    public IValueSource Claim { get; init; } = EmptyValueSource.Instance;

    /// <summary>Identifying prefix of the calling API key (for telemetry / cache vary-by).</summary>
    public string? ApiKeyPrefix { get; init; }
}
