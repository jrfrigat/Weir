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
        var result = await WeirResponseWriter.WriteAsync(stream, execution, endpoint, new JsonWriterOptions(), maxRows: 0, flushBytes: 0, CancellationToken.None);

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

    // A Utf8JsonWriter over a stream writes nothing until it is flushed - it piles up in an internal
    // buffer that doubles on plain heap arrays. With only the one flush at the end, this class buffered
    // the entire envelope and handed it over in a single write: the client waited for the last row
    // before the first byte, and a big result set sat in memory (and on the LOH) meanwhile. The row loop
    // flushes on a threshold now, and this holds that in place - the mistake is invisible from the
    // outside, since the bytes are identical either way and only their timing differs.
    [Fact]
    public async Task Rows_Reach_The_Output_While_The_Reader_Is_Still_Being_Read()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var setup = connection.CreateCommand())
        {
            // Comfortably past the writer's flush threshold, so a streaming write must chunk.
            setup.CommandText = """
                CREATE TABLE t(id INTEGER, name TEXT);
                WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 4000)
                INSERT INTO t SELECT n, 'customer-name-' || n || '-padded-out-to-look-like-a-real-row' FROM seq;
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT id, name FROM t ORDER BY id";
        var reader = await query.ExecuteReaderAsync();

        await using var execution = new TestExecution(reader);
        var endpoint = new EndpointDefinition { Route = "x", ConnectionName = "default", ObjectName = "usp" };

        await using var spy = new WriteSpyStream();
        var result = await WeirResponseWriter.WriteAsync(spy, execution, endpoint, new JsonWriterOptions(), maxRows: 0, flushBytes: 0, CancellationToken.None);

        Assert.Equal(4000, result.RowCount);

        // The tell: more than one write means bytes left while rows were still coming, and no single
        // write carried the whole payload the way a buffer-it-all-then-dump would.
        Assert.True(spy.Writes > 1, $"expected the response to be written in chunks, got {spy.Writes} write(s) of {spy.Total} bytes");
        Assert.True(spy.LargestWrite < spy.Total, $"one write carried the whole {spy.Total}-byte payload, so nothing streamed");

        // And it is still exactly the same document - streaming must not cost correctness.
        using var document = JsonDocument.Parse(spy.ToArray());
        var rows = document.RootElement.GetProperty("data")[0];
        Assert.Equal(4000, rows.GetArrayLength());
        Assert.Equal(1, rows[0].GetProperty("id").GetInt32());
        Assert.Equal(4000, rows[3999].GetProperty("id").GetInt32());
    }

    /// <summary>
    /// Records how the bytes arrived, not just what they were. This wraps a MemoryStream rather than
    /// deriving from one on purpose: MemoryStream's own WriteAsync forwards to its Write(span), so an
    /// override on both counts a single write twice and the numbers quietly double.
    /// </summary>
    private sealed class WriteSpyStream : Stream
    {
        private readonly MemoryStream _inner = new();

        /// <summary>How many separate writes reached the stream.</summary>
        public int Writes { get; private set; }

        /// <summary>The size of the largest single write.</summary>
        public int LargestWrite { get; private set; }

        /// <summary>Total bytes written.</summary>
        public int Total { get; private set; }

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override bool CanRead => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override long Length => Total;

        /// <inheritdoc />
        public override long Position { get => Total; set => throw new NotSupportedException(); }

        /// <summary>The bytes written so far.</summary>
        /// <returns>The full payload.</returns>
        public byte[] ToArray() => _inner.ToArray();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
        {
            Note(count);
            _inner.Write(buffer, offset, count);
        }

        /// <inheritdoc />
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Note(buffer.Length);
            _inner.Write(buffer);
        }

        /// <inheritdoc />
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Note(buffer.Length);
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Records one write.</summary>
        /// <param name="count">Bytes in this write.</param>
        private void Note(int count)
        {
            if (count == 0)
            {
                return;
            }

            Writes++;
            Total += count;
            LargestWrite = Math.Max(LargestWrite, count);
        }
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
        var result = await WeirResponseWriter.WriteAsync(stream, execution, endpoint, new JsonWriterOptions(), maxRows: 2, flushBytes: 0, CancellationToken.None);

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
        var result = await WeirResponseWriter.WriteAsync(stream, execution, endpoint, new JsonWriterOptions(), maxRows: 0, flushBytes: 0, CancellationToken.None);

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
        await WeirResponseWriter.WriteAsync(stream, execution, endpoint, new JsonWriterOptions(), maxRows: 0, flushBytes: 0, CancellationToken.None);

        using var document = JsonDocument.Parse(stream.ToArray());
        Assert.Equal(expectedCount, document.RootElement.GetProperty("messages").GetArrayLength());
    }
}
