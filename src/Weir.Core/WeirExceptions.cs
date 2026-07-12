namespace Weir.Core;

/// <summary>Base type for errors raised by the Weir engine.</summary>
public abstract class WeirException : Exception
{
    /// <summary>Creates the exception.</summary>
    protected WeirException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

/// <summary>Raised when request parameters fail validation. Maps to HTTP 400.</summary>
public sealed class WeirValidationException : WeirException
{
    /// <summary>Creates a validation error, optionally with per-field messages.</summary>
    public WeirValidationException(string message, IReadOnlyDictionary<string, string[]>? errors = null)
        : base(message) => Errors = errors ?? new Dictionary<string, string[]>();

    /// <summary>Per-parameter validation messages.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

/// <summary>Raised when the service is misconfigured (unknown connection or provider). Maps to HTTP 500.</summary>
public sealed class WeirConfigurationException : WeirException
{
    /// <summary>Creates the exception.</summary>
    public WeirConfigurationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Raised when a data connection is temporarily refusing work - its circuit breaker is open, or its
/// concurrency bulkhead is full. Maps to HTTP 503 so the caller can retry later. Carries no server
/// detail, so it is safe to surface directly.
/// </summary>
public sealed class WeirConnectionUnavailableException : WeirException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A generic, caller-safe reason.</param>
    public WeirConnectionUnavailableException(string message) : base(message)
    {
    }
}
