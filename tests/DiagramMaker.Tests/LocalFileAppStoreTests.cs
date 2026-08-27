using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;

namespace DiagramMaker.Tests;

public sealed class LocalFileAppStoreTests
{
    [Fact]
    public async Task RepositoryRegistration_SurvivesStoreRestart()
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, $"repository-registry-{Guid.NewGuid():N}.json");
        var repository = new RepositoryDefinition(
            Guid.NewGuid(), "Test repository", @"C:\Work\Repository", "main", ["Reviewer"], DateTimeOffset.UtcNow);

        await using (var first = new LocalFileAppStore(filePath))
        {
            await first.InitializeAsync(CancellationToken.None);
            await first.SaveRepositoryAsync(repository, CancellationToken.None);
        }

        await using var second = new LocalFileAppStore(filePath);
        await second.InitializeAsync(CancellationToken.None);
        var restored = await second.GetRepositoryAsync(repository.Id, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(repository.LocalPath, restored.LocalPath);
        Assert.Equal(repository.DefaultBranch, restored.DefaultBranch);
    }

    [Fact]
    public void LocalRepositoryPath_AcceptsRepositoryRootAndDotGitDirectory()
    {
        var repositoryPath = Path.Combine(AppContext.BaseDirectory, $"local-repository-{Guid.NewGuid():N}");
        var dotGitPath = Path.Combine(repositoryPath, ".git");
        Directory.CreateDirectory(dotGitPath);

        Assert.Equal(repositoryPath, LocalRepositoryPath.NormalizeAndValidate(repositoryPath));
        Assert.Equal(repositoryPath, LocalRepositoryPath.NormalizeAndValidate(dotGitPath));
    }

    [Fact]
    public void LocalRepositoryPath_RejectsRelativePath()
    {
        var exception = Assert.Throws<ArgumentException>(() => LocalRepositoryPath.NormalizeAndValidate("relative-repository"));
        Assert.Contains("absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
