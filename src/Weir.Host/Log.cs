using Microsoft.Extensions.Logging;

namespace Weir.Host;

/// <summary>Source-generated, allocation-free log messages for the host.</summary>
internal static partial class Log
{
    /// <summary>Logs that startup initialization completed.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Weir control plane initialized and endpoint catalog loaded.")]
    public static partial void Initialized(ILogger logger);

    /// <summary>Logs that the bootstrap admin account was created.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="username">The created admin username.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrapped initial admin account '{username}'.")]
    public static partial void BootstrappedAdmin(ILogger logger, string username);

    /// <summary>Logs an unhandled data-plane error.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The unhandled exception.</param>
    /// <param name="route">The route being served.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error executing endpoint {route}.")]
    public static partial void DataPlaneError(ILogger logger, Exception exception, string route);

    /// <summary>Logs that a queued data-plane audit entry failed to persist.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write a data-plane audit entry.")]
    public static partial void AuditWriteFailed(ILogger logger, Exception exception);

    /// <summary>Logs that data-plane audit entries were dropped because the queue was full.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="totalDropped">The cumulative number of dropped entries since start.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Data-plane audit queue is full; {totalDropped} entries dropped so far. Raise Weir:Audit:QueueCapacity or reduce audit volume.")]
    public static partial void AuditEntriesDropped(ILogger logger, long totalDropped);

    /// <summary>Logs that a background prune removed old audit entries.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="deleted">Number of entries deleted.</param>
    /// <param name="retentionDays">The configured retention window in days.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {deleted} audit entries older than {retentionDays} days.")]
    public static partial void AuditPruned(ILogger logger, int deleted, int retentionDays);

    /// <summary>Logs that a background audit prune failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit retention prune failed; will retry on the next cycle.")]
    public static partial void AuditPruneFailed(ILogger logger, Exception exception);

    /// <summary>Logs that a queued request-log entry failed to persist.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write a request-log entry.")]
    public static partial void RequestLogWriteFailed(ILogger logger, Exception exception);

    /// <summary>Logs that request-log entries were dropped because the queue was full.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="totalDropped">The cumulative number of dropped entries since start.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Request-log queue is full; {totalDropped} entries dropped so far.")]
    public static partial void RequestLogEntriesDropped(ILogger logger, long totalDropped);

    /// <summary>Logs that a background prune removed old request-log entries.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="deleted">Number of entries deleted.</param>
    /// <param name="retentionDays">The configured retention window in days.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {deleted} request-log entries older than {retentionDays} days.")]
    public static partial void RequestLogPruned(ILogger logger, int deleted, int retentionDays);

    /// <summary>Logs a failed admin sign-in (security event).</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="username">The username that was attempted.</param>
    /// <param name="client">The client identifier (source IP).</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin sign-in failed for '{username}' from {client}.")]
    public static partial void LoginFailed(ILogger logger, string username, string client);

    /// <summary>Logs that a client was locked out after too many failed sign-ins (security event).</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="client">The client identifier (source IP).</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin sign-in locked out for {client} after repeated failures.")]
    public static partial void LoginLockedOut(ILogger logger, string client);

    /// <summary>Logs that an API key was denied a scope required by an endpoint (security event).</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="keyPrefix">The calling key's identifying prefix.</param>
    /// <param name="route">The route that was denied.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "API key {keyPrefix} denied by scope/grant on {route}.")]
    public static partial void DataPlaneForbidden(ILogger logger, string keyPrefix, string route);

    /// <summary>Logs that an API key exceeded its rate limit (security event).</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="keyPrefix">The calling key's identifying prefix.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "API key {keyPrefix} exceeded its rate limit.")]
    public static partial void RateLimited(ILogger logger, string keyPrefix);

    /// <summary>Logs that the distributed (Redis) rate limiter backend was unavailable; requests fail open.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The Redis failure.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Distributed rate-limiter backend unavailable; allowing the request (fail-open).")]
    public static partial void RateLimiterBackendUnavailable(ILogger logger, Exception exception);

    /// <summary>Logs that an admin changed the runtime system settings (security-relevant config change).</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="actor">The admin that made the change.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Runtime settings updated by {actor}.")]
    public static partial void SettingsChanged(ILogger logger, string? actor);

    /// <summary>Logs that a periodic endpoint-catalog reload failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Periodic endpoint-catalog reload failed; keeping the previous snapshot.")]
    public static partial void CatalogReloadFailed(ILogger logger, Exception exception);

    /// <summary>Logs that the configured bootstrap admin password was rejected by the password policy.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="reason">Why the password was rejected.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap admin not created: {reason} Set a stronger Weir:Admin:Password.")]
    public static partial void BootstrapPasswordRejected(ILogger logger, string reason);

    /// <summary>Logs that a plugin was loaded and registered.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="name">The plugin name.</param>
    /// <param name="path">The plugin assembly path.</param>
    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded plugin '{name}' from {path}.")]
    public static partial void PluginLoaded(ILogger logger, string name, string path);

    /// <summary>Logs that a configured plugin assembly could not be found.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="path">The configured plugin path.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin assembly not found at {path}; skipping.")]
    public static partial void PluginNotFound(ILogger logger, string path);

    /// <summary>Logs that a plugin assembly contained no plugin entry point.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="path">The plugin assembly path.</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Assembly {path} contains no IWeirPlugin implementation; skipping.")]
    public static partial void PluginNoEntryPoint(ILogger logger, string path);

    /// <summary>Logs that a plugin failed to load.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="path">The plugin assembly path.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to load plugin from {path}.")]
    public static partial void PluginLoadFailed(ILogger logger, Exception exception, string path);

    /// <summary>Logs that schema introspection for objects failed on a connection.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="connection">The connection name.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Introspection of objects failed for connection {connection}.")]
    public static partial void IntrospectObjectsFailed(ILogger logger, Exception exception, string connection);

    /// <summary>Logs that schema introspection for parameters failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="schema">The schema name.</param>
    /// <param name="objectName">The object name.</param>
    /// <param name="connection">The connection name.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Introspection of parameters failed for {schema}.{objectName} on {connection}.")]
    public static partial void IntrospectParametersFailed(ILogger logger, Exception exception, string schema, string objectName, string connection);

    /// <summary>Logs that a single-endpoint sync failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="endpointId">The endpoint id.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Sync failed for endpoint {endpointId}.")]
    public static partial void SyncEndpointFailed(ILogger logger, Exception exception, Guid endpointId);

    /// <summary>Logs that a bulk object-set load failed during sync.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="connection">The connection name.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to load objects for connection {connection} during sync.")]
    public static partial void SyncObjectLoadFailed(ILogger logger, Exception exception, string connection);

    /// <summary>Logs that a per-endpoint sync failed during bulk sync.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="endpointId">The endpoint id.</param>
    /// <param name="route">The endpoint route.</param>
    [LoggerMessage(Level = LogLevel.Error, Message = "Sync failed for endpoint {endpointId} ({route}) during bulk sync.")]
    public static partial void BulkSyncEndpointFailed(ILogger logger, Exception exception, Guid endpointId, string route);
}
