using System.Text.Json;
using Hispania.Web.Tests;

if (args.Length != 1) return 2;
using var fixture = new WebPackageFixture(Path.GetFullPath(args[0]));
fixture.Archive(document: Document("Version One"));
fixture.Archive("1.1.0", Document("Version Two"), unhealthy: true);
fixture.Archive("2.0.0", Document("Version Two"));
fixture.Archive("3.0.0", Document("Broken"), unhealthy: true);
File.WriteAllText(Path.Combine(fixture.Root, "keys.json"), JsonSerializer.Serialize(fixture.Keys));
return 0;

static Hispania.ModuleSystem.Contracts.WebContributionDocument Document(string title)
{
    var page = WebPackageFixture.Document(title).Contributions[0];
    return new(1, [page with { Navigation = page.Navigation with { Asset = "web/assets/icon.png" } }]);
}
