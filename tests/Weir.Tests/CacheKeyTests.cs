using System.Text.Json;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

public class CacheKeyTests
{
    private static EndpointDefinition Endpoint(CachePolicy cache) => new()
    {
        Route = "orders/get",
        HttpMethod = "POST",
        ConnectionName = "default",
        ObjectName = "usp_Get",
        Cache = cache,
    };

    [Fact]
    public void Build_SameInputs_ProducesSameKey()
    {
        var endpoint = Endpoint(new CachePolicy { Enabled = true, VaryByParameters = ["id"] });
        var values = new Dictionary<string, object?> { ["id"] = 1 };
        Assert.Equal(CacheKey.Build(endpoint, values, null), CacheKey.Build(endpoint, values, null));
    }

    [Fact]
    public void Build_DifferentParameter_ProducesDifferentKey()
    {
        var endpoint = Endpoint(new CachePolicy { Enabled = true, VaryByParameters = ["id"] });
        var a = CacheKey.Build(endpoint, new Dictionary<string, object?> { ["id"] = 1 }, null);
        var b = CacheKey.Build(endpoint, new Dictionary<string, object?> { ["id"] = 2 }, null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Build_VaryByApiKey_ProducesDifferentKeyPerApiKey()
    {
        var endpoint = Endpoint(new CachePolicy { Enabled = true, VaryByApiKey = true });
        var a = CacheKey.Build(endpoint, new Dictionary<string, object?>(), "key-1");
        var b = CacheKey.Build(endpoint, new Dictionary<string, object?>(), "key-2");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Build_DifferentBinaryValues_ProduceDifferentKeys()
    {
        // Regression: Convert.ToString(byte[]) returns "System.Byte[]" for every array, so two distinct
        // binary vary-by values used to collide and serve one caller another caller's cached response.
        var endpoint = Endpoint(new CachePolicy { Enabled = true, VaryByParameters = ["blob"] });
        var a = CacheKey.Build(endpoint, new Dictionary<string, object?> { ["blob"] = new byte[] { 1, 2, 3 } }, null);
        var b = CacheKey.Build(endpoint, new Dictionary<string, object?> { ["blob"] = new byte[] { 9, 9, 9 } }, null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Build_DelimiterInValues_DoesNotCollideAcrossParameters()
    {
        // Regression: without length-prefixing, {a="x|b=y", b=""} and {a="x", b="y|b="} both flattened to
        // the same "a=x|b=y|b=" string and produced one cache key.
        var endpoint = Endpoint(new CachePolicy { Enabled = true, VaryByParameters = ["a", "b"] });
        var a = CacheKey.Build(endpoint, new Dictionary<string, object?> { ["a"] = "x|b=y", ["b"] = "" }, null);
        var b = CacheKey.Build(endpoint, new Dictionary<string, object?> { ["a"] = "x", ["b"] = "y|b=" }, null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Build_VaryByParameterMissingFromValues_ReturnsNull()
    {
        // Regression: a vary-by parameter that never reached the values map (an output parameter, a TVP
        // with no token, or a typo) used to encode as NULL for every caller and collapse distinct requests
        // onto one cache entry - a cross-caller disclosure. Now the key is refused, disabling caching.
        var endpoint = Endpoint(new CachePolicy { Enabled = true, VaryByParameters = ["items"] });
        Assert.Null(CacheKey.Build(endpoint, new Dictionary<string, object?>(), null));
    }

    [Fact]
    public void Build_VaryByPresentNullValue_StillBuildsKey()
    {
        // A parameter that is present but legitimately NULL (key exists, value null) must still key,
        // distinct from the "missing" case above.
        var endpoint = Endpoint(new CachePolicy { Enabled = true, VaryByParameters = ["id"] });
        Assert.NotNull(CacheKey.Build(endpoint, new Dictionary<string, object?> { ["id"] = null }, null));
    }

    [Fact]
    public void Build_VaryByParametersInDifferentOrder_ProducesSameKey()
    {
        // The vary-by set is sorted so the key does not depend on the order the names were configured
        // in; two policies that differ only in that order must address the same cache entry.
        var forward = Endpoint(new CachePolicy { Enabled = true, VaryByParameters = ["a", "b", "c"] });
        var reversed = Endpoint(new CachePolicy { Enabled = true, VaryByParameters = ["c", "b", "a"] });
        var values = new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2, ["c"] = 3 };

        Assert.Equal(CacheKey.Build(forward, values, null), CacheKey.Build(reversed, values, null));
    }

    [Fact]
    public void Build_SortedVaryBy_StillSeparatesDistinctValues()
    {
        // Sorting must not flatten the values into each other: the same vary-by set with different
        // values still has to land on different entries.
        var endpoint = Endpoint(new CachePolicy { Enabled = true, VaryByParameters = ["b", "a"] });
        var a = CacheKey.Build(endpoint, new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 }, null);
        var b = CacheKey.Build(endpoint, new Dictionary<string, object?> { ["a"] = 2, ["b"] = 1 }, null);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SortedVaryByParameters_SortsOrdinalAndIsStablePerInstance()
    {
        var policy = new CachePolicy { VaryByParameters = ["b", "A", "a"] };

        Assert.Equal(["A", "a", "b"], policy.SortedVaryByParameters);

        // Cached per instance: the same list object comes back rather than a fresh sort per read.
        Assert.Same(policy.SortedVaryByParameters, policy.SortedVaryByParameters);
    }

    [Fact]
    public void SortedVaryByParameters_WithExpressionReplacingSource_DoesNotInheritStaleOrder()
    {
        // The derived value is cached against the instance, so a `with` that replaces the source is a
        // new instance and must sort afresh instead of carrying the original's order over.
        var original = new CachePolicy { VaryByParameters = ["b", "a"] };
        Assert.Equal(["a", "b"], original.SortedVaryByParameters);

        var replaced = original with { VaryByParameters = ["z", "y"] };
        Assert.Equal(["y", "z"], replaced.SortedVaryByParameters);
        Assert.Equal(["a", "b"], original.SortedVaryByParameters);
    }

    [Fact]
    public void QualifiedName_MatchesSchemaAndObjectName()
    {
        var endpoint = Endpoint(new CachePolicy()) with { Schema = "sales", ObjectName = "usp_GetOrder" };

        Assert.Equal("sales.usp_GetOrder", endpoint.QualifiedName);
        Assert.Equal($"{endpoint.Schema}.{endpoint.ObjectName}", endpoint.QualifiedName);

        // Default schema, and stable per instance.
        Assert.Equal("dbo.usp_Get", Endpoint(new CachePolicy()).QualifiedName);
        Assert.Same(endpoint.QualifiedName, endpoint.QualifiedName);
    }

    [Fact]
    public void QualifiedName_WithExpressionChangingSchema_DoesNotInheritStaleName()
    {
        var original = Endpoint(new CachePolicy());
        Assert.Equal("dbo.usp_Get", original.QualifiedName);

        Assert.Equal("sales.usp_Get", (original with { Schema = "sales" }).QualifiedName);
        Assert.Equal("dbo.usp_Other", (original with { ObjectName = "usp_Other" }).QualifiedName);
        Assert.Equal("dbo.usp_Get", original.QualifiedName);
    }

    [Fact]
    public void DerivedMembers_DoNotAffectRecordEquality()
    {
        // The derived values are cached off-instance precisely so they stay out of the records'
        // synthesized Equals/GetHashCode. A backing field would join both, and equality would then flip
        // the first time any thread read the property - a race no caller could see coming.
        var endpoint = Endpoint(new CachePolicy { VaryByParameters = ["b", "a"] });
        var copy = endpoint with { };

        Assert.Equal(endpoint, copy);
        Assert.Equal(endpoint.GetHashCode(), copy.GetHashCode());

        _ = endpoint.QualifiedName;
        _ = endpoint.Cache.SortedVaryByParameters;

        Assert.Equal(endpoint, copy);
        Assert.Equal(endpoint.GetHashCode(), copy.GetHashCode());
    }

    [Fact]
    public void DerivedMembers_AreNotSerialized()
    {
        // These types round-trip through the control plane (the CacheJson column) and the admin API.
        // A derived member must not leak into either representation.
        var endpoint = Endpoint(new CachePolicy { Enabled = true, VaryByParameters = ["b", "a"] });
        _ = endpoint.QualifiedName;
        _ = endpoint.Cache.SortedVaryByParameters;

        Assert.DoesNotContain("QualifiedName", JsonSerializer.Serialize(endpoint), StringComparison.Ordinal);
        Assert.DoesNotContain("SortedVaryByParameters", JsonSerializer.Serialize(endpoint.Cache), StringComparison.Ordinal);
    }
}
