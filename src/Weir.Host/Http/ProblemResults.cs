using Microsoft.AspNetCore.Http;

namespace Weir.Host.Http;

/// <summary>Writes RFC 7807 problem+json error responses.</summary>
public static class ProblemResults
{
    /// <summary>
    /// Writes a problem+json body with the given status. No-op if the response has already started.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="status">HTTP status code.</param>
    /// <param name="title">Short, human-readable summary.</param>
    /// <param name="detail">Optional detail.</param>
    /// <param name="errors">Optional per-field validation messages.</param>
    /// <returns>A task that completes when the body is written.</returns>
    public static async Task WriteAsync(
        HttpContext context,
        int status,
        string title,
        string? detail = null,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;

        var problem = new ProblemPayload
        {
            Type = $"https://weir.dev/errors/{status}",
            Title = title,
            Status = status,
            Detail = detail,
            TraceId = context.TraceIdentifier,
            Errors = errors,
        };

        await context.Response.WriteAsJsonAsync(
            problem, options: null, contentType: "application/problem+json", context.RequestAborted);
    }

    /// <summary>The serialized shape of a problem+json response.</summary>
    private sealed record ProblemPayload
    {
        /// <summary>A URI reference identifying the problem type.</summary>
        public required string Type { get; init; }

        /// <summary>Short, human-readable summary.</summary>
        public required string Title { get; init; }

        /// <summary>HTTP status code.</summary>
        public required int Status { get; init; }

        /// <summary>Human-readable detail.</summary>
        public string? Detail { get; init; }

        /// <summary>Correlation id for the request.</summary>
        public string? TraceId { get; init; }

        /// <summary>Per-field validation messages, when applicable.</summary>
        public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
    }
}
