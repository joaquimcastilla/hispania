using Hispania.ModuleSystem.Contracts;
using Xunit;

namespace Hispania.Runtime.Tests;

public sealed class PackageLifecycleResolverTests
{
    private static PackageCandidate Candidate(string id, string version = "1.0.0", params PackageDependency[] dependencies) => new(new ModuleManifest
    {
        Id = id, Name = id, Version = "1.0.0", PackageVersion = version, PackageFormatVersion = 2, PackageDependencies = dependencies,
        Implements = [], MinimumHostVersion = "1.0.0", Runtime = "dotnet-inprocess-trusted"
    }, id + version, "local");

    private static PackageOperationPlan Resolve(PackageCandidate[] candidates, string root = "test.a", string range = "[1.0.0,)") =>
        PackageDependencyResolver.Plan([], candidates, new Dictionary<string, string> { [root] = range });

    [Fact] public void NoDependenciesSelectsOnlyExplicitRoot() => Assert.Equal(["test.a"], Resolve([Candidate("test.a"), Candidate("test.b")]).ActivationOrder);
    [Fact] public void RequiredDependencyStartsFirst() => Assert.Equal(["test.b", "test.a"], Resolve([Candidate("test.a", "1.0.0", new PackageDependency("test.b", "[1.0.0]")), Candidate("test.b")]).ActivationOrder);
    [Fact] public void TransitiveDependenciesStartFirst() => Assert.Equal(["test.c", "test.b", "test.a"], Resolve([Candidate("test.a", "1.0.0", new PackageDependency("test.b", "[1.0.0]")), Candidate("test.b", "1.0.0", new PackageDependency("test.c", "[1.0.0]")), Candidate("test.c")]).ActivationOrder);
    [Fact] public void MissingDependencyIsExplicit() => Assert.Equal("MissingDependency", Assert.Single(Resolve([Candidate("test.a", "1.0.0", new PackageDependency("test.b", "[1.0.0]"))]).Findings).Code);
    [Fact] public void ExactRangeRejectsDifferentVersion() => Assert.False(Resolve([Candidate("test.a", "1.1.0")], range: "[1.0.0]").Resolved);
    [Fact] public void BoundsChooseHighestCompatibleStable() => Assert.Equal("1.4.0", Assert.Single(Resolve([Candidate("test.a", "1.3.0"), Candidate("test.a", "1.4.0"), Candidate("test.a", "2.0.0")], range: "[1.0.0,2.0.0)").Selected).Version);
    [Fact] public void UpperBoundExcludesBoundary() => Assert.False(Resolve([Candidate("test.a", "2.0.0")], range: "(,2.0.0)").Resolved);
    [Fact] public void ConflictingTransitiveRangesFail() => Assert.False(Resolve([Candidate("test.a", "1.0.0", new PackageDependency("test.b", "[1.0.0]"), new PackageDependency("test.c", "[1.0.0]")), Candidate("test.b", "1.0.0", new PackageDependency("test.d", "[1.0.0]")), Candidate("test.c", "1.0.0", new PackageDependency("test.d", "[2.0.0]")), Candidate("test.d"), Candidate("test.d", "2.0.0")]).Resolved);
    [Fact] public void CycleIsRejected() => Assert.False(Resolve([Candidate("test.a", "1.0.0", new PackageDependency("test.b", "[1.0.0]")), Candidate("test.b", "1.0.0", new PackageDependency("test.a", "[1.0.0]"))]).Resolved);
    [Fact] public void SelfDependencyRejectedAtManifest() => Assert.Throws<InvalidDataException>(() => PackageDependencyResolver.ValidateManifest(Candidate("test.a", "1.0.0", new PackageDependency("test.a", "[1.0.0]")).Manifest));
    [Fact] public void DuplicateDependencyRejectedAtManifest() => Assert.Throws<InvalidDataException>(() => PackageDependencyResolver.ValidateManifest(Candidate("test.a", "1.0.0", new PackageDependency("test.b", "[1.0.0]"), new PackageDependency("test.b", "[1.0.0,)")).Manifest));
    [Fact] public void CandidateEnumerationOrderIsIrrelevant()
    {
        var candidates = new[] { Candidate("test.a", "1.0.0", new PackageDependency("test.b", "[1.0.0,2.0.0)")), Candidate("test.b", "1.2.0"), Candidate("test.b", "1.9.0") };
        var expected = Resolve(candidates);
        Assert.Equal(["test.b", "test.a"], expected.ActivationOrder);
        Assert.Equal(["1.0.0", "1.9.0"], expected.Selected.Select(item => item.Version));
        for (var repetition = 0; repetition < 20; repetition++)
        {
            var actual = Resolve(repetition % 2 == 0 ? candidates : candidates.Reverse().ToArray());
            Assert.Equal(expected.Selected.Select(item => item.ArtifactHash), actual.Selected.Select(item => item.ArtifactHash));
            Assert.Equal(expected.ActivationOrder, actual.ActivationOrder);
            Assert.Equal(expected.DeactivationOrder, actual.DeactivationOrder);
            Assert.Equal(expected.Findings, actual.Findings);
        }
    }
    [Fact] public void SameVersionDifferentBytesIsAmbiguous() => Assert.Equal("AmbiguousArtifact", Assert.Single(Resolve([Candidate("test.a"), Candidate("test.a") with { ArtifactHash = "different" }]).Findings).Code);
    [Fact] public void StableRangeNeverSelectsPrerelease() => Assert.Equal("1.0.0", Assert.Single(Resolve([Candidate("test.a"), Candidate("test.a", "2.0.0-rc.1")]).Selected).Version);
    [Fact] public void ExplicitPrereleaseRangeIsAllowed() => Assert.Equal("2.0.0-rc.1", Assert.Single(Resolve([Candidate("test.a", "2.0.0-rc.1")], range: "[2.0.0-rc.1]").Selected).Version);
    [Fact] public void DependencyCountIsBounded() => Assert.False(Resolve([Candidate("test.a", "1.0.0", Enumerable.Range(0, 33).Select(index => new PackageDependency("test.b" + index, "[1.0.0]")).ToArray())]).Resolved);
    [Fact] public void InstalledSetDoesNotUpgradeWithoutExplicitTarget()
    {
        var old = Candidate("test.a");
        Assert.Equal("1.0.0", Assert.Single(PackageDependencyResolver.Plan([old], [Candidate("test.a", "2.0.0")]).Selected).Version);
    }
    [Fact] public void DisabledInstalledDependentStillBlocksRemoval()
    {
        var set = new[] { Candidate("test.a", "1.0.0", new PackageDependency("test.b", "[1.0.0]")), Candidate("test.b") };
        Assert.False(PackageDependencyResolver.Plan(set, [], remove: new HashSet<string> { "test.b" }).Resolved);
    }
    [Theory]
    [InlineData("Test.a")][InlineData("../a")][InlineData("tést.a")][InlineData("con")][InlineData("test.a.")][InlineData("test/a")]
    public void IdsArePortableAndCanonical(string value) => Assert.Throws<InvalidDataException>(() => PackageDependencyResolver.ValidateId(value));
    [Theory]
    [InlineData("1.0")][InlineData("01.0.0")][InlineData("1.0.0.0")][InlineData("1.0.0-01")][InlineData("v1.0.0")]
    public void VersionsUseStrictSemVer(string value) => Assert.Throws<InvalidDataException>(() => PackageDependencyResolver.ParseVersion(value));
    [Theory]
    [InlineData("*")][InlineData("^1.0.0")][InlineData(">=1.0.0")][InlineData("[2.0.0,1.0.0)")][InlineData("[1.0,2.0)")]
    public void MalformedRangesFailSafely(string value) => Assert.Throws<InvalidDataException>(() => PackageDependencyResolver.ParseRange(value));
    [Fact] public void FormatOneCannotCarryNewDependencies() => Assert.Throws<InvalidDataException>(() => PackageDependencyResolver.ValidateManifest(Candidate("test.a").Manifest with { PackageFormatVersion = 1 }));
}
