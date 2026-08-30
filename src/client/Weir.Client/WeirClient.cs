using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Weir.Client;

/// <summary>
/// Calls a Weir gateway's data plane. It knows the envelope and the error shape and nothing else:
/// routes, parameter names and result columns all live in the endpoint metadata on the gateway, so
/// this type does not change when a procedure does.
/// <para>
/// Register it with <c>AddWeirClient</c> and inject it, or construct it over an <see cref="HttpClient"/>
/// whose <c>BaseAddress</c> is the gateway origin.
/// </para>
/// </summary>
public sealed class WeirClient
{
    /// <summary>Serializer settings for request bodies: omit nulls so an unset parameter is absent, not null.</summary>
    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The configured HTTP client, pointed at the gateway origin.</summary>
    private readonly HttpClient _http;

    /// <summary>Creates the client over a configured <see cref="HttpClient"/>.</summary>
    /// <param name="httpClient">A client whose <c>BaseAddress</c> is the gateway origin.</param>
    public WeirClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
    }

    /// <summary>The underlying client, for a caller that needs to reach past this one.</summary>
    public HttpClient HttpClient => _http;

    // ===== Raw calls ==============================================================================

    /// <summary>Calls a GET endpoint; parameters travel in the query string.</summary>
    /// <param name="route">Route relative to the gateway, e.g. <c>api/orders</c>.</param>
    /// <param name="query">Parameters as an object or dictionary; null members are left out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed envelope.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public async Task<WeirResult> GetAsync(
        string route, object? query = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(BuildUrl(route, query), cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Calls a POST endpoint; parameters travel as a flat JSON body.</summary>
    /// <param name="route">Route relative to the gateway, e.g. <c>api/orders/create</c>.</param>
    /// <param name="body">Parameters as an object or dictionary; may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed envelope.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public Task<WeirResult> PostAsync(
        string route, object? body = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, route, body, cancellationToken);

    /// <summary>Calls a PUT endpoint; parameters travel as a flat JSON body.</summary>
    /// <param name="route">Route relative to the gateway.</param>
    /// <param name="body">Parameters as an object or dictionary; may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed envelope.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public Task<WeirResult> PutAsync(
        string route, object? body = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, route, body, cancellationToken);

    /// <summary>Calls a PATCH endpoint; parameters travel as a flat JSON body.</summary>
    /// <param name="route">Route relative to the gateway.</param>
    /// <param name="body">Parameters as an object or dictionary; may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed envelope.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public Task<WeirResult> PatchAsync(
        string route, object? body = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, route, body, cancellationToken);

    /// <summary>Calls a DELETE endpoint; parameters travel in the query string.</summary>
    /// <param name="route">Route relative to the gateway.</param>
    /// <param name="query">Parameters as an object or dictionary; null members are left out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed envelope.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public async Task<WeirResult> DeleteAsync(
        string route, object? query = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(BuildUrl(route, query), cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // ===== Typed calls ============================================================================

    /// <summary>Calls a GET endpoint and reads its first result set as typed rows.</summary>
    /// <typeparam name="T">The row model; its property names must match the SQL column names.</typeparam>
    /// <param name="route">Route relative to the gateway.</param>
    /// <param name="query">Parameters as an object or dictionary; null members are left out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The typed rows.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public async Task<IReadOnlyList<T>> GetListAsync<T>(
        string route, object? query = null, CancellationToken cancellationToken = default) =>
        (await GetAsync(route, query, cancellationToken).ConfigureAwait(false)).Set<T>();

    /// <summary>Calls a GET endpoint and reads the first row of its first result set.</summary>
    /// <typeparam name="T">The row model; its property names must match the SQL column names.</typeparam>
    /// <param name="route">Route relative to the gateway.</param>
    /// <param name="query">Parameters as an object or dictionary; null members are left out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The first typed row, or null when the set is empty.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public async Task<T?> GetSingleAsync<T>(
        string route, object? query = null, CancellationToken cancellationToken = default)
        where T : class =>
        (await GetAsync(route, query, cancellationToken).ConfigureAwait(false)).First<T>();

    /// <summary>Calls a GET endpoint and reads its single scalar value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="route">Route relative to the gateway.</param>
    /// <param name="query">Parameters as an object or dictionary; null members are left out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The value, or the default when the endpoint returned no row.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public async Task<T?> GetScalarAsync<T>(
        string route, object? query = null, CancellationToken cancellationToken = default) =>
        (await GetAsync(route, query, cancellationToken).ConfigureAwait(false)).Scalar<T>();

    /// <summary>Calls a POST endpoint and reads its first result set as typed rows.</summary>
    /// <typeparam name="T">The row model; its property names must match the SQL column names.</typeparam>
    /// <param name="route">Route relative to the gateway.</param>
    /// <param name="body">Parameters as an object or dictionary; may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The typed rows.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public async Task<IReadOnlyList<T>> PostListAsync<T>(
        string route, object? body = null, CancellationToken cancellationToken = default) =>
        (await PostAsync(route, body, cancellationToken).ConfigureAwait(false)).Set<T>();

    /// <summary>Calls a POST endpoint and reads the first row of its first result set.</summary>
    /// <typeparam name="T">The row model; its property names must match the SQL column names.</typeparam>
    /// <param name="route">Route relative to the gateway.</param>
    /// <param name="body">Parameters as an object or dictionary; may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The first typed row, or null when the set is empty.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public async Task<T?> PostSingleAsync<T>(
        string route, object? body = null, CancellationToken cancellationToken = default)
        where T : class =>
        (await PostAsync(route, body, cancellationToken).ConfigureAwait(false)).First<T>();

    // ===== Dictionary endpoints ===================================================================

    /// <summary>
    /// Reads one page from a dictionary endpoint. The reserved query keys - <c>search</c>,
    /// <c>page</c>, <c>pageSize</c>, <c>sort</c> and <c>sortDir</c> - are filled from the arguments;
    /// anything in <paramref name="filters"/> is sent alongside as the endpoint's own filters.
    /// </summary>
    /// <typeparam name="T">The row model; its property names must match the projected column names.</typeparam>
    /// <param name="route">Route of the dictionary endpoint, e.g. <c>api/dict/countries</c>.</param>
    /// <param name="search">Free-text search across the endpoint's search columns, or null.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Rows per page; the endpoint clamps it to its own maximum.</param>
    /// <param name="sort">Column to sort by; must be one the endpoint declares. Null uses its default.</param>
    /// <param name="descending">Whether to sort descending.</param>
    /// <param name="filters">The endpoint's own filters, keyed by their parameter names.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, with the unpaged total when the endpoint reports one.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public async Task<WeirPage<T>> GetPageAsync<T>(
        string route,
        string? search = null,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        bool descending = false,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (filters is not null)
        {
            foreach (var pair in filters)
            {
                query[pair.Key] = pair.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query["search"] = search;
        }

        query["page"] = page;
        if (pageSize is { } size)
        {
            query["pageSize"] = size;
        }

        if (!string.IsNullOrWhiteSpace(sort))
        {
            query["sort"] = sort;
            query["sortDir"] = descending ? "desc" : "asc";
        }

        var result = await GetAsync(route, query, cancellationToken).ConfigureAwait(false);
        return new WeirPage<T>
        {
            Items = result.Set<T>(),
            TotalCount = result.Output is not null && result.Output.ContainsKey("totalCount")
                ? result.OutputValue<int>("totalCount")
                : null,
            Page = page,
            PageSize = pageSize ?? (result.Data.Count > 0 ? result.Data[0].Count : 0),
            Truncated = result.Truncated,
        };
    }

    /// <summary>
    /// Reads every row of a dictionary endpoint by walking its pages, for a lookup small enough to
    /// hold. Stops when a page comes back short, so an endpoint that does not page returns in one call.
    /// </summary>
    /// <typeparam name="T">The row model; its property names must match the projected column names.</typeparam>
    /// <param name="route">Route of the dictionary endpoint.</param>
    /// <param name="filters">The endpoint's own filters, keyed by their parameter names.</param>
    /// <param name="pageSize">Rows per request; the endpoint clamps it to its own maximum.</param>
    /// <param name="maxRows">
    /// The most rows to accumulate before stopping, so a lookup that turns out to be a fact table does
    /// not quietly become an out-of-memory error. Zero means no limit.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every row the endpoint returned, up to the limit.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public async Task<IReadOnlyList<T>> GetAllAsync<T>(
        string route,
        IReadOnlyDictionary<string, object?>? filters = null,
        int pageSize = 500,
        int maxRows = 100_000,
        CancellationToken cancellationToken = default)
    {
        var all = new List<T>();
        for (var page = 1; ; page++)
        {
            var slice = await GetPageAsync<T>(
                route, page: page, pageSize: pageSize, filters: filters, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            all.AddRange(slice.Items);

            if (slice.Items.Count < pageSize || (maxRows > 0 && all.Count >= maxRows))
            {
                break;
            }
        }

        return maxRows > 0 && all.Count > maxRows ? all.GetRange(0, maxRows) : all;
    }

    // ===== Import endpoints =======================================================================

    /// <summary>
    /// Sends rows to an import endpoint in one request. The endpoint decides which table they land in
    /// and which property feeds each column, so a row object only has to carry the properties it names.
    /// </summary>
    /// <typeparam name="T">The row model.</typeparam>
    /// <param name="route">Route of the import endpoint, e.g. <c>api/import/products</c>.</param>
    /// <param name="rows">The rows to write.</param>
    /// <param name="rowsProperty">
    /// The body property to put the array under, matching the endpoint's configuration. Defaults to
    /// <c>rows</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the gateway reported writing.</returns>
    /// <exception cref="WeirApiException">The call failed; the message is fit to show.</exception>
    public async Task<WeirImportResult> ImportAsync<T>(
        string route,
        IEnumerable<T> rows,
        string rowsProperty = "rows",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [rowsProperty] = rows is IReadOnlyList<T> list ? list : rows.ToList(),
        };

        var result = await PostAsync(route, body, cancellationToken).ConfigureAwait(false);
        return new WeirImportResult
        {
            Written = result.OutputValue<int?>("written") ?? result.RowsAffected,
            Rows = result.OutputValue<int?>("rows") ?? 0,
            Batches = result.OutputValue<int?>("batches") ?? 0,
            Deleted = result.OutputValue<int?>("deleted") ?? 0,
            Mode = result.OutputValue<string>("mode"),
        };
    }

    /// <summary>
    /// Sends rows to an import endpoint in chunks, one request each, for a payload larger than one
    /// request should carry. The gateway batches within a request too; this is the layer above that,
    /// for when the whole set would exceed the endpoint's row limit or the request body cap.
    /// <para>
    /// Each chunk is its own request and so its own transaction: a failure part-way leaves the chunks
    /// already accepted in place. For an all-or-nothing load, send one request and size the endpoint's
    /// limits to fit it.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The row model.</typeparam>
    /// <param name="route">Route of the import endpoint.</param>
    /// <param name="rows">The rows to write.</param>
    /// <param name="chunkSize">Rows per request.</param>
    /// <param name="rowsProperty">The body property to put each array under.</param>
    /// <param name="progress">Reports the running total of rows written, after each chunk.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The totals across every chunk.</returns>
    /// <exception cref="WeirApiException">A chunk failed; the message is fit to show.</exception>
    public async Task<WeirImportResult> ImportChunkedAsync<T>(
        string route,
        IEnumerable<T> rows,
        int chunkSize = 5_000,
        string rowsProperty = "rows",
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);

        var written = 0;
        var sent = 0;
        var batches = 0;
        var deleted = 0;
        string? mode = null;

        foreach (var chunk in rows.Chunk(chunkSize))
        {
            var result = await ImportAsync(route, chunk, rowsProperty, cancellationToken).ConfigureAwait(false);
            written += result.Written;
            sent += result.Rows;
            batches += result.Batches;
            deleted += result.Deleted;
            mode ??= result.Mode;
            progress?.Report(written);
        }

        return new WeirImportResult
        {
            Written = written,
            Rows = sent,
            Batches = batches,
            Deleted = deleted,
            Mode = mode,
        };
    }

    // ===== Plumbing ===============================================================================

    /// <summary>Sends a request with a JSON body and reads the envelope.</summary>
    private async Task<WeirResult> SendAsync(
        HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, route);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: BodyOptions);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds "route?key=value&amp;..." from an object or dictionary, leaving nulls out.</summary>
    /// <param name="route">Route relative to the gateway.</param>
    /// <param name="query">The parameters, or null.</param>
    /// <returns>The route with a query string, or the route unchanged.</returns>
    private static string BuildUrl(string route, object? query)
    {
        if (query is null)
        {
            return route;
        }

        var pairs = new List<string>();
        foreach (var (key, value) in Enumerate(query))
        {
            if (value is null)
            {
                continue;
            }

            pairs.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(Render(value)));
        }

        if (pairs.Count == 0)
        {
            return route;
        }

        var separator = route.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return route + separator + string.Join("&", pairs);
    }

    /// <summary>Yields the name/value pairs of a dictionary or of an object's public properties.</summary>
    private static IEnumerable<KeyValuePair<string, object?>> Enumerate(object query)
    {
        if (query is IReadOnlyDictionary<string, object?> readOnly)
        {
            return readOnly;
        }

        if (query is IDictionary<string, object?> dictionary)
        {
            return dictionary;
        }

        return query.GetType().GetProperties()
            .Select(property => new KeyValuePair<string, object?>(property.Name, property.GetValue(query)));
    }

    /// <summary>
    /// Renders one query value in the form the gateway parses back. The date and boolean cases are
    /// spelled out because the invariant default for either is not what the gateway reads: a
    /// <c>DateTime</c> would arrive with a culture-shaped separator and a <c>bool</c> capitalized.
    /// </summary>
    private static string Render(object value) => value switch
    {
        string text => text,
        bool flag => flag ? "true" : "false",
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTime moment => moment.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        // A list becomes the comma-separated form an "in" filter is read from.
        System.Collections.IEnumerable list and not string => Join(list),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>Renders a list value as the comma-separated form the gateway splits on.</summary>
    private static string Join(System.Collections.IEnumerable list)
    {
        var builder = new StringBuilder();
        foreach (var item in list)
        {
            if (item is null)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append(Render(item));
        }

        return builder.ToString();
    }

    /// <summary>Reads the envelope, or turns the error body into an exception.</summary>
    private static async Task<WeirResult> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(response, cancellationToken).ConfigureAwait(false);
        }

        // 204 and 304 carry no body; both are successful answers and neither has an envelope to read.
        if (response.Content.Headers.ContentLength == 0)
        {
            return new WeirResult();
        }

        try
        {
            return await response.Content
                .ReadFromJsonAsync<WeirResult>(cancellationToken)
                .ConfigureAwait(false) ?? new WeirResult();
        }
        catch (JsonException ex)
        {
            throw new WeirApiException("The gateway returned a body that is not a Weir envelope.", ex);
        }
    }

    /// <summary>Builds the exception from a problem+json body, falling back to the status line.</summary>
    private static async Task<WeirApiException> ToExceptionAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ProblemBody? problem = null;
        try
        {
            problem = await response.Content
                .ReadFromJsonAsync<ProblemBody>(ProblemJson.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Not JSON at all - a proxy error page, say. The status line below is then all there is.
        }
        catch (NotSupportedException)
        {
            // A content type the reader will not parse; same fallback.
        }

        var status = response.StatusCode;
        var message = string.IsNullOrWhiteSpace(problem?.Detail)
            ? $"{(int)status} {response.ReasonPhrase}"
            : problem.Detail;

        return problem?.Errors is { Count: > 0 } errors
            ? new WeirValidationException(message, status, errors, problem.Title)
            : new WeirApiException(message, status, problem?.Title);
    }
}
