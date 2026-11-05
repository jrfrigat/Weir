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
            await using var final = await connector.ExecuteAsync(postgres ? InOutRequest() : request)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await final.CompleteAsync();
            Assert.Equal(7, final.Outputs["total"]);
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
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
