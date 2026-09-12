using System.Collections;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

// The flush threshold alone makes a large result stream, but it holds back the tail of every burst: a
// procedure that returns a few rows and then pauses leaves them sitting in the writer, under the
// threshold, until the next burst or the end. Measured against SQL Server through nginx, a four-batch
// procedure with a second between batches delivered its whole body at the end. On the streaming path the
// writer now also flushes when the reader has to wait - on a row read that does not complete at once, and
// at every result-set boundary, because SqlClient's NextResultAsync sits out a pause synchronously and
// hands back a finished task, so waiting there cannot be seen. These tests hold both in place, and hold
// the buffered path to its one write.
public class StreamingFlushTests
{
    [Fact]
    public async Task A_Result_Set_Boundary_Sends_The_Set_Before_Asking_For_The_Next()
    {
        await using var streamed = new ChunkSpyStream();
        await WriteAsync(streamed, "SELECT 1 AS id; SELECT 2 AS id;", flushWhenWaiting: true, slowReads: false);

        // Every chunk before the last must hold a whole result set and nothing of the next one.
        Assert.True(streamed.Chunks.Count > 1, $"expected a write per result set, got {streamed.Chunks.Count}");
        var first = Encoding.UTF8.GetString(streamed.Chunks[0]);
        Assert.Contains("\"id\":1", first, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":2", first, StringComparison.Ordinal);
        AssertSameDocument(streamed);
    }

    [Fact]
    public async Task A_Row_Read_That_Has_To_Wait_Sends_What_Is_Pending()
    {
        await using var streamed = new ChunkSpyStream();
        await WriteAsync(streamed, "SELECT 1 AS id UNION ALL SELECT 2 UNION ALL SELECT 3;", flushWhenWaiting: true, slowReads: true);

        // Three small rows are nowhere near the threshold; only the waiting reads can have pushed them out.
        Assert.True(streamed.Chunks.Count >= 3, $"expected a write per waiting read, got {streamed.Chunks.Count}");
        AssertSameDocument(streamed);
    }

    [Fact]
    public async Task The_Buffered_Path_Still_Writes_Once()
    {
        // Filling a buffer gains nothing from early flushes, so the flag is off there and a small result
        // leaves in the single write at the end, pauses or not.
        await using var buffered = new ChunkSpyStream();
        await WriteAsync(buffered, "SELECT 1 AS id; SELECT 2 AS id;", flushWhenWaiting: false, slowReads: true);

        Assert.Single(buffered.Chunks);
        AssertSameDocument(buffered);
    }

    /// <summary>Runs the writer over an in-memory SQLite query.</summary>
    /// <param name="output">Where the envelope is written.</param>
    /// <param name="sql">The query; several statements produce several result sets.</param>
    /// <param name="flushWhenWaiting">The writer's flush-while-waiting switch.</param>
    /// <param name="slowReads">Whether every row read yields before completing, like a read waiting on the network.</param>
    /// <returns>A task that completes when the envelope is written.</returns>
    private static async Task WriteAsync(Stream output, string sql, bool flushWhenWaiting, bool slowReads)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        DbDataReader reader = await command.ExecuteReaderAsync();
        if (slowReads)
        {
            reader = new YieldingReader(reader);
        }

        await using var execution = new ReaderExecution(reader);
        var endpoint = new EndpointDefinition { Route = "x", ConnectionName = "default", ObjectName = "usp" };
        await WeirResponseWriter.WriteAsync(output, execution, endpoint, new JsonWriterOptions(), maxRows: 0, flushBytes: 0, flushWhenWaiting, CancellationToken.None);
    }

    /// <summary>Asserts the chunks join into one valid envelope: early flushes must not cost correctness.</summary>
    /// <param name="spy">The stream the envelope was written to.</param>
    private static void AssertSameDocument(ChunkSpyStream spy)
    {
        using var document = JsonDocument.Parse(spy.Chunks.SelectMany(c => c).ToArray());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("data").ValueKind);
    }

    /// <summary>An execution over a bare reader, with no outputs or messages.</summary>
    /// <param name="reader">The reader to expose.</param>
    private sealed class ReaderExecution(DbDataReader reader) : IDbExecution
    {
        /// <inheritdoc />
        public DbDataReader Reader => reader;

        /// <inheritdoc />
        public IReadOnlyList<SqlMessage> Messages => [];

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Outputs => new Dictionary<string, object?>();

        /// <inheritdoc />
        public int? ReturnValue => null;

        /// <inheritdoc />
        public int RecordsAffected => 0;

        /// <inheritdoc />
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public async ValueTask DisposeAsync() => await reader.DisposeAsync();
    }

    /// <summary>Records each write as its own chunk, so a test can see where the writer flushed.</summary>
    private sealed class ChunkSpyStream : Stream
    {
        /// <summary>The writes, in order.</summary>
        public List<byte[]> Chunks { get; } = [];

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override bool CanRead => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override long Length => Chunks.Sum(c => (long)c.Length);

        /// <inheritdoc />
        public override long Position { get => Length; set => throw new NotSupportedException(); }

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        /// <inheritdoc />
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!buffer.IsEmpty)
            {
                Chunks.Add(buffer.ToArray());
            }
        }

        /// <inheritdoc />
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
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
    }

    /// <summary>
    /// A reader whose row reads yield before completing, the way a driver's read does when the next row
    /// has not reached its buffer yet. Everything else passes straight through to the inner reader.
    /// </summary>
    /// <param name="inner">The real reader.</param>
    private sealed class YieldingReader(DbDataReader inner) : DbDataReader
    {
        /// <inheritdoc />
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            return inner.Read();
        }

        /// <inheritdoc />
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);

        /// <inheritdoc />
        public override int Depth => inner.Depth;

        /// <inheritdoc />
        public override int FieldCount => inner.FieldCount;

        /// <inheritdoc />
        public override bool HasRows => inner.HasRows;

        /// <inheritdoc />
        public override bool IsClosed => inner.IsClosed;

        /// <inheritdoc />
        public override int RecordsAffected => inner.RecordsAffected;

        /// <inheritdoc />
        public override object this[int ordinal] => inner[ordinal];

        /// <inheritdoc />
        public override object this[string name] => inner[name];

        /// <inheritdoc />
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);

        /// <inheritdoc />
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);

        /// <inheritdoc />
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

        /// <inheritdoc />
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);

        /// <inheritdoc />
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

        /// <inheritdoc />
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);

        /// <inheritdoc />
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);

        /// <inheritdoc />
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);

        /// <inheritdoc />
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);

        /// <inheritdoc />
        public override IEnumerator GetEnumerator() => inner.GetEnumerator();

        /// <inheritdoc />
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);

        /// <inheritdoc />
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);

        /// <inheritdoc />
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);

        /// <inheritdoc />
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);

        /// <inheritdoc />
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);

        /// <inheritdoc />
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);

        /// <inheritdoc />
        public override string GetName(int ordinal) => inner.GetName(ordinal);

        /// <inheritdoc />
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);

        /// <inheritdoc />
        public override string GetString(int ordinal) => inner.GetString(ordinal);

        /// <inheritdoc />
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);

        /// <inheritdoc />
        public override int GetValues(object[] values) => inner.GetValues(values);

        /// <inheritdoc />
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);

        /// <inheritdoc />
        public override bool NextResult() => inner.NextResult();

        /// <inheritdoc />
        public override bool Read() => inner.Read();

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
