using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

// Exercises the streaming JSON writer with a real DbDataReader (SQLite), which also confirms the
// patched SQLitePCLRaw works at runtime.
public class ResponseWriterTests
{
    private sealed class TestExecution(DbDataReader reader, IReadOnlyList<SqlMessage>? messages = null) : IDbExecution
    {
        public DbDataReader Reader => reader;

        public IReadOnlyList<SqlMessage> Messages => messages ?? [];

        public IReadOnlyDictionary<string, object?> Outputs => new Dictionary<string, object?>();

        public int? ReturnValue => null;

        public int RecordsAffected => 0;

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async ValueTask DisposeAsync() => await reader.DisposeAsync();
    }

    [Fact]
    public async Task Writes_Envelope_From_Reader()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE t(id INTEGER, name TEXT); INSERT INTO t VALUES (1,'a'),(2,'b');";
            await setup.ExecuteNonQueryAsync();
        }

        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT id, name FROM t ORDER BY id";
        var reader = await query.ExecuteReaderAsync();

        await using var execution = new TestExecution(reader);
        var endpoint = new EndpointDefinition { Route = "x", ConnectionName = "default", ObjectName = "usp" };

        using var stream = new MemoryStream();
        var result = await WeirResponseWriter.WriteAsync(stream, execution, endpoint, new JsonWriterOptions(), maxRows: 0, CancellationToken.None);

        Assert.Equal(2, result.RowCount);
        Assert.False(result.Truncated);

        using var document = JsonDocument.Parse(stream.ToArray());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());

        var firstSet = data[0];
        Assert.Equal(2, firstSet.GetArrayLength());
        Assert.Equal(1, firstSet[0].GetProperty("id").GetInt32());
        Assert.Equal("a", firstSet[0].GetProperty("name").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("rowsAffected").GetInt32());
        Assert.False(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Caps_Rows_And_Flags_Truncation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE t(id INTEGER); INSERT INTO t VALUES (1),(2),(3),(4),(5);";
            await setup.ExecuteNonQueryAsync();
        }

        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT id FROM t ORDER BY id";
        var reader = await query.ExecuteReaderAsync();

        await using var execution = new TestExecution(reader);
        var endpoint = new EndpointDefinition { Route = "x", ConnectionName = "default", ObjectName = "usp" };

        using var stream = new MemoryStream();
        var result = await WeirResponseWriter.WriteAsync(stream, execution, endpoint, new JsonWriterOptions(), maxRows: 2, CancellationToken.None);

        Assert.Equal(2, result.RowCount);
        Assert.True(result.Truncated);

        using var document = JsonDocument.Parse(stream.ToArray());
        Assert.Equal(2, document.RootElement.GetProperty("data")[0].GetArrayLength());
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Writes_Typed_Columns_And_Nulls()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        // Mix of SQLite storage classes: INTEGER (long), REAL (double), TEXT (string), BLOB (byte[]),
        // plus a NULL, so every typed getter path and the null path are exercised.
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT 42 AS i, 3.5 AS r, 'hello' AS t, x'01ff' AS b, NULL AS n";
        var reader = await query.ExecuteReaderAsync();

        await using var execution = new TestExecution(reader);
        var endpoint = new EndpointDefinition { Route = "x", ConnectionName = "default", ObjectName = "usp" };

        using var stream = new MemoryStream();
        var result = await WeirResponseWriter.WriteAsync(stream, execution, endpoint, new JsonWriterOptions(), maxRows: 0, CancellationToken.None);

        Assert.Equal(1, result.RowCount);
        using var document = JsonDocument.Parse(stream.ToArray());
        var row = document.RootElement.GetProperty("data")[0][0];
        Assert.Equal(42, row.GetProperty("i").GetInt64());
        Assert.Equal(3.5, row.GetProperty("r").GetDouble());
        Assert.Equal("hello", row.GetProperty("t").GetString());
        Assert.Equal(new byte[] { 0x01, 0xff }, row.GetProperty("b").GetBytesFromBase64());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("n").ValueKind);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task SuppressMessages_Controls_Messages_Array(bool suppress, int expectedCount)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT 1 AS id";
        var reader = await query.ExecuteReaderAsync();

        var messages = new SqlMessage[] { new() { Text = "diagnostic", Severity = 0 } };
        await using var execution = new TestExecution(reader, messages);
        var endpoint = new EndpointDefinition
        {
            Route = "x",
            ConnectionName = "default",
            ObjectName = "usp",
            SuppressMessages = suppress,
        };

        using var stream = new MemoryStream();
        await WeirResponseWriter.WriteAsync(stream, execution, endpoint, new JsonWriterOptions(), maxRows: 0, CancellationToken.None);

        using var document = JsonDocument.Parse(stream.ToArray());
        Assert.Equal(expectedCount, document.RootElement.GetProperty("messages").GetArrayLength());
    }
}
