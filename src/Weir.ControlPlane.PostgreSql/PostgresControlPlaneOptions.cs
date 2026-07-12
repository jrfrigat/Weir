namespace Weir.ControlPlane.PostgreSql;

/// <summary>Configuration for the PostgreSQL control-plane store.</summary>
public sealed class PostgresControlPlaneOptions
{
    /// <summary>
    /// Npgsql connection string for the shared control-plane database. Required; several Weir
    /// instances point at the same database for high availability.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}
