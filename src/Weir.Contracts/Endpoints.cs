using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Weir.Contracts;

/// <summary>
/// A single endpoint definition: the metadata that maps an HTTP route to a stored procedure or
/// function. Managed from the admin UI and resolved into the in-memory routing table at runtime.
/// </summary>
public sealed record EndpointDefinition
{
    /// <summary>
    /// Per-instance cache of <see cref="QualifiedName"/>, keyed on the definition itself.
    /// <para>
    /// Deliberately a static table rather than an instance field. A record's synthesized
    /// <c>Equals</c> and <c>GetHashCode</c> compare every instance field it declares, so a
    /// lazily-populated backing field would make two otherwise-equal definitions compare unequal from
    /// the moment one of them had its qualified name read - equality that depends on whether some
    /// other thread happened to read a property first. Keying off the instance also rules out a stale
    /// value: a <c>with</c> expression that changes <see cref="Schema"/> or <see cref="ObjectName"/>
    /// produces a new object, which is a new key here, so it can never inherit the original's name.
    /// Entries are collected together with the definition they belong to.
    /// </para>
    /// </summary>
    private static readonly ConditionalWeakTable<EndpointDefinition, string> QualifiedNameCache = new();

    /// <summary>Stable identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Route relative to the API base, e.g. <c>orders/create</c>.</summary>
    public required string Route { get; init; }

    /// <summary>HTTP method the endpoint answers on. Defaults to <c>POST</c>.</summary>
    public string HttpMethod { get; init; } = "POST";

    /// <summary>Name of the target data connection (see <c>Weir:DataConnections</c>).</summary>
    public required string ConnectionName { get; init; }

    /// <summary>The kind of database object being invoked.</summary>
    public DbObjectType ObjectType { get; init; } = DbObjectType.StoredProcedure;

    /// <summary>Schema of the object. Defaults to <c>dbo</c>.</summary>
    public string Schema { get; init; } = "dbo";

    /// <summary>Name of the procedure / function (without schema).</summary>
    public required string ObjectName { get; init; }

    /// <summary>
    /// <see cref="Schema"/> and <see cref="ObjectName"/> joined with a dot, e.g. <c>dbo.usp_GetOrder</c>.
    /// Constant for the life of the definition, so it is built once per instance and reused rather than
    /// concatenated again on every call that reports it to an observer. Derived, and so not part of the
    /// stored or transmitted representation.
    /// </summary>
    [JsonIgnore]
    public string QualifiedName =>
        QualifiedNameCache.GetValue(this, static endpoint => string.Concat(endpoint.Schema, ".", endpoint.ObjectName));

    /// <summary>Expected shape of the primary result.</summary>
    public ResultMode ResultMode { get; init; } = ResultMode.MultiRow;

    /// <summary>Per-command timeout override, in seconds. Null uses the connection default.</summary>
    public int? CommandTimeoutSeconds { get; init; }

    /// <summary>Whether the endpoint is currently served.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When true, SQL informational messages (<c>PRINT</c> / notices / info) are omitted from the
    /// response envelope - <c>messages</c> is written as an empty array. Useful for procedures that emit
    /// chatty diagnostics you do not want to leak to callers. Defaults to false (messages are returned).
    /// </summary>
    public bool SuppressMessages { get; init; }

    /// <summary>Result-caching policy for the endpoint.</summary>
    public CachePolicy Cache { get; init; } = new();

    /// <summary>Request-logging policy for the endpoint (what to record in the request log).</summary>
    public EndpointLogging Logging { get; init; } = new();

    /// <summary>Parameter definitions, in declaration order.</summary>
    public IReadOnlyList<EndpointParameter> Parameters { get; init; } = [];

    /// <summary>Scopes an API key must hold to call this endpoint. Empty = any authenticated key.</summary>
    public IReadOnlyList<string> RequiredScopes { get; init; } = [];

    /// <summary>Human-readable description shown in the admin UI and generated docs.</summary>
    public string? Description { get; init; }

    /// <summary>When the definition was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the definition was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Per-endpoint request-logging policy. Controls what the data-plane request log records for this
/// endpoint. Parameter and result capture are opt-in (off by default) because they can hold PII; the
/// engine only captures them when the corresponding flag is set. All logging is additionally gated by
/// the global <c>RequestLogEnabled</c> setting.
/// </summary>
public sealed record EndpointLogging
{
    /// <summary>Whether this endpoint's calls are written to the request log at all. Defaults to true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether to capture the request's scalar parameter values (PII-bearing; opt-in).</summary>
    public bool LogParameters { get; init; }

    /// <summary>Whether to capture the response body, capped in size (PII-bearing; opt-in).</summary>
    public bool LogResult { get; init; }

    /// <summary>
    /// Per-endpoint override for the "slow" threshold, as a percentage above this endpoint's rolling
    /// average duration. Null uses the global <c>SlowRequestThresholdPercent</c> setting.
    /// </summary>
    public int? SlowThresholdPercent { get; init; }
}

/// <summary>Definition of one endpoint parameter and how it binds to the database.</summary>
public sealed record EndpointParameter
{
    /// <summary>Logical name - the JSON body property / query key the client uses.</summary>
    public required string Name { get; init; }

    /// <summary>Database parameter name (e.g. <c>@CustomerId</c>). Defaults to <see cref="Name"/>.</summary>
    public string? DbParameterName { get; init; }

    /// <summary>Where the value is read from on the request.</summary>
    public ParameterSource Source { get; init; } = ParameterSource.Body;

    /// <summary>Parameter direction.</summary>
    public ParameterDirection Direction { get; init; } = ParameterDirection.Input;

    /// <summary>Provider-agnostic type. Use <see cref="WeirDbType.Structured"/> for a TVP.</summary>
    public WeirDbType DbType { get; init; } = WeirDbType.String;

    /// <summary>Whether the request must supply the value.</summary>
    public bool Required { get; init; }

    /// <summary>Default applied when the request omits the value.</summary>
    public object? DefaultValue { get; init; }

    /// <summary>Size / max length for sized types.</summary>
    public int? Size { get; init; }

    /// <summary>Numeric precision.</summary>
    public byte? Precision { get; init; }

    /// <summary>Numeric scale.</summary>
    public byte? Scale { get; init; }

    /// <summary>For <see cref="WeirDbType.Structured"/> / UDTs: the SQL type name, e.g. <c>dbo.OrderItemType</c>.</summary>
    public string? TypeName { get; init; }

    /// <summary>For a TVP: the ordered column schema of each row.</summary>
    public IReadOnlyList<TvpColumn>? TableColumns { get; init; }

    /// <summary>Optional regular expression the value must match.</summary>
    public string? ValidationRegex { get; init; }

    /// <summary>Header name when <see cref="Source"/> is <see cref="ParameterSource.Header"/>.</summary>
    public string? HeaderName { get; init; }

    /// <summary>Claim type when <see cref="Source"/> is <see cref="ParameterSource.Claim"/>.</summary>
    public string? ClaimType { get; init; }
}

/// <summary>One column of a table-valued parameter's row schema.</summary>
public sealed record TvpColumn
{
    /// <summary>Column name, matched against the keys of each incoming row object.</summary>
    public required string Name { get; init; }

    /// <summary>Column type.</summary>
    public WeirDbType DbType { get; init; } = WeirDbType.String;

    /// <summary>Size / max length for sized types.</summary>
    public int? Size { get; init; }

    /// <summary>Numeric precision.</summary>
    public byte? Precision { get; init; }

    /// <summary>Numeric scale.</summary>
    public byte? Scale { get; init; }

    /// <summary>Zero-based column position in the TVP row.</summary>
    public int Ordinal { get; init; }
}

/// <summary>Per-endpoint result-caching policy. Configured from the admin UI; clients cannot bypass it.</summary>
public sealed record CachePolicy
{
    /// <summary>
    /// Per-instance cache of <see cref="SortedVaryByParameters"/>, keyed on the policy itself.
    /// <para>
    /// Deliberately a static table rather than an instance field, for the same reasons as
    /// <see cref="EndpointDefinition.QualifiedName"/>: an instance field would join this record's
    /// synthesized <c>Equals</c> and <c>GetHashCode</c> and make equality depend on whether the sorted
    /// list had been read yet, and a <c>with</c> expression that replaces
    /// <see cref="VaryByParameters"/> would copy the field and carry the old order over. Keying off the
    /// instance avoids both: a new object is a new key, so it sorts afresh.
    /// </para>
    /// </summary>
    private static readonly ConditionalWeakTable<CachePolicy, string[]> SortedVaryByCache = new();

    /// <summary>Whether responses for the endpoint are cached.</summary>
    public bool Enabled { get; init; }

    /// <summary>Time-to-live for a cached response, in seconds.</summary>
    public int TtlSeconds { get; init; }

    /// <summary>Names of the input parameters whose values form the cache key. Empty = key on route only.</summary>
    public IReadOnlyList<string> VaryByParameters { get; init; } = [];

    /// <summary>
    /// <see cref="VaryByParameters"/> in ordinal order. The cache key must not depend on the order the
    /// names happen to be configured in, and the set is constant for the life of the policy, so the sort
    /// runs once per instance instead of on every cache-eligible request. Derived, and so not part of the
    /// stored or transmitted representation.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> SortedVaryByParameters =>
        SortedVaryByCache.GetValue(this, static policy => [.. policy.VaryByParameters.OrderBy(x => x, StringComparer.Ordinal)]);

    /// <summary>Whether the calling API key is part of the cache key (per-key isolation).</summary>
    public bool VaryByApiKey { get; init; }
}
