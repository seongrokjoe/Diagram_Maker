using System.Diagnostics;

namespace DiagramMaker.Security;

public static class WorkerEnvironment
{
    public static void Apply(ProcessStartInfo info)
    {
        var allowed = new[] { "SystemRoot", "WINDIR", "COMSPEC", "PATH", "PATHEXT", "TEMP", "TMP", "LANG", "LC_ALL", "OneDrive", "OneDriveConsumer", "OneDriveCommercial" };
        info.Environment.Clear();
        foreach (var key in allowed)
            if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        info.Environment["GIT_ALLOW_PROTOCOL"] = "";
        info.Environment["GIT_NO_LAZY_FETCH"] = "1";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
    }
}
