using System.Text.Json;
using System.Text.Json.Serialization;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

/// <summary>Offline, bounded declarative validation. No network or package code execution.</summary>
public static class WebContributionValidator
{
    public const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12 };

    public static WebContributionDocument Parse(ModuleManifest manifest, ReadOnlySpan<byte> bytes)
    {
        if (manifest.PackageFormatVersion != 2) throw new InvalidDataException("Web contributions require Package Format 2.");
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Web descriptor exceeds 64 KiB.");
        using var tree = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 12 });
        RejectDuplicates(tree.RootElement);
        var document = tree.Deserialize<WebContributionDocument>(Json) ?? throw new InvalidDataException("Empty Web descriptor.");
        if (document.SchemaGeneration != WebContributionSchema.Generation) throw new InvalidDataException("Unsupported Web schema generation.");
        if (document.Contributions is null || document.Contributions.Count is < 1 or > 32) throw new InvalidDataException("Web contribution count must be 1..32.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in document.Contributions)
        {
            if (page is null) throw new InvalidDataException("Null contribution.");
            try
            {
                PackageDependencyResolver.ValidateId(page.Id);
                if (!ids.Add(page.Id)) throw new InvalidDataException("Duplicate contribution ID.");
                if (page.Route != $"/extensions/{manifest.Id}/{page.Id}" || !routes.Add(page.Route)) throw new InvalidDataException("Route must belong to its package and contribution.");
                Text(page.Title, 128);
                // Declarations never grant permissions. Reuse Platform permission identifiers.
                if (page.Permission is not ("platform.observability.read" or "platform.jobs.read" or "platform.jobs.manage") &&
                    !(page.Permission?.StartsWith($"extensions.{manifest.Id}.", StringComparison.Ordinal) == true && manifest.Permissions.Contains(page.Permission, StringComparer.Ordinal)))
                    throw new InvalidDataException("Undeclared or unsupported permission.");
                if (page.Navigation is null || page.Navigation.Icon is not ("package" or "status" or "table") ||
                    page.Navigation.Section is not ("PrimaryNavigation" or "AdminNavigation") || Math.Abs((long)page.Navigation.Order) > 1000)
                    throw new InvalidDataException("Unknown navigation slot/icon/order.");
                if (page.Navigation.Section == "AdminNavigation" && !page.Permission.StartsWith("platform.", StringComparison.Ordinal))
                    throw new InvalidDataException("Admin navigation requires a Platform permission.");
                if (page.Navigation.Asset is not null)
                {
                    ValidateAssetPath(page.Navigation.Asset);
                    if (!manifest.Files.ContainsKey(page.Navigation.Asset)) throw new InvalidDataException("Asset is outside signed inventory.");
                }
                if (page.Components is null || page.Components.Count is < 1 or > 32) throw new InvalidDataException("Component count must be 1..32.");
                foreach (var component in page.Components)
                {
                    if (component is null) throw new InvalidDataException("Null component.");
                    Text(component.Text, 2048);
                    if (component.Type is not ("heading" or "text" or "status" or "table" or "button")) throw new InvalidDataException($"Unknown component type '{Safe(component.Type)}'.");
                    if (component.Type is "status" or "table" or "button")
                    {
                        if (component.Operation != (component.Type == "button" ? "platform.health.refresh" : "platform.health"))
                            throw new InvalidDataException("Unsupported API operation for component.");
                    }
                    else if (component.Operation is not null) throw new InvalidDataException("Text cannot declare an operation.");
                }
            }
            catch (InvalidDataException error) { throw new InvalidDataException($"Contribution {Safe(page.Id)}: {error.Message}", error); }
        }
        return document with { Contributions = Array.AsReadOnly(document.Contributions.Select(page => page with { Components = Array.AsReadOnly(page.Components.ToArray()) }).ToArray()) };
    }

    private static void Text(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(c => c < 32 && c is not '\n' and not '\t'))
            throw new InvalidDataException("Display text is missing or exceeds bounds.");
    }
    public static void ValidateAssetPath(string path)
    {
        if (!path.StartsWith("web/assets/", StringComparison.Ordinal) || !path.EndsWith(".png", StringComparison.Ordinal) || path.Length > 128)
            throw new InvalidDataException("Only package PNG assets are supported.");
        var name = path[11..^4];
        if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidDataException("Unsafe asset path.");
    }

    public static void ValidatePng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 1024 * 1024 || bytes.Length < 45 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Invalid or oversized PNG asset.");
        var offset = 8; var first = true;
        while (offset <= bytes.Length - 12)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > bytes.Length - offset - 12) throw new InvalidDataException("Invalid PNG chunk bounds.");
            var type = bytes.Slice(offset + 4, 4);
            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13) throw new InvalidDataException("Invalid PNG header.");
                var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8, 4));
                var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 12, 4));
                if (width is 0 or > 2048 || height is 0 or > 2048) throw new InvalidDataException("PNG dimensions exceed bounds.");
                first = false;
            }
            offset += checked((int)length + 12);
            if (type.SequenceEqual("IEND"u8))
            { if (length != 0 || offset != bytes.Length) throw new InvalidDataException("PNG has trailing content."); return; }
        }
        throw new InvalidDataException("PNG end chunk missing.");
    }
    private static string Safe(string? value) => value is null ? "missing" : new string(value.Take(80).Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate Web JSON member."); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
