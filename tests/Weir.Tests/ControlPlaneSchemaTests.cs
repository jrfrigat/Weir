using Weir.Abstractions;
using Xunit;

namespace Weir.Tests;

// The rule that refuses a control-plane schema migrated by a newer Weir. It is covered here rather than in
// each store because all three apply it, and because the PostgreSQL and SQL Server integration tests only
// run under WEIR_CONTAINER_TESTS: without this, the rule itself would be untested on a normal run.
public class ControlPlaneSchemaTests
{
    [Fact]
    public void A_Version_Level_With_The_Shipped_Migrations_Is_Accepted() =>
        ControlPlaneSchema.EnsureNotAheadOf(14, 14, "SQLite");

    [Fact]
    public void A_Store_That_Has_Applied_Nothing_Is_Accepted() =>
        ControlPlaneSchema.EnsureNotAheadOf(0, 14, "SQLite");

    [Fact]
    public void A_Version_Ahead_Of_The_Shipped_Migrations_Is_Refused()
    {
        var exception = Assert.Throws<ControlPlaneMigrationException>(
            () => ControlPlaneSchema.EnsureNotAheadOf(15, 14, "PostgreSQL"));

        // The message has to name both numbers and the provider: an operator running more than one store
        // reads it to decide between upgrading Weir and restoring a backup.
        Assert.Contains("15", exception.Message, StringComparison.Ordinal);
        Assert.Contains("14", exception.Message, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Unnamed_Provider_Is_Refused() =>
        Assert.Throws<ArgumentException>(() => ControlPlaneSchema.EnsureNotAheadOf(1, 0, " "));
}
