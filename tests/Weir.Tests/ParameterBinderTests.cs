using System.Globalization;
using System.Text;
using System.Text.Json;
using Weir.Contracts;
using Weir.Core;
using Xunit;

namespace Weir.Tests;

public class ParameterBinderTests
{
    private static WeirInvocation Invocation(EndpointDefinition endpoint, string? bodyJson)
    {
        JsonElement body = default;
        var hasBody = false;
        if (bodyJson is not null)
        {
            body = JsonDocument.Parse(bodyJson).RootElement;
            hasBody = true;
        }

        return new WeirInvocation { Endpoint = endpoint, Body = body, HasBody = hasBody };
    }

    private static EndpointDefinition Endpoint(params EndpointParameter[] parameters) => new()
    {
        Route = "x",
        ConnectionName = "default",
        ObjectName = "usp",
        Parameters = parameters,
    };

    [Fact]
    public void Binds_BodyParameter()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "id", DbType = WeirDbType.Int32 });
        var result = new ParameterBinder().Bind(Invocation(endpoint, "{\"id\":5}"));
        Assert.Single(result.Parameters);
        Assert.Equal(5, result.Parameters[0].Value);
        Assert.Equal("id", result.Parameters[0].Name);
    }

    [Fact]
    public void Missing_Required_Throws()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "id", DbType = WeirDbType.Int32, Required = true });
        Assert.Throws<WeirValidationException>(() => new ParameterBinder().Bind(Invocation(endpoint, "{}")));
    }

    [Fact]
    public void Default_Applied_When_Missing()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "flag", DbType = WeirDbType.String, DefaultValue = "def" });
        var result = new ParameterBinder().Bind(Invocation(endpoint, "{}"));
        Assert.Equal("def", result.Parameters[0].Value);
    }

    [Fact]
    public void UsesDbParameterName_When_Set()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "id", DbParameterName = "CustomerId", DbType = WeirDbType.Int32 });
        var result = new ParameterBinder().Bind(Invocation(endpoint, "{\"id\":7}"));
        Assert.Equal("CustomerId", result.Parameters[0].Name);
    }

    [Fact]
    public void Tvp_BuildsTable()
    {
        var endpoint = Endpoint(new EndpointParameter
        {
            Name = "items",
            DbType = WeirDbType.Structured,
            TypeName = "dbo.OrderItemType",
            TableColumns =
            [
                new TvpColumn { Name = "sku" },
                new TvpColumn { Name = "qty", DbType = WeirDbType.Int32 },
            ],
        });

        var result = new ParameterBinder().Bind(Invocation(endpoint, "{\"items\":[{\"sku\":\"A1\",\"qty\":2}]}"));
        var parameter = result.Parameters[0];
        Assert.NotNull(parameter.Table);
        Assert.Single(parameter.Table!.Rows);
        Assert.Equal("A1", parameter.Table.Rows[0][0]);
        Assert.Equal(2, parameter.Table.Rows[0][1]);
    }

    [Fact]
    public void ValidationRegex_Rejects_BadValue()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "code", DbType = WeirDbType.String, ValidationRegex = "^[A-Z]{3}$" });
        Assert.Throws<WeirValidationException>(() => new ParameterBinder().Bind(Invocation(endpoint, "{\"code\":\"abc\"}")));
    }

    [Fact]
    public void ValidationRegex_Accepts_GoodValue()
    {
        var endpoint = Endpoint(new EndpointParameter { Name = "code", DbType = WeirDbType.String, ValidationRegex = "^[A-Z]{3}$" });
        var result = new ParameterBinder().Bind(Invocation(endpoint, "{\"code\":\"ABC\"}"));
        Assert.Equal("ABC", result.Parameters[0].Value);
    }

    [Fact]
    public void ValidationRegex_Applies_ToNonStringParameter()
    {
        // The regex is matched against the canonical invariant form of the coerced value, so it still
        // guards parameters that bound to a non-string CLR type instead of being silently skipped.
        var endpoint = Endpoint(new EndpointParameter { Name = "year", DbType = WeirDbType.Int32, ValidationRegex = "^[0-9]{4}$" });

        var result = new ParameterBinder().Bind(Invocation(endpoint, "{\"year\":2026}"));
        Assert.Equal(2026, result.Parameters[0].Value);

        Assert.Throws<WeirValidationException>(() => new ParameterBinder().Bind(Invocation(endpoint, "{\"year\":42}")));
    }

    [Theory]
    [InlineData(WeirDbType.Guid, "^[0-9a-f-]{36}$", "\"e6f1d0c2-1111-4222-8333-444455556666\"")]
    [InlineData(WeirDbType.Int64, "^-?[0-9]+$", "-9007199254740993")]
    [InlineData(WeirDbType.Boolean, "^True$", "true")]
    [InlineData(WeirDbType.Decimal, "^12\\.50$", "12.50")]
    public void ValidationRegex_MatchesCanonicalInvariantForm_OfCoercedTypes(WeirDbType dbType, string pattern, string bodyValue)
    {
        // Each of these coerces to an ISpanFormattable CLR type, which the binder renders without
        // allocating. The rendered text must stay exactly what Convert.ToString(value, InvariantCulture)
        // produced, or an operator's existing pattern would quietly stop matching.
        var endpoint = Endpoint(new EndpointParameter { Name = "v", DbType = dbType, ValidationRegex = pattern });
        var result = new ParameterBinder().Bind(Invocation(endpoint, $"{{\"v\":{bodyValue}}}"));
        Assert.Single(result.Parameters);
    }

    [Fact]
    public void ValidationRegex_HandlesManyDistinctPatterns_OnOneBinder()
    {
        // The cliff scenario. Regex.Cache holds 15 entries by default, so more distinct patterns than
        // that used to thrash it and re-parse per request; the binder now owns the cache. Well past that
        // bound, every parameter must still be matched against its OWN pattern - no mix-ups, no leakage
        // between parameters that share a binder.
        const int patternCount = 60;
        var parameters = new EndpointParameter[patternCount];
        for (var i = 0; i < patternCount; i++)
        {
            // A distinct pattern per parameter, each accepting only its own index.
            parameters[i] = new EndpointParameter
            {
                Name = $"p{i}",
                DbType = WeirDbType.String,
                ValidationRegex = $"^value-{i}$",
            };
        }

        var endpoint = Endpoint(parameters);
        var binder = new ParameterBinder();

        var body = new StringBuilder("{");
        for (var i = 0; i < patternCount; i++)
        {
            body.Append(i == 0 ? "" : ",").Append(CultureInfo.InvariantCulture, $"\"p{i}\":\"value-{i}\"");
        }

        body.Append('}');

        // Every parameter matches its own pattern.
        var result = binder.Bind(Invocation(endpoint, body.ToString()));
        Assert.Equal(patternCount, result.Parameters.Count);

        // Each pattern still rejects a value that belongs to a different parameter, proving the cache
        // returns the right regex per pattern rather than whichever was constructed last.
        for (var i = 0; i < patternCount; i++)
        {
            var wrong = new StringBuilder("{");
            for (var j = 0; j < patternCount; j++)
            {
                var value = j == i ? $"value-{(i + 1) % patternCount}" : $"value-{j}";
                wrong.Append(j == 0 ? "" : ",").Append(CultureInfo.InvariantCulture, $"\"p{j}\":\"{value}\"");
            }

            wrong.Append('}');

            var ex = Assert.Throws<WeirValidationException>(() => binder.Bind(Invocation(endpoint, wrong.ToString())));
            var failed = Assert.Single(ex.Errors);
            Assert.Equal($"p{i}", failed.Key);
        }
    }

    [Fact]
    public void ValidationRegex_ReportsEveryFailure_InTheErrorMap()
    {
        // The error map is built lazily now, so a bind with several failures must still carry one entry
        // per offending parameter, keyed by name.
        var endpoint = Endpoint(
            new EndpointParameter { Name = "a", DbType = WeirDbType.String, ValidationRegex = "^[A-Z]$" },
            new EndpointParameter { Name = "b", DbType = WeirDbType.String, ValidationRegex = "^[0-9]$" },
            new EndpointParameter { Name = "ok", DbType = WeirDbType.String, ValidationRegex = "^[a-z]$" });

        var ex = Assert.Throws<WeirValidationException>(() =>
            new ParameterBinder().Bind(Invocation(endpoint, "{\"a\":\"1\",\"b\":\"x\",\"ok\":\"z\"}")));

        Assert.Equal("One or more parameters are invalid.", ex.Message);
        Assert.Equal(2, ex.Errors.Count);
        Assert.Contains("a", ex.Errors.Keys);
        Assert.Contains("b", ex.Errors.Keys);
        Assert.DoesNotContain("ok", ex.Errors.Keys);
    }

    [Fact]
    public void Bind_Succeeds_WhenNoParameterFails()
    {
        // Guards the lazy error map from the other side: a clean bind must not manufacture an exception.
        var endpoint = Endpoint(new EndpointParameter { Name = "code", DbType = WeirDbType.String, ValidationRegex = "^[A-Z]{3}$" });
        var result = new ParameterBinder().Bind(Invocation(endpoint, "{\"code\":\"XYZ\"}"));
        Assert.Single(result.Parameters);
        Assert.Equal("XYZ", result.Values["code"]);
    }

    /// <summary>A TVP parameter definition used by the token tests.</summary>
    private static EndpointParameter TvpParameter() => new()
    {
        Name = "items",
        DbType = WeirDbType.Structured,
        TypeName = "dbo.OrderItemType",
        TableColumns = [new TvpColumn { Name = "sku" }],
    };

    [Fact]
    public void Tvp_RecordsTokenForCacheKeying_WhenTheCacheVariesByIt()
    {
        // A TVP must contribute a value to the cache-key map so an endpoint that varies by it keys on
        // the actual rows instead of colliding on NULL. Distinct row sets must yield distinct tokens.
        var endpoint = Endpoint(TvpParameter());
        endpoint = new EndpointDefinition
        {
            Route = endpoint.Route,
            ConnectionName = endpoint.ConnectionName,
            ObjectName = endpoint.ObjectName,
            Parameters = endpoint.Parameters,
            Cache = new CachePolicy { Enabled = true, TtlSeconds = 60, VaryByParameters = ["items"] },
        };

        var a = new ParameterBinder().Bind(Invocation(endpoint, "{\"items\":[{\"sku\":\"A1\"}]}"));
        var b = new ParameterBinder().Bind(Invocation(endpoint, "{\"items\":[{\"sku\":\"B2\"}]}"));
        Assert.True(a.Values.ContainsKey("items"));
        Assert.NotEqual(a.Values["items"], b.Values["items"]);
    }

    [Fact]
    public void Tvp_VaryByIsMatchedCaseInsensitively()
    {
        // CacheKey.Build looks the value up in a case-insensitive map, so a vary-by entry that differs
        // only in case must still produce a token. If this gate and that lookup ever disagree, the key
        // silently loses the TVP and distinct row sets collide onto one cached response.
        var endpoint = new EndpointDefinition
        {
            Route = "x",
            ConnectionName = "default",
            ObjectName = "usp",
            Parameters = [TvpParameter()],
            Cache = new CachePolicy { Enabled = true, TtlSeconds = 60, VaryByParameters = ["ITEMS"] },
        };

        var result = new ParameterBinder().Bind(Invocation(endpoint, "{\"items\":[{\"sku\":\"A1\"}]}"));
        Assert.True(result.Values.ContainsKey("items"));
    }

    [Fact]
    public void Tvp_SkipsTheTokenWhenNothingReadsIt()
    {
        // The token is only ever read by the cache key and by parameter capture. Building it walks every
        // cell, so an endpoint that does neither must not pay for it. The parameter itself still binds.
        var endpoint = Endpoint(TvpParameter());

        var result = new ParameterBinder().Bind(Invocation(endpoint, "{\"items\":[{\"sku\":\"A1\"}]}"));

        Assert.False(result.Values.ContainsKey("items"));
        Assert.Single(result.Parameters);
        Assert.NotNull(result.Parameters[0].Table);
    }

    [Fact]
    public void Tvp_RecordsTokenWhenParametersAreCaptured()
    {
        // Parameter capture serializes the whole value map, so an endpoint that opts into it keeps the
        // TVP in the log exactly as before.
        var endpoint = new EndpointDefinition
        {
            Route = "x",
            ConnectionName = "default",
            ObjectName = "usp",
            Parameters = [TvpParameter()],
            Logging = new EndpointLogging { Enabled = true, LogParameters = true },
        };

        var result = new ParameterBinder().Bind(Invocation(endpoint, "{\"items\":[{\"sku\":\"A1\"}]}"));
        Assert.True(result.Values.ContainsKey("items"));
    }

    [Fact]
    public void Tvp_ExceedingRowCap_Throws()
    {
        var endpoint = Endpoint(new EndpointParameter
        {
            Name = "items",
            DbType = WeirDbType.Structured,
            TypeName = "dbo.OrderItemType",
            TableColumns = [new TvpColumn { Name = "sku" }],
        });

        var binder = new ParameterBinder(new StubRuntimeSettings(new WeirSystemSettings { MaxTvpRows = 2 }));
        Assert.Throws<WeirValidationException>(() =>
            binder.Bind(Invocation(endpoint, "{\"items\":[{\"sku\":\"A\"},{\"sku\":\"B\"},{\"sku\":\"C\"}]}")));
    }

    /// <summary>Fixed <see cref="IRuntimeSettings"/> returning a preset snapshot, for cap tests.</summary>
    private sealed class StubRuntimeSettings(WeirSystemSettings settings) : IRuntimeSettings
    {
        public WeirSystemSettings Current { get; } = settings;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(WeirSystemSettings s, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
