using System.Buffers;
using System.Data.Common;
using System.Text.Json;
using Weir.Abstractions;
using Weir.Contracts;

namespace Weir.Core;

/// <summary>
/// Streams the Weir response envelope directly from a live <see cref="IDbExecution"/> into a JSON
/// writer: all result sets are written as <c>data</c>, then the reader is completed and the output
/// parameters, return value, affected-row count and messages are appended.
/// </summary>
internal static class WeirResponseWriter
{
    /// <summary>
    /// Fallback flush threshold. Both bytes committed to the pooled buffer and bytes still pending
    /// in the JSON writer count toward it; committing to an IBufferWriter does not send data to the client.
    /// </summary>
    private const int DefaultFlushThresholdBytes = 32 * 1024;

    /// <summary>The outcome of streaming one response: rows written and whether the response is partial.</summary>
    /// <param name="RowCount">Number of data rows written across all result sets.</param>
    /// <param name="Truncated">Whether the response is partial - the row cap was reached, or a binary value was cut to the byte cap.</param>
    internal readonly record struct WriteResult(int RowCount, bool Truncated);

    /// <summary>Streams the full response envelope for one execution into <paramref name="output"/>.</summary>
    /// <param name="output">Destination stream (client body or cache buffer).</param>
    /// <param name="execution">The live database execution to stream from.</param>
    /// <param name="endpoint">The endpoint definition (for output-parameter name mapping).</param>
    /// <param name="options">JSON writer options.</param>
    /// <param name="maxRows">Row cap across all result sets; 0 or less means unlimited.</param>
    /// <param name="flushBytes">
    /// How many bytes may sit unflushed before the row loop pushes them to <paramref name="output"/>.
    /// Zero or less falls back to <see cref="DefaultFlushThresholdBytes"/>. Flushing on a threshold
    /// rather than per row keeps the write count sane while bounding the accumulated JSON to this
    /// threshold plus one row. The intermediate bytes are rented instead of allocated per response.
    /// </param>
    /// <param name="flushWhenWaiting">
    /// Whether to push out whatever is pending, however little, whenever the reader has to wait for the
    /// database: when the next row is not already in the driver's buffer, and at every result-set boundary.
    /// Set on the streaming path: without it, rows a procedure produced before a pause sit in the writer
    /// until the threshold fills - which can be the next burst of rows, or the end - so the client sees them
    /// late. A row read that completes synchronously never triggers it, which is every row of a result the
    /// driver already holds.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="maxBinaryBytes">
    /// Maximum bytes of one binary value emitted before flagging truncation; zero or less means
    /// unlimited output. Sequential-value executions read bounded chunks even without a cap.
    /// Legacy custom readers retain their existing materializing getter fallback.
    /// </param>
    /// <returns>The number of data rows written and whether the response was truncated.</returns>
    public static async Task<WriteResult> WriteAsync(
        Stream output,
        IDbExecution execution,
        EndpointDefinition endpoint,
        JsonWriterOptions options,
        int maxRows,
        int flushBytes,
        bool flushWhenWaiting,
        CancellationToken cancellationToken,
        int maxBinaryBytes = 0)
    {
        var rowCount = 0;
        var truncated = false;
        var reader = execution.Reader;
        var flushThreshold = flushBytes > 0 ? flushBytes : DefaultFlushThresholdBytes;

        using var buffer = new JsonOutputBuffer(output);
        using var writer = new Utf8JsonWriter(buffer, options);
        writer.WriteStartObject();

        writer.WritePropertyName("data");
        writer.WriteStartArray();
        do
        {
            writer.WriteStartArray();
            var fieldCount = reader.FieldCount;
            if (fieldCount > 0)
            {
                // Column names and value-writer kinds are stable across every row of a result set, so
                // resolve them once here rather than per cell. The kind drives a typed getter in the row
                // loop (GetInt32 / GetDouble / ...) instead of the boxing GetValue on the streaming path.
                // The names are pre-encoded: WritePropertyName(string) re-runs JSON escape analysis and a
                // UTF-16 to UTF-8 transcode on every call, which over a wide result set means repeating
                // that work per row for the same handful of names. JsonEncodedText does it once per result
                // set and leaves the row loop copying ready-made UTF-8 bytes.
                var names = new JsonEncodedText[fieldCount];
                var kinds = new ColumnKind[fieldCount];
                var schema = execution.SupportsSequentialValues && reader.CanGetColumnSchema() ? reader.GetColumnSchema() : null;
                var utf8Text = execution.SupportsUtf8TextStreams;
                for (var i = 0; i < fieldCount; i++)
                {
                    names[i] = JsonEncodedText.Encode(reader.GetName(i), options.Encoder);
                    kinds[i] = Classify(reader.GetFieldType(i));
                    if (kinds[i] == ColumnKind.String && execution.CanStreamTextColumn(i) && schema is not null &&
                        schema[i].ColumnSize is not (> 0 and <= 4096))
                        kinds[i] = ColumnKind.TextChunks;
                }

                while (await FlushWhileWaitingAsync(reader.ReadAsync(cancellationToken), writer, buffer, flushWhenWaiting, cancellationToken))
                {
                    if (maxRows > 0 && rowCount >= maxRows)
                    {
                        // Stop before writing another row; the response is capped and flagged.
                        truncated = true;
                        break;
                    }

                    writer.WriteStartObject();
                    for (var i = 0; i < fieldCount; i++)
                    {
                        writer.WritePropertyName(names[i]);
                        if (kinds[i] == ColumnKind.TextChunks)
                        {
                            if (utf8Text) await WriteUtf8TextAsync(writer, buffer, reader, i, cancellationToken);
                            else await WriteTextAsync(writer, buffer, reader, i, cancellationToken);
                        }
                        else if (kinds[i] == ColumnKind.Bytes && execution.SupportsSequentialValues)
                        {
                            truncated |= await WriteBinaryAsync(writer, buffer, reader, i, maxBinaryBytes, cancellationToken);
                        }
                        else if (WriteCell(writer, reader, i, kinds[i], maxBinaryBytes))
                        {
                            truncated = true;
                        }
                    }

                    writer.WriteEndObject();
                    rowCount++;

                    // Push what has piled up out to the destination. On the direct path that is the
                    // client's body, so this is what makes the response actually stream; on the buffered
                    // path it moves bytes into the caller-owned response buffer.
                    if ((long)buffer.WrittenCount + writer.BytesPending >= flushThreshold)
                    {
                        await FlushAsync(writer, buffer, cancellationToken);
                    }
                }
            }

            writer.WriteEndArray();

            // A result-set boundary is where a procedure pauses between two SELECTs, and SqlClient's
            // NextResultAsync sits out that pause synchronously - it hands back a task that is already
            // complete - so waiting cannot be detected here the way it is for a row. Flush before asking
            // instead: it costs one write per result set, and a procedure returns a handful of those.
            if (flushWhenWaiting && !truncated && buffer.WrittenCount + writer.BytesPending > 0)
            {
                await FlushAsync(writer, buffer, cancellationToken);
            }
        }
        while (!truncated && await reader.NextResultAsync(cancellationToken));

        writer.WriteEndArray(); // data

        await execution.CompleteAsync(cancellationToken);

        writer.WritePropertyName("output");
        var outputs = execution.Outputs;
        if (outputs.Count == 0)
        {
            writer.WriteNullValue();
        }
        else
        {
            var map = BuildOutputNameMap(endpoint);
            writer.WriteStartObject();
            foreach (var pair in outputs)
            {
                writer.WritePropertyName(map.TryGetValue(pair.Key, out var logical) ? logical : pair.Key);
                WriteValue(writer, pair.Value);
            }

            writer.WriteEndObject();
        }

        if (execution.ReturnValue is { } returnValue)
        {
            writer.WriteNumber("returnValue", returnValue);
        }
        else
        {
            writer.WritePropertyName("returnValue");
            writer.WriteNullValue();
        }

        writer.WriteNumber("rowsAffected", execution.RecordsAffected);
        writer.WriteBoolean("truncated", truncated);

        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        // A per-endpoint toggle suppresses SQL PRINT / notice / info messages: the property is still
        // present (a stable envelope), just always empty, so chatty diagnostics never reach callers.
        foreach (var message in endpoint.SuppressMessages ? [] : execution.Messages)
        {
            writer.WriteStartObject();
            writer.WriteString("text", message.Text);
            writer.WriteNumber("severity", message.Severity);
            writer.WriteNumber("number", message.Number);
            if (message.Procedure is null)
            {
                writer.WritePropertyName("procedure");
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteString("procedure", message.Procedure);
            }

            writer.WriteNumber("line", message.Line);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
        await FlushAsync(writer, buffer, cancellationToken);
        return new WriteResult(rowCount, truncated);
    }

    /// <summary>
    /// Awaits the next row, first flushing the writer if the read has to wait on the database and there is
    /// something to send. The flush runs while the read is in flight, which is safe because they touch
    /// different streams.
    /// </summary>
    /// <param name="read">The read, already started.</param>
    /// <param name="writer">The JSON writer over the response.</param>
    /// <param name="buffer">The pooled bytes committed by the writer.</param>
    /// <param name="flushWhenWaiting">Whether a waiting read flushes the pending bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether there was a row.</returns>
    private static async ValueTask<bool> FlushWhileWaitingAsync(Task<bool> read, Utf8JsonWriter writer, JsonOutputBuffer buffer, bool flushWhenWaiting, CancellationToken cancellationToken)
    {
        if (!flushWhenWaiting || read.IsCompleted || (buffer.WrittenCount == 0 && writer.BytesPending == 0))
        {
            return await read;
        }

        try
        {
            await FlushAsync(writer, buffer, cancellationToken);
        }
        catch
        {
            // The client is gone or the call was cancelled. Let the read settle before the exception
            // unwinds into disposing the reader, which must not happen under a pending read.
            try
            {
                await read;
            }
            catch (Exception ex) when (ex is DbException or OperationCanceledException or InvalidOperationException)
            {
                // The flush failure is the one worth reporting; this read is being abandoned with it.
            }

            throw;
        }

        return await read;
    }

    /// <summary>Commits JSON before asynchronously handing its pooled bytes to the destination.</summary>
    /// <param name="writer">The JSON producer, which releases its current memory on flush.</param>
    /// <param name="buffer">The owner of the committed bytes.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>The completed handover.</returns>
    private static ValueTask FlushAsync(Utf8JsonWriter writer, JsonOutputBuffer buffer, CancellationToken cancellationToken)
    {
        writer.Flush();
        return buffer.FlushAsync(cancellationToken);
    }

    /// <summary>Owns rented JSON memory until the destination's asynchronous write and flush have finished.</summary>
    /// <param name="output">The caller-owned destination, which is never disposed here.</param>
    private sealed class JsonOutputBuffer(Stream output) : IBufferWriter<byte>, IDisposable
    {
        /// <summary>The current rental, released only after the JSON writer stops using it.</summary>
        private byte[] _bytes = ArrayPool<byte>.Shared.Rent(16 * 1024);

        /// <summary>Whether this buffer has released its rental.</summary>
        private bool _disposed;

        /// <summary>The lazily rented text chunk, shared by all cells of this response.</summary>
        private char[]? _text;

        /// <summary>The lazily rented binary chunk, shared by all cells of this response.</summary>
        private byte[]? _binary;

        /// <summary>Reusable storage for one bounded text read.</summary>
        internal char[] TextBuffer => _text ??= ArrayPool<char>.Shared.Rent(4096);

        /// <summary>Reusable storage for one bounded binary read.</summary>
        internal byte[] BinaryBuffer => _binary ??= ArrayPool<byte>.Shared.Rent(16 * 1024);

        /// <summary>Bytes committed by the JSON writer since the previous transport flush.</summary>
        internal int WrittenCount { get; private set; }

        /// <inheritdoc />
        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _bytes.Length - WrittenCount);
            WrittenCount += count;
        }

        /// <inheritdoc />
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _bytes.AsMemory(WrittenCount);
        }

        /// <inheritdoc />
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _bytes.AsSpan(WrittenCount);
        }

        /// <summary>Preserves committed bytes when the producer needs a larger contiguous region.</summary>
        /// <param name="sizeHint">The minimum free region requested by the producer.</param>
        private void EnsureCapacity(int sizeHint)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            var required = checked(WrittenCount + Math.Max(sizeHint, 1));
            if (required <= _bytes.Length) return;
            var capacity = _bytes.Length <= Array.MaxLength / 2 ? Math.Max(required, _bytes.Length * 2) : required;
            var next = ArrayPool<byte>.Shared.Rent(capacity);
            _bytes.AsSpan(0, WrittenCount).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_bytes);
            _bytes = next;
        }

        /// <summary>Awaits backpressure before making the producer's rental writable again.</summary>
        /// <param name="cancellationToken">Request cancellation.</param>
        /// <returns>The completed destination write and flush.</returns>
        internal async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var offset = 0; offset < WrittenCount;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(WrittenCount - offset, 256 * 1024);
                await output.WriteAsync(_bytes.AsMemory(offset, count), cancellationToken);
                offset += count;
            }
            await output.FlushAsync(cancellationToken);
            WrittenCount = 0;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_bytes);
            if (_text is not null) ArrayPool<char>.Shared.Return(_text);
            if (_binary is not null) ArrayPool<byte>.Shared.Return(_binary);
            _bytes = [];
        }
    }

    /// <summary>Writes an unbounded text column without constructing a string for the whole value.</summary>
    /// <param name="writer">The JSON producer.</param>
    /// <param name="buffer">The request's pooled chunk owner.</param>
    /// <param name="reader">The positioned sequential reader.</param>
    /// <param name="ordinal">The column being consumed.</param>
    /// <param name="cancellationToken">Cancellation checked between bounded reads and writes.</param>
    /// <returns>The completed value write.</returns>
    private static async ValueTask WriteTextAsync(Utf8JsonWriter writer, JsonOutputBuffer buffer,
        DbDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        if (reader.IsDBNull(ordinal))
        {
            writer.WriteNullValue();
            return;
        }
        var chars = buffer.TextBuffer;
        long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)reader.GetChars(ordinal, offset, chars, 0, 4096);
            var final = read < 4096;
            if (offset == 0 && final) writer.WriteStringValue(chars.AsSpan(0, read));
            else writer.WriteStringValueSegment(chars.AsSpan(0, read), final);
            offset += read;
            if ((long)buffer.WrittenCount + writer.BytesPending >= DefaultFlushThresholdBytes)
                await FlushAsync(writer, buffer, cancellationToken);
            if (final) return;
        }
    }

    /// <summary>Writes a binary value with bounded memory and at most one byte of cap lookahead.</summary>
    /// <param name="writer">The JSON producer.</param>
    /// <param name="buffer">The request's pooled chunk owner.</param>
    /// <param name="reader">The positioned sequential reader.</param>
    /// <param name="ordinal">The binary column.</param>
    /// <param name="maxBinaryBytes">The output cap, or zero for unlimited.</param>
    /// <param name="cancellationToken">Cancellation of database reads and transport writes.</param>
    /// <returns>Whether the value exceeded the cap.</returns>
    private static async ValueTask<bool> WriteBinaryAsync(Utf8JsonWriter writer, JsonOutputBuffer buffer,
        DbDataReader reader, int ordinal, int maxBinaryBytes, CancellationToken cancellationToken)
    {
        if (reader.IsDBNull(ordinal))
        {
            writer.WriteNullValue();
            return false;
        }
        using var stream = reader.GetStream(ordinal);
        var bytes = buffer.BinaryBuffer;
        var remaining = maxBinaryBytes > 0 ? (long)maxBinaryBytes : long.MaxValue;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(remaining, 16 * 1024);
            var read = await stream.ReadAsync(bytes.AsMemory(0, count), cancellationToken);
            if (read == 0) break;
            writer.WriteBase64StringSegment(bytes.AsSpan(0, read), false);
            remaining -= read;
            if ((long)buffer.WrittenCount + writer.BytesPending >= DefaultFlushThresholdBytes)
                await FlushAsync(writer, buffer, cancellationToken);
        }
        var truncated = remaining == 0 && await stream.ReadAsync(bytes.AsMemory(0, 1), cancellationToken) != 0;
        if (truncated)
        {
            // Disposing an unread SqlClient column can allocate a large drain buffer. Consume it
            // through the same bounded rental before moving to another column or capturing outputs.
            while (await stream.ReadAsync(bytes.AsMemory(0, 16 * 1024), cancellationToken) != 0)
                cancellationToken.ThrowIfCancellationRequested();
        }
        writer.WriteBase64StringSegment(ReadOnlySpan<byte>.Empty, true);
        return truncated;
    }

    /// <summary>Writes UTF-8 text streams without allocating a UTF-16 string or per-cell TextReader.</summary>
    /// <param name="writer">The JSON producer, which performs escaping and segment validation.</param>
    /// <param name="buffer">The request's pooled input and output chunks.</param>
    /// <param name="reader">The positioned reader advertising UTF-8 streams with a known length.</param>
    /// <param name="ordinal">The text column.</param>
    /// <param name="cancellationToken">Cancellation of reads and destination writes.</param>
    /// <returns>The completed value write.</returns>
    private static async ValueTask WriteUtf8TextAsync(Utf8JsonWriter writer, JsonOutputBuffer buffer,
        DbDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        if (reader.IsDBNull(ordinal))
        {
            writer.WriteNullValue();
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = reader.GetStream(ordinal);
        var bytes = buffer.BinaryBuffer;
        var length = stream.Length;
        if (length <= 4096)
        {
            // Small bounded reads retain the same synchronous getter behavior as GetString.
            stream.ReadExactly(bytes.AsSpan(0, checked((int)length)));
            writer.WriteStringValue(bytes.AsSpan(0, (int)length));
            return;
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(bytes.AsMemory(0, 16 * 1024), cancellationToken);
            writer.WriteStringValueSegment(bytes.AsSpan(0, read), read == 0);
            if ((long)buffer.WrittenCount + writer.BytesPending >= DefaultFlushThresholdBytes)
                await FlushAsync(writer, buffer, cancellationToken);
            if (read == 0) return;
        }
    }

    /// <summary>Maps driver output-parameter names (db name, minus any leading '@') to their logical names.</summary>
    /// <param name="endpoint">The endpoint definition whose output parameters are mapped.</param>
    /// <returns>A case-insensitive map from database parameter name to logical name.</returns>
    private static Dictionary<string, string> BuildOutputNameMap(EndpointDefinition endpoint)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in endpoint.Parameters)
        {
            if (parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
            {
                var dbName = (parameter.DbParameterName ?? parameter.Name).TrimStart('@');
                map[dbName] = parameter.Name;
            }
        }

        return map;
    }

    /// <summary>
    /// The value-writing strategy for a column, resolved once per result set from its field type. Every
    /// member except <see cref="Object"/> has a typed <see cref="System.Data.Common.DbDataReader"/> getter
    /// that avoids boxing on the streaming hot path; <see cref="Object"/> falls back to <see cref="WriteValue"/>.
    /// </summary>
    private enum ColumnKind : byte
    {
        /// <summary>Unknown or provider-specific type; read via <c>GetValue</c> and dispatched by <see cref="WriteValue"/>.</summary>
        Object = 0,

        /// <summary><see cref="bool"/> (<c>GetBoolean</c>).</summary>
        Boolean,

        /// <summary><see cref="byte"/> (<c>GetByte</c>).</summary>
        Byte,

        /// <summary><see cref="short"/> (<c>GetInt16</c>).</summary>
        Int16,

        /// <summary><see cref="int"/> (<c>GetInt32</c>).</summary>
        Int32,

        /// <summary><see cref="long"/> (<c>GetInt64</c>).</summary>
        Int64,

        /// <summary><see cref="float"/> (<c>GetFloat</c>).</summary>
        Single,

        /// <summary><see cref="double"/> (<c>GetDouble</c>).</summary>
        Double,

        /// <summary><see cref="decimal"/> (<c>GetDecimal</c>).</summary>
        Decimal,

        /// <summary><see cref="System.DateTime"/> (<c>GetDateTime</c>).</summary>
        DateTime,

        /// <summary><see cref="System.DateTimeOffset"/> (<c>GetFieldValue</c>).</summary>
        DateTimeOffset,

        /// <summary><see cref="System.Guid"/> (<c>GetGuid</c>).</summary>
        Guid,

        /// <summary><see cref="string"/> (<c>GetString</c>).</summary>
        String,

        /// <summary><c>byte[]</c> (<c>GetFieldValue</c>), written as base64.</summary>
        Bytes,

        /// <summary><see cref="System.TimeSpan"/> (<c>GetFieldValue</c>).</summary>
        TimeSpan,

        /// <summary>A large or unbounded text value read through reusable character chunks.</summary>
        TextChunks,
    }

    /// <summary>
    /// Classifies a column's field type into a <see cref="ColumnKind"/>. Only types whose typed getter is
    /// guaranteed to match the field type are given a dedicated kind; anything else (including unsigned or
    /// provider-specific types) maps to <see cref="ColumnKind.Object"/> so behaviour is unchanged.
    /// </summary>
    /// <param name="type">The column's CLR field type from <c>GetFieldType</c>.</param>
    /// <returns>The value-writing kind for the column.</returns>
    private static ColumnKind Classify(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.Boolean => ColumnKind.Boolean,
        TypeCode.Byte => ColumnKind.Byte,
        TypeCode.Int16 => ColumnKind.Int16,
        TypeCode.Int32 => ColumnKind.Int32,
        TypeCode.Int64 => ColumnKind.Int64,
        TypeCode.Single => ColumnKind.Single,
        TypeCode.Double => ColumnKind.Double,
        TypeCode.Decimal => ColumnKind.Decimal,
        TypeCode.DateTime => ColumnKind.DateTime,
        TypeCode.String => ColumnKind.String,
        _ when type == typeof(Guid) => ColumnKind.Guid,
        _ when type == typeof(DateTimeOffset) => ColumnKind.DateTimeOffset,
        _ when type == typeof(TimeSpan) => ColumnKind.TimeSpan,
        _ when type == typeof(byte[]) => ColumnKind.Bytes,
        _ => ColumnKind.Object,
    };

    /// <summary>
    /// Writes one cell using the column's resolved <see cref="ColumnKind"/>. Null is emitted as JSON null;
    /// every non-object kind uses a typed getter so no value is boxed. The typed number getters widen
    /// through the <see cref="Utf8JsonWriter"/> number overloads exactly as the boxed path did.
    /// </summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="reader">The positioned data reader.</param>
    /// <param name="ordinal">The column ordinal.</param>
    /// <param name="kind">The column's value-writing kind.</param>
    /// <param name="maxBinaryBytes">Cap on a binary value's bytes; 0 or less means unlimited.</param>
    /// <returns>True when a binary value was truncated to <paramref name="maxBinaryBytes"/>.</returns>
    private static bool WriteCell(Utf8JsonWriter writer, DbDataReader reader, int ordinal, ColumnKind kind, int maxBinaryBytes)
    {
        if (reader.IsDBNull(ordinal))
        {
            writer.WriteNullValue();
            return false;
        }

        switch (kind)
        {
            case ColumnKind.Boolean: writer.WriteBooleanValue(reader.GetBoolean(ordinal)); break;
            case ColumnKind.Byte: writer.WriteNumberValue(reader.GetByte(ordinal)); break;
            case ColumnKind.Int16: writer.WriteNumberValue(reader.GetInt16(ordinal)); break;
            case ColumnKind.Int32: writer.WriteNumberValue(reader.GetInt32(ordinal)); break;
            case ColumnKind.Int64: writer.WriteNumberValue(reader.GetInt64(ordinal)); break;
            case ColumnKind.Single: writer.WriteNumberValue(reader.GetFloat(ordinal)); break;
            case ColumnKind.Double: writer.WriteNumberValue(reader.GetDouble(ordinal)); break;
            case ColumnKind.Decimal: writer.WriteNumberValue(reader.GetDecimal(ordinal)); break;
            case ColumnKind.DateTime: writer.WriteStringValue(reader.GetDateTime(ordinal)); break;
            case ColumnKind.DateTimeOffset: writer.WriteStringValue(reader.GetFieldValue<DateTimeOffset>(ordinal)); break;
            case ColumnKind.Guid: writer.WriteStringValue(reader.GetGuid(ordinal)); break;
            case ColumnKind.String: writer.WriteStringValue(reader.GetString(ordinal)); break;
            case ColumnKind.Bytes: return WriteBytes(writer, reader, ordinal, maxBinaryBytes);
            case ColumnKind.TimeSpan: writer.WriteStringValue(reader.GetFieldValue<TimeSpan>(ordinal).ToString()); break;
            default: WriteValue(writer, reader.GetValue(ordinal)); break;
        }

        return false;
    }

    /// <summary>
    /// Writes a binary value as Base64, capping how much is materialized. Without a cap the value is read
    /// whole (the historical behaviour); with one, a value larger than the cap is read into a pooled buffer
    /// of exactly the cap's size, so a huge BLOB costs a bounded allocation instead of the value's own size.
    /// </summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="reader">The positioned data reader.</param>
    /// <param name="ordinal">The column ordinal.</param>
    /// <param name="maxBinaryBytes">The byte cap; 0 or less means unlimited.</param>
    /// <returns>True when the value was truncated to the cap.</returns>
    private static bool WriteBytes(Utf8JsonWriter writer, DbDataReader reader, int ordinal, int maxBinaryBytes)
    {
        if (maxBinaryBytes <= 0)
        {
            writer.WriteBase64StringValue(reader.GetFieldValue<byte[]>(ordinal));
            return false;
        }

        // GetBytes with a null buffer returns the field's length without reading it, so a value under the
        // cap is materialized the ordinary way and only one over the cap pays the chunked read.
        if (reader.GetBytes(ordinal, 0, null, 0, 0) <= maxBinaryBytes)
        {
            writer.WriteBase64StringValue(reader.GetFieldValue<byte[]>(ordinal));
            return false;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(maxBinaryBytes);
        try
        {
            var read = (int)reader.GetBytes(ordinal, 0, buffer, 0, maxBinaryBytes);
            writer.WriteBase64StringValue(buffer.AsSpan(0, read));
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Writes a single CLR value to the JSON writer, choosing the correct JSON representation.</summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="value">The value to write; null and <see cref="DBNull"/> become JSON null.</param>
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte n:
                writer.WriteNumberValue(n);
                break;
            case sbyte n:
                writer.WriteNumberValue(n);
                break;
            case short n:
                writer.WriteNumberValue(n);
                break;
            case int n:
                writer.WriteNumberValue(n);
                break;
            case long n:
                writer.WriteNumberValue(n);
                break;
            case float n:
                writer.WriteNumberValue(n);
                break;
            case double n:
                writer.WriteNumberValue(n);
                break;
            case decimal n:
                writer.WriteNumberValue(n);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt);
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto);
                break;
            case Guid g:
                writer.WriteStringValue(g);
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            case TimeSpan ts:
                writer.WriteStringValue(ts.ToString());
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
