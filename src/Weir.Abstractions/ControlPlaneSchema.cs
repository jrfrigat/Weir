namespace Weir.Abstractions;

/// <summary>
/// The one rule every control-plane store applies before it touches the schema: a store whose recorded
/// schema version is ahead of the migrations this build ships was migrated by a newer Weir, and a build
/// that starts against a schema it does not know is exactly what must not happen after a rollback to an
/// earlier image.
/// </summary>
public static class ControlPlaneSchema
{
    /// <summary>
    /// Refuses a store whose recorded schema version is ahead of the migrations this build ships.
    /// </summary>
    /// <param name="appliedVersion">The schema version the store reports.</param>
    /// <param name="shippedMigrations">How many migrations this build ships.</param>
    /// <param name="providerName">The store's provider, named in the message so an operator running more than one knows which store refused.</param>
    /// <exception cref="ControlPlaneMigrationException">The recorded version is ahead of the shipped migrations.</exception>
    public static void EnsureNotAheadOf(int appliedVersion, int shippedMigrations, string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        if (appliedVersion <= shippedMigrations)
        {
            return;
        }

        throw new ControlPlaneMigrationException(
            $"The {providerName} control-plane schema is at version {appliedVersion}, but this Weir build "
            + $"ships {shippedMigrations} migrations. The database was migrated by a newer Weir build, so "
            + "this one does not understand its schema and will not start against it. Upgrade Weir, or "
            + "restore a control-plane backup taken before the newer schema was applied.");
    }
}
