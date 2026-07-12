using Weir.Contracts;

namespace Weir.Core;

/// <summary>
/// Reconciles an endpoint's parameter list with the parameters discovered in the database. The
/// merge is non-destructive: it keeps each parameter's binding configuration (source, required flag,
/// validation, header / claim names, default value) and only updates the database-derived shape
/// (type, direction, size, precision, scale, type name, TVP columns). New database parameters are
/// added as body parameters; parameters the database no longer declares are removed.
/// </summary>
public static class EndpointSynchronizer
{
    /// <summary>Produces the synchronized endpoint and a report of the changes.</summary>
    /// <param name="endpoint">The current endpoint definition.</param>
    /// <param name="dbParameters">The parameters discovered on the target object.</param>
    /// <returns>The updated endpoint and a description of what changed.</returns>
    public static (EndpointDefinition Endpoint, EndpointSyncResult Result) Merge(
        EndpointDefinition endpoint, IReadOnlyList<DbParameterDescriptor> dbParameters)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(dbParameters);

        // Index the existing parameters by the database parameter name they bind to (last wins).
        var existingByDbName = new Dictionary<string, EndpointParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in endpoint.Parameters)
        {
            existingByDbName[EffectiveDbName(parameter)] = parameter;
        }

        var added = new List<string>();
        var updated = new List<string>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newParameters = new List<EndpointParameter>(dbParameters.Count);

        foreach (var db in dbParameters)
        {
            if (existingByDbName.TryGetValue(db.Name, out var existing))
            {
                matched.Add(EffectiveDbName(existing));
                var merged = MergeExisting(existing, db);
                if (merged != existing)
                {
                    updated.Add(db.Name);
                }

                newParameters.Add(merged);
            }
            else
            {
                newParameters.Add(FromDb(db));
                added.Add(db.Name);
            }
        }

        var removed = endpoint.Parameters
            .Where(p => !matched.Contains(EffectiveDbName(p)))
            .Select(EffectiveDbName)
            .ToList();

        var changed = added.Count > 0 || updated.Count > 0 || removed.Count > 0;
        var result = new EndpointSyncResult
        {
            EndpointId = endpoint.Id,
            Route = endpoint.Route,
            HttpMethod = endpoint.HttpMethod,
            Status = changed ? "updated" : "unchanged",
            Added = added,
            Updated = updated,
            Removed = removed,
        };

        return (endpoint with { Parameters = newParameters }, result);
    }

    /// <summary>Updates an existing parameter's database-derived shape, keeping its binding config.</summary>
    private static EndpointParameter MergeExisting(EndpointParameter existing, DbParameterDescriptor db)
    {
        // A table-valued parameter keeps its configured type and columns; only its direction syncs.
        if (existing.DbType == WeirDbType.Structured)
        {
            return existing with
            {
                Direction = db.Direction,
                TypeName = db.TypeName ?? existing.TypeName,
                TableColumns = db.TableColumns ?? existing.TableColumns,
            };
        }

        return existing with
        {
            DbType = db.DbType,
            Direction = db.Direction,
            Size = db.Size ?? existing.Size,
            Precision = db.Precision ?? existing.Precision,
            Scale = db.Scale ?? existing.Scale,
            TypeName = db.TypeName ?? existing.TypeName,
            TableColumns = db.TableColumns ?? existing.TableColumns,
        };
    }

    /// <summary>Builds a new body-sourced parameter from a discovered database parameter.</summary>
    private static EndpointParameter FromDb(DbParameterDescriptor db) => new()
    {
        Name = db.Name,
        Source = ParameterSource.Body,
        Direction = db.Direction,
        DbType = db.DbType,
        Size = db.Size,
        Precision = db.Precision,
        Scale = db.Scale,
        TypeName = db.TypeName,
        TableColumns = db.TableColumns,
    };

    /// <summary>The database parameter name a binding maps to (its DB name, else its logical name).</summary>
    private static string EffectiveDbName(EndpointParameter parameter) =>
        (parameter.DbParameterName ?? parameter.Name).TrimStart('@');
}
