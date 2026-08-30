using System.Globalization;
using System.Text.Json;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>
/// Turns a request against a dictionary or import endpoint into the shape the connector executes.
/// The counterpart of <see cref="ParameterBinder"/> for endpoints that name a table rather than a
/// procedure: there are no declared parameters to bind, so what the caller sent is checked against the
/// endpoint's policy instead.
/// <para>
/// Everything a caller can influence is validated here and nothing is passed through as SQL. A filter
/// is applied only if the endpoint declares it; a sort column must be one the endpoint allows; a page
/// size is clamped rather than trusted; an import row may only carry the columns the endpoint lists.
/// </para>
/// </summary>
internal static class TableRequestBinder
{
    /// <summary>Query-string key carrying the free-text search.</summary>
    private const string SearchKey = "search";

    /// <summary>Query-string key carrying the 1-based page number.</summary>
    private const string PageKey = "page";

    /// <summary>Query-string key carrying the page size.</summary>
    private const string PageSizeKey = "pageSize";

    /// <summary>Query-string key carrying an explicit sort column.</summary>
    private const string SortKey = "sort";

    /// <summary>Query-string key carrying the sort direction ("asc" or "desc").</summary>
    private const string SortDirectionKey = "sortDir";

    /// <summary>
    /// Resolves one dictionary read from the request, and the values it should be cached by.
    /// </summary>
    /// <param name="invocation">The invocation carrying the request inputs.</param>
    /// <returns>The resolved read and the values keyed by their request names.</returns>
    /// <exception cref="WeirValidationException">The request asked for something the endpoint does not offer.</exception>
    /// <exception cref="WeirConfigurationException">The endpoint is not a usable dictionary endpoint.</exception>
    public static (DictionaryQuery Query, IReadOnlyDictionary<string, object?> Values) BindDictionary(
        WeirInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var policy = invocation.Endpoint.Dictionary
            ?? throw new WeirConfigurationException(
                $"Endpoint '{invocation.Endpoint.Route}' is a dictionary endpoint but carries no dictionary policy.");

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string[]>? errors = null;

        var filters = new List<DictionaryFilterValue>(policy.Filters.Count);
        foreach (var filter in policy.Filters)
        {
            var name = filter.RequestName;
            var (present, value, list) = ReadFilter(invocation, filter);
            if (!present)
            {
                if (filter.Required)
                {
                    errors ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                    errors[name] = [$"Filter '{name}' is required."];
                }

                continue;
            }

            filters.Add(new DictionaryFilterValue { Filter = filter, Value = value, Values = list });
            values[name] = list is null ? value : string.Join(",", list.Select(Format));
        }

        var search = ReadString(invocation, SearchKey);
        if (!string.IsNullOrWhiteSpace(search))
        {
            if (policy.SearchColumns.Count == 0)
            {
                // Ignoring it would let a client believe it had filtered when it had not, and quietly
                // return the whole table as if that were the answer to its search.
                errors ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                errors[SearchKey] = ["This endpoint does not support search."];
            }
            else
            {
                values[SearchKey] = search;
            }
        }

        if (errors is { Count: > 0 })
        {
            throw new WeirValidationException("One or more filters are invalid.", errors);
        }

        var orderBy = ResolveOrdering(invocation, policy);
        var (skip, take) = ResolvePaging(invocation, policy, values);

        var query = new DictionaryQuery
        {
            Policy = policy,
            Filters = filters,
            Search = string.IsNullOrWhiteSpace(search) || policy.SearchColumns.Count == 0 ? null : search,
            OrderBy = orderBy,
            Skip = skip,
            Take = take,
            // A count over an unordered, unpaged read tells the caller the same thing the rows already
            // do, so it is only worth a round trip when there is a page to put it in context.
            IncludeTotalCount = policy.IncludeTotalCount && skip is not null,
        };

        if (orderBy.Count > 0)
        {
            values["__sort"] = string.Join(",", orderBy.Select(s => s.Column + (s.Descending ? " desc" : string.Empty)));
        }

        return (query, values);
    }

    /// <summary>
    /// Resolves one import from the request body: the rows, coerced to the endpoint's column types.
    /// </summary>
    /// <param name="invocation">The invocation carrying the request body.</param>
    /// <param name="maxImportRows">The system-wide row ceiling; zero means unlimited.</param>
    /// <returns>The payload the connector writes.</returns>
    /// <exception cref="WeirValidationException">The body is missing, misshapen or too large.</exception>
    /// <exception cref="WeirConfigurationException">The endpoint is not a usable import endpoint.</exception>
    public static ImportPayload BindImport(WeirInvocation invocation, int maxImportRows)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var policy = invocation.Endpoint.Import
            ?? throw new WeirConfigurationException(
                $"Endpoint '{invocation.Endpoint.Route}' is an import endpoint but carries no import policy.");

        if (policy.Columns.Count == 0)
        {
            throw new WeirConfigurationException(
                $"Endpoint '{invocation.Endpoint.Route}' declares no columns to import into.");
        }

        var array = ReadRowArray(invocation, policy);
        var limit = EffectiveRowLimit(policy.MaxRows, maxImportRows);
        var declared = array.GetArrayLength();
        if (limit > 0 && declared > limit)
        {
            throw new WeirValidationException(
                $"The request carries {declared.ToString(CultureInfo.InvariantCulture)} rows; " +
                $"this endpoint accepts at most {limit.ToString(CultureInfo.InvariantCulture)}.");
        }

        var rows = new List<IReadOnlyList<object?>>(declared);
        Dictionary<string, string[]>? errors = null;
        var index = 0;

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                errors[RowKey(index)] = ["Each row must be a JSON object."];
                index++;
                continue;
            }

            var cells = new object?[policy.Columns.Count];
            for (var column = 0; column < policy.Columns.Count; column++)
            {
                var definition = policy.Columns[column];
                if (!element.TryGetProperty(definition.RowProperty, out var cell) || cell.ValueKind == JsonValueKind.Null)
                {
                    if (definition.Required)
                    {
                        errors ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                        errors[RowKey(index)] = [$"Column '{definition.Name}' is required."];
                    }

                    cells[column] = definition.DefaultValue;
                    continue;
                }

                try
                {
                    cells[column] = ValueCoercion.FromJson(cell, definition.DbType);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or JsonException or InvalidOperationException)
                {
                    errors ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                    errors[RowKey(index)] = [$"Column '{definition.Name}' has an invalid value."];
                }
            }

            rows.Add(cells);
            index++;
        }

        if (errors is { Count: > 0 })
        {
            throw new WeirValidationException("One or more rows are invalid.", errors);
        }

        if (policy.Mode == ImportMode.Upsert && policy.KeyColumns.Count == 0)
        {
            throw new WeirConfigurationException(
                $"Endpoint '{invocation.Endpoint.Route}' imports in upsert mode but names no key columns.");
        }

        return new ImportPayload
        {
            Columns = policy.Columns,
            Rows = rows,
            Mode = policy.Mode,
            KeyColumns = policy.KeyColumns,
            BatchSize = policy.BatchSize,
            Transactional = policy.Transactional,
        };
    }

    /// <summary>Finds the array of rows in the request body, whatever shape the endpoint allows.</summary>
    private static JsonElement ReadRowArray(WeirInvocation invocation, ImportPolicy policy)
    {
        if (!invocation.HasBody)
        {
            throw new WeirValidationException("An import request must carry a JSON body.");
        }

        // A bare array is accepted whatever the endpoint names its rows property: there is then nothing
        // to name, and refusing it would be pedantry about a body that is unambiguous.
        if (invocation.Body.ValueKind == JsonValueKind.Array)
        {
            return invocation.Body;
        }

        var property = string.IsNullOrWhiteSpace(policy.RowsProperty) ? "rows" : policy.RowsProperty;
        if (invocation.Body.ValueKind == JsonValueKind.Object &&
            invocation.Body.TryGetProperty(property, out var rows) &&
            rows.ValueKind == JsonValueKind.Array)
        {
            return rows;
        }

        throw new WeirValidationException($"The request body must be an array, or an object with an array '{property}'.");
    }

    /// <summary>The row limit actually in force: the endpoint's, the system's, whichever is smaller.</summary>
    private static int EffectiveRowLimit(int endpointLimit, int systemLimit)
    {
        var endpoint = Math.Max(0, endpointLimit);
        var system = Math.Max(0, systemLimit);
        if (endpoint == 0)
        {
            return system;
        }

        return system == 0 ? endpoint : Math.Min(endpoint, system);
    }

    /// <summary>Reads one filter's value from wherever the request carries it.</summary>
    private static (bool Present, object? Value, IReadOnlyList<object?>? Values) ReadFilter(
        WeirInvocation invocation, DictionaryFilter filter)
    {
        var name = filter.RequestName;

        if (invocation.HasBody && invocation.Body.ValueKind == JsonValueKind.Object &&
            invocation.Body.TryGetProperty(name, out var element))
        {
            if (filter.Operator == DictionaryOperator.In)
            {
                return element.ValueKind == JsonValueKind.Array
                    ? (true, null, element.EnumerateArray().Select(e => ValueCoercion.FromJson(e, filter.DbType)).ToList())
                    : (true, null, new List<object?> { ValueCoercion.FromJson(element, filter.DbType) });
            }

            return (true, ValueCoercion.FromJson(element, filter.DbType), null);
        }

        if (invocation.Query.TryGet(name, out var text) && text is not null)
        {
            if (filter.Operator == DictionaryOperator.In)
            {
                // A repeated query key is not visible through the value source, which yields one string,
                // so a comma-separated list is the form an IN filter takes on a GET.
                var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return (true, null, parts.Select(part => ValueCoercion.FromString(part, filter.DbType)).ToList());
            }

            return (true, ValueCoercion.FromString(text, filter.DbType), null);
        }

        if (invocation.Route.TryGet(name, out var route) && route is not null)
        {
            return (true, ValueCoercion.FromString(route, filter.DbType), null);
        }

        return (false, null, null);
    }

    /// <summary>Reads a reserved query-string value, falling back to the body for a non-GET call.</summary>
    private static string? ReadString(WeirInvocation invocation, string key)
    {
        if (invocation.Query.TryGet(key, out var text) && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (invocation.HasBody && invocation.Body.ValueKind == JsonValueKind.Object &&
            invocation.Body.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return null;
    }

    /// <summary>
    /// Resolves the ordering: the caller's sort column when they named one the endpoint allows,
    /// otherwise the endpoint's own, otherwise the label or value column.
    /// </summary>
    private static IReadOnlyList<DictionarySort> ResolveOrdering(WeirInvocation invocation, DictionaryPolicy policy)
    {
        var requested = ReadString(invocation, SortKey);
        if (!string.IsNullOrWhiteSpace(requested))
        {
            // Only a column the endpoint already projects, sorts by or filters on may be named. A sort
            // column arrives as text and ends up in the statement, so the allow-list is what keeps it
            // from being anything but a column this endpoint has declared.
            var allowed = Allowed(policy).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var match = allowed.FirstOrDefault(column => string.Equals(column, requested, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                // Name what is sortable rather than only what is not. An endpoint that projects every
                // column still only sorts by the ones it declares somewhere, which is a surprise worth
                // answering in the error instead of leaving the caller to guess.
                var sortable = allowed.Count == 0
                    ? "This endpoint declares no sortable columns."
                    : "Sortable columns: " + string.Join(", ", allowed) + ".";

                throw new WeirValidationException(
                    "Invalid sort column.",
                    new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [SortKey] = [$"'{requested}' is not a sortable column of this endpoint. {sortable}"],
                    });
            }

            var direction = ReadString(invocation, SortDirectionKey);
            return [new DictionarySort
            {
                Column = match,
                Descending = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase),
            }];
        }

        if (policy.OrderBy.Count > 0)
        {
            return policy.OrderBy;
        }

        var fallback = !string.IsNullOrWhiteSpace(policy.LabelColumn) ? policy.LabelColumn
            : !string.IsNullOrWhiteSpace(policy.ValueColumn) ? policy.ValueColumn
            : null;

        return fallback is null ? [] : [new DictionarySort { Column = fallback }];
    }

    /// <summary>The columns a caller may sort by: everything the endpoint has already named.</summary>
    private static IEnumerable<string> Allowed(DictionaryPolicy policy)
    {
        foreach (var column in policy.Columns)
        {
            yield return column.Name;
        }

        foreach (var sort in policy.OrderBy)
        {
            yield return sort.Column;
        }

        foreach (var filter in policy.Filters)
        {
            yield return filter.Column;
        }

        foreach (var column in policy.SearchColumns)
        {
            yield return column;
        }

        if (!string.IsNullOrWhiteSpace(policy.ValueColumn))
        {
            yield return policy.ValueColumn;
        }

        if (!string.IsNullOrWhiteSpace(policy.LabelColumn))
        {
            yield return policy.LabelColumn;
        }
    }

    /// <summary>
    /// Resolves the page. Returns nulls when the endpoint does not page or the read has no ordering:
    /// paging an unordered result set returns an arbitrary subset that changes between identical calls,
    /// which is worse than returning everything.
    /// </summary>
    private static (int? Skip, int? Take) ResolvePaging(
        WeirInvocation invocation, DictionaryPolicy policy, Dictionary<string, object?> values)
    {
        if (!policy.AllowPaging)
        {
            return (null, null);
        }

        var maxPageSize = policy.MaxPageSize > 0 ? policy.MaxPageSize : 1000;
        var defaultPageSize = policy.DefaultPageSize > 0 ? policy.DefaultPageSize : maxPageSize;

        var page = Math.Max(1, ReadInt(invocation, PageKey) ?? 1);
        var pageSize = ReadInt(invocation, PageSizeKey) ?? defaultPageSize;
        pageSize = Math.Clamp(pageSize, 1, maxPageSize);

        values[PageKey] = page;
        values[PageSizeKey] = pageSize;
        return ((page - 1) * pageSize, pageSize);
    }

    /// <summary>Reads a reserved integer query value; null when absent or not a number.</summary>
    private static int? ReadInt(WeirInvocation invocation, string key)
    {
        var text = ReadString(invocation, key);
        if (text is not null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (invocation.HasBody && invocation.Body.ValueKind == JsonValueKind.Object &&
            invocation.Body.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var number))
        {
            return number;
        }

        return null;
    }

    /// <summary>Names one row in a validation-error dictionary.</summary>
    private static string RowKey(int index) =>
        "rows[" + index.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>Renders a filter value for the cache key.</summary>
    private static string Format(object? value) =>
        value is null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
