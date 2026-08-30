using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weir.Client;

/// <summary>
/// A call the gateway refused or could not complete. The message is the problem's <c>detail</c> where
/// there is one, which for a database failure is the SQL error text: without unwrapping the
/// problem+json body a caller would show "400 Bad Request" and lose the only line that says what
/// actually went wrong.
/// </summary>
public class WeirApiException : Exception
{
    /// <summary>Creates the exception from a failed response.</summary>
    /// <param name="message">The message to show.</param>
    /// <param name="status">HTTP status of the failed response.</param>
    /// <param name="title">The problem's title, when the body carried one.</param>
    public WeirApiException(string message, HttpStatusCode status, string? title = null)
        : base(message)
    {
        Status = status;
        Title = title;
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">The message to show.</param>
    /// <param name="innerException">The underlying failure.</param>
    public WeirApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Status = HttpStatusCode.ServiceUnavailable;
    }

    /// <summary>Creates the exception with only a message, for the framework's exception contract.</summary>
    /// <param name="message">The message to show.</param>
    public WeirApiException(string message)
        : base(message)
    {
        Status = HttpStatusCode.InternalServerError;
    }

    /// <summary>Creates an empty exception, for the framework's exception contract.</summary>
    public WeirApiException()
    {
        Status = HttpStatusCode.InternalServerError;
    }

    /// <summary>HTTP status of the failed response.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>The problem's title, when the body carried one.</summary>
    public string? Title { get; }

    /// <summary>
    /// Whether retrying the same call unchanged could plausibly succeed: the gateway was busy, a
    /// circuit was open, or the database timed out. A 4xx means the request itself is the problem and
    /// sending it again will fail the same way.
    /// </summary>
    public bool IsTransient => Status is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;
}

/// <summary>
/// A call the gateway rejected because the request itself was invalid: a missing required parameter, a
/// value of the wrong type, a row an import could not accept. <see cref="Errors"/> says which fields,
/// so a client can put the messages back on the fields that produced them.
/// </summary>
public sealed class WeirValidationException : WeirApiException
{
    /// <summary>Creates the exception from a validation problem body.</summary>
    /// <param name="message">The message to show.</param>
    /// <param name="status">HTTP status of the failed response.</param>
    /// <param name="errors">Per-field messages, keyed by field name.</param>
    /// <param name="title">The problem's title, when the body carried one.</param>
    public WeirValidationException(
        string message,
        HttpStatusCode status,
        IReadOnlyDictionary<string, string[]> errors,
        string? title = null)
        : base(message, status, title) => Errors = errors;

    /// <summary>Creates the exception with only a message, for the framework's exception contract.</summary>
    /// <param name="message">The message to show.</param>
    public WeirValidationException(string message)
        : base(message) => Errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    /// <summary>Creates the exception with an inner cause, for the framework's exception contract.</summary>
    /// <param name="message">The message to show.</param>
    /// <param name="innerException">The underlying failure.</param>
    public WeirValidationException(string message, Exception innerException)
        : base(message, innerException) => Errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    /// <summary>Creates an empty exception, for the framework's exception contract.</summary>
    public WeirValidationException() => Errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    /// <summary>Per-field messages, keyed by the field they belong to. Empty when the body named none.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

/// <summary>The part of an RFC 7807 problem body this client reads back.</summary>
internal sealed record ProblemBody
{
    /// <summary>Short machine-readable summary of the problem.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The human-readable explanation; this is what gets shown.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>Per-field messages, present on a validation problem.</summary>
    [JsonPropertyName("errors")]
    public Dictionary<string, string[]>? Errors { get; init; }
}

/// <summary>Shared serializer settings for reading problem bodies.</summary>
internal static class ProblemJson
{
    /// <summary>Options used to read a problem body.</summary>
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}
