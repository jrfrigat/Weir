using System.Text.Json;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

public class ValueCoercionTests
{
    [Fact]
    public void FromJson_Int32_Parses()
    {
        var element = JsonDocument.Parse("42").RootElement;
        Assert.Equal(42, ValueCoercion.FromJson(element, WeirDbType.Int32));
    }

    [Fact]
    public void FromJson_Null_ReturnsNull()
    {
        var element = JsonDocument.Parse("null").RootElement;
        Assert.Null(ValueCoercion.FromJson(element, WeirDbType.String));
    }

    [Fact]
    public void FromJson_Decimal_Parses()
    {
        var element = JsonDocument.Parse("12.5").RootElement;
        Assert.Equal(12.5m, ValueCoercion.FromJson(element, WeirDbType.Decimal));
    }

    [Fact]
    public void FromString_Guid_Parses()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, ValueCoercion.FromString(id.ToString(), WeirDbType.Guid));
    }

    [Fact]
    public void FromString_Boolean_Parses()
    {
        Assert.Equal(true, ValueCoercion.FromString("true", WeirDbType.Boolean));
    }

    [Fact]
    public void FromString_Null_ReturnsNull()
    {
        Assert.Null(ValueCoercion.FromString(null, WeirDbType.Int32));
    }
}
