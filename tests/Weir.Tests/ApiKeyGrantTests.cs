using Weir.Contracts;
using Xunit;

namespace Weir.Tests;

public class ApiKeyGrantTests
{
    [Fact]
    public void Wildcard_Levels_Match_Anything()
    {
        var grant = new ApiKeyGrant(); // all levels default to "*"
        Assert.True(grant.Allows("any", "any", "any"));
    }

    [Fact]
    public void Connection_Grant_Covers_Every_Schema_And_Object()
    {
        var grant = new ApiKeyGrant { Connection = "sales", Schema = "*", ObjectName = "*" };
        Assert.True(grant.Allows("sales", "dbo", "GetOrders"));
        Assert.False(grant.Allows("hr", "dbo", "GetOrders"));
    }

    [Fact]
    public void Schema_Grant_Covers_Every_Object_In_That_Schema()
    {
        var grant = new ApiKeyGrant { Connection = "sales", Schema = "dbo", ObjectName = "*" };
        Assert.True(grant.Allows("sales", "dbo", "GetOrders"));
        Assert.False(grant.Allows("sales", "reporting", "GetOrders"));
    }

    [Fact]
    public void Procedure_Grant_Matches_Only_That_Object()
    {
        var grant = new ApiKeyGrant { Connection = "sales", Schema = "dbo", ObjectName = "GetOrders" };
        Assert.True(grant.Allows("sales", "dbo", "GetOrders"));
        Assert.False(grant.Allows("sales", "dbo", "CreateOrder"));
    }

    [Fact]
    public void Matching_Is_Case_Insensitive()
    {
        var grant = new ApiKeyGrant { Connection = "Sales", Schema = "DBO", ObjectName = "GetOrders" };
        Assert.True(grant.Allows("sales", "dbo", "getorders"));
    }

    [Fact]
    public void Empty_Level_Is_Treated_As_Wildcard()
    {
        var grant = new ApiKeyGrant { Connection = "sales", Schema = "", ObjectName = "" };
        Assert.True(grant.Allows("sales", "anything", "anything"));
    }
}
