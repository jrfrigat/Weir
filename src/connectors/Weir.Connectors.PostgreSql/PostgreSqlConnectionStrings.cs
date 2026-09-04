using Npgsql;
using Weir.Abstractions;

namespace Weir.Connectors.PostgreSql;

/// <summary>
/// Translates Weir's driver-neutral <see cref="ConnectionPoolOptions"/> onto the Npgsql connection
/// string that expresses the same thing.
/// </summary>
internal static class PostgreSqlConnectionStrings
{
    /// <summary>
    /// Returns the connection string with the pool settings applied. A property left null is not
    /// written, so whatever the connection string already said about it survives.
    /// </summary>
    /// <param name="connectionString">The configured connection string.</param>
    /// <param name="pool">The pool settings to overlay.</param>
    /// <returns>The effective connection string.</returns>
    public static string ApplyPool(string connectionString, ConnectionPoolOptions pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (pool.IsEmpty)
        {
            return connectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        if (pool.Enabled is { } enabled)
        {
            builder.Pooling = enabled;
        }

        if (pool.MinSize is { } min)
        {
            builder.MinPoolSize = min;
        }

        if (pool.MaxSize is { } max)
        {
            builder.MaxPoolSize = max;
        }

        if (pool.AcquireTimeoutSeconds is { } acquire)
        {
            // Npgsql's Timeout covers both connecting and waiting for a pooled connection, the same way
            // SqlClient's Connect Timeout does; there is no separate queue timeout.
            builder.Timeout = acquire;
        }

        return builder.ConnectionString;
    }
}
