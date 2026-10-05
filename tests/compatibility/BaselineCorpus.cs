using System.Security.Cryptography;
using System.Text.Json;

namespace Hispania.Compatibility.Tests;

internal static class BaselineCorpus
{
    public const string SourceSha = "78f291fdacf9442c6e2d8e23094dc0e309828693";
    public static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Hispania.sln"))) return dir.FullName;
            throw new DirectoryNotFoundException("Repository root missing");
        }
    }
    public static string PathFor(string path) => Path.Combine(Root, "compatibility", path);
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(PathFor(path))) ?? throw new InvalidDataException(path);
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static IReadOnlyList<string> Validate(string manifestJson, string lockJson, Func<string, byte[]?> read)
    {
        using var manifest = JsonDocument.Parse(manifestJson);
        using var frozen = JsonDocument.Parse(lockJson);
        var errors = new List<string>();
        if (manifest.RootElement.GetProperty("baseline").GetString() != "0.7" || manifest.RootElement.GetProperty("sourceSha").GetString() != SourceSha)
            errors.Add("BREAKING: baseline identity changed");
        var rows = manifest.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
        if (rows.Select(row => row.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count() != rows.Length) errors.Add("Duplicate artifact ID");
        if (rows.Select(row => row.GetProperty("path").GetString()).Distinct(StringComparer.Ordinal).Count() != rows.Length) errors.Add("Duplicate artifact path");
        foreach (var pinned in frozen.RootElement.EnumerateArray())
        {
            var id = pinned.GetProperty("id").GetString();
            var matches = rows.Where(row => row.GetProperty("id").GetString() == id).ToArray();
            if (matches.Length != 1 || !JsonElement.DeepEquals(matches[0], pinned)) errors.Add("BREAKING: frozen manifest entry removed/changed: " + id);
        }
        foreach (var row in rows)
        {
            foreach (var key in new[] { "id", "axis", "path", "sha256", "owner", "origin", "version", "expectation", "regenerationPolicy" })
                if (string.IsNullOrWhiteSpace(row.GetProperty(key).GetString())) errors.Add("Missing " + key);
            var path = row.GetProperty("path").GetString()!;
            if (path.Contains('\\') || path.StartsWith('/') || path.Split('/').Any(part => part is ".." or ".")) { errors.Add("Unsafe fixture path: " + path); continue; }
            var bytes = read(path);
            if (bytes is null) errors.Add("BREAKING: missing fixture " + path);
            else if (Hash(bytes) != row.GetProperty("sha256").GetString()) errors.Add("BREAKING: fixture hash changed " + path);
        }
        return errors;
    }
}
