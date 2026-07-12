namespace Weir.ControlPlane.Sqlite;

/// <summary>Configuration for the SQLite control-plane store.</summary>
public sealed class SqliteControlPlaneOptions
{
    /// <summary>
    /// ADO.NET connection string for the control-plane database.
    /// Defaults to a local file <c>weir-control.db</c> in the working directory.
    /// </summary>
    public string ConnectionString { get; set; } = "Data Source=weir-control.db";

    /// <summary>
    /// How long a write waits for a lock before failing with SQLITE_BUSY, in milliseconds. Applied as
    /// <c>PRAGMA busy_timeout</c> on every connection so concurrent writers queue instead of throwing.
    /// </summary>
    public int BusyTimeoutMs { get; set; } = 5_000;
}
