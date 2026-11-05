using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Weir.Abstractions;
using Weir.Connectors.PostgreSql;
using Weir.Connectors.SqlServer;
using Weir.Contracts;
using Weir.Core;
using Xunit;
using WeirDirection = Weir.Contracts.ParameterDirection;

namespace Weir.Tests;

/// <summary>Exercises live sequential readers and their lifecycle against both real database drivers.</summary>
public sealed class SequentialConnectorIntegrationTests
{
    /// <summary>The binary value returned by both fixtures.</summary>
    private static readonly byte[] ExpectedBlob = [1, 2, 3, 4];

    /// <summary>A surrogate pair straddles the text chunk boundary.</summary>
    private static readonly string BoundaryText = CreateBoundaryText();

    /// <summary>Places surrogate and UTF-8 sequence boundaries inside separate chunks.</summary>
    /// <returns>The exact text expected after both provider paths.</returns>
    private static string CreateBoundaryText()
    {
        var prefix = new string('x', 4095) + "\uD83D\uDE80\"\\\n\u041F\u0440\u0438\u0432\u0435\u0442";
        return prefix + new string('z', 16383 - System.Text.Encoding.UTF8.GetByteCount(prefix)) + "\uD83D\uDE80" + new string('q', 8192);
    }

    /// <summary>A binary value spans chunks and has a nonzero base64 remainder.</summary>
    private static readonly byte[] BoundaryBlob = Enumerable.Range(0, 32771).Select(i => (byte)i).ToArray();

    /// <summary>Checks ordered getters, envelopes, completion, cancellation and a single-slot connection pool.</summary>
    /// <param name="postgres">Whether the test targets PostgreSQL instead of SQL Server.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveReader_IsSequential_AndReleasesItsPoolSlot(bool postgres)
    {
        if (Environment.GetEnvironmentVariable("WEIR_CONTAINER_TESTS") != "1") return;

        await using var pg = postgres ? new PostgreSqlBuilder("postgres:18").Build() : null;
        await using var sql = postgres ? null : new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "1024").Build();
        if (pg is not null) await pg.StartAsync();
        if (sql is not null) await sql.StartAsync();
        var connectionString = pg?.GetConnectionString() ?? sql!.GetConnectionString();
        await SeedAsync(postgres, connectionString);
        var registry = new Registry(postgres, connectionString);
        IDbConnector connector = postgres ? new PostgreSqlConnector(registry) : new SqlServerConnector(registry);
        try
        {
            var request = RowsRequest(postgres);
            await using (var execution = await connector.ExecuteAsync(request))
            {
                Assert.Equal(postgres, execution.SupportsUtf8TextStreams);
                var reader = execution.Reader;
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.IsDBNull(0));
                Assert.Equal(1, reader.GetInt32(1));
                Assert.Equal(131072, reader.GetString(2).Length);
                var bytes = new byte[4];
                Assert.Equal(4, reader.GetBytes(3, 0, bytes, 0, bytes.Length));
                Assert.Equal(ExpectedBlob, bytes);
                Assert.Throws<InvalidOperationException>(() => reader.GetInt32(1));
            }

            // An unread result was disposed above. With one pool slot, a leak blocks this next call.
            await using (var execution = await connector.ExecuteAsync(request).WaitAsync(TimeSpan.FromSeconds(10)))
            {
                using var output = new MemoryStream();
                var result = await WeirResponseWriter.WriteAsync(output, execution,
                    new EndpointDefinition { Route = "seq", ConnectionName = "default", ObjectName = "weir_seq" },
                    new JsonWriterOptions(), 0, 32768, true, CancellationToken.None);
                using var json = JsonDocument.Parse(output.ToArray());
                Assert.Equal(postgres ? 3 : 4, result.RowCount);
                var sets = json.RootElement.GetProperty("data");
                Assert.Equal(postgres ? 1 : 2, sets.GetArrayLength());
                Assert.Equal(131072, sets[0][0].GetProperty("payload").GetString()!.Length);
                if (!postgres)
                {
                    Assert.Equal(7, execution.Outputs["total"]);
                    Assert.Equal(9, execution.ReturnValue);
                }
            }

            await using (var execution = await connector.ExecuteAsync(request))
            {
                using var output = new MemoryStream();
                var result = await WeirResponseWriter.WriteAsync(output, execution,
                    new EndpointDefinition { Route = "seq", ConnectionName = "default", ObjectName = "weir_seq" },
                    new JsonWriterOptions(), 1, 32768, true, CancellationToken.None, 2);
                Assert.True(result.Truncated);
                using var json = JsonDocument.Parse(output.ToArray());
                Assert.Equal("AQI=", json.RootElement.GetProperty("data")[0][0].GetProperty("blob").GetString());
                if (!postgres) Assert.Equal(7, execution.Outputs["total"]);
            }

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connector.ExecuteAsync(request, cancelled.Token));
            }
            await using (var execution = await connector.ExecuteAsync(request).WaitAsync(TimeSpan.FromSeconds(10)))
            {
                Assert.True(await execution.Reader.ReadAsync());
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                // A driver may satisfy buffered ReadAsync synchronously despite a cancelled token.
                // The complete gateway operation must still observe cancellation when flushing.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WeirResponseWriter.WriteAsync(
                    Stream.Null, execution,
                    new EndpointDefinition { Route = "seq", ConnectionName = "default", ObjectName = "weir_seq" },
                    new JsonWriterOptions(), 0, 32768, true, cancelled.Token));
            }
            await using (var final = await connector.ExecuteAsync(postgres ? InOutRequest() : request)
                .WaitAsync(TimeSpan.FromSeconds(10)))
            {
                await final.CompleteAsync();
                Assert.Equal(7, final.Outputs["total"]);
            }
            foreach (var cap in new[] { 0, 2 })
            {
                await using var values = await connector.ExecuteAsync(new DbExecutionRequest
                {
                    ConnectionName = "default", Schema = postgres ? "public" : "dbo", ObjectName = "weir_values",
                    ObjectType = postgres ? DbObjectType.TableValuedFunction : DbObjectType.StoredProcedure,
                });
                using var output = new MemoryStream();
                var result = await WeirResponseWriter.WriteAsync(output, values,
                    new EndpointDefinition { Route = "values", ConnectionName = "default", ObjectName = "weir_values" },
                    new JsonWriterOptions(), 0, 32768, true, CancellationToken.None, cap);
                using var json = JsonDocument.Parse(output.ToArray());
                var row = json.RootElement.GetProperty("data")[0][0];
                Assert.Equal(BoundaryText, row.GetProperty("text").GetString());
                Assert.Equal(BoundaryText, row.GetProperty("tail").GetString());
                Assert.Equal("short", row.GetProperty("small").GetString());
                Assert.NotEmpty(row.GetProperty("metadata").GetString()!);
                Assert.Equal(BoundaryBlob.AsSpan(0, cap > 0 ? cap : BoundaryBlob.Length).ToArray(), row.GetProperty("blob").GetBytesFromBase64());
                Assert.Equal(cap > 0, result.Truncated);
            }
            if (postgres)
            {
                var latinConnection = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    Encoding = "LATIN1", ClientEncoding = "LATIN1",
                }.ConnectionString;
                await using var latinConnector = new PostgreSqlConnector(new Registry(true, latinConnection));
                await using var latin = await latinConnector.ExecuteAsync(new DbExecutionRequest
                {
                    ConnectionName = "default", Schema = "public", ObjectName = "weir_latin", ObjectType = DbObjectType.ScalarFunction,
                });
                Assert.False(latin.SupportsUtf8TextStreams);
                using var output = new MemoryStream();
                await WeirResponseWriter.WriteAsync(output, latin,
                    new EndpointDefinition { Route = "latin", ConnectionName = "default", ObjectName = "weir_latin" },
                    new JsonWriterOptions(), 0, 32768, true, CancellationToken.None);
                using var json = JsonDocument.Parse(output.ToArray());
                Assert.Equal("\u00E9", json.RootElement.GetProperty("data")[0][0].GetProperty("Value").GetString());
            }
        }
        finally
        {
            if (connector is IAsyncDisposable disposable) await disposable.DisposeAsync();
            else (connector as IDisposable)?.Dispose();
        }
    }

    /// <summary>Builds the provider's row-producing invocation.</summary>
    /// <param name="postgres">Whether the request invokes a PostgreSQL table-valued function.</param>
    /// <returns>The request consumed by the actual connector.</returns>
    private static DbExecutionRequest RowsRequest(bool postgres) => new()
    {
        ConnectionName = "default", Schema = postgres ? "public" : "dbo", ObjectName = "weir_seq",
        ObjectType = postgres ? DbObjectType.TableValuedFunction : DbObjectType.StoredProcedure,
        Parameters = postgres ? [] : [new WeirParameter { Name = "total", DbType = WeirDbType.Int32, Direction = WeirDirection.Output }],
    };

    /// <summary>Builds a PostgreSQL CALL whose INOUT value must survive closing the sequential reader.</summary>
    /// <returns>The procedure request.</returns>
    private static DbExecutionRequest InOutRequest() => new()
    {
        ConnectionName = "default", Schema = "public", ObjectName = "weir_inout", ObjectType = DbObjectType.StoredProcedure,
        Parameters = [new WeirParameter { Name = "total", DbType = WeirDbType.Int32, Direction = WeirDirection.InputOutput, Value = 5 }],
    };

    /// <summary>Creates disposable fixtures in the test container only.</summary>
    /// <param name="postgres">Whether to use PostgreSQL syntax.</param>
    /// <param name="connectionString">The newly created container's connection.</param>
    /// <returns>The completed fixture creation.</returns>
    private static async Task SeedAsync(bool postgres, string connectionString)
    {
        await using DbConnection connection = postgres ? new NpgsqlConnection(connectionString) : new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = postgres ? """
            CREATE FUNCTION public.weir_seq() RETURNS TABLE(nullable integer,id integer,payload text,blob bytea)
            LANGUAGE sql AS $$ SELECT NULL::integer,g,repeat('x',131072),decode('01020304','hex') FROM generate_series(1,3) g $$;
            CREATE PROCEDURE public.weir_inout(INOUT total integer) LANGUAGE plpgsql AS $$ BEGIN total := total + 2; END $$;
            CREATE FUNCTION public.weir_latin() RETURNS text LANGUAGE sql AS $$ SELECT chr(233) $$;
            """ : """
            CREATE PROCEDURE dbo.weir_seq @total int OUTPUT AS BEGIN
                SET NOCOUNT ON;
                SELECT CAST(NULL AS int) AS nullable,v.id,REPLICATE(CAST('x' AS varchar(max)),131072) AS payload,
                    CAST(0x01020304 AS varbinary(max)) AS blob FROM (VALUES(1),(2),(3)) v(id);
                SELECT CAST(42 AS bigint) AS value;
                SET @total=7;
                RETURN 9;
            END
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText = postgres
            ? "CREATE TABLE public.weir_value_data(value_text text,value_blob bytea,small varchar(64));"
            : "CREATE TABLE dbo.weir_value_data(value_text nvarchar(max),value_blob varbinary(max),small nvarchar(64));";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO " + (postgres ? "public" : "dbo") + ".weir_value_data VALUES (@text,@blob,'short')";
        var text = command.CreateParameter(); text.ParameterName = "text"; text.DbType = DbType.String; text.Value = BoundaryText;
        var blob = command.CreateParameter(); blob.ParameterName = "blob"; blob.DbType = DbType.Binary; blob.Value = BoundaryBlob;
        command.Parameters.Add(text); command.Parameters.Add(blob);
        await command.ExecuteNonQueryAsync();
        command.Parameters.Clear();
        command.CommandText = postgres ? """
            CREATE FUNCTION public.weir_values() RETURNS TABLE(text text,blob bytea,tail text,small varchar,metadata jsonb)
            LANGUAGE sql AS $$ SELECT value_text,value_blob,value_text,small,'{"x":1}'::jsonb FROM public.weir_value_data $$;
            """ : """
            CREATE PROCEDURE dbo.weir_values AS
            SELECT value_text AS text,value_blob AS blob,value_text AS tail,small,CAST('<x/>' AS xml) AS metadata FROM dbo.weir_value_data;
            """;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Limits the real driver pool to one connection to expose leaked readers.</summary>
    /// <param name="postgres">Whether the descriptor selects PostgreSQL.</param>
    /// <param name="connectionString">The disposable database address.</param>
    private sealed class Registry(bool postgres, string connectionString) : IDataConnectionRegistry
    {
        /// <summary>The single data connection.</summary>
        private readonly DataConnectionDescriptor _descriptor = new()
        {
            Name = "default", Provider = postgres ? "PostgreSql" : "SqlServer", ConnectionString = connectionString,
            Pool = new ConnectionPoolOptions { MaxSize = 1, AcquireTimeoutSeconds = 3 },
        };

        /// <inheritdoc />
        public IReadOnlyCollection<DataConnectionDescriptor> All => [_descriptor];

        /// <inheritdoc />
        public DataConnectionDescriptor Resolve(string name) => _descriptor;

        /// <inheritdoc />
        public bool TryGet(string name, out DataConnectionDescriptor descriptor)
        {
            descriptor = _descriptor;
            return true;
        }
    }
}
