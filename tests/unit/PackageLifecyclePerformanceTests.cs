using System.Diagnostics;
using Hispania.ModuleSystem.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace Hispania.Runtime.Tests;

public sealed class PackageLifecyclePerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void ModestGraphResolutionIsBoundedAndMeasured()
    {
        PackageCandidate Candidate(string id, PackageDependency[] dependencies) => new(new ModuleManifest
        {
            Id = id, Name = id, Version = "1.0.0", PackageVersion = "1.0.0", PackageFormatVersion = 2,
            PackageDependencies = dependencies, Implements = [], MinimumHostVersion = "1.0.0", Runtime = "dotnet-inprocess-trusted"
        }, id, "TEST ONLY local candidate");
        var packages = new List<PackageCandidate>();
        for (var branch = 0; branch < 16; branch++)
        {
            var dependencies = new List<PackageDependency>();
            for (var leaf = 0; leaf < 4; leaf++)
            {
                var id = $"test.leaf.{branch}.{leaf}";
                packages.Add(Candidate(id, [])); dependencies.Add(new(id, "[1.0.0]"));
            }
            packages.Add(Candidate($"test.branch.{branch}", dependencies.ToArray()));
        }
        packages.Add(Candidate("test.root", Enumerable.Range(0, 16).Select(branch => new PackageDependency($"test.branch.{branch}", "[1.0.0]")).ToArray()));
        var timer = Stopwatch.StartNew();
        var plan = PackageDependencyResolver.Plan([], packages, new Dictionary<string, string> { ["test.root"] = "[1.0.0]" });
        timer.Stop();
        Assert.True(plan.Resolved); Assert.Equal(81, plan.Selected.Count);
        Assert.Equal("test.root", plan.ActivationOrder[^1]);
        output.WriteLine($"Resolved 81 packages / 80 edges in {timer.Elapsed.TotalMilliseconds:F3} ms; limits remain 128 packages / depth 32 / 8192 search steps.");
    }
}
