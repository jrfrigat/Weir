using System.Text.Json;
using Weir.Contracts;

namespace Weir.Abstractions;

/// <summary>
/// Reads and writes an API key's client-certificate and CORS policies as the JSON a control-plane
/// store keeps them in. One copy for all three providers, with one set of serializer options, so a
/// policy written by a Weir on SQLite is read back byte-identically by a Weir on PostgreSQL - which
/// matters because the control-plane export is how configuration moves between them.
/// <para>
/// A null policy is a SQL NULL, not the four characters <c>null</c>: "this key states no policy" is a
/// different fact from "this key states an empty policy", and only the absent column can carry the
/// first one.
/// </para>
/// </summary>
public static class ApiKeyPolicyJson
{
    /// <summary>
    /// The one serializer shape policies are stored in. Web defaults, matching what every store
    /// already uses for the other JSON columns on these tables.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes a client-certificate policy, or returns null when there is none to store.</summary>
    /// <param name="policy">The policy, or null.</param>
    /// <returns>The JSON to store, or null for a SQL NULL.</returns>
    public static string? Write(ApiKeyMtlsPolicy? policy) =>
        policy is null ? null : JsonSerializer.Serialize(policy, Options);

    /// <summary>Serializes a CORS policy, or returns null when there is none to store.</summary>
    /// <param name="policy">The policy, or null.</param>
    /// <returns>The JSON to store, or null for a SQL NULL.</returns>
    public static string? Write(ApiKeyCorsPolicy? policy) =>
        policy is null ? null : JsonSerializer.Serialize(policy, Options);

    /// <summary>
    /// Deserializes a stored client-certificate policy. An absent or blank column reads as "no
    /// policy"; so does a column holding the JSON literal null, which is what a store written by an
    /// older convention could contain.
    /// </summary>
    /// <param name="json">The stored JSON, or null.</param>
    /// <returns>The policy, or null when the key states none.</returns>
    public static ApiKeyMtlsPolicy? ReadMtls(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ApiKeyMtlsPolicy>(json, Options);

    /// <summary>Deserializes a stored CORS policy, reading an absent or blank column as "no policy".</summary>
    /// <param name="json">The stored JSON, or null.</param>
    /// <returns>The policy, or null when the key states none.</returns>
    public static ApiKeyCorsPolicy? ReadCors(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ApiKeyCorsPolicy>(json, Options);
}
