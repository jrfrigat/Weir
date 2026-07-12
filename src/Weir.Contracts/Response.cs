namespace Weir.Contracts;

/// <summary>
/// The single, consistent success envelope returned by every data-plane endpoint.
/// On the hot path the host streams this shape directly from the data reader rather than
/// materializing this type; it also serves as the documented contract and the model used by
/// the admin test console and generated OpenAPI.
/// </summary>
public sealed class WeirResponse
{
    /// <summary>
    /// All result sets produced by the object, as an array of result sets, each an array of rows,
    /// each row an ordered map of column name to value. A single result set is <c>[[ ... ]]</c>;
    /// an empty result set is <c>[[]]</c>.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Data { get; init; } = [];

    /// <summary>Output / input-output parameter values, keyed by logical parameter name. Null when none.</summary>
    public IReadOnlyDictionary<string, object?>? Output { get; init; }

    /// <summary>The procedure's integer RETURN value, when applicable.</summary>
    public int? ReturnValue { get; init; }

    /// <summary>Rows affected as reported by the driver.</summary>
    public int RowsAffected { get; init; }

    /// <summary>Whether the result was capped by the configured data-plane row limit and cut short.</summary>
    public bool Truncated { get; init; }

    /// <summary>SQL informational messages captured during execution (e.g. <c>PRINT</c>).</summary>
    public IReadOnlyList<SqlMessage> Messages { get; init; } = [];
}

/// <summary>
/// A SQL informational message surfaced during execution. Fields are filled on a best-effort
/// basis per provider; for SQL Server they come from <c>SqlConnection.InfoMessage</c>.
/// </summary>
public sealed record SqlMessage
{
    /// <summary>The message text.</summary>
    public required string Text { get; init; }

    /// <summary>Severity / class of the message (0 for a plain <c>PRINT</c>).</summary>
    public int Severity { get; init; }

    /// <summary>Provider error number, when available.</summary>
    public int Number { get; init; }

    /// <summary>Originating procedure name, when available.</summary>
    public string? Procedure { get; init; }

    /// <summary>Line number within the batch/procedure, when available.</summary>
    public int Line { get; init; }
}
