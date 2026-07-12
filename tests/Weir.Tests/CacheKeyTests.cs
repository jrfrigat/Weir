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
}
