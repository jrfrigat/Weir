namespace Weir.Contracts;

/// <summary>
/// A portable snapshot of the control-plane configuration, produced by the admin backup/export endpoint.
/// It carries only non-secret data - endpoint definitions, scopes, the runtime settings, and the
/// non-secret views of API keys and admin accounts. Secrets (API-key hashes, admin password hashes) are
/// intentionally excluded, so this is a configuration and inventory backup, not a credential dump; a
/// full disaster-recovery backup should use a database-level snapshot of the control store.
/// </summary>
public sealed record ControlPlaneExport
{
    /// <summary>A short marker identifying the document as a Weir control-plane export.</summary>
    public string Kind { get; init; } = "weir.control-plane.export";

    /// <summary>Format version of this export document.</summary>
    public int Version { get; init; } = 1;

    /// <summary>When the export was produced (UTC).</summary>
    public DateTimeOffset ExportedAt { get; init; }

    /// <summary>All endpoint definitions.</summary>
    public IReadOnlyList<EndpointDefinition> Endpoints { get; init; } = [];

    /// <summary>All scopes.</summary>
    public IReadOnlyList<Scope> Scopes { get; init; } = [];

    /// <summary>Non-secret views of the API keys (no key material or hashes).</summary>
    public IReadOnlyList<ApiKeyInfo> ApiKeys { get; init; } = [];

    /// <summary>Non-secret views of the admin accounts (no password hashes).</summary>
    public IReadOnlyList<AdminUserInfo> Admins { get; init; } = [];

    /// <summary>The current runtime system settings.</summary>
    public WeirSystemSettings Settings { get; init; } = new();
}
