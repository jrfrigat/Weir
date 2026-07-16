using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>Binds request inputs to database parameters per an endpoint's parameter definitions.</summary>
public interface IParameterBinder
{
    /// <summary>Produces the bound parameters (and the input values, for cache keying) or throws
    /// <see cref="WeirValidationException"/> on invalid input.</summary>
    BindingResult Bind(WeirInvocation invocation);
}

/// <summary>The result of binding: driver parameters plus scalar input values keyed by logical name.</summary>
public sealed record BindingResult(
    IReadOnlyList<WeirParameter> Parameters,
    IReadOnlyDictionary<string, object?> Values);

/// <summary>Default parameter binder.</summary>
public sealed class ParameterBinder : IParameterBinder
{
    /// <summary>Upper bound on validation-regex evaluation, guarding against catastrophic backtracking.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Runtime settings; the table-valued-parameter row cap is read from here on each bind.</summary>
    private readonly IRuntimeSettings? _settings;

    /// <summary>Creates a binder with no table-valued-parameter row cap (used by tests).</summary>
    public ParameterBinder() => _settings = null;

    /// <summary>Creates a binder that enforces the runtime table-valued-parameter row cap.</summary>
    /// <param name="settings">Runtime settings (for <see cref="WeirSystemSettings.MaxTvpRows"/>).</param>
    public ParameterBinder(IRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>The current table-valued-parameter row cap; 0 (no settings) means unlimited.</summary>
    private int MaxTvpRows => _settings is null ? 0 : Math.Max(0, _settings.Current.MaxTvpRows);

    /// <inheritdoc />
    public BindingResult Bind(WeirInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var endpoint = invocation.Endpoint;
        var parameters = new List<WeirParameter>(endpoint.Parameters.Count);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in endpoint.Parameters)
        {
            if (definition.Direction == ParameterDirection.ReturnValue)
            {
                continue; // the connector captures the procedure return value itself
            }

            try
            {
                parameters.Add(BindOne(invocation, definition, values));
            }
            catch (WeirValidationException ex)
            {
                errors[definition.Name] = [ex.Message];
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or JsonException or InvalidOperationException or ArgumentException)
            {
                errors[definition.Name] = [$"Invalid value for '{definition.Name}'."];
            }
        }

        if (errors.Count > 0)
        {
            throw new WeirValidationException("One or more parameters are invalid.", errors);
        }

        return new BindingResult(parameters, values);
    }

    /// <summary>Binds a single parameter definition to a driver parameter, recording its scalar value.</summary>
    /// <param name="invocation">The current invocation carrying the request inputs.</param>
    /// <param name="definition">The parameter definition to bind.</param>
    /// <param name="values">Accumulates input values keyed by logical name, for cache keying.</param>
    /// <returns>The bound driver parameter.</returns>
    private WeirParameter BindOne(WeirInvocation invocation, EndpointParameter definition, Dictionary<string, object?> values)
    {
        var dbName = definition.DbParameterName ?? definition.Name;

        // Pure Output parameters have no client-supplied value: we emit a driver parameter with
        // Value = null and skip ReadValue entirely.  InputOutput parameters, by contrast, fall
        // through to ReadValue which returns (false, null) when the client omits them -- this is
        // intentional: SQL Server maps all output-capable parameters as InputOutput, so leaving
        // the value null lets the driver send the default and the procedure populates the output.
        if (definition.Direction == ParameterDirection.Output)
        {
            return new WeirParameter
            {
                Name = dbName,
                Direction = definition.Direction,
                DbType = definition.DbType,
                Size = definition.Size,
                Precision = definition.Precision,
                Scale = definition.Scale,
                TypeName = definition.TypeName,
                Value = null,
            };
        }

        if (definition.DbType == WeirDbType.Structured)
        {
            var table = BindTable(invocation, definition);

            // Record a stable token of the TVP content so an endpoint that varies its cache by this
            // parameter keys on the actual rows instead of colliding on NULL (see CacheKey.Build).
            // Building it walks every cell and allocates a string per cell, which on a large TVP is the
            // most expensive thing on the bind path - so only build it when something will read it.
            if (NeedsTvpToken(invocation.Endpoint, definition.Name))
            {
                values[definition.Name] = TvpToken(table.Rows);
            }

            return new WeirParameter
            {
                Name = dbName,
                Direction = definition.Direction,
                DbType = WeirDbType.Structured,
                TypeName = definition.TypeName,
                Table = table,
            };
        }

        var (present, value) = ReadValue(invocation, definition);
        if (!present)
        {
            if (definition.Required)
            {
                throw new WeirValidationException($"Parameter '{definition.Name}' is required.");
            }

            value = definition.DefaultValue;
        }

        // Apply a configured validation regex to the canonical string form of the value, so it also
        // guards parameters that coerced to a non-string CLR type (int, Guid, ...) rather than being
        // silently skipped for anything but strings.
        if (definition.ValidationRegex is { } pattern && value is not null)
        {
            var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
            if (text is not null && !Regex.IsMatch(text, pattern, RegexOptions.None, RegexTimeout))
            {
                throw new WeirValidationException($"Parameter '{definition.Name}' does not match the required format.");
            }
        }

        values[definition.Name] = value;

        return new WeirParameter
        {
            Name = dbName,
            Direction = definition.Direction,
            DbType = definition.DbType,
            Size = definition.Size,
            Precision = definition.Precision,
            Scale = definition.Scale,
            TypeName = definition.TypeName,
            Value = value,
        };
    }

    /// <summary>Reads a parameter's raw value from its configured source and coerces it to the target type.</summary>
    /// <param name="invocation">The current invocation.</param>
    /// <param name="definition">The parameter definition (source and target type).</param>
    /// <returns>Whether the value was present, and the coerced value.</returns>
    private static (bool Present, object? Value) ReadValue(WeirInvocation invocation, EndpointParameter definition)
    {
        switch (definition.Source)
        {
            case ParameterSource.Body:
                if (invocation.HasBody && invocation.Body.ValueKind == JsonValueKind.Object &&
                    invocation.Body.TryGetProperty(definition.Name, out var element))
                {
                    return (true, ValueCoercion.FromJson(element, definition.DbType));
                }

                return (false, null);

            case ParameterSource.Query:
                return invocation.Query.TryGet(definition.Name, out var q)
                    ? (true, ValueCoercion.FromString(q, definition.DbType))
                    : (false, null);

            case ParameterSource.Route:
                return invocation.Route.TryGet(definition.Name, out var r)
                    ? (true, ValueCoercion.FromString(r, definition.DbType))
                    : (false, null);

            case ParameterSource.Header:
                return invocation.Header.TryGet(definition.HeaderName ?? definition.Name, out var h)
                    ? (true, ValueCoercion.FromString(h, definition.DbType))
                    : (false, null);

            case ParameterSource.Claim:
                return invocation.Claim.TryGet(definition.ClaimType ?? definition.Name, out var c)
                    ? (true, ValueCoercion.FromString(c, definition.DbType))
                    : (false, null);

            case ParameterSource.Const:
                return (true, definition.DefaultValue);

            default:
                return (false, null);
        }
    }

    /// <summary>Binds a table-valued parameter's rows from the request body per its column schema.</summary>
    /// <param name="invocation">The current invocation.</param>
    /// <param name="definition">The TVP parameter definition, including its column schema.</param>
    /// <returns>The bound table parameter.</returns>
    private TableParameter BindTable(WeirInvocation invocation, EndpointParameter definition)
    {
        if (definition.TableColumns is null || definition.TableColumns.Count == 0)
        {
            throw new WeirValidationException($"Parameter '{definition.Name}' is a table-valued parameter but declares no columns.");
        }

        var columns = definition.TableColumns;
        var rows = new List<IReadOnlyList<object?>>();
        var maxTvpRows = MaxTvpRows;

        if (invocation.HasBody && invocation.Body.ValueKind == JsonValueKind.Object &&
            invocation.Body.TryGetProperty(definition.Name, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var rowElement in array.EnumerateArray())
            {
                if (maxTvpRows > 0 && rows.Count >= maxTvpRows)
                {
                    // Reject an oversized TVP up front rather than materializing an unbounded row set.
                    throw new WeirValidationException(
                        $"Table-valued parameter '{definition.Name}' exceeds the maximum of {maxTvpRows} rows.");
                }

                var cells = new object?[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                {
                    var column = columns[i];
                    cells[i] = rowElement.ValueKind == JsonValueKind.Object && rowElement.TryGetProperty(column.Name, out var cell)
                        ? ValueCoercion.FromJson(cell, column.DbType)
                        : null;
                }

                rows.Add(cells);
            }
        }
        else if (definition.Required)
        {
            throw new WeirValidationException($"Table-valued parameter '{definition.Name}' is required.");
        }

        return new TableParameter { Columns = columns, Rows = rows };
    }

    /// <summary>
    /// Whether anything will read a table-valued parameter's content token: the cache key varies by it,
    /// or the endpoint captures its parameters for the request log. Nothing else consumes the token, and
    /// building it is O(cells), so an endpoint that neither caches on the TVP nor logs its parameters
    /// should not pay for it.
    /// </summary>
    /// <param name="endpoint">The endpoint being invoked.</param>
    /// <param name="name">The logical parameter name.</param>
    /// <returns>True when the token must be built.</returns>
    private static bool NeedsTvpToken(EndpointDefinition endpoint, string name)
    {
        // Matches CacheKey.Build's lookup, which is case-insensitive: a vary-by name that resolves to no
        // value there disables caching for the call, so this gate has to agree with it exactly.
        if (endpoint.Logging.LogParameters)
        {
            return true;
        }

        if (!endpoint.Cache.Enabled)
        {
            return false;
        }

        foreach (var varyBy in endpoint.Cache.VaryByParameters)
        {
            if (string.Equals(varyBy, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds a stable, collision-resistant token from a TVP's rows for cache keying.</summary>
    /// <param name="rows">The bound TVP rows, each a list of cell values in column order.</param>
    /// <returns>A canonical string that maps distinct row sets to distinct tokens.</returns>
    private static string TvpToken(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var builder = new StringBuilder("tvp:").Append(rows.Count).Append(';');
        foreach (var row in rows)
        {
            builder.Append(row.Count).Append(':');
            foreach (var cell in row)
            {
                // Reuse the cache-key value encoder so every cell is type-tagged, then delimit with a
                // unit separator that cannot appear in the encoded scalar forms.
                builder.Append(CacheKey.Encode(cell)).Append('\u001f');
            }

            builder.Append(';');
        }

        return builder.ToString();
    }
}
