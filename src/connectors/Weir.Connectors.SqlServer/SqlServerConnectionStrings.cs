using Microsoft.Data.SqlClient;
using Weir.Abstractions;

namespace Weir.Connectors.SqlServer;

/// <summary>
/// Translates Weir's driver-neutral <see cref="ConnectionPoolOptions"/> onto the SqlClient connection
/// string that expresses the same thing.
/// </summary>
internal static class SqlServerConnectionStrings
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

        var builder = new SqlConnectionStringBuilder(connectionString);

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
            // SqlClient has one timeout for "get me a connection", covering both opening a new one and
            // waiting for a pooled one to come back. There is no separate queue timeout to set.
            builder.ConnectTimeout = acquire;
        }

        return builder.ConnectionString;
    }
}
