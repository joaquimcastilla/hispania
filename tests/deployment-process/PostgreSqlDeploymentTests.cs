using Hispania.Hosting;
using Npgsql;
using Xunit;

namespace Hispania.Deployment.ProcessTests;

[Trait("Category", "Phase18PostgreSql")]
public sealed class PostgreSqlDeploymentTests
{
    [Fact]
    public async Task LivePasswordRotationRejectsOldCredentialAndDisasterRestoreStartsWithReprovisionedSecret()
    {
        var connection = Environment.GetEnvironmentVariable("HISPANIA_PHASE18_POSTGRES_CONNECTION") ?? throw new InvalidOperationException("Mandatory password-auth PostgreSQL fixture missing.");
        var settings = new NpgsqlConnectionStringBuilder(connection) { Pooling = false };
        await using var admin = new NpgsqlConnection(settings.ConnectionString); await admin.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N"); var role = "h18_" + suffix; var source = "h18_source_" + suffix; var target = "h18_target_" + suffix;
        var passwordA = "PHASE18_SUPER_SECRET_CANARY_A_" + Guid.NewGuid().ToString("N"); var passwordB = "PHASE18_SUPER_SECRET_CANARY_B_" + Guid.NewGuid().ToString("N");
        async Task ExecuteAsync(string sql) { await using var command = admin.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        await ExecuteAsync($"CREATE ROLE \"{role}\" LOGIN PASSWORD '{passwordA}'");
        await ExecuteAsync($"CREATE DATABASE \"{source}\" OWNER \"{role}\""); await ExecuteAsync($"CREATE DATABASE \"{target}\" OWNER \"{role}\"");
        try
        {
            using var fixture = new ArtifactProcessTests.HostFixture();
            fixture.WriteConfiguration(new { persistence = "postgresql", postgreSql = new { host = settings.Host, port = settings.Port, database = source, username = role, passwordSecret = "env:MEDIA_PASSWORD" } });
            using (var server = fixture.Start(environment: new() { ["HISPANIA_SECRET_MEDIA_PASSWORD"] = passwordA })) { await fixture.ReadyAsync(server); await fixture.StopAsync(server); }
            var bundle = Path.Combine(fixture.Home, "backups", "postgres-disaster");
            var libpq = new Dictionary<string, string> { ["PGHOST"] = settings.Host!, ["PGPORT"] = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["PGDATABASE"] = source, ["PGUSER"] = role, ["PGPASSWORD"] = passwordA };
            Assert.Equal(0, (await fixture.CommandAsync(["--backup-create", Path.Combine(fixture.Home, "state"), bundle], libpq)).Code);
            Assert.Equal(0, (await fixture.CommandAsync(["--backup-verify", bundle])).Code);
            await ExecuteAsync($"ALTER ROLE \"{role}\" PASSWORD '{passwordB}'");
            var old = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Database = source, Username = role, Password = passwordA, Pooling = false };
            await using (var rejected = new NpgsqlConnection(old.ConnectionString))
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(() => rejected.OpenAsync()); Assert.Equal("28P01", failure.SqlState);
            }
            var failedHost = await fixture.CommandAsync(["--home", fixture.Home], new() { ["HISPANIA_SECRET_MEDIA_PASSWORD"] = passwordA });
            Assert.NotEqual(0, failedHost.Code);
            Assert.DoesNotContain(passwordA, failedHost.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(passwordB, failedHost.Output, StringComparison.Ordinal);
            using (var server = fixture.Start(environment: new() { ["HISPANIA_SECRET_MEDIA_PASSWORD"] = passwordB })) { await fixture.ReadyAsync(server); await fixture.StopAsync(server); }
            libpq["PGPASSWORD"] = passwordB;
            using var recovered = new ArtifactProcessTests.HostFixture();
            recovered.WriteConfiguration(new { persistence = "postgresql", postgreSql = new { host = settings.Host, port = settings.Port, database = target, username = role, passwordSecret = "env:MEDIA_PASSWORD" } });
            Directory.Delete(Path.Combine(recovered.Home, "state"), true); libpq["PGDATABASE"] = target;
            Assert.Equal(0, (await fixture.CommandAsync(["--backup-restore", bundle, Path.Combine(recovered.Home, "state")], libpq)).Code);
            var missing = await recovered.CommandAsync(["--home", recovered.Home, "--check"]); Assert.Equal(3, missing.Code);
            using var restored = recovered.Start(environment: new() { ["HISPANIA_SECRET_MEDIA_PASSWORD"] = passwordB });
            await recovered.ReadyAsync(restored); await recovered.StopAsync(restored);
            foreach (var file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories))
            {
                var data = File.ReadAllBytes(file);
                Assert.True(data.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(passwordA)) < 0);
                Assert.True(data.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(passwordB)) < 0);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync($"DROP DATABASE \"{source}\" WITH (FORCE)"); await ExecuteAsync($"DROP DATABASE \"{target}\" WITH (FORCE)"); await ExecuteAsync($"DROP ROLE \"{role}\"");
        }
    }
}
