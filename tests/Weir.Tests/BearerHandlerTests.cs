using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flare.Components;
using Weir.Admin.Services;
using Weir.Contracts;
using Xunit;

namespace Weir.Tests;

// Guards the admin console's token-refresh path. A page issues several API calls at once, so an expired
// access token produces several 401s at the same instant. Refresh tokens are rotated on use, so a second
// exchange started from the same stale token sends an already revoked one - the server rejects it and the
// user is signed out in the middle of a page load. Nothing about that is visible in a single-request test.
public class BearerHandlerTests
{
    /// <summary>The access token the session starts with, which the fake API answers 401 for.</summary>
    private static readonly string StaleAccess = Jwt("stale");

    /// <summary>The access token the refresh exchange issues, which the fake API accepts.</summary>
    private static readonly string NewAccess = Jwt("new");

    /// <summary>
    /// Builds a token that parses as a JWT. It has to: on a successful refresh the handler notifies the
    /// auth state provider, which re-reads the stored token and CLEARS it when its claims do not parse -
    /// so an opaque placeholder would delete the very token this test is checking for.
    /// </summary>
    /// <param name="name">Value of the <c>unique_name</c> claim, which distinguishes the two tokens.</param>
    /// <returns>An unsigned but well-formed JWT that expires an hour from now.</returns>
    private static string Jwt(string name)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["unique_name"] = name,
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        });

        var segment = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"header.{segment}.signature";
    }

    /// <summary>In-memory stand-in for Flare's browser storage, so a TokenStore can run outside a browser.</summary>
    private sealed class MemoryStorage : IBrowserStorage
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        public ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default);

        public ValueTask SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            _values[key] = JsonSerializer.Serialize(value);
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.TryRemove(key, out _);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values.ContainsKey(key));
    }

    /// <summary>
    /// Answers 401 for the stale access token and 200 for the new one, and serves the refresh exchange
    /// behind a gate the test opens, so two requests can be held inside the handler at the same time.
    /// </summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many refresh exchanges reached the server.</summary>
        public int RefreshCalls { get; private set; }

        /// <summary>The refresh token each of those exchanges presented, in order.</summary>
        public List<string?> RefreshTokensSeen { get; } = [];

        /// <summary>Completes once a request has entered the refresh exchange and is waiting on the gate.</summary>
        public TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets the held refresh exchange complete.</summary>
        public void OpenGate() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The handler posts the refresh with a relative URI (it never sees the client's BaseAddress),
            // so match on the raw string rather than on AbsolutePath, which a relative Uri refuses to give.
            if (request.RequestUri!.OriginalString.Contains("auth/refresh", StringComparison.Ordinal))
            {
                RefreshCalls++;
                RefreshTokensSeen.Add((await request.Content!.ReadFromJsonAsync<RefreshRequest>(cancellationToken))?.RefreshToken);
                RefreshEntered.TrySetResult();
                await _gate.Task;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new LoginResponse
                    {
                        Token = NewAccess,
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
                        Username = "admin",
                        RefreshToken = "new-refresh",
                    }),
                };
            }

            var token = request.Headers.Authorization?.Parameter;
            return new HttpResponseMessage(token == NewAccess ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Concurrent_Unauthorized_Responses_Refresh_The_Session_Once()
    {
        var tokens = new TokenStore(new MemoryStorage());
        await tokens.SetAsync(StaleAccess);
        await tokens.SetRefreshAsync("stale-refresh");

        var api = new FakeApi();
        using var handler = new BearerHandler(tokens, new WeirAuthStateProvider(tokens)) { InnerHandler = api };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://weir.test/") };

        // Both requests are in flight before either can refresh: the first reaches the exchange and blocks
        // on the gate, which is the window the second one used to slip into.
        var first = client.GetAsync("admin/api/endpoints");
        await api.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.GetAsync("admin/api/scopes");
        api.OpenGate();

        var responses = await Task.WhenAll(first, second);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, api.RefreshCalls);
        Assert.Equal(["stale-refresh"], api.RefreshTokensSeen);
        Assert.Equal(NewAccess, await tokens.GetAsync());
        Assert.Equal("new-refresh", await tokens.GetRefreshAsync());

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
