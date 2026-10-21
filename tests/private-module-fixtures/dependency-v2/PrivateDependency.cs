namespace Hispania.PrivateFixture.Dependency;

public static class PrivateDependency
{
    public static string GetValue(string fixtureLabel) => fixtureLabel == "v2" ? "private-dependency-v2" : "private-dependency-v2-incompatible-api";
}
