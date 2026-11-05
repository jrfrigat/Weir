using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.Host.Realtime;
using Xunit;

namespace Weir.Tests;

/// <summary>Checks the secured polling view against an idle, independently sampling host.</summary>
public sealed class MemoryMetricsApiTests
{
    /// <summary>Memory history advances without data-plane traffic or connected dashboard clients.</summary>
    [Fact]
    public async Task Idle_History_Is_Secured_And_Uses_The_Shared_Sampler()
    {
        await using var factory = new TransportGatingTests.HostFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/admin/api/metrics/memory")).StatusCode);
        using var admin = await factory.CreateAdminClientAsync();
        var first = await admin.GetFromJsonAsync<MemorySnapshot>("/admin/api/metrics/memory");
        Assert.NotNull(first);
        MemorySnapshot? next = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do
        {
            await Task.Delay(100, deadline.Token);
            next = await admin.GetFromJsonAsync<MemorySnapshot>("/admin/api/metrics/memory", deadline.Token);
        } while (next!.SampledAt is null || next.SampledAt <= first.SampledAt);
        Assert.Equal(first.InstanceId, next.InstanceId);
        Assert.InRange(next.Points.Count, 1, MemorySnapshot.HistorySeconds);
        Assert.True(next.Points[^1].ProcessWorkingSetBytes > 0);
        Assert.Equal(0, next.Points[^1].LocalCachePayloadBytes);
        Assert.False(factory.Services.GetRequiredService<DashboardClientTracker>().HasClients);
        Assert.Equal(0, factory.Services.GetRequiredService<IMetricsAggregator>().GetOverview().TotalRequests);
        Assert.Equal(next.InstanceId, factory.Services.GetRequiredService<IMemoryMetrics>().GetSnapshot().InstanceId);
    }
}
