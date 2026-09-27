using Xunit;

namespace Weir.Tests;

/// <summary>Pins the cleanup contract the rest of the suite relies on: disposing removes the database and its sidecars.</summary>
public class TempSqliteDatabaseTests
{
    [Fact]
    public void Disposing_Removes_The_Database_And_The_Sidecars_Beside_It()
    {
        var database = new TempSqliteDatabase("weir-tempdb");
        File.WriteAllText(database.Path, string.Empty);
        File.WriteAllText(database.Path + "-wal", string.Empty);
        File.WriteAllText(database.Path + "-shm", string.Empty);

        database.Dispose();

        Assert.False(File.Exists(database.Path), "the database file should be gone");
        Assert.False(File.Exists(database.Path + "-wal"), "the write-ahead log should be gone");
        Assert.False(File.Exists(database.Path + "-shm"), "the shared-memory file should be gone");
    }
}
