using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class SampleTestIsolationTests
{
    [Theory]
    [InlineData("POST", "/api/v1/repositories")]
    [InlineData("POST", "/api/v1/repositories/inspect")]
    [InlineData("POST", "/api/v1/analysis-plans")]
    [InlineData("POST", "/api/v1/analyses")]
    [InlineData("PUT", "/api/v1/analysis-plans/123/selection")]
    [InlineData("POST", "/api/v1/analysis-plans/123/generate")]
    [InlineData("POST", "/api/v1/natural-diagrams")]
    [InlineData("POST", "/api/v1/natural-diagrams/123/regenerate")]
    [InlineData("POST", "/api/v1/natural-diagrams/123/views/revise")]
    [InlineData("POST", "/api/v1/natural-diagrams/123/dsl-revisions")]
    [InlineData("POST", "/api/v1/llm/tests/thinking-contract")]
    public void OrdinaryWriteEndpointsCannotBypassSamplePolicy(string method, string path)
    {
        Assert.False(SampleTestEndpoints.IsAllowedRequest(method, path));
    }

    [Theory]
    [InlineData("GET", "/api/v1/analyses/123")]
    [InlineData("POST", "/api/v1/sample-tests/cpp-packets/generate")]
    [InlineData("POST", "/api/v1/analyses/123/groups/sample/views/flow/edits")]
    [InlineData("POST", "/api/v1/natural-diagrams/123/views/flow/edit-preview")]
    public void TestGenerationAndLocalEditingRemainAvailable(string method, string path)
    {
        Assert.True(SampleTestEndpoints.IsAllowedRequest(method, path));
    }

    [Theory]
    [InlineData(false, "http://127.0.0.1:5081")]
    [InlineData(true, "http://0.0.0.0:5081")]
    [InlineData(true, "http://localhost:5081")]
    [InlineData(true, "http://127.0.0.1:5081;http://*:5082")]
    public void ExternalBindingsAndProductionFailClosed(bool development, string urls)
    {
        Assert.Throws<InvalidOperationException>(() => new CodexTestOptions { Enabled = true }.Validate(development, urls));
    }

    [Fact]
    public void OnlyFixedRefinementsAndEnumeratedOptionsCanBecomePrompts()
    {
        Assert.Throws<ArgumentException>(() => CodexSampleCatalog.Refinement("send arbitrary company code"));
        var catalog = new DiagramPresetCatalog();
        var refinement = CodexSampleCatalog.Refinement("summarize");
        Assert.Throws<ArgumentException>(() => SampleTestEndpoints.CreateViews("git", new(null, Direction: "arbitrary text"), refinement, catalog));
        Assert.Throws<ArgumentException>(() => SampleTestEndpoints.CreateViews("git", new(null, DiagramTypes: ["state"]), refinement, catalog));
        Assert.Throws<ArgumentException>(() => SampleTestEndpoints.CreateViews("git", new(null, CallerDepth: 500), refinement, catalog));
        var views = SampleTestEndpoints.CreateViews("git", new(null), refinement, catalog);
        Assert.Equal(4, views.Length);
        Assert.All(views, view => Assert.Equal(refinement.Instruction, view.RefinementInstruction));
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<SampleGenerateRequest>("""{"planId":null,"prompt":"must not pass through"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task ModifiedSampleFilesAndCrossRepositorySourcesAreRejected()
    {
        var root = Directory.CreateTempSubdirectory("diagram-sample-isolation-").FullName;
        try
        {
            var runtime = Path.Combine(root, "artifacts", "codex-test");
            var repo = Path.Combine(runtime, "repositories", "fixture");
            Directory.CreateDirectory(repo);
            var before = "class Sample {}\n";
            var after = "class Sample { int value; }\n";
            var id = Guid.NewGuid();
            var baseSha = new string('a', 40); var targetSha = new string('b', 40);
            await File.WriteAllTextAsync(Path.Combine(repo, "Sample.cs"), after);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await File.WriteAllTextAsync(Path.Combine(root, "samples.json"), JsonSerializer.Serialize(new SampleAssets("1",
                [new("sample", "Synthetic", "git", "Sample.cs", before, after, null)]), json));
            await File.WriteAllTextAsync(Path.Combine(runtime, "samples-manifest.json"), JsonSerializer.Serialize(new SampleManifest("1",
                [new("sample", id, repo, baseSha, targetSha)]), json));
            var catalog = new CodexSampleCatalog(new CodexTestOptions { AssetsRoot = root, RuntimeRoot = runtime });
            var repository = new RepositoryDefinition(id, "Synthetic", repo, "main", [], DateTimeOffset.UtcNow);
            await catalog.VerifyRepositoryAsync(repository, default);
            var comparison = new GitComparison(baseSha, targetSha,
                [new("Sample.cs", null, ChangeKind.Modified, "before-blob", "after-blob", [], before, after)]);
            catalog.VerifyComparison(id, comparison);
            Assert.Throws<ArgumentException>(() => catalog.VerifyComparison(id, comparison with
                { ContextFiles = [new("Sample.cs", targetSha, "blob", "unapproved source")] }));
            await File.WriteAllTextAsync(Path.Combine(repo, "Sample.cs"), "unapproved source");
            await Assert.ThrowsAsync<ArgumentException>(() => catalog.VerifyRepositoryAsync(repository, default));
        }
        finally
        {
            // Exact test-owned random directory; there are no user files or links here.
            Directory.Delete(root, recursive: true);
        }
    }
}
