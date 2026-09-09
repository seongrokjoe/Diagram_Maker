using System.Text.Json;
using NuGet.Packaging;

// NuGet lock contentHash differs from a raw archive hash after repository
// signing. Use the SDK implementation instead of trusting cache metadata.
var hashes = new Dictionary<string, string>();
foreach (var file in args)
{
    using var reader = new PackageArchiveReader(file);
    hashes.Add(file, reader.GetContentHash(CancellationToken.None, null));
}
Console.WriteLine(JsonSerializer.Serialize(hashes));
