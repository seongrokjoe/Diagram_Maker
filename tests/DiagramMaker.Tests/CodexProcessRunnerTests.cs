using System.Diagnostics;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class CodexProcessRunnerTests
{
    [Fact]
    public async Task WindowsChildReceivesUtf8StdinWithoutPrivateEnvironment()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("diagram-process-test-").FullName;
        const string marker = "DIAGRAM_MAKER_PROCESS_TEST_PRIVATE";
        var old = Environment.GetEnvironmentVariable(marker);
        Environment.SetEnvironmentVariable(marker, "must-not-be-inherited");
        try
        {
            var runner = new CodexProcessRunner();
            var result = await runner.RunAsync(PowerShell(), ["-NoProfile", "-NonInteractive", "-Command",
                "[Console]::InputEncoding = [Text.Encoding]::UTF8; [Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
                "[Console]::Write([Console]::In.ReadToEnd()); [Console]::Write([Environment]::GetEnvironmentVariable('DIAGRAM_MAKER_PROCESS_TEST_PRIVATE'))"],
                root, "합성 데이터 \"인용\" $()", default);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("합성 데이터 \"인용\" $()", result.Output);
        }
        finally { Environment.SetEnvironmentVariable(marker, old); Directory.Delete(root); }
    }

    [Fact]
    public async Task CancellationEndsOwnedProcessPromptly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("diagram-process-cancel-").FullName;
        try
        {
            var watch = Stopwatch.StartNew();
            using var cancel = new CancellationTokenSource(500);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodexProcessRunner().RunAsync(PowerShell(),
                ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], root, null, cancel.Token));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8));
        }
        finally { Directory.Delete(root); }
    }

    private static string PowerShell() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
}
