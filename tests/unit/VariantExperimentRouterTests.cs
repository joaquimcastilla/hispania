using Hispania.Runtime;
using Xunit;

namespace Hispania.Runtime.Tests;

public sealed class VariantExperimentRouterTests
{
    private const string Capability = "hispania.playback.planner";
    private static readonly Version ContractVersion = new(1, 0);

    [Fact]
    public void AssignmentPersistsAcrossPercentageChangesAndFallbackUsesOnlyHealthyVariant()
    {
        var store = new InMemoryVariantAssignmentStore();
        var router = new VariantExperimentRouter<string>(store, "test-pseudonym-salt");
        var first = new VariantIdentity("planner-a", Capability, ContractVersion, 1);
        var second = new VariantIdentity("planner-b", Capability, ContractVersion, 1);
        var implementations = new Dictionary<VariantIdentity, string> { [first] = "A", [second] = "B" };
        var initialWeights = new[] { new VariantWeight(first, 5_000), new VariantWeight(second, 5_000) };
        router.Configure("playback-test", Capability, ContractVersion, VariantRoutingMode.TrafficSplit, implementations, initialWeights);

        var selected = router.Resolve("session-42", out var originalVariant);
        Assert.True(selected is "A" or "B");

        router.Configure("playback-test", Capability, ContractVersion, VariantRoutingMode.TrafficSplit, implementations,
            new[] { new VariantWeight(first, 1_000), new VariantWeight(second, 9_000) }, fallbackPolicy: VariantFallbackPolicy.RedistributeToHealthy);
        Assert.Equal(selected, router.Resolve("session-42", out var retainedVariant));
        Assert.Equal(originalVariant, retainedVariant);

        router.SetHealth(originalVariant, false);
        var fallback = router.Resolve("session-42", out var fallbackVariant);
        Assert.NotEqual(originalVariant, fallbackVariant);
        Assert.Equal(implementations[fallbackVariant], fallback);
    }

    [Fact]
    public void ConfigurationRejectsMismatchedContractsAndInvalidPercentages()
    {
        var router = new VariantExperimentRouter<string>(pseudonymSalt: "salt");
        var first = new VariantIdentity("planner-a", Capability, ContractVersion, 1);
        var wrongContract = new VariantIdentity("planner-b", "hispania.other", ContractVersion, 1);
        Assert.Throws<ArgumentException>(() => router.Configure("e", Capability, ContractVersion, VariantRoutingMode.Manual,
            new Dictionary<VariantIdentity, string> { [wrongContract] = "B" }, [], wrongContract));

        var second = new VariantIdentity("planner-b", Capability, ContractVersion, 1);
        var modules = new Dictionary<VariantIdentity, string> { [first] = "A", [second] = "B" };
        Assert.Throws<ArgumentException>(() => router.Configure("e", Capability, ContractVersion, VariantRoutingMode.TrafficSplit,
            modules, new[] { new VariantWeight(first, 5_000), new VariantWeight(second, 4_999) }));
    }
}
