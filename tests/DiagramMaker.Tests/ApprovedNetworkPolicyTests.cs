using System.Net;
using DiagramMaker.Security;
using DiagramMaker.Services;
using DiagramMaker.Configuration;

namespace DiagramMaker.Tests;

public sealed class ApprovedNetworkPolicyTests
{
    private static ApprovedNetworkPolicy Policy() => new()
    {
        LlmOrigins = ["https://model.corp.invalid:8443"],
        LlmAddressRanges = ["10.20.0.0/16"],
        Databases = [new("10.20.1.2", 5432)]
    };

    [Theory]
    [InlineData("https://external.invalid/v1/chat/completions")]
    [InlineData("https://model.corp.invalid:443/v1/chat/completions")]
    [InlineData("http://model.corp.invalid:8443/v1/chat/completions")]
    public void RejectsUnapprovedOriginsEvenWhenApplicationOriginMatches(string endpoint)
    {
        Assert.Throws<InvalidOperationException>(() => new VllmClient(new LlmOptions
        {
            Enabled = true, Endpoint = endpoint, AllowedOrigin = new Uri(endpoint).GetLeftPart(UriPartial.Authority)
        }, networkPolicy: Policy()));
    }

    [Fact]
    public void RejectsMixedDnsAnswersAndMappedExternalAddresses()
    {
        Policy().ValidateAddresses([IPAddress.Parse("10.20.1.5"), IPAddress.Parse("::ffff:10.20.2.8")]);
        Assert.Throws<InvalidOperationException>(() => Policy().ValidateAddresses([IPAddress.Parse("10.20.1.5"), IPAddress.Parse("203.0.113.9")]));
        Assert.Throws<InvalidOperationException>(() => Policy().ValidateAddresses([IPAddress.Parse("::ffff:203.0.113.9")]));
        Assert.Throws<InvalidOperationException>(() => Policy().ValidateAddresses([]));
    }

    [Theory]
    [InlineData("Host=external.invalid;SSL Mode=VerifyFull")]
    [InlineData("Host=10.20.1.2,203.0.113.1;SSL Mode=VerifyFull")]
    [InlineData("Host=10.20.1.2;Port=5433;SSL Mode=VerifyFull")]
    [InlineData("Host=10.20.1.2;SSL Mode=Disable")]
    public void RejectsDatabaseHostListsDnsAndUnapprovedPorts(string connection)
        => Assert.Throws<InvalidOperationException>(() => Policy().ValidateDatabase(connection));

    [Fact]
    public void AcceptsOnlyApprovedEncryptedDatabase()
        => Assert.Contains("10.20.1.2", Policy().ValidateDatabase("Host=10.20.1.2;SSL Mode=VerifyFull"));

    [Fact]
    public void RejectsCloudPathsEvenIfAllowlisted()
    {
        var root = Path.Combine(Path.GetTempPath(), "OneDrive", "company-source");
        Assert.Throws<InvalidOperationException>(() => new ApprovedNetworkPolicy { LocalRoots = [root] }.ValidateLocalPath(root));
    }

    [Fact]
    public void RejectsSiblingPrefixWhenPolicyIsSpecified()
    {
        var root = Path.Combine(Path.GetTempPath(), "approved");
        Assert.Throws<InvalidOperationException>(() => new ApprovedNetworkPolicy { LocalRoots = [root] }.ValidateLocalPath(root + "-other"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BasicModeNeedsNoSeparateIpOrLocalRootList(string? path)
    {
        var policy = ApprovedNetworkPolicy.Load(path);
        policy.ValidateLocalPath(Path.GetTempPath());
        policy.ValidateLlm(new Uri("https://model.corp.invalid:8443/v1/chat/completions"));
        policy.ValidateAddresses([IPAddress.Parse("10.20.1.5")]);
        Assert.Throws<InvalidOperationException>(() => policy.ValidateAddresses([]));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateLocalPath(Path.Combine(Path.GetTempPath(), "OneDrive", "source")));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateLocalPath("relative/source"));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateDatabase("Host=10.20.1.2;SSL Mode=Disable"));
    }

    [Fact]
    public void BasicLlmTransportStillRequiresMatchingApplicationOrigin()
    {
        var options = new LlmOptions
        {
            Enabled = true, Endpoint = "http://127.0.0.1:9999/v1/chat/completions", AllowedOrigin = "http://127.0.0.1:9999"
        };
        using var client = new VllmClient(options);
        Assert.True(client.IsEnabled);
        options.AllowedOrigin = "http://127.0.0.1:9998";
        Assert.Throws<InvalidOperationException>(() => new VllmClient(options));
    }

    [Fact]
    public void ExplicitPolicyNeverFallsBackWhenMissingMalformedOrEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "network.json");
        try
        {
            Assert.Throws<FileNotFoundException>(() => ApprovedNetworkPolicy.Load(path));
            File.WriteAllText(path, "not-json");
            Assert.Throws<System.Text.Json.JsonException>(() => ApprovedNetworkPolicy.Load(path));
            File.WriteAllText(path, "{\"IsRestricted\":false}");
            Assert.Throws<System.Text.Json.JsonException>(() => ApprovedNetworkPolicy.Load(path));
            File.WriteAllText(path, "{}");
            var empty = ApprovedNetworkPolicy.Load(path);
            Assert.Throws<InvalidOperationException>(() => empty.ValidateLocalPath(root));
            Assert.Throws<InvalidOperationException>(() => empty.ValidateLlm(new Uri("http://127.0.0.1:9999")));
            Assert.Throws<InvalidOperationException>(() => empty.ValidateAddresses([IPAddress.Loopback]));
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { LocalRoots = new[] { root } }));
            var restricted = ApprovedNetworkPolicy.Load(path);
            restricted.ValidateLocalPath(Path.Combine(root, "data"));
            Assert.Throws<InvalidOperationException>(() => restricted.ValidateLocalPath(root + "-sibling"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
