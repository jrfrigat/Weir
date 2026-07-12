using Weir.Abstractions;
using Weir.Contracts;
using Weir.Core;
using Weir.Host.Security;
using Xunit;

namespace Weir.Tests;

// Exercises the in-memory per-key fixed-window rate limiter through the async interface.
public class RateLimiterTests
{
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedSettings(int defaultLimit) : IRuntimeSettings
    {
        public WeirSystemSettings Current { get; } = new() { DefaultApiKeyRateLimitPerMinute = defaultLimit };

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateAsync(WeirSystemSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static ApiKeyRecord Key(int? limit) => new()
    {
        Id = Guid.NewGuid(),
        Name = "k",
        Prefix = "wk_",
        Hash = "h",
        RateLimitPerMinute = limit,
    };

    [Fact]
    public async Task Allows_Up_To_Limit_Then_Blocks_And_Resets_Next_Minute()
    {
        var clock = new MovableClock(DateTimeOffset.UnixEpoch.AddDays(1));
        var limiter = new ApiKeyRateLimiter(clock, new FixedSettings(0));
        var key = Key(2);

        Assert.True(await limiter.TryAcquireAsync(key));
        Assert.True(await limiter.TryAcquireAsync(key));
        Assert.False(await limiter.TryAcquireAsync(key)); // third exceeds the limit of 2

        // A new minute window resets the count.
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True(await limiter.TryAcquireAsync(key));
    }

    [Fact]
    public async Task Unlimited_When_No_Key_Or_Default_Limit()
    {
        var clock = new MovableClock(DateTimeOffset.UnixEpoch.AddDays(1));
        var limiter = new ApiKeyRateLimiter(clock, new FixedSettings(0));
        var key = Key(null);

        for (var i = 0; i < 1000; i++)
        {
            Assert.True(await limiter.TryAcquireAsync(key));
        }
    }

    [Fact]
    public async Task Falls_Back_To_Default_Limit_When_Key_Has_None()
    {
        var clock = new MovableClock(DateTimeOffset.UnixEpoch.AddDays(1));
        var limiter = new ApiKeyRateLimiter(clock, new FixedSettings(1));
        var key = Key(null);

        Assert.Equal(1, limiter.EffectiveLimit(key));
        Assert.True(await limiter.TryAcquireAsync(key));
        Assert.False(await limiter.TryAcquireAsync(key));
    }
}
