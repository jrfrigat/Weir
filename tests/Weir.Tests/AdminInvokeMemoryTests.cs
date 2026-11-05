using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

/// <summary>Checks that admin test calls share the engine's bounded Full path.</summary>
public sealed class AdminInvokeMemoryTests
{
    /// <summary>A Full response exceeds the bound before HTTP starts and releases its reservation.</summary>
    [Fact]
    public async Task Full_TestCall_Uses_The_Same_Budget_And_Envelope()
    {
        await using var source = new TransportGatingTests.HostFactory();
        await using var host = source.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDataConnectionRegistry, Registry>();
            services.AddSingleton<IDbConnector, Connector>();
        }));
        using var client = host.CreateClient();
        using var login = await client.PostAsJsonAsync("/admin/api/auth/login", new LoginRequest { Username = "admin", Password = "admin-password" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        var store = host.Services.GetRequiredService<IControlPlaneStore>();
        var endpoint = new EndpointDefinition
        {
            Route = "admin-memory", ConnectionName = "test", ObjectName = "rows",
            Delivery = new DeliveryPolicy { Mode = ResponseDeliveryMode.Full }
        };
        endpoint = await store.UpsertEndpointAsync(endpoint);
        using (var ok = await client.PostAsJsonAsync($"/admin/api/endpoints/{endpoint.Id}/invoke", new { }))
        {
            ok.EnsureSuccessStatusCode();
            var json = await ok.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(131072, json.GetProperty("data")[0][0].GetProperty("text").GetString()!.Length);
        }
        var settings = host.Services.GetRequiredService<IRuntimeSettings>();
        await settings.UpdateAsync(settings.Current with { BufferedResponseMaxBytes = 100 }, CancellationToken.None);
        using var refused = await client.PostAsJsonAsync($"/admin/api/endpoints/{endpoint.Id}/invoke", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType!.MediaType);
        Assert.Equal(0, host.Services.GetRequiredService<IResponseBufferBudget>().Used);
    }

    /// <summary>Resolves only the synthetic data source.</summary>
    private sealed class Registry : IDataConnectionRegistry
    {
        /// <summary>The synthetic connector descriptor.</summary>
        private static readonly DataConnectionDescriptor Descriptor = new() { Name = "test", Provider = "test", ConnectionString = "test" };
        /// <inheritdoc />
        public bool TryGet(string name, out DataConnectionDescriptor descriptor) { descriptor = Descriptor; return true; }
        /// <inheritdoc />
        public DataConnectionDescriptor Resolve(string name) => Descriptor;
        /// <inheritdoc />
        public IReadOnlyCollection<DataConnectionDescriptor> All => [Descriptor];
    }

    /// <summary>Supplies a real reader with one large value without using a database fixture.</summary>
    private sealed class Connector : IDbConnector
    {
        /// <inheritdoc />
        public string ProviderName => "test";
        /// <inheritdoc />
        public Task<IDbExecution> ExecuteAsync(DbExecutionRequest request, CancellationToken cancellationToken = default)
        {
            var table = new DataTable();
            table.Columns.Add("text", typeof(string));
            table.Rows.Add(new string('x', 131072));
            return Task.FromResult<IDbExecution>(new Execution(table.CreateDataReader()));
        }
        /// <inheritdoc />
        public Task ProbeAsync(string connectionName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        /// <inheritdoc />
        public Task<IReadOnlyList<DbObjectDescriptor>> ListObjectsAsync(string connectionName, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DbObjectDescriptor>>([]);
        /// <inheritdoc />
        public Task<IReadOnlyList<DbParameterDescriptor>> DescribeParametersAsync(string connectionName, string schema, string objectName, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DbParameterDescriptor>>([]);
    }

    /// <summary>Owns the synthetic reader through the response's completion.</summary>
    /// <param name="reader">The reader consumed by the production serializer.</param>
    private sealed class Execution(DbDataReader reader) : IDbExecution
    {
        /// <inheritdoc />
        public DbDataReader Reader => reader;
        /// <inheritdoc />
        public IReadOnlyList<SqlMessage> Messages => [];
        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Outputs { get; } = new Dictionary<string, object?>();
        /// <inheritdoc />
        public int? ReturnValue => null;
        /// <inheritdoc />
        public int RecordsAffected => -1;
        /// <inheritdoc />
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        /// <inheritdoc />
        public ValueTask DisposeAsync() => reader.DisposeAsync();
    }
}
