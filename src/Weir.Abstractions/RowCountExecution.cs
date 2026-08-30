using System.Collections;
using System.Data.Common;
using Weir.Contracts;

namespace Weir.Abstractions;

/// <summary>
/// An <see cref="IDbExecution"/> for work that writes rather than reads: it produces a row count and
/// output values, and has no result set to stream. Import endpoints run several statements inside one
/// transaction, so there is no single live reader to hand back the way a procedure call has - the work
/// is already finished by the time the execution exists.
/// <para>
/// It still satisfies the streaming contract rather than sidestepping it, so the response writer,
/// caching, request logging and telemetry all treat an import exactly like any other call. The
/// envelope comes out with one empty result set, which is the truthful answer to "what rows did this
/// return".
/// </para>
/// </summary>
public sealed class RowCountExecution : IDbExecution
{
    /// <summary>The empty reader handed to the response writer.</summary>
    private readonly EmptyDataReader _reader = new();

    /// <summary>Creates the execution from the outcome of the work that already ran.</summary>
    /// <param name="recordsAffected">Rows written.</param>
    /// <param name="outputs">Values reported back to the caller as the envelope's <c>output</c>.</param>
    /// <param name="messages">Informational messages captured while the work ran.</param>
    public RowCountExecution(
        int recordsAffected,
        IReadOnlyDictionary<string, object?>? outputs = null,
        IReadOnlyList<SqlMessage>? messages = null)
    {
        RecordsAffected = recordsAffected;
        Outputs = outputs ?? new Dictionary<string, object?>();
        Messages = messages ?? [];
    }

    /// <inheritdoc />
    public DbDataReader Reader => _reader;

    /// <inheritdoc />
    public IReadOnlyList<SqlMessage> Messages { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Outputs { get; }

    /// <inheritdoc />
    public int? ReturnValue => null;

    /// <inheritdoc />
    public int RecordsAffected { get; }

    /// <inheritdoc />
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A reader over nothing: no columns, no rows, no further result sets. Every accessor that would have
/// to invent a value throws instead, because a caller reaching one has misread the contract rather
/// than found an empty row.
/// </summary>
internal sealed class EmptyDataReader : DbDataReader
{
    /// <inheritdoc />
    public override int FieldCount => 0;

    /// <inheritdoc />
    public override bool HasRows => false;

    /// <inheritdoc />
    public override bool IsClosed => false;

    /// <inheritdoc />
    public override int RecordsAffected => 0;

    /// <inheritdoc />
    public override int Depth => 0;

    /// <inheritdoc />
    public override object this[int ordinal] => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override object this[string name] => throw new ArgumentOutOfRangeException(nameof(name));

    /// <inheritdoc />
    public override bool Read() => false;

    /// <inheritdoc />
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    /// <inheritdoc />
    public override bool NextResult() => false;

    /// <inheritdoc />
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();

    /// <inheritdoc />
    public override string GetName(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override int GetOrdinal(string name) => throw new ArgumentOutOfRangeException(nameof(name));

    /// <inheritdoc />
    public override Type GetFieldType(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override string GetDataTypeName(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override object GetValue(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override int GetValues(object[] values) => 0;

    /// <inheritdoc />
    public override bool IsDBNull(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override byte GetByte(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override char GetChar(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override double GetDouble(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override float GetFloat(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override Guid GetGuid(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override short GetInt16(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override int GetInt32(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override long GetInt64(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <inheritdoc />
    public override string GetString(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
}
