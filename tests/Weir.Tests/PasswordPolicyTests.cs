using Weir.Host.Security;
using Xunit;

namespace Weir.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("1234567")]
    public void Validate_RejectsWeakPasswords(string? password) =>
        Assert.NotNull(PasswordPolicy.Validate(password));

    [Theory]
    [InlineData("a-strong-password")]
    [InlineData("12345678")]
    public void Validate_AcceptsPasswordsMeetingTheLengthFloor(string password) =>
        Assert.Null(PasswordPolicy.Validate(password));
}
