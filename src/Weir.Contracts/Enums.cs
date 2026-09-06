namespace Weir.Contracts;

/// <summary>Where the value of an endpoint parameter is read from on the incoming request.</summary>
public enum ParameterSource
{
    /// <summary>A property of the JSON request body (the default).</summary>
    Body,

    /// <summary>A query-string value.</summary>
    Query,

    /// <summary>A segment of the route template.</summary>
    Route,

    /// <summary>An HTTP request header (see <see cref="EndpointParameter.HeaderName"/>).</summary>
    Header,

    /// <summary>A claim from the authenticated principal (see <see cref="EndpointParameter.ClaimType"/>).</summary>
    Claim,

    /// <summary>A fixed server-side constant; never taken from the client.</summary>
    Const,
}

/// <summary>Direction of a database parameter. Mirrors ADO.NET but is provider-agnostic.</summary>
public enum ParameterDirection
{
    /// <summary>Value flows in to the procedure.</summary>
    Input,

    /// <summary>Value is produced by the procedure and returned to the caller.</summary>
    Output,

    /// <summary>Value flows in and an updated value flows back out.</summary>
    InputOutput,

    /// <summary>The procedure's integer RETURN value.</summary>
    ReturnValue,
}

/// <summary>The kind of database object an endpoint maps to.</summary>
public enum DbObjectType
{
    /// <summary>A stored procedure.</summary>
    StoredProcedure,

    /// <summary>A table-valued function (produces a row set).</summary>
    TableValuedFunction,

    /// <summary>A scalar function (produces a single value).</summary>
    ScalarFunction,

    /// <summary>
    /// A base table. Not callable: an endpoint naming one composes its own statement, so this is only
    /// valid together with <see cref="EndpointOperation.Dictionary"/> or
    /// <see cref="EndpointOperation.Import"/>.
    /// </summary>
    Table,

    /// <summary>
    /// A view. Readable but not writable through Weir, so this pairs with
    /// <see cref="EndpointOperation.Dictionary"/> only.
    /// </summary>
    View,
}

/// <summary>The shape of the primary result produced by the database object.</summary>
public enum ResultMode
{
    /// <summary>Zero or more rows (the default).</summary>
    MultiRow,

    /// <summary>At most one row.</summary>
    SingleRow,

    /// <summary>A single scalar value.</summary>
    Scalar,

    /// <summary>No result set; only affected-row count / output parameters.</summary>
    NonQuery,

    /// <summary>Several result sets.</summary>
    MultiResultSet,
}

/// <summary>
/// How a response body reaches the caller. The trade is memory against error semantics: a streamed
/// response cannot be taken back, so a database failure part-way through a result set aborts the
/// connection instead of returning a clean problem+json - the bytes are already gone.
/// <para>
/// This only decides anything for an endpoint that is neither cached nor capturing its result for the
/// request log. Both of those have to hold the whole body anyway - there is nothing to store in the
/// cache, and nothing to write to the log, until it exists - so they buffer regardless of this.
/// </para>
/// </summary>
public enum ResponseDeliveryMode
{
    /// <summary>
    /// Decide from the endpoint's <see cref="ResultMode"/>: buffer where the result is declared small
    /// (<see cref="ResultMode.SingleRow"/>, <see cref="ResultMode.Scalar"/>,
    /// <see cref="ResultMode.NonQuery"/>), where atomic errors cost nothing worth counting, and stream
    /// the row-returning ones. The default, and the right answer for almost every endpoint.
    /// </summary>
    Auto,

    /// <summary>
    /// Write rows out as they are read. Time-to-first-byte does not wait for the last row and memory
    /// stays flat, at the cost of the abort-instead-of-400 behaviour described above.
    /// </summary>
    Stream,

    /// <summary>
    /// Build the whole envelope, then send it. The caller gets either a complete response or a clean
    /// error, never half of one - paid for by holding the entire body in memory first.
    /// </summary>
    Full,
}

/// <summary>Whether a data-plane response body is compressed (Brotli/gzip) before it is sent.</summary>
public enum ResponseCompressionMode
{
    /// <summary>
    /// Decide from the endpoint's <see cref="ResultMode"/>: compress the row-returning results
    /// (<see cref="ResultMode.MultiRow"/>), where a large JSON body pays for the CPU many times over on
    /// the wire, and skip the ones declared small (<see cref="ResultMode.SingleRow"/>,
    /// <see cref="ResultMode.Scalar"/>, <see cref="ResultMode.NonQuery"/>), where compression would cost
    /// more than it saves. The default, and the right answer for almost every endpoint.
    /// </summary>
    Auto,

    /// <summary>Always compress, whatever the result shape. For a route whose small-looking result is
    /// in fact large, or that is always read over a slow link.</summary>
    On,

    /// <summary>Never compress. For a route on a fast internal network where the bytes are cheap and the
    /// CPU is not, or whose payload is already incompressible.</summary>
    Off,
}

/// <summary>A provider-agnostic classification of a database failure, for error-rate telemetry.</summary>
public enum DbErrorCategory
{
    /// <summary>Not a recognized database error (for example a validation or cancellation failure).</summary>
    None = 0,

    /// <summary>A command or lock timeout.</summary>
    Timeout,

    /// <summary>A deadlock victim.</summary>
    Deadlock,

    /// <summary>A constraint violation (unique, foreign key, check, not-null).</summary>
    Constraint,

    /// <summary>A connection-level failure (server unreachable, dropped, login).</summary>
    Connection,

    /// <summary>A database error that did not fall into a more specific category.</summary>
    Other,
}

/// <summary>Convention markers for the outcome field on the per-call telemetry context.</summary>
public static class OutcomeCodes
{
    /// <summary>The call succeeded.</summary>
    public const string Ok = "ok";

    /// <summary>The call failed.</summary>
    public const string Error = "error";
}

/// <summary>
/// Provider-agnostic parameter type. <see cref="Structured"/> denotes a table-valued parameter (TVP).
/// Concrete connectors map these to their native DB types.
/// </summary>
public enum WeirDbType
{
    String,
    AnsiString,
    Boolean,
    Byte,
    Int16,
    Int32,
    Int64,
    Decimal,
    Double,
    Single,
    DateTime,
    DateTimeOffset,
    Date,
    Time,
    Guid,
    Binary,
    Json,
    Xml,

    /// <summary>A table-valued parameter (TVP). Carries rows rather than a scalar value.</summary>
    Structured,
}

/// <summary>
/// The front doors an endpoint answers on. Flags rather than one choice, because the same procedure is
/// often worth reaching two ways: HTTP for the caller whose client is one line of curl, and a long-lived
/// connection for the service that calls it a thousand times a minute.
/// <para>
/// A transport decides framing and authentication carrier, and nothing else. Parameter binding, the
/// response envelope, the cache, the required scopes, the grants and the rate limit are the same on all
/// three - a transport is a way in, not a second gateway with its own rules.
/// </para>
/// </summary>
[Flags]
public enum EndpointTransports
{
    /// <summary>
    /// No transport at all, which makes the endpoint unreachable. Refused when an endpoint is saved:
    /// <see cref="EndpointDefinition.Enabled"/> already says "not served", and says it where an operator
    /// will look for it.
    /// </summary>
    None = 0,

    /// <summary>
    /// Ordinary HTTP under <c>/api</c>. The default, and what every endpoint defined before this setting
    /// existed answers on.
    /// </summary>
    Http = 1,

    /// <summary>
    /// gRPC on the <c>weir.v1.WeirGateway</c> service: one unary call for a whole envelope, one
    /// server-streaming call that delivers it in chunks as the rows are read. For a service caller that
    /// wants HTTP/2 multiplexing and one connection instead of a request per call.
    /// </summary>
    Grpc = 2,

    /// <summary>
    /// A WebSocket session at <c>/ws</c>: the caller opens one connection, authenticates once on the
    /// handshake, and sends a request frame per call. For a client that makes many small calls and does
    /// not want to pay for a connection, a TLS handshake and an API-key lookup on each of them.
    /// </summary>
    WebSocket = 4,
}
