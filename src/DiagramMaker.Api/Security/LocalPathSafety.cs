namespace DiagramMaker.Security;

public static class LocalPathSafety
{
    public static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    public static void Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidOperationException("An absolute non-network local path is required.");
        var full = Path.GetFullPath(path);
        if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part =>
                part.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) || part.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Cloud synchronized paths are not permitted.");
        foreach (var key in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } cloud && IsWithin(cloud, full))
                throw new InvalidOperationException("Cloud synchronized paths are not permitted.");
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked paths and cloud placeholders are not permitted in approved local roots.");
        }
    }
}
