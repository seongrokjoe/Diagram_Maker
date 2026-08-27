namespace DiagramMaker.Services;

public static class LocalRepositoryPath
{
    public static string NormalizeAndValidate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Git repository path is required.");
        }

        var candidate = value.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(candidate))
        {
            throw new ArgumentException("Enter an absolute local Git repository path.");
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Network paths are not supported in local-only mode.");
        }

        if (Path.GetFileName(fullPath).Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            {
                throw new DirectoryNotFoundException("The .git path does not exist.");
            }

            fullPath = Directory.GetParent(fullPath)?.FullName
                ?? throw new ArgumentException("The .git path has no repository parent directory.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("Repository directory does not exist.");
        }

        var dotGit = Path.Combine(fullPath, ".git");
        var isWorkTree = Directory.Exists(dotGit) || File.Exists(dotGit);
        var isBare = File.Exists(Path.Combine(fullPath, "HEAD")) && Directory.Exists(Path.Combine(fullPath, "objects"));
        if (!isWorkTree && !isBare)
        {
            throw new ArgumentException("The directory is not a Git repository or bare repository.");
        }

        return fullPath;
    }
}
