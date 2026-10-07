using System.Text.Json;
using Hispania.Web.Tests;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;

if (args.Length != 1) return 2;
using var fixture = new WebPackageFixture(Path.GetFullPath(args[0]));
fixture.Archive(document: Document("Version One"));
fixture.Archive("1.1.0", Document("Version Two"), unhealthy: true);
fixture.Archive("2.0.0", Document("Version Two"), assetBytes: Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
fixture.Archive("3.0.0", Document("Broken"), unhealthy: true);
fixture.Archive("10.0.0", Document("<script>window.__xss=1</script>"));
fixture.Archive("11.0.0", Document("<img src=x onerror=\"window.__xss=1\">"));
var page = Document("Matrix").Contributions[0];
var matrix = new Dictionary<string, bool>();
var negativeVersion = 4;
foreach (var scenario in new[] { "malformed", "future", "reserved", "traversal", "url", "duplicate" })
{
    var document = scenario switch
    {
        "future" => new WebContributionDocument(2, [page]),
        "reserved" => new(1, [page with { Route = "/admin" }]),
        "traversal" => new(1, [page with { Navigation = page.Navigation with { Asset = "web/assets/../bad.png" } }]),
        "url" => new(1, [page with { Components = [new("button", "bad", "javascript:alert(1)")] }]),
        "duplicate" => new(1, [page, page]),
        _ => new WebContributionDocument(1, [page])
    };
    var path = fixture.Archive($"{negativeVersion++}.0.0", document, descriptorBytes: scenario == "malformed" ? "{"u8.ToArray() : null);
    try { await PackageArchiveInspector.InspectAsync(path); throw new InvalidOperationException("Runtime accepted negative fixture: " + scenario); }
    catch (Exception error) when (error is InvalidDataException or JsonException) { matrix[Path.GetFileName(path)] = false; }
}
matrix["1.0.0.hispania"] = true;
File.WriteAllText(Path.Combine(fixture.Root, "preflight-matrix.json"), JsonSerializer.Serialize(matrix));
File.WriteAllText(Path.Combine(fixture.Root, "keys.json"), JsonSerializer.Serialize(fixture.Keys));
return 0;

static Hispania.ModuleSystem.Contracts.WebContributionDocument Document(string title)
{
    var page = WebPackageFixture.Document(title).Contributions[0];
    return new(1, [page with { Navigation = page.Navigation with { Asset = "web/assets/icon.png" } }]);
}
