namespace Weir.ControlPlane.SqlServer;

/// <summary>Configuration for the SQL Server control-plane store.</summary>
public sealed class SqlServerControlPlaneOptions
{
    /// <summary>
    /// Microsoft.Data.SqlClient connection string for the shared control-plane database. Required;
    /// several Weir instances can point at the same database for high availability.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}
