using Microsoft.Extensions.Logging.Abstractions;
using Weir.Contracts;
using Weir.Host.Realtime;
using Xunit;

namespace Weir.Tests;

// The audit publisher is the seam between storing an audit entry and showing it live. Its one interesting
// behaviour is the one that keeps it out of the audit writer's way: with nobody watching, publishing is a
// no-op rather than a message into the void.
public class AuditPublisherTests
{
    /// <summary>A store-free entry.</summary>
    /// <param name="id">The id to stamp it with.</param>
    /// <returns>The entry.</returns>
    private static AuditEntry Entry(long id) => new()
    {
        Id = id,
        Category = "data.call",
        Actor = "key-1",
        Outcome = "ok",
        Timestamp = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task With_No_Dashboard_Connected_It_Does_Not_Reach_For_The_Hub()
    {
        // The hub context is deliberately null: with no client connected the publisher must return before
        // it needs one. That early-out is what keeps every audit write from paying for a feed nobody reads.
        var publisher = new AuditPublisher(null!, new DashboardClientTracker(), NullLogger<AuditPublisher>.Instance);

        await publisher.PublishAsync(Entry(1));
    }
}
