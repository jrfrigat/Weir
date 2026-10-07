using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Weir.Client;
using Xunit;

namespace Weir.Tests;

/// <summary>
/// Covers the client package against a stub transport: what it puts on the wire and what it makes of
/// what comes back. No gateway is involved, so these pin the client's own contract.
/// </summary>
public class WeirClientTests
{
    /// <summary>A transport that records the request and replies with a canned response.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string _contentType;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json")
        {
            _body = body;
            _status = status;
            _contentType = contentType;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, _contentType),
            };
        }
    }

    private static (WeirClient Client, StubHandler Handler) Build(
        string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json")
    {
        var handler = new StubHandler(body, status, contentType);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://weir.test/") };
        return (new WeirClient(http), handler);
    }

    private sealed record Product(int ProductId, string Name, decimal Price);

    /// <summary>Row options matching the client's own, so assertions read a row the same way it does.</summary>
    private static readonly JsonSerializerOptions RowOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>A transport that answers with a body delivered in scripted chunks, one per read.</summary>
    private sealed class ChunkedHandler : HttpMessageHandler
    {
        private readonly IReadOnlyList<string> _chunks;
        private readonly Func<int, CancellationToken, Task>? _before;

        public ChunkedHandler(IReadOnlyList<string> chunks, Func<int, CancellationToken, Task>? before = null)
        {
            _chunks = chunks;
            _before = before;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var content = new StreamContent(new ChunkStream(_chunks, _before));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Hands out one scripted chunk per read, then reports the end; can wait before a chunk.</summary>
    private sealed class ChunkStream : Stream
    {
        private readonly IReadOnlyList<string> _chunks;
        private readonly Func<int, CancellationToken, Task>? _before;
        private int _index;

        public ChunkStream(IReadOnlyList<string> chunks, Func<int, CancellationToken, Task>? before)
        {
            _chunks = chunks;
            _before = before;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_before is not null)
            {
                await _before(_index, cancellationToken).ConfigureAwait(false);
            }

            if (_index >= _chunks.Count)
            {
                return 0;
            }

            var chunk = Encoding.UTF8.GetBytes(_chunks[_index++]);
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Builds a client whose response body arrives in the given chunks, one per read.</summary>
    private static (WeirClient Client, ChunkedHandler Handler) BuildStreaming(
        IReadOnlyList<string> chunks, Func<int, CancellationToken, Task>? before = null)
    {
        var handler = new ChunkedHandler(chunks, before);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://weir.test/") };
        return (new WeirClient(http), handler);
    }

    /// <summary>A stable array, so the query-rendering test is not re-allocating a constant each run.</summary>
    private static readonly int[] ThreeIds = [1, 2, 3];

    private const string TwoProducts =
        """{"data":[[{"ProductId":1,"Name":"Widget","Price":9.99},{"ProductId":2,"Name":"Gadget","Price":19.5}]],"rowsAffected":-1,"truncated":false,"messages":[]}""";

    [Fact]
    public async Task Set_ReadsRowsIntoTheModelIgnoringCasing()
    {
        // Row properties are SQL column names, which will not agree with any C# naming convention.
        var (client, _) = Build(TwoProducts);
        var rows = await client.GetListAsync<Product>("api/products");

        Assert.Equal(2, rows.Count);
        Assert.Equal("Widget", rows[0].Name);
        Assert.Equal(19.5m, rows[1].Price);
    }

    [Fact]
    public async Task First_ReturnsNullForAnEmptySet()
    {
        var (client, _) = Build("""{"data":[[]]}""");
        Assert.Null(await client.GetSingleAsync<Product>("api/products"));
    }

    [Fact]
    public async Task Scalar_ReadsTheFirstColumnOfTheFirstRow()
    {
        var (client, _) = Build("""{"data":[[{"Value":42}]]}""");
        Assert.Equal(42, await client.GetScalarAsync<int>("api/count"));
    }

    [Fact]
    public async Task Query_SkipsNullsAndEscapesValues()
    {
        var (client, handler) = Build(TwoProducts);
        await client.GetAsync("api/products", new { search = "a b", category = (string?)null, page = 2 });

        var url = handler.LastRequest!.RequestUri!.AbsoluteUri;
        Assert.Contains("search=a%20b", url, StringComparison.Ordinal);
        Assert.Contains("page=2", url, StringComparison.Ordinal);
        Assert.DoesNotContain("category", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_RendersDatesAndBooleansTheWayTheGatewayParsesThem()
    {
        var (client, handler) = Build(TwoProducts);
        await client.GetAsync("api/x", new { on = true, day = new DateOnly(2026, 3, 4) });

        var url = handler.LastRequest!.RequestUri!.AbsoluteUri;
        Assert.Contains("on=true", url, StringComparison.Ordinal);
        Assert.Contains("day=2026-03-04", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_RendersAListAsTheCommaSeparatedFormAnInFilterReads()
    {
        var (client, handler) = Build(TwoProducts);
        await client.GetAsync("api/x", new { id = ThreeIds });

        Assert.Contains("id=1%2C2%2C3", handler.LastRequest!.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPage_ReadsTheTotalCountFromTheOutputValues()
    {
        var (client, handler) = Build(
            """{"data":[[{"ProductId":1,"Name":"Widget","Price":9.99}]],"output":{"totalCount":57}}""");

        var page = await client.GetPageAsync<Product>("api/products", search: "wid", page: 3, pageSize: 25);

        Assert.Equal(57, page.TotalCount);
        Assert.Equal(3, page.Page);
        Assert.Equal(25, page.PageSize);
        Assert.Single(page.Items);

        var url = handler.LastRequest!.RequestUri!.AbsoluteUri;
        Assert.Contains("search=wid", url, StringComparison.Ordinal);
        Assert.Contains("page=3", url, StringComparison.Ordinal);
        Assert.Contains("pageSize=25", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPage_LeavesTheTotalNullWhenTheEndpointReportsNone()
    {
        var (client, _) = Build(TwoProducts);
        var page = await client.GetPageAsync<Product>("api/products");
        Assert.Null(page.TotalCount);
    }

    [Fact]
    public async Task Import_WrapsTheRowsInTheConfiguredPropertyAndReadsTheSummary()
    {
        var (client, handler) = Build(
            """{"data":[[]],"output":{"rows":2,"written":2,"batches":1,"mode":"Insert"},"rowsAffected":2}""");

        var result = await client.ImportAsync("api/import/widgets", new[]
        {
            new { Name = "a", Price = 1m },
            new { Name = "b", Price = 2m },
        });

        Assert.Equal(2, result.Written);
        Assert.Equal(1, result.Batches);
        Assert.Equal("Insert", result.Mode);
        Assert.Contains("\"rows\":", handler.LastBody, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
    }

    [Fact]
    public async Task ImportChunked_SendsOneRequestPerChunkAndSumsTheResults()
    {
        var (client, handler) = Build(
            """{"data":[[]],"output":{"rows":2,"written":2,"batches":1,"mode":"Insert"},"rowsAffected":2}""");

        var rows = Enumerable.Range(0, 5).Select(i => new { Name = "row" + i }).ToList();
        var result = await client.ImportChunkedAsync("api/import/widgets", rows, chunkSize: 2);

        // Three chunks of the stubbed reply: 2 + 2 + 1 rows, each answered with "written 2".
        Assert.Equal(6, result.Written);
        Assert.NotNull(handler.LastBody);
    }

    [Fact]
    public async Task Failure_UnwrapsTheProblemDetailSoTheSqlErrorSurvives()
    {
        var (client, _) = Build(
            """{"title":"Database error","detail":"Invalid column name 'Foo'."}""",
            HttpStatusCode.BadRequest,
            "application/problem+json");

        var ex = await Assert.ThrowsAsync<WeirApiException>(() => client.GetAsync("api/x"));

        Assert.Equal("Invalid column name 'Foo'.", ex.Message);
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal("Database error", ex.Title);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task Failure_WithFieldErrorsBecomesAValidationException()
    {
        var (client, _) = Build(
            """{"title":"Invalid parameters","detail":"One or more rows are invalid.","errors":{"rows[1]":["Column 'Name' is required."]}}""",
            HttpStatusCode.BadRequest,
            "application/problem+json");

        var ex = await Assert.ThrowsAsync<WeirValidationException>(() => client.PostAsync("api/import/x"));

        Assert.Contains("rows[1]", ex.Errors.Keys);
        Assert.Equal("Column 'Name' is required.", ex.Errors["rows[1]"][0]);
    }

    [Fact]
    public async Task Failure_WithNoJsonBodyFallsBackToTheStatusLine()
    {
        var (client, _) = Build("<html>gateway timeout</html>", HttpStatusCode.GatewayTimeout, "text/html");

        var ex = await Assert.ThrowsAsync<WeirApiException>(() => client.GetAsync("api/x"));

        Assert.Contains("504", ex.Message, StringComparison.Ordinal);

        // A gateway timeout is worth retrying; a 400 is not.
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Truncated_IsCarriedThroughSoAPartialAnswerIsNotMistakenForTheWhole()
    {
        var (client, _) = Build("""{"data":[[]],"truncated":true}""");
        Assert.True((await client.GetAsync("api/x")).Truncated);
    }

    [Fact]
    public async Task Stream_YieldsRowsWhileTheBodyIsStillOpen()
    {
        // The second batch is not read until the test lets it through, so a row already in hand proves the
        // read did not wait for the body to end - which is the whole point of the streaming API.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, _) = BuildStreaming(
            [
                """{"data":[[{"ProductId":1,"Name":"Widget","Price":9.99}]""",
                """,[{"ProductId":2,"Name":"Gadget","Price":19.5}]]}""",
            ],
            (index, _) => index == 1 ? gate.Task : Task.CompletedTask);

        await using var rows = client.StreamEventsAsync("api/stream").GetAsyncEnumerator();

        Assert.True(await rows.MoveNextAsync());
        Assert.Equal(WeirStreamEventKind.ResultSetStarted, rows.Current.Kind);

        Assert.True(await rows.MoveNextAsync());
        Assert.Equal(WeirStreamEventKind.Row, rows.Current.Kind);
        Assert.Equal(1, JsonSerializer.Deserialize<Product>(rows.Current.Json.Span, RowOptions)!.ProductId);

        // The row is in hand and the second batch has not been requested: the response is still open.
        Assert.False(gate.Task.IsCompleted);

        gate.SetResult();
        Assert.True(await rows.MoveNextAsync());
        Assert.Equal(WeirStreamEventKind.ResultSetStarted, rows.Current.Kind);
        Assert.Equal(1, rows.Current.ResultSetIndex);
        Assert.True(await rows.MoveNextAsync());
        Assert.Equal(2, JsonSerializer.Deserialize<Product>(rows.Current.Json.Span, RowOptions)!.ProductId);
        Assert.False(await rows.MoveNextAsync());
    }

    [Fact]
    public async Task Stream_ReassemblesARowSplitAcrossReads()
    {
        const string Body =
            """{"data":[[{"ProductId":1,"Name":"Widget","Price":9.99}],[{"ProductId":2,"Name":"Gadget","Price":19.5}]],"rowsAffected":-1}""";

        // One byte per read, so every token boundary falls on a chunk boundary at some point.
        var (client, _) = BuildStreaming([.. Body.Select(character => character.ToString())]);

        var events = new List<WeirStreamEvent>();
        await foreach (var item in client.StreamEventsAsync("api/stream"))
        {
            events.Add(item);
        }

        var sets = events.Where(item => item.Kind == WeirStreamEventKind.ResultSetStarted).ToList();
        var rows = events.Where(item => item.Kind == WeirStreamEventKind.Row).ToList();

        Assert.Equal(new[] { 0, 1 }, sets.Select(item => item.ResultSetIndex));
        Assert.Equal(2, rows.Count);
        Assert.Equal(0, rows[0].ResultSetIndex);
        Assert.Equal(1, rows[1].ResultSetIndex);
        Assert.Equal("Gadget", JsonSerializer.Deserialize<Product>(rows[1].Json.Span, RowOptions)!.Name);
        Assert.Equal(19.5m, JsonSerializer.Deserialize<Product>(rows[1].Json.Span, RowOptions)!.Price);
    }

    [Fact]
    public async Task Stream_AsTypedRowsReadsEverySet()
    {
        var (client, _) = BuildStreaming([TwoProducts]);

        var names = new List<string>();
        await foreach (var row in client.StreamAsync<Product>("api/products"))
        {
            names.Add(row.Name);
        }

        Assert.Equal(new[] { "Widget", "Gadget" }, names);
    }

    [Fact]
    public async Task Stream_ReportsTheProblemBeforeTheFirstRow()
    {
        var (client, _) = Build(
            """{"title":"Database error","detail":"Invalid column name 'Foo'."}""",
            HttpStatusCode.BadRequest,
            "application/problem+json");

        var ex = await Assert.ThrowsAsync<WeirApiException>(async () =>
        {
            await foreach (var _ in client.StreamEventsAsync("api/stream"))
            {
            }
        });

        Assert.Equal("Invalid column name 'Foo'.", ex.Message);
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
    }

    [Fact]
    public async Task Stream_ReportsABodyThatEndsInsideAValue()
    {
        var (client, _) = BuildStreaming(["{\"data\":[[{\"ProductId\":1,\""]);

        var ex = await Assert.ThrowsAsync<WeirApiException>(async () =>
        {
            await foreach (var _ in client.StreamEventsAsync("api/stream"))
            {
            }
        });

        Assert.Contains("ended inside a value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_ReportsABodyThatIsNotAWeirEnvelope()
    {
        var (client, _) = BuildStreaming(["<html>nginx</html>"]);

        var ex = await Assert.ThrowsAsync<WeirApiException>(async () =>
        {
            await foreach (var _ in client.StreamEventsAsync("api/stream"))
            {
            }
        });

        Assert.Contains("not a Weir envelope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_StopsWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var (client, _) = BuildStreaming(
            ["{\"data\":[[{\"ProductId\":1", "}]]}"],
            (index, token) => index == 1 ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask);

        await using var rows = client
            .StreamEventsAsync("api/stream", null, cancellation.Token)
            .GetAsyncEnumerator();

        Assert.True(await rows.MoveNextAsync());
        Assert.Equal(WeirStreamEventKind.ResultSetStarted, rows.Current.Kind);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rows.MoveNextAsync());
    }

    [Fact]
    public async Task Stream_IgnoresAnEnvelopeWhoseDataIsNotAnArray()
    {
        // A "data" that is null must not let a later array (here one inside "messages") pass for the data
        // array, which would report that array's objects as rows.
        var (client, _) = BuildStreaming(["""{"data":null,"messages":[{"text":"PRINT"}],"output":{}}"""]);

        var events = new List<WeirStreamEvent>();
        await foreach (var item in client.StreamEventsAsync("api/x"))
        {
            events.Add(item);
        }

        Assert.Empty(events);
    }
}
