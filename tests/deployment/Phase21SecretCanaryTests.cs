using Hispania.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hispania.Deployment.Tests;

[CollectionDefinition("phase21-console", DisableParallelization = true)]
public sealed class Phase21ConsoleTestsGroup;

[Collection("phase21-console")]
public sealed class Phase21SecretCanaryTests
{
    [Fact]
    public void PasswordTokenDatabaseKeyAndBootstrapCanariesNeverEnterProductionLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase21-log-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "security.jsonl");
        var secrets = new[] { "PHASE21_PASSWORD_CANARY", "PHASE21_BEARER_CANARY", "PHASE21_SESSION_CANARY", "PHASE21_POSTGRES_CANARY", "PHASE21_MTLS_PRIVATE_KEY_CANARY", "PHASE21_BOOTSTRAP_CANARY", "PHASE21_SIGNING_PRIVATE_KEY_CANARY" };
        var original = Console.Out; using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            using var provider = new ProductionLogProvider(path, secrets);
            var logger = provider.CreateLogger("security-test");
            logger.Log(LogLevel.Error, new EventId(21), string.Join(" ", secrets) + "\r\nFAKE LOG", new InvalidOperationException(string.Join(" ", secrets)), static (state, _) => state);
            var captured = output.ToString();
            foreach (var secret in secrets) Assert.DoesNotContain(secret, captured, StringComparison.Ordinal);
            Assert.Single(captured.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            if (OperatingSystem.IsWindows()) Assert.Equal(captured, File.ReadAllText(path));
        }
        finally { Console.SetOut(original); Directory.Delete(root, true); }
    }
}
