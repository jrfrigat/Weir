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
    /// How many bytes may sit unflushed in the writer before the row loop hands them to the output.
    /// <para>
    /// A <see cref="Utf8JsonWriter"/> over a stream writes nothing until it is flushed: it accumulates
    /// in an internal buffer that starts small and doubles, on plain heap arrays. Left to the single
    /// flush at the end, this method did not stream at all - the whole envelope was built in memory,
    /// every intermediate copy above ~85 KB landed on the large-object heap, and the client waited for
    /// the last row before receiving the first byte. At the default 100k row cap that is tens of
    /// megabytes held per in-flight request.
    /// </para>
    /// <para>
    /// Flushing on a threshold rather than per row keeps the syscall count sane while capping the
    /// buffer: the writer never grows past this plus one row. 32 KB is comfortably above a typical row
    /// and comfortably below the LOH threshold, so the buffer stays a reusable gen-0 array.
    /// </para>
    /// </summary>
    private const int FlushThresholdBytes = 32 * 1024;

    /// <summary>The outcome of streaming one response: rows written and whether the row cap was hit.</summary>
    /// <param name="RowCount">Number of data rows written across all result sets.</param>
    /// <param name="Truncated">Whether the row cap was reached and the response closed early.</param>
    internal readonly record struct WriteResult(int RowCount, bool Truncated);

    /// <summary>Streams the full response envelope for one execution into <paramref name="output"/>.</summary>
    /// <param name="output">Destination stream (client body or cache buffer).</param>
    /// <param name="execution">The live database execution to stream from.</param>
    /// <param name="endpoint">The endpoint definition (for output-parameter name mapping).</param>
    /// <param name="options">JSON writer options.</param>
    /// <param name="maxRows">Row cap across all result sets; 0 or less means unlimited.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of data rows written and whether the response was truncated.</returns>
    public static async Task<WriteResult> WriteAsync(
        Stream output,
        IDbExecution execution,
        EndpointDefinition endpoint,
        JsonWriterOptions options,
        int maxRows,
        CancellationToken cancellationToken)
    {
        var rowCount = 0;
        var truncated = false;
        var reader = execution.Reader;

        await using var writer = new Utf8JsonWriter(output, options);
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
                for (var i = 0; i < fieldCount; i++)
                {
                    names[i] = JsonEncodedText.Encode(reader.GetName(i), options.Encoder);
                    kinds[i] = Classify(reader.GetFieldType(i));
                }

                while (await reader.ReadAsync(cancellationToken))
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
                        WriteCell(writer, reader, i, kinds[i]);
                    }

                    writer.WriteEndObject();
                    rowCount++;

                    // Push what has piled up out to the destination. On the direct path that is the
                    // client's body, so this is what makes the response actually stream; on the buffered
                    // path it just moves bytes into the caller's MemoryStream, which is pre-sized.
                    if (writer.BytesPending >= FlushThresholdBytes)
                    {
                        await writer.FlushAsync(cancellationToken);
                    }
                }
            }

            writer.WriteEndArray();
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
        await writer.FlushAsync(cancellationToken);
        return new WriteResult(rowCount, truncated);
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
    private static void WriteCell(Utf8JsonWriter writer, DbDataReader reader, int ordinal, ColumnKind kind)
    {
        if (reader.IsDBNull(ordinal))
        {
            writer.WriteNullValue();
            return;
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
            case ColumnKind.Bytes: writer.WriteBase64StringValue(reader.GetFieldValue<byte[]>(ordinal)); break;
            case ColumnKind.TimeSpan: writer.WriteStringValue(reader.GetFieldValue<TimeSpan>(ordinal).ToString()); break;
            default: WriteValue(writer, reader.GetValue(ordinal)); break;
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
