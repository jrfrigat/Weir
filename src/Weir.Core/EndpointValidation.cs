using Weir.Contracts;

namespace Weir.Core;

/// <summary>
/// Checks an endpoint definition before it is stored. Everything here would otherwise surface as a
/// failed call at run time, on a request that did nothing wrong: an operation whose policy is missing,
/// an upsert with nothing to match on, a page over rows in no particular order. Catching it at save
/// time turns a 500 in production into a message in the editor.
/// </summary>
public static class EndpointValidation
{
    /// <summary>Validates one endpoint definition.</summary>
    /// <param name="endpoint">The definition to check.</param>
    /// <returns>The problems found, keyed by the field they belong to. Empty when the endpoint is usable.</returns>
    public static IReadOnlyDictionary<string, string[]> Validate(EndpointDefinition endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // An endpoint reachable over nothing is not a configuration, it is a mistake wearing one: the
        // route table would carry it, the admin list would show it enabled, and every call would 404.
        if (endpoint.Transports == EndpointTransports.None)
        {
            Add(errors, nameof(endpoint.Transports),
                "An endpoint must be served over at least one transport. Disable it instead to take it out of service.");
        }

        switch (endpoint.Operation)
        {
            case EndpointOperation.Dictionary:
                ValidateDictionary(endpoint, errors);
                break;

            case EndpointOperation.Import:
                ValidateImport(endpoint, errors);
                break;

            default:
                if (endpoint.ObjectType is DbObjectType.Table or DbObjectType.View)
                {
                    Add(errors, nameof(endpoint.ObjectType),
                        "A table or view cannot be invoked. Choose the dictionary or import operation, or name a procedure or function.");
                }

                break;
        }

        return errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Checks a dictionary endpoint's target and policy.</summary>
    private static void ValidateDictionary(EndpointDefinition endpoint, Dictionary<string, List<string>> errors)
    {
        if (endpoint.ObjectType is not (DbObjectType.Table or DbObjectType.View))
        {
            Add(errors, nameof(endpoint.ObjectType), "A dictionary endpoint must target a table or a view.");
        }

        var policy = endpoint.Dictionary;
        if (policy is null)
        {
            Add(errors, nameof(endpoint.Dictionary), "A dictionary endpoint needs a dictionary policy.");
            return;
        }

        foreach (var column in policy.Columns)
        {
            if (string.IsNullOrWhiteSpace(column.Name))
            {
                Add(errors, nameof(policy.Columns), "A projected column needs a name.");
            }
        }

        foreach (var filter in policy.Filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Column))
            {
                Add(errors, nameof(policy.Filters), "A filter needs a column.");
            }
        }

        foreach (var sort in policy.OrderBy)
        {
            if (string.IsNullOrWhiteSpace(sort.Column))
            {
                Add(errors, nameof(policy.OrderBy), "A sort term needs a column.");
            }
        }

        if (policy.SearchColumns.Any(string.IsNullOrWhiteSpace))
        {
            Add(errors, nameof(policy.SearchColumns), "A search column needs a name.");
        }

        // Paging without an ordering hands back an arbitrary subset that can differ between two
        // identical calls, so page 2 may repeat or skip rows from page 1. The database is free to do
        // that, and does under concurrency, so the fix belongs here rather than in a bug report later.
        if (policy.AllowPaging && policy.OrderBy.Count == 0 &&
            string.IsNullOrWhiteSpace(policy.LabelColumn) && string.IsNullOrWhiteSpace(policy.ValueColumn))
        {
            Add(errors, nameof(policy.OrderBy),
                "Paging needs a stable order. Set an order-by column, a label column or a value column, or turn paging off.");
        }

        if (policy.MaxPageSize < 0 || policy.DefaultPageSize < 0)
        {
            Add(errors, nameof(policy.MaxPageSize), "Page sizes must not be negative.");
        }
        else if (policy.MaxPageSize > 0 && policy.DefaultPageSize > policy.MaxPageSize)
        {
            Add(errors, nameof(policy.DefaultPageSize), "The default page size must not exceed the maximum.");
        }
    }

    /// <summary>Checks an import endpoint's target and policy.</summary>
    private static void ValidateImport(EndpointDefinition endpoint, Dictionary<string, List<string>> errors)
    {
        if (endpoint.ObjectType != DbObjectType.Table)
        {
            Add(errors, nameof(endpoint.ObjectType), "An import endpoint must target a table.");
        }

        var policy = endpoint.Import;
        if (policy is null)
        {
            Add(errors, nameof(endpoint.Import), "An import endpoint needs an import policy.");
            return;
        }

        if (policy.Columns.Count == 0)
        {
            Add(errors, nameof(policy.Columns), "An import endpoint needs at least one target column.");
        }

        foreach (var column in policy.Columns)
        {
            if (string.IsNullOrWhiteSpace(column.Name))
            {
                Add(errors, nameof(policy.Columns), "A target column needs a name.");
            }
        }

        if (policy.Mode == ImportMode.Upsert)
        {
            if (policy.KeyColumns.Count == 0)
            {
                Add(errors, nameof(policy.KeyColumns), "An upsert needs at least one key column to match on.");
            }

            foreach (var key in policy.KeyColumns)
            {
                if (!policy.Columns.Any(column => string.Equals(column.Name, key, StringComparison.OrdinalIgnoreCase)))
                {
                    // A key the payload never carries matches nothing, so every row would insert and the
                    // upsert would behave as a plain insert until the first constraint violation.
                    Add(errors, nameof(policy.KeyColumns), $"Key column '{key}' is not one of the imported columns.");
                }
            }
        }

        if (policy.BatchSize < 0)
        {
            Add(errors, nameof(policy.BatchSize), "The batch size must not be negative.");
        }

        if (policy.MaxRows < 0)
        {
            Add(errors, nameof(policy.MaxRows), "The row limit must not be negative.");
        }
    }

    /// <summary>Records one problem against a field.</summary>
    private static void Add(Dictionary<string, List<string>> errors, string field, string message)
    {
        if (!errors.TryGetValue(field, out var list))
        {
            list = [];
            errors[field] = list;
        }

        if (!list.Contains(message, StringComparer.Ordinal))
        {
            list.Add(message);
        }
    }
}
