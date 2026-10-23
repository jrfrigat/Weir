using Microsoft.Extensions.Options;
using Weir.Abstractions;
using Weir.Contracts;
using Weir.ControlPlane.Sqlite;
using Xunit;

namespace Weir.Tests;

// The per-key mTLS and CORS policies travel through five places in every control-plane store: the two
// SELECT column lists, the INSERT, the row model and the two mappers. Miss one and the policy exists in
// the database but not in the record the authenticator caches - a key that looks restricted in the admin
// UI and is wide open on the request path. That failure is silent, so it is checked here rather than
// assumed, and the null case is checked beside it: a key that states no policy must keep reading as
// "no policy", because that is what every key written before these columns existed says.
public class ApiKeyPolicyStoreTests : IDisposable
{
    /// <summary>Throwaway databases this test opened; each is removed when the test ends.</summary>
    private readonly List<TempSqliteDatabase> _databases = [];

    /// <summary>Removes the databases this test created.</summary>
    public void Dispose()
    {
        foreach (var database in _databases)
        {
            database.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_Key_Created_With_Policies_Reads_Them_Back()
    {
        var store = await NewStoreAsync();
        var create = new ApiKeyCreate
        {
            Name = "with-policies",
            Mtls = new ApiKeyMtlsPolicy
            {
                RequireCertificate = true,
                Thumbprints = ["AA:BB:CC:DD"],
                IssuerThumbprints = ["1234abcd"],
                SubjectPatterns = ["CN=svc-*"],
            },
            Cors = new ApiKeyCorsPolicy
            {
                AllowedOrigins = ["https://app.example.com"],
                AllowedMethods = ["GET", "POST"],
                AllowedHeaders = ["X-Api-Key"],
                ExposedHeaders = ["X-Correlation-ID"],
                AllowCredentials = true,
                MaxAgeSeconds = 600,
            },
        };

        var created = await store.CreateApiKeyAsync(create, "hash-with-policies", "wk_test_aa");

        // The value returned by the create call, the admin list and the authentication record are three
        // separate mappings of the same row; all three have to agree, so all three are checked.
        AssertPolicies(created.Mtls, created.Cors);

        var listed = Assert.Single(await store.GetApiKeysAsync());
        AssertPolicies(listed.Mtls, listed.Cors);

        var record = await store.FindApiKeyByHashAsync("hash-with-policies");
        Assert.NotNull(record);
        AssertPolicies(record.Mtls, record.Cors);

        static void AssertPolicies(ApiKeyMtlsPolicy? mtls, ApiKeyCorsPolicy? cors)
        {
            Assert.NotNull(mtls);
            Assert.True(mtls.RequireCertificate);
            Assert.Equal(["AA:BB:CC:DD"], mtls.Thumbprints);
            Assert.Equal(["1234abcd"], mtls.IssuerThumbprints);
            Assert.Equal(["CN=svc-*"], mtls.SubjectPatterns);

            Assert.NotNull(cors);
            Assert.Equal(["https://app.example.com"], cors.AllowedOrigins);
            Assert.Equal(["GET", "POST"], cors.AllowedMethods);
            Assert.Equal(["X-Api-Key"], cors.AllowedHeaders);
            Assert.Equal(["X-Correlation-ID"], cors.ExposedHeaders);
            Assert.True(cors.AllowCredentials);
            Assert.Equal(600, cors.MaxAgeSeconds);
        }
    }

    [Fact]
    public async Task A_Key_Created_Without_Policies_Reads_As_Stating_None()
    {
        var store = await NewStoreAsync();
        await store.CreateApiKeyAsync(new ApiKeyCreate { Name = "plain" }, "hash-plain", "wk_test_bb");

        var listed = Assert.Single(await store.GetApiKeysAsync());
        Assert.Null(listed.Mtls);
        Assert.Null(listed.Cors);

        var record = await store.FindApiKeyByHashAsync("hash-plain");
        Assert.NotNull(record);
        Assert.Null(record.Mtls);
        Assert.Null(record.Cors);
    }

    [Fact]
    public async Task Updating_The_Policies_Replaces_Them_And_Null_Clears_Them()
    {
        var store = await NewStoreAsync();
        var created = await store.CreateApiKeyAsync(new ApiKeyCreate { Name = "edited" }, "hash-edited", "wk_test_cc");

        await store.UpdateApiKeyPoliciesAsync(
            created.Id,
            new ApiKeyMtlsPolicy { RequireCertificate = true },
            new ApiKeyCorsPolicy { AllowedOrigins = ["https://one.example.com"] });

        var afterSet = await store.FindApiKeyByHashAsync("hash-edited");
        Assert.NotNull(afterSet);
        Assert.True(afterSet.Mtls?.RequireCertificate);
        Assert.Equal(["https://one.example.com"], afterSet.Cors?.AllowedOrigins);

        // An empty CORS policy is not the same fact as no policy: it closes cross-origin access for this
        // key alone, so it has to survive the round trip as a present-but-empty policy.
        await store.UpdateApiKeyPoliciesAsync(created.Id, null, new ApiKeyCorsPolicy());

        var afterEmpty = await store.FindApiKeyByHashAsync("hash-edited");
        Assert.NotNull(afterEmpty);
        Assert.Null(afterEmpty.Mtls);
        Assert.NotNull(afterEmpty.Cors);
        Assert.True(afterEmpty.Cors.IsEmpty);

        await store.UpdateApiKeyPoliciesAsync(created.Id, null, null);

        var afterClear = await store.FindApiKeyByHashAsync("hash-edited");
        Assert.NotNull(afterClear);
        Assert.Null(afterClear.Mtls);
        Assert.Null(afterClear.Cors);
    }

    [Fact]
    public async Task Updating_A_Key_That_Is_Gone_Is_Not_An_Error()
    {
        var store = await NewStoreAsync();

        // The admin UI offers the edit from a list it read earlier, so a revoke that removed the key in
        // between is a race, not a failure to report.
        await store.UpdateApiKeyPoliciesAsync(Guid.NewGuid(), new ApiKeyMtlsPolicy { RequireCertificate = true }, null);
    }

    [Fact]
    public void A_Policy_Round_Trips_Through_The_Shared_Json_Helper()
    {
        // All three providers store the policy through this one helper, so the shape in the database does
        // not depend on which provider wrote it - which is what makes a control-plane export portable.
        var mtls = new ApiKeyMtlsPolicy { RequireCertificate = true, SubjectPatterns = ["CN=a"] };
        var cors = new ApiKeyCorsPolicy { AllowedOrigins = ["https://a.example.com"], MaxAgeSeconds = 30 };

        // Field by field, not record by record: these are records with list properties, and a record's
        // generated equality compares a list by reference, so two policies holding equal lists are never
        // equal to it. Anything that needs to tell two policies apart (a cached CORS index, say) has to
        // compare their contents itself.
        var readMtls = ApiKeyPolicyJson.ReadMtls(ApiKeyPolicyJson.Write(mtls));
        Assert.NotNull(readMtls);
        Assert.True(readMtls.RequireCertificate);
        Assert.Equal(["CN=a"], readMtls.SubjectPatterns);

        var readCors = ApiKeyPolicyJson.ReadCors(ApiKeyPolicyJson.Write(cors));
        Assert.NotNull(readCors);
        Assert.Equal(["https://a.example.com"], readCors.AllowedOrigins);
        Assert.Equal(30, readCors.MaxAgeSeconds);

        Assert.Null(ApiKeyPolicyJson.Write((ApiKeyMtlsPolicy?)null));
        Assert.Null(ApiKeyPolicyJson.Write((ApiKeyCorsPolicy?)null));
        Assert.Null(ApiKeyPolicyJson.ReadMtls(null));
        Assert.Null(ApiKeyPolicyJson.ReadCors("   "));
    }

    /// <summary>Opens a throwaway SQLite control plane, registered for cleanup.</summary>
    /// <returns>The initialized store.</returns>
    private async Task<SqliteControlPlaneStore> NewStoreAsync()
    {
        var database = new TempSqliteDatabase("weir-keypolicy");
        _databases.Add(database);
        var store = new SqliteControlPlaneStore(Options.Create(database.Options), TimeProvider.System);
        await store.InitializeAsync();
        return store;
    }
}
