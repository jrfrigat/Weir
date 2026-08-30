namespace Weir.Contracts;

/// <summary>
/// What an endpoint does with the database object it names. The default,
/// <see cref="EndpointOperation.Invoke"/>, is the original behaviour and the only one that existed
/// before this was introduced: call a stored procedure or function and stream what it returns.
/// <para>
/// The other two describe work against a table or a view, where there is no procedure to call and
/// Weir composes the statement itself from the endpoint's metadata. They exist because the two
/// commonest reasons to write a procedure at all - "read this lookup list" and "load these rows" -
/// are pure plumbing: the procedure would carry no logic, and every table that needs one needs its
/// own. The metadata says which table, which columns and which filters, and the connector builds the
/// same parameterized statement it would have called.
/// </para>
/// </summary>
public enum EndpointOperation
{
    /// <summary>Call the stored procedure or function named by the endpoint (the default).</summary>
    Invoke,

    /// <summary>Read a table or view as a lookup list: projection, filters, search, ordering, paging.</summary>
    Dictionary,

    /// <summary>Write a batch of rows from the request body into a table.</summary>
    Import,
}

/// <summary>How one dictionary filter compares its column against the value the caller supplied.</summary>
public enum DictionaryOperator
{
    /// <summary>Column equals the value.</summary>
    Equals,

    /// <summary>Column differs from the value.</summary>
    NotEquals,

    /// <summary>Column is greater than the value.</summary>
    GreaterThan,

    /// <summary>Column is greater than or equal to the value.</summary>
    GreaterOrEqual,

    /// <summary>Column is less than the value.</summary>
    LessThan,

    /// <summary>Column is less than or equal to the value.</summary>
    LessOrEqual,

    /// <summary>Column contains the value as a substring (case-insensitive).</summary>
    Contains,

    /// <summary>Column starts with the value (case-insensitive).</summary>
    StartsWith,

    /// <summary>Column matches any element of the array the caller supplied.</summary>
    In,
}

/// <summary>What an import does with a row whose key already exists in the target table.</summary>
public enum ImportMode
{
    /// <summary>Insert every row. A row that collides with a constraint fails the request.</summary>
    Insert,

    /// <summary>
    /// Insert rows whose key is new and update the rest, matched on
    /// <see cref="ImportPolicy.KeyColumns"/>. Needs at least one key column.
    /// </summary>
    Upsert,

    /// <summary>
    /// Empty the target table, then insert every row, both inside one transaction. The table is
    /// whatever the request says it is; a failed request leaves the previous contents untouched.
    /// </summary>
    Replace,
}

/// <summary>
/// A dictionary endpoint's read shape: which columns of the table or view are projected, which
/// filters the caller may apply, what the free-text search covers, how rows are ordered and how they
/// are paged. Only meaningful when the endpoint's <see cref="EndpointDefinition.Operation"/> is
/// <see cref="EndpointOperation.Dictionary"/>.
/// <para>
/// Nothing here is taken from the request as SQL. Column and table names come from this metadata,
/// which only an admin can write; every value the caller supplies travels as a bound parameter. The
/// caller chooses among what is configured (which filter, which sort column) and never names a column
/// the endpoint has not already declared.
/// </para>
/// </summary>
public sealed record DictionaryPolicy
{
    /// <summary>
    /// Projected columns, in output order. Empty selects every column of the object, which is
    /// convenient while building an endpoint and a poor default to leave in place: a later
    /// <c>ALTER TABLE</c> silently widens every response.
    /// </summary>
    public IReadOnlyList<DictionaryColumn> Columns { get; init; } = [];

    /// <summary>
    /// The column holding each row's identity, for a client binding the result to a picker. Optional,
    /// and advisory: it is reported in the endpoint's description and does not change the query.
    /// </summary>
    public string? ValueColumn { get; init; }

    /// <summary>
    /// The column holding each row's display text, the companion to <see cref="ValueColumn"/>. Also
    /// advisory, with one exception: it is the fallback sort when <see cref="OrderBy"/> is empty.
    /// </summary>
    public string? LabelColumn { get; init; }

    /// <summary>
    /// Columns the free-text <c>search</c> query parameter matches against, combined with OR. Empty
    /// disables search: a <c>search</c> value on an endpoint that declares none is refused rather
    /// than ignored, so a client cannot believe it filtered when it did not.
    /// </summary>
    public IReadOnlyList<string> SearchColumns { get; init; } = [];

    /// <summary>Filters the caller may apply, each bound to its own request parameter.</summary>
    public IReadOnlyList<DictionaryFilter> Filters { get; init; } = [];

    /// <summary>
    /// Default ordering. Empty falls back to <see cref="LabelColumn"/>, then to
    /// <see cref="ValueColumn"/>; if neither is set the rows come back in whatever order the database
    /// produces them, which is not an order and must not be paged over.
    /// </summary>
    public IReadOnlyList<DictionarySort> OrderBy { get; init; } = [];

    /// <summary>
    /// Whether the caller may page with <c>page</c> / <c>pageSize</c>. Off returns the whole object,
    /// capped by the global row limit like any other response.
    /// </summary>
    public bool AllowPaging { get; init; } = true;

    /// <summary>Page size applied when the caller does not ask for one. Zero uses <see cref="MaxPageSize"/>.</summary>
    public int DefaultPageSize { get; init; } = 100;

    /// <summary>
    /// The largest page a caller may ask for. A larger <c>pageSize</c> is clamped to this rather than
    /// refused, so a client that asks for too much still gets an answer.
    /// </summary>
    public int MaxPageSize { get; init; } = 1000;

    /// <summary>
    /// Whether the response carries the unpaged row count as an output value. It costs a second
    /// <c>COUNT(*)</c> over the same filters, which is why it is opt-in: a picker that only ever shows
    /// the first page does not need it.
    /// </summary>
    public bool IncludeTotalCount { get; init; }
}

/// <summary>One projected column of a dictionary endpoint.</summary>
public sealed record DictionaryColumn
{
    /// <summary>Column name as it exists on the table or view.</summary>
    public required string Name { get; init; }

    /// <summary>Name to return it under. Null returns it under <see cref="Name"/>.</summary>
    public string? Alias { get; init; }
}

/// <summary>One sort term of a dictionary endpoint's default ordering.</summary>
public sealed record DictionarySort
{
    /// <summary>Column to sort by. Must be a column of the object, not an alias.</summary>
    public required string Column { get; init; }

    /// <summary>Whether the sort is descending.</summary>
    public bool Descending { get; init; }
}

/// <summary>
/// One filter a dictionary endpoint offers its callers: a column, how it is compared, and the request
/// parameter carrying the value. A filter whose parameter the request omits is not applied, unless it
/// is <see cref="Required"/>, in which case the request is refused.
/// </summary>
public sealed record DictionaryFilter
{
    /// <summary>Column being filtered. Must be a column of the object, not an alias.</summary>
    public required string Column { get; init; }

    /// <summary>How the column is compared against the value.</summary>
    public DictionaryOperator Operator { get; init; } = DictionaryOperator.Equals;

    /// <summary>
    /// The request parameter carrying the value: a query-string key for a GET, a body property
    /// otherwise. Defaults to <see cref="Column"/>.
    /// </summary>
    public string? ParameterName { get; init; }

    /// <summary>Type the value is coerced to before it is bound.</summary>
    public WeirDbType DbType { get; init; } = WeirDbType.String;

    /// <summary>Whether the request must supply the value.</summary>
    public bool Required { get; init; }

    /// <summary>
    /// The name the value is read from on the request: <see cref="ParameterName"/> when set,
    /// <see cref="Column"/> otherwise. Derived, and so not part of the stored representation.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string RequestName => string.IsNullOrWhiteSpace(ParameterName) ? Column : ParameterName;
}

/// <summary>
/// An import endpoint's write shape: the target columns, where each one's value comes from in an
/// incoming row, what to do about rows that already exist, and the limits on one request. Only
/// meaningful when the endpoint's <see cref="EndpointDefinition.Operation"/> is
/// <see cref="EndpointOperation.Import"/>.
/// <para>
/// The rows themselves are the only part of the request that carries data. Which table they land in,
/// which columns exist and which JSON property feeds each column are all fixed by this metadata, so a
/// caller can neither reach a column the endpoint does not declare nor write to a different table by
/// naming one.
/// </para>
/// </summary>
public sealed record ImportPolicy
{
    /// <summary>Target columns, in the order they are written. At least one is required.</summary>
    public IReadOnlyList<ImportColumn> Columns { get; init; } = [];

    /// <summary>What to do with a row whose key already exists.</summary>
    public ImportMode Mode { get; init; } = ImportMode.Insert;

    /// <summary>
    /// Columns identifying an existing row, for <see cref="ImportMode.Upsert"/>. Ignored by the other
    /// modes; an upsert with none configured is refused when the endpoint is saved.
    /// </summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>
    /// The body property holding the array of rows. Defaults to <c>rows</c>. A body that is itself an
    /// array is accepted whatever this says, since there is then nothing to name.
    /// </summary>
    public string? RowsProperty { get; init; }

    /// <summary>
    /// Rows per statement sent to the database. Larger batches mean fewer round trips and more
    /// parameters per command; every provider caps the latter, and the connector lowers the batch on
    /// its own when the column count would exceed that cap. Zero uses the connector's default.
    /// </summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>
    /// The most rows one request may carry. Zero uses the system's <c>MaxImportRows</c> setting. The
    /// smaller of the two always wins, so an endpoint can tighten the system limit but not raise it.
    /// </summary>
    public int MaxRows { get; init; }

    /// <summary>
    /// Whether every batch of one request runs inside a single transaction, so a failure part-way
    /// through leaves the table as it was. <see cref="ImportMode.Replace"/> is transactional whatever
    /// this says - emptying a table outside one would be a window with no data in it.
    /// </summary>
    public bool Transactional { get; init; } = true;
}

/// <summary>One target column of an import endpoint, and where its value comes from.</summary>
public sealed record ImportColumn
{
    /// <summary>Column name on the target table.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Property to read from each incoming row object. Null reads the property named
    /// <see cref="Name"/>.
    /// </summary>
    public string? SourceProperty { get; init; }

    /// <summary>Type the value is coerced to before it is bound.</summary>
    public WeirDbType DbType { get; init; } = WeirDbType.String;

    /// <summary>Whether every row must carry a value for this column.</summary>
    public bool Required { get; init; }

    /// <summary>Value written when a row omits the property. Null writes SQL NULL.</summary>
    public object? DefaultValue { get; init; }

    /// <summary>Size / max length for sized types.</summary>
    public int? Size { get; init; }

    /// <summary>Numeric precision.</summary>
    public byte? Precision { get; init; }

    /// <summary>Numeric scale.</summary>
    public byte? Scale { get; init; }

    /// <summary>
    /// The property the value is read from: <see cref="SourceProperty"/> when set, <see cref="Name"/>
    /// otherwise. Derived, and so not part of the stored representation.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string RowProperty => string.IsNullOrWhiteSpace(SourceProperty) ? Name : SourceProperty;
}
