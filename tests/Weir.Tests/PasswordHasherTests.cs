using System.Globalization;
using Weir.Host.Security;
using Xunit;

namespace Weir.Tests;

// The work factor is a setting because the right value rises with hardware. What makes raising it safe
// is that the count travels inside each hash, so these tests are really about one property: a stored
// hash must keep verifying at whatever count it was written with, no matter what the setting says now.
public class PasswordHasherTests
{
    private const int Fast = PasswordHasher.MinimumIterations;

    [Fact]
    public void A_Hash_Verifies_Against_Its_Password()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple", Fast);
        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
        Assert.False(PasswordHasher.Verify("Correct horse battery staple", hash));
    }

    [Fact]
    public void The_Work_Factor_Is_Written_Into_The_Hash()
    {
        var hash = PasswordHasher.Hash("pw", 12_345);
        Assert.Equal("12345", hash.Split('$')[1]);
    }

    [Fact]
    public void A_Hash_Written_At_Another_Work_Factor_Still_Verifies()
    {
        // The whole reason the setting needs no migration: raising it does not invalidate a single
        // existing password. If this ever broke, every admin would be locked out by a config change.
        var old = PasswordHasher.Hash("pw", Fast);
        var raised = PasswordHasher.Hash("pw", Fast * 2);

        Assert.True(PasswordHasher.Verify("pw", old));
        Assert.True(PasswordHasher.Verify("pw", raised));
        Assert.NotEqual(old.Split('$')[1], raised.Split('$')[1]);
    }

    [Fact]
    public void Two_Hashes_Of_One_Password_Differ()
    {
        // Salted, so a stolen table cannot be attacked once for every account that shares a password.
        Assert.NotEqual(PasswordHasher.Hash("pw", Fast), PasswordHasher.Hash("pw", Fast));
    }

    [Fact]
    public void A_Malformed_Hash_Is_Rejected_Rather_Than_Thrown_At()
    {
        // Reached with whatever is in the database, which may predate any format this code knows.
        Assert.False(PasswordHasher.Verify("pw", "not-a-hash"));
        Assert.False(PasswordHasher.Verify("pw", "pbkdf2$notanumber$c2FsdA==$aGFzaA=="));
        Assert.False(PasswordHasher.Verify("pw", string.Empty));
    }

    [Fact]
    public void The_Decoy_Carries_The_Requested_Work_Factor()
    {
        // The decoy exists to make a sign-in for a missing account cost what a real one costs. Built at
        // the wrong count it would do the opposite: raising the setting would make real accounts
        // measurably slower than missing ones, and the timing would say which usernames exist.
        Assert.Equal(Fast.ToString(CultureInfo.InvariantCulture), PasswordHasher.Decoy(Fast).Split('$')[1]);
        Assert.Equal((Fast * 2).ToString(CultureInfo.InvariantCulture), PasswordHasher.Decoy(Fast * 2).Split('$')[1]);
    }

    [Fact]
    public void The_Decoy_Is_Built_Once_Per_Work_Factor()
    {
        // Cached, not rebuilt per sign-in: it is verified on the failing path of every login for an
        // unknown username, which is exactly the path an attacker hammers.
        Assert.Same(PasswordHasher.Decoy(Fast), PasswordHasher.Decoy(Fast));
        Assert.NotSame(PasswordHasher.Decoy(Fast), PasswordHasher.Decoy(Fast * 3));

        // It is never anyone's stored hash, so what matters here is only that it behaves like one - a
        // password put to it fails, at full cost, the same way a wrong password for a real account does.
        Assert.False(PasswordHasher.Verify("pw", PasswordHasher.Decoy(Fast)));
    }
}
