using System.Collections;
using System.Data.Common;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;

namespace Weir.Benchmarks;

/// <summary>Runs memory diagnosis of the exact production serializer source.</summary>
internal static class Program
{
    /// <summary>Starts the selected benchmark job.</summary>
    /// <param name="args">BenchmarkDotNet command-line options.</param>
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

/// <summary>Separates serializer overhead from per-value allocations in a database driver.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class WriterBenchmarks
{
    /// <summary>The number of rows serialized in one invocation.</summary>
    [Params(10, 1000, 100000)]
    public int Rows { get; set; }

    /// <summary>The value getter behavior used to isolate string materialization.</summary>
    [Params("primitives", "text-reused", "text-materialized")]
    public string Shape { get; set; } = "primitives";

    /// <summary>The stable metadata needed by the response envelope.</summary>
    private static readonly EndpointDefinition Endpoint = new() { Route="bench",ConnectionName="default",ObjectName="rows" };

    /// <summary>Writes a complete response without retaining its body.</summary>
    /// <returns>The completed serialization.</returns>
    [Benchmark]
    public async Task Serialize()
    {
        await using var reader = new SyntheticReader(Rows, Shape);
        await using var execution = new SyntheticExecution(reader);
        var result = await WeirResponseWriter.WriteAsync(Stream.Null, execution, Endpoint, new JsonWriterOptions(), 0, 32768, false, CancellationToken.None);
        if (result.RowCount != Rows) throw new InvalidOperationException("Incomplete response");
    }
}

/// <summary>Provides typed primitives and optionally strings without materializing row objects.</summary>
/// <param name="rows">The finite row count.</param>
/// <param name="shape">The string getter strategy.</param>
internal sealed class SyntheticReader(int rows, string shape) : DbDataReader
{
    /// <summary>The current synthetic row ordinal.</summary>
    private int _row;
    /// <summary>The names shared by every row.</summary>
    private static readonly string[] Names = ["Id","Flag","Price","Payload"];
    /// <summary>A string reused by the idealized allocation-free getter.</summary>
    private static readonly string Text = new('x',64);
    /// <inheritdoc />
    public override int FieldCount => shape=="primitives" ? 3 : 4;
    /// <inheritdoc />
    public override bool HasRows => rows>0;
    /// <inheritdoc />
    public override bool IsClosed => false;
    /// <inheritdoc />
    public override int RecordsAffected => -1;
    /// <inheritdoc />
    public override int Depth => 0;
    /// <inheritdoc />
    public override object this[int ordinal] => GetValue(ordinal);
    /// <inheritdoc />
    public override object this[string name] => GetValue(GetOrdinal(name));
    /// <inheritdoc />
    public override bool Read() => ++_row<=rows;
    /// <inheritdoc />
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
    /// <inheritdoc />
    public override bool NextResult() => false;
    /// <inheritdoc />
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    /// <inheritdoc />
    public override string GetName(int ordinal) => Names[ordinal];
    /// <inheritdoc />
    public override int GetOrdinal(string name) => Array.IndexOf(Names,name);
    /// <inheritdoc />
    public override Type GetFieldType(int ordinal) => ordinal switch {0=>typeof(int),1=>typeof(bool),2=>typeof(decimal),_=>typeof(string)};
    /// <inheritdoc />
    public override bool IsDBNull(int ordinal) => false;
    /// <inheritdoc />
    public override int GetInt32(int ordinal) => _row;
    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) => true;
    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal) => 12.34m;
    /// <inheritdoc />
    public override string GetString(int ordinal) => shape=="text-materialized" ? new string('x',64) : Text;
    /// <inheritdoc />
    public override object GetValue(int ordinal) => ordinal switch {0=>GetInt32(ordinal),1=>GetBoolean(ordinal),2=>GetDecimal(ordinal),_=>GetString(ordinal)};
    /// <inheritdoc />
    public override int GetValues(object[] values) { for(var i=0;i<Math.Min(values.Length,FieldCount);i++) values[i]=GetValue(i);return Math.Min(values.Length,FieldCount); }
    /// <inheritdoc />
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    /// <inheritdoc />
    public override byte GetByte(int ordinal) => throw new NotSupportedException();
    /// <inheritdoc />
    public override long GetBytes(int ordinal,long offset,byte[]? buffer,int bufferOffset,int length) => throw new NotSupportedException();
    /// <inheritdoc />
    public override char GetChar(int ordinal) => throw new NotSupportedException();
    /// <inheritdoc />
    public override long GetChars(int ordinal,long offset,char[]? buffer,int bufferOffset,int length) => throw new NotSupportedException();
    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
    /// <inheritdoc />
    public override double GetDouble(int ordinal) => throw new NotSupportedException();
    /// <inheritdoc />
    public override float GetFloat(int ordinal) => throw new NotSupportedException();
    /// <inheritdoc />
    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
    /// <inheritdoc />
    public override short GetInt16(int ordinal) => throw new NotSupportedException();
    /// <inheritdoc />
    public override long GetInt64(int ordinal) => throw new NotSupportedException();
}

/// <summary>Supplies an envelope around the reader without extra per-row state.</summary>
/// <param name="reader">The reader being serialized.</param>
internal sealed class SyntheticExecution(DbDataReader reader) : IDbExecution
{
    /// <inheritdoc />
    public DbDataReader Reader => reader;
    /// <inheritdoc />
    public IReadOnlyList<SqlMessage> Messages => [];
    /// <inheritdoc />
    public IReadOnlyDictionary<string,object?> Outputs { get; } = new Dictionary<string,object?>();
    /// <inheritdoc />
    public int? ReturnValue => null;
    /// <inheritdoc />
    public int RecordsAffected => -1;
    /// <inheritdoc />
    public ValueTask CompleteAsync(CancellationToken cancellationToken=default) => ValueTask.CompletedTask;
    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
