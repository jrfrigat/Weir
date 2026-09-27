using Microsoft.Data.Sqlite;
using Weir.ControlPlane.Sqlite;

namespace Weir.Tests;

/// <summary>
/// A throwaway SQLite control-plane database on disk that removes itself when disposed.
/// <para>
/// The tests use a real file rather than <c>:memory:</c> deliberately: the store sets WAL and busy_timeout
/// for a file, and the tests that pin that behaviour would stop testing it against an in-memory database.
/// A file, though, has to be cleaned up - together with the <c>-wal</c> and <c>-shm</c> sidecars WAL mode
/// creates - or a run leaves the temp directory full of databases.
/// </para>
/// </summary>
internal sealed class TempSqliteDatabase : IDisposable
{
    /// <summary>Suffixes SQLite may leave beside a database file.</summary>
    private static readonly string[] Sidecars = ["-wal", "-shm", "-journal"];

    /// <summary>Creates the helper and picks a unique path; the file appears when the store opens it.</summary>
    /// <param name="prefix">File-name prefix, so a leftover names the suite that produced it.</param>
    public TempSqliteDatabase(string prefix = "weir")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.db");
        ConnectionString = $"Data Source={Path}";
        Options = new SqliteControlPlaneOptions { ConnectionString = ConnectionString };
    }

    /// <summary>The database file path.</summary>
    public string Path { get; }

    /// <summary>A connection string pointing at <see cref="Path"/>.</summary>
    public string ConnectionString { get; }

    /// <summary>Store options pointing at <see cref="Path"/>.</summary>
    public SqliteControlPlaneOptions Options { get; }

    /// <summary>
    /// Removes the database and the sidecars beside it. Pooled connections are dropped first: Microsoft.Data.Sqlite
    /// keeps an idle connection per connection string, and Windows will not delete a file one of them still holds.
    /// The delete itself stays best effort - cleanup must never fail the test that produced the file.
    /// </summary>
    public void Dispose()
    {
        // Closing the pools releases the file handles the deletes below need.
        SqliteConnection.ClearAllPools();

        // The sidecars first, then the database they belong to.
        foreach (var suffix in Sidecars)
        {
            Delete(Path + suffix);
        }

        Delete(Path);
    }

    /// <summary>Deletes one file if it exists, swallowing the failure a locked file raises.</summary>
    /// <param name="path">The file to remove.</param>
    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Still held by something; there is nothing useful a test can do about it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: cleanup is best effort.
        }
    }
}
