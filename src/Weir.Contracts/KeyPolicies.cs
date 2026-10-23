namespace Weir.Contracts;

/// <summary>
/// The client-certificate (mTLS) requirement attached to one API key: whether the key may be used at
/// all without a certificate, and which certificates it accepts.
/// <para>
/// This type lives in the browser-safe contracts assembly, so it holds no <c>X509Certificate2</c> and
/// no cryptography: it compares strings. The host reads the thumbprint, the issuer's thumbprint and
/// the subject off the presented certificate and asks this policy about those, which is what lets one
/// rule be enforced by the server and shown by the admin UI without either side carrying the other's
/// dependencies.
/// </para>
/// <para>
/// An empty list does not restrict - the same rule <see cref="ApiKeyGrant"/> follows, where a wildcard
/// level matches everything and a key with no grants is unrestricted. A policy whose lists are all
/// empty and which does not require a certificate therefore allows exactly what no policy allows.
/// </para>
/// </summary>
public sealed record ApiKeyMtlsPolicy
{
    /// <summary>
    /// Whether the key may only be used over a connection that presented a client certificate. With
    /// no certificate the call is refused even when every list below is empty.
    /// </summary>
    public bool RequireCertificate { get; init; }

    /// <summary>
    /// Accepted certificate thumbprints (hex, case- and separator-insensitive). Empty accepts any
    /// certificate.
    /// </summary>
    public IReadOnlyList<string> Thumbprints { get; init; } = [];

    /// <summary>
    /// Accepted issuer thumbprints - the way to pin a key to one internal CA without listing every
    /// certificate that CA will ever sign. Empty accepts any issuer.
    /// </summary>
    public IReadOnlyList<string> IssuerThumbprints { get; init; } = [];

    /// <summary>
    /// Accepted subject patterns, matched against the certificate's subject with <c>*</c> standing for
    /// any run of characters (for example <c>CN=svc-*</c>). Empty accepts any subject.
    /// </summary>
    public IReadOnlyList<string> SubjectPatterns { get; init; } = [];

    /// <summary>Whether this policy asks for nothing at all, and so is indistinguishable from none.</summary>
    public bool IsEmpty =>
        !RequireCertificate && !HasCertificateConstraints;

    /// <summary>Whether the policy constrains which certificate is accepted, rather than only demanding one.</summary>
    public bool HasCertificateConstraints =>
        Thumbprints.Count > 0 || IssuerThumbprints.Count > 0 || SubjectPatterns.Count > 0;

    /// <summary>
    /// Whether the presented certificate satisfies this policy. Each non-empty list is an alternative:
    /// a certificate that matches by thumbprint need not also match a subject pattern, because the
    /// three lists are three ways of naming the same trusted client, not three separate demands.
    /// </summary>
    /// <param name="thumbprint">The certificate's own thumbprint, or null when none was presented.</param>
    /// <param name="issuerThumbprint">The issuing certificate's thumbprint, or null when it is not known.</param>
    /// <param name="subject">The certificate's subject distinguished name, or null when none was presented.</param>
    /// <returns>True when the call may proceed under this policy.</returns>
    public bool Accepts(string? thumbprint, string? issuerThumbprint, string? subject)
    {
        if (thumbprint is null && subject is null)
        {
            // No certificate was presented: only a policy that asks for nothing lets this through.
            return !RequireCertificate && !HasCertificateConstraints;
        }

        if (!HasCertificateConstraints)
        {
            // Any certificate will do - the policy only insisted that there be one.
            return true;
        }

        return MatchesAny(Thumbprints, thumbprint, ThumbprintEquals)
            || MatchesAny(IssuerThumbprints, issuerThumbprint, ThumbprintEquals)
            || MatchesAny(SubjectPatterns, subject, SubjectMatches);
    }

    /// <summary>Whether any entry of a list matches the value under the given comparison.</summary>
    /// <param name="patterns">The list to scan. An empty one never matches here; the caller handles that case.</param>
    /// <param name="value">The value read off the certificate, or null when it is not available.</param>
    /// <param name="match">The comparison to apply.</param>
    /// <returns>True if at least one entry matches.</returns>
    private static bool MatchesAny(IReadOnlyList<string> patterns, string? value, Func<string, string, bool> match)
    {
        if (patterns.Count == 0 || value is null)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (match(pattern, value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Compares two thumbprints as the hex strings they are: case does not matter, and neither do the
    /// spaces and colons that certificate viewers insert when they show one, because an operator
    /// pasting a thumbprint out of a viewer should not be refused over punctuation.
    /// </summary>
    /// <param name="left">One thumbprint.</param>
    /// <param name="right">The other thumbprint.</param>
    /// <returns>True when both denote the same value.</returns>
    public static bool ThumbprintEquals(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var i = 0;
        var j = 0;
        while (true)
        {
            while (i < left.Length && !IsHex(left[i]))
            {
                i++;
            }

            while (j < right.Length && !IsHex(right[j]))
            {
                j++;
            }

            if (i >= left.Length || j >= right.Length)
            {
                // Equal only when both ran out together: one with hex left over is a different thumbprint.
                return i >= left.Length && j >= right.Length;
            }

            if (char.ToUpperInvariant(left[i]) != char.ToUpperInvariant(right[j]))
            {
                return false;
            }

            i++;
            j++;
        }

        static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    /// <summary>
    /// Matches a subject pattern against a certificate subject. <c>*</c> stands for any run of
    /// characters, comparison is case-insensitive, and everything else is literal. Deliberately not a
    /// regular expression: a pattern typed into the admin UI is caller input, and a regular expression
    /// on the request path is an invitation to catastrophic backtracking.
    /// </summary>
    /// <param name="pattern">The pattern, with <c>*</c> as its only metacharacter.</param>
    /// <param name="subject">The certificate's subject.</param>
    /// <returns>True when the subject matches.</returns>
    public static bool SubjectMatches(string pattern, string subject)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(subject);

        if (pattern.Length == 0 || pattern == "*")
        {
            return true;
        }

        // Two-cursor wildcard match with a single backtrack point: linear in practice and, unlike a
        // regular expression, with no input that can make it blow up.
        var p = 0;
        var s = 0;
        var star = -1;
        var resume = 0;

        while (s < subject.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = s;
            }
            else if (p < pattern.Length && CharEquals(pattern[p], subject[s]))
            {
                p++;
                s++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                s = ++resume;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;

        static bool CharEquals(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }
}

/// <summary>
/// The CORS policy attached to one API key: which browser origins may call the data plane with this
/// key, and what the preflight answer tells them.
/// <para>
/// A key whose policy is present but whose <see cref="AllowedOrigins"/> is empty allows no
/// cross-origin call at all, and the server-wide default does not open it back up. That is the
/// difference between an empty policy and no policy (<c>null</c>), and it is the only way to close one
/// key while leaving <c>Weir:Cors</c> in place for the rest.
/// </para>
/// </summary>
public sealed record ApiKeyCorsPolicy
{
    /// <summary>
    /// Origins allowed to call with this key, each a scheme, host and port as a browser sends it (for
    /// example <c>https://app.example.com</c>). <c>*</c> allows any origin, which the host refuses to
    /// combine with <see cref="AllowCredentials"/>.
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary>
    /// Methods the preflight answer permits. Empty falls back to the data plane's own methods.
    /// <para>
    /// This is advice to the browser, not an authorization rule: CORS method lists are enforced by
    /// the browser before it sends the request, and nothing on the server consults this list again.
    /// What a key may actually call is decided by its scopes and grants and by the endpoint's own
    /// method - this only shapes what a preflight answer says.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AllowedMethods { get; init; } = [];

    /// <summary>
    /// Request headers the preflight answer permits. Empty echoes what the preflight asked for, which
    /// is what a key's own clients need: they send <c>X-Api-Key</c> and <c>Content-Type</c>.
    /// </summary>
    public IReadOnlyList<string> AllowedHeaders { get; init; } = [];

    /// <summary>Response headers the browser may read, beyond the CORS-safelisted ones.</summary>
    public IReadOnlyList<string> ExposedHeaders { get; init; } = [];

    /// <summary>
    /// Whether the browser may send credentials (cookies, a TLS client certificate) with the call.
    /// With this on, the answer names the caller's origin rather than <c>*</c>, because the
    /// specification forbids the wildcard alongside credentials and the browser refuses the pair.
    /// </summary>
    public bool AllowCredentials { get; init; }

    /// <summary>How long the browser may cache the preflight answer. Null leaves the browser's default.</summary>
    public int? MaxAgeSeconds { get; init; }

    /// <summary>Whether the policy names no origin at all, and so allows no cross-origin call.</summary>
    public bool IsEmpty => AllowedOrigins.Count == 0;

    /// <summary>
    /// Whether this policy allows the given origin. Comparison ignores case, as origins do, and
    /// tolerates one trailing slash, which is the commonest way to get an otherwise correct origin
    /// wrong.
    /// </summary>
    /// <param name="origin">The request's <c>Origin</c> header value.</param>
    /// <returns>True when the origin is allowed.</returns>
    public bool AllowsOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return false;
        }

        foreach (var allowed in AllowedOrigins)
        {
            if (allowed == "*" || OriginEquals(allowed, origin))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the policy allows any origin at all, through a <c>*</c> entry.</summary>
    /// <returns>True when one of the allowed origins is the wildcard.</returns>
    public bool AllowsAnyOrigin()
    {
        foreach (var allowed in AllowedOrigins)
        {
            if (allowed == "*")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Compares two origins, ignoring case and one optional trailing slash.</summary>
    /// <param name="left">The configured origin.</param>
    /// <param name="right">The origin the browser sent.</param>
    /// <returns>True when both denote the same origin.</returns>
    public static bool OriginEquals(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The accepted shape of an <see cref="ApiKeyMtlsPolicy"/> and an <see cref="ApiKeyCorsPolicy"/>, in
/// one place so the admin form and the admin API cannot disagree about it - the same arrangement
/// <see cref="SettingsBounds"/> uses for the system settings, and for the same reason: a client is not
/// a place to enforce a limit.
/// <para>
/// The list caps are not recommendations. They are the point past which an entry can only be a paste
/// or a generated dump, and where the policy stops being something an operator can read; the preflight
/// index is scanned per origin, so an unbounded list is a request-path cost too. The max-age ceiling is
/// a day, because a preflight cached for longer outlives any policy change an operator would expect to
/// take effect.
/// </para>
/// </summary>
public static class ApiKeyPolicyBounds
{
    /// <summary>Largest accepted <see cref="ApiKeyCorsPolicy.MaxAgeSeconds"/>, in seconds (one day).</summary>
    public const int MaxCorsMaxAgeSeconds = 86_400;

    /// <summary>Largest number of entries accepted in any one policy list.</summary>
    public const int MaxListEntries = 64;

    /// <summary>Largest accepted length of one list entry: an origin, a thumbprint or a pattern.</summary>
    public const int MaxEntryLength = 256;

    /// <summary>
    /// The first thing wrong with a pair of policies, or null when both are acceptable. One function
    /// for both sides: the admin form constrains its inputs with it, and the admin API refuses with
    /// it, because a limit is not enforced by the page that edits it and that route is reachable
    /// without the page.
    /// </summary>
    /// <param name="mtls">The client-certificate policy, or null.</param>
    /// <param name="cors">The CORS policy, or null.</param>
    /// <returns>A message naming what is wrong, or null when nothing is.</returns>
    public static string? FirstViolation(ApiKeyMtlsPolicy? mtls, ApiKeyCorsPolicy? cors)
    {
        if (mtls is not null)
        {
            var problem = CheckList(mtls.Thumbprints, nameof(ApiKeyMtlsPolicy.Thumbprints))
                ?? CheckList(mtls.IssuerThumbprints, nameof(ApiKeyMtlsPolicy.IssuerThumbprints))
                ?? CheckList(mtls.SubjectPatterns, nameof(ApiKeyMtlsPolicy.SubjectPatterns));
            if (problem is not null)
            {
                return problem;
            }
        }

        if (cors is null)
        {
            return null;
        }

        var corsProblem = CheckList(cors.AllowedOrigins, nameof(ApiKeyCorsPolicy.AllowedOrigins))
            ?? CheckList(cors.AllowedMethods, nameof(ApiKeyCorsPolicy.AllowedMethods))
            ?? CheckList(cors.AllowedHeaders, nameof(ApiKeyCorsPolicy.AllowedHeaders))
            ?? CheckList(cors.ExposedHeaders, nameof(ApiKeyCorsPolicy.ExposedHeaders));
        if (corsProblem is not null)
        {
            return corsProblem;
        }

        if (cors.MaxAgeSeconds is { } maxAge && (maxAge < 0 || maxAge > MaxCorsMaxAgeSeconds))
        {
            return $"'{nameof(ApiKeyCorsPolicy.MaxAgeSeconds)}' must be between 0 and {MaxCorsMaxAgeSeconds}.";
        }

        // The specification forbids a wildcard origin together with credentials, and every browser
        // refuses the pair, so a policy saying both would look permissive and allow nothing. Refused
        // on the way in rather than quietly corrected on the way out: a setting that does not do what
        // it says is worse than one that would not save.
        if (cors.AllowCredentials && cors.AllowsAnyOrigin())
        {
            return "A CORS policy cannot set AllowCredentials together with the wildcard origin '*'. "
                + "Name the origins explicitly, or turn AllowCredentials off.";
        }

        return null;
    }

    /// <summary>Checks one policy list against the entry-count and entry-length caps.</summary>
    /// <param name="values">The list to check.</param>
    /// <param name="name">Its name, for the message.</param>
    /// <returns>A message naming what is wrong, or null.</returns>
    private static string? CheckList(IReadOnlyList<string> values, string name)
    {
        if (values.Count > MaxListEntries)
        {
            return $"'{name}' holds {values.Count} entries; at most {MaxListEntries} are accepted.";
        }

        foreach (var value in values)
        {
            if (value.Length > MaxEntryLength)
            {
                return $"'{name}' holds an entry of {value.Length} characters; at most {MaxEntryLength} are accepted.";
            }
        }

        return null;
    }
}

/// <summary>
/// A change to one API key's policies, sent to the admin API. Carries only the policies: a key's
/// name, scopes and grants are a different capability with different consequences, and the route
/// that edits policies must not be able to widen what a key may call.
/// </summary>
public sealed record ApiKeyPolicyUpdate
{
    /// <summary>The new client-certificate policy, or null to state none.</summary>
    public ApiKeyMtlsPolicy? Mtls { get; init; }

    /// <summary>The new CORS policy, or null to state none, so the server-wide default applies.</summary>
    public ApiKeyCorsPolicy? Cors { get; init; }
}

/// <summary>
/// The answer to a policy change: the stored key, plus anything the operator should know that is not
/// a refusal.
/// </summary>
public sealed record ApiKeyPolicyUpdated
{
    /// <summary>The key as stored, with its new policies.</summary>
    public required ApiKeyInfo Key { get; init; }

    /// <summary>
    /// A warning about the saved policy in English, or null when there is nothing to say. The one case
    /// today is a client-certificate policy saved while <c>Weir:Mtls</c> is switched off: no
    /// certificate can reach a request then, so the key will be refused every call. Saving is still
    /// allowed, because configuring keys before enabling mTLS is a perfectly ordinary order of work -
    /// but an operator who is not told would be left with a key that stopped working for no visible
    /// reason.
    /// <para>
    /// This text is for callers that have nowhere to translate it - a script, a log. The admin UI
    /// renders its own localized wording from <see cref="WarningCode"/> instead, because the panel is
    /// bilingual and an English sentence in the middle of a Russian screen is a loose end.
    /// </para>
    /// </summary>
    public string? Warning { get; init; }

    /// <summary>
    /// A stable identifier for <see cref="Warning"/>, so a client can say the same thing in its own
    /// language. Null when there is no warning. See <see cref="ApiKeyPolicyWarnings"/>.
    /// </summary>
    public string? WarningCode { get; init; }
}

/// <summary>The warning codes <see cref="ApiKeyPolicyUpdated.WarningCode"/> can carry.</summary>
public static class ApiKeyPolicyWarnings
{
    /// <summary>
    /// A client-certificate policy was saved while server-wide mTLS is switched off, so no certificate
    /// can reach a request and the key will be refused every call until mTLS is enabled.
    /// </summary>
    public const string MtlsDisabled = "mtls-disabled";
}
