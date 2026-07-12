namespace Weir.Host.Options;

/// <summary>
/// HTTP security settings, bound from <c>Weir:Security</c>. Controls the security response headers
/// and whether plain HTTP requests are redirected to HTTPS (leave off when TLS is terminated by a
/// reverse proxy / ingress, which is the common container deployment).
/// </summary>
public sealed class SecurityOptions
{
    /// <summary>
    /// Content-Security-Policy header value. The default is compatible with the Blazor WebAssembly
    /// admin (it allows the WASM runtime and Flare's inline styles). Set to empty to omit the header.
    /// </summary>
    public string ContentSecurityPolicy { get; set; } =
        "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; object-src 'none'; " +
        "img-src 'self' data:; font-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' 'wasm-unsafe-eval'; connect-src 'self'";

    /// <summary>Whether to redirect plain HTTP to HTTPS in-process. Off by default (proxy usually does TLS).</summary>
    public bool RequireHttps { get; set; }
}
