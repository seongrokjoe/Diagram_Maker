using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Storage;

namespace DiagramMaker.Services;

public sealed record SampleScenario(string Id, string Title, string Kind, string? FileName, string? Before, string? After, string? Prompt);
public sealed record SampleAssets(string Version, IReadOnlyList<SampleScenario> Scenarios);
public sealed record SampleRepository(string ScenarioId, Guid RepositoryId, string LocalPath, string BaseSha, string TargetSha);
public sealed record SampleManifest(string Version, IReadOnlyList<SampleRepository> Repositories);
public sealed record SampleRefinement(string Id, string Label, string Instruction);

public sealed class CodexSampleCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public SampleAssets Assets { get; }
    public SampleManifest Manifest { get; }
    public static readonly SampleRefinement[] Refinements =
    [
        new("default", "기본 생성", ""),
        new("summarize", "동작을 짧게 요약", "각 처리 단계를 코드 대신 짧은 한국어 동작 의미로 요약하세요."),
        new("exceptions", "예외 경로 강조", "실패, 검증 거절, 조기 반환과 default 분기를 코드 근거 안에서 명확히 구분하세요."),
        new("connected", "변경에 연결된 요소 중심", "변경점과 실제로 연결되는 호출과 관계를 중심으로 표시하고 무관한 요소는 제외하세요."),
        new("detail", "상세 수준 높이기", "주요 동작과 각 조건 분기를 별도로 설명하고, 클래스 접근 제어와 연결 방향을 명확히 표시하세요.")
    ];

    public CodexSampleCatalog(CodexTestOptions options)
    {
        Assets = JsonSerializer.Deserialize<SampleAssets>(File.ReadAllText(Path.Combine(options.AssetsRoot, "samples.json")), Json)
            ?? throw new InvalidOperationException("Missing sample assets.");
        Manifest = JsonSerializer.Deserialize<SampleManifest>(File.ReadAllText(Path.Combine(options.RuntimeRoot, "samples-manifest.json")), Json)
            ?? throw new InvalidOperationException("Run the Codex test launcher to prepare samples.");
        var root = Path.GetFullPath(Path.Combine(options.RuntimeRoot, "repositories")) + Path.DirectorySeparatorChar;
        if (Assets.Version != Manifest.Version || Manifest.Repositories.Any(repository =>
                !Path.GetFullPath(repository.LocalPath).StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !Assets.Scenarios.Any(sample => sample.Id == repository.ScenarioId && sample.Kind == "git") ||
                !ValidSha(repository.BaseSha) || !ValidSha(repository.TargetSha)))
            throw new InvalidOperationException("Invalid synthetic sample manifest.");
    }

    private static bool ValidSha(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
    public SampleScenario Scenario(string id) => Assets.Scenarios.SingleOrDefault(sample => sample.Id == id)
        ?? throw new ArgumentException("등록된 샘플 ID만 사용할 수 있습니다.");
    public SampleRepository Repository(Guid id) => Manifest.Repositories.SingleOrDefault(sample => sample.RepositoryId == id)
        ?? throw new ArgumentException("샘플 외 저장소는 사용할 수 없습니다.");
    public static SampleRefinement Refinement(string id) => Refinements.SingleOrDefault(sample => sample.Id == id)
        ?? throw new ArgumentException("준비된 수정 요청만 사용할 수 있습니다.");

    public async Task InitializeAsync(IAppStore store, CancellationToken token)
    {
        var registered = await store.ListRepositoriesAsync(token);
        if (registered.Any(repository => !Manifest.Repositories.Any(sample => sample.RepositoryId == repository.Id)))
            throw new InvalidOperationException("The test store contains a non-sample repository. Use the isolated test launcher.");
        foreach (var sample in Manifest.Repositories)
        {
            var scenario = Scenario(sample.ScenarioId);
            var repository = new RepositoryDefinition(sample.RepositoryId, scenario.Title, sample.LocalPath, "main", ["Reviewer"],
                DateTimeOffset.UtcNow, new RepositoryAnalysisRules(0, []));
            await VerifyRepositoryAsync(repository, token);
            await store.SaveRepositoryAsync(repository, token);
        }
    }

    public async Task VerifyRepositoryAsync(RepositoryDefinition repository, CancellationToken token)
    {
        var entry = Repository(repository.Id);
        if (!Path.GetFullPath(repository.LocalPath).Equals(Path.GetFullPath(entry.LocalPath), StringComparison.OrdinalIgnoreCase) ||
            repository.AnalysisRules?.IndirectCalls.Count > 0)
            throw new ArgumentException("샘플 저장소 정의가 변경되었습니다.");
        var directory = new DirectoryInfo(entry.LocalPath);
        for (var ancestor = directory; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.LinkTarget is not null) throw new ArgumentException("연결된 경로는 샘플로 사용할 수 없습니다.");
        // Reject altered working files, git links and object alternates before the worker reads anything.
        var pending = new Stack<DirectoryInfo>();
        pending.Push(directory);
        var count = 0;
        while (pending.TryPop(out var current))
            foreach (var item in current.EnumerateFileSystemInfos())
            {
                // OneDrive cloud placeholders are reparse points but not symlinks/junctions.
                if (++count > 2000 || item.LinkTarget is not null)
                    throw new ArgumentException("샘플 파일 구성이 변경되었습니다.");
                if (item is DirectoryInfo child) pending.Push(child);
                else if (item.Name is "alternates" or "http-alternates") throw new ArgumentException("외부 Git 객체는 사용할 수 없습니다.");
            }
        var scenario = Scenario(entry.ScenarioId);
        var files = directory.GetFiles();
        if (files.Length != 1 || files[0].Name != scenario.FileName || files[0].Length > 100_000 ||
            await File.ReadAllTextAsync(files[0].FullName, token) != scenario.After ||
            directory.GetDirectories().Any(child => child.Name != ".git"))
            throw new ArgumentException("샘플 코드가 변경되었습니다. 테스트 앱을 다시 시작하여 새 샘플을 준비하세요.");
    }

    public void VerifyComparison(Guid id, GitComparison comparison)
    {
        var repository = Repository(id);
        var scenario = Scenario(repository.ScenarioId);
        if (comparison.BaseSha != repository.BaseSha || comparison.TargetSha != repository.TargetSha ||
            comparison.Files.Count != 1 || comparison.Files[0].Path != scenario.FileName ||
            comparison.Files[0].BeforeContent != scenario.Before || comparison.Files[0].AfterContent != scenario.After ||
            comparison.ContextFiles?.Any(file => file.Path != scenario.FileName ||
                file.RevisionSha != repository.BaseSha && file.RevisionSha != repository.TargetSha ||
                file.Content != (file.RevisionSha == repository.BaseSha ? scenario.Before : scenario.After)) == true)
            throw new ArgumentException("등록된 샘플 커밋과 코드만 분석할 수 있습니다.");
    }
}
