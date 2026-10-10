using System.IO.Compression;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;

if (args.Length != 1) { Console.Error.WriteLine("Usage: package-preflight <archive.hispania>"); return 2; }
try
{
    var candidate = await PackageArchiveInspector.InspectAsync(args[0]);
    using var zip = ZipFile.OpenRead(args[0]);
    var entry = zip.GetEntry(WebContributionSchema.Path);
    if (entry is not null)
    {
        if (entry.Length > WebContributionValidator.MaximumBytes) throw new InvalidDataException("Web descriptor exceeds 64 KiB.");
        using var input = entry.Open(); using var content = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer)) > 0)
        { if (content.Length + count > WebContributionValidator.MaximumBytes) throw new InvalidDataException("Web descriptor exceeds 64 KiB."); content.Write(buffer, 0, count); }
        var document = WebContributionValidator.Parse(candidate.Manifest, content.ToArray());
        long total = 0;
        foreach (var asset in document.Contributions.Select(page => page.Navigation.Asset).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var assetEntry = zip.GetEntry(asset) ?? throw new InvalidDataException("Signed Web asset is missing.");
            if (assetEntry.Length > 1024 * 1024) throw new InvalidDataException("Web asset exceeds 1 MiB.");
            using var source = assetEntry.Open(); using var bytes = new MemoryStream();
            while ((count = source.Read(buffer)) > 0)
            { total += count; if (bytes.Length + count > 1024 * 1024 || total > 4 * 1024 * 1024) throw new InvalidDataException("Web asset budget exceeded."); bytes.Write(buffer, 0, count); }
            WebContributionValidator.ValidatePng(bytes.ToArray());
        }
        Console.WriteLine($"Web schema {document.SchemaGeneration}: {document.Contributions.Count} validated contributions.");
    }
    Console.WriteLine("Archive integrity and declarative schema validated offline; publisher trust, host authorization and activation require target-host preflight.");
    return 0;
}
catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or ArgumentException or IOException)
{ Console.Error.WriteLine("Package preflight rejected: " + error.Message); return 1; }
