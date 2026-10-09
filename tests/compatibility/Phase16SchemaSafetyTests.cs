using System.Diagnostics;
using System.Text.Json;
using Hispania.Persistence.Schema;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Hispania.Compatibility.Tests;
public sealed partial class Phase16HistoricalDataUpgradeTests
{
    [Theory]
    [InlineData("identity-authorization.sqlite")][InlineData("events.sqlite")][InlineData("jobs.sqlite")]
    [InlineData("assets.sqlite")][InlineData("audit.sqlite")][InlineData("inventory.sqlite")]
    [InlineData("coordinator.sqlite")][InlineData("media-initial.sqlite")][InlineData("media-library.sqlite")]
    public async Task FutureSchemaIsRejectedWithoutChangingDatabaseBytes(string file)
    {
        using var copy=new HistoricalDataCopy(file);var path=copy.PathFor(file);await OpenOwnerAsync(file,path);
        if(file.StartsWith("media-",StringComparison.Ordinal))HistoricalDataCorpus.Execute(path,"INSERT INTO __EFMigrationsHistory VALUES('20990101000100_future','10.0.12')");
        else
        {
            var futureVersion = file is "jobs.sqlite" or "assets.sqlite" ? 3 : 2;
            var scope = file is "jobs.sqlite" or "assets.sqlite" ? " WHERE version=2"
                : file == "identity-authorization.sqlite" ? " WHERE owner='Platform.Authorization'" : "";
            HistoricalDataCorpus.Execute(path,$"UPDATE hispania_schema_history SET version={futureVersion},migration_id='000{futureVersion}_future'"+scope);
        }
        SqliteConnection.ClearAllPools();var hash=BaselineCorpus.Hash(File.ReadAllBytes(path));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OpenOwnerAsync(file,path));SqliteConnection.ClearAllPools();
        Assert.Equal(hash,BaselineCorpus.Hash(File.ReadAllBytes(path)));
        // Also exercise a separate current executable, not merely the test's in-process caller.
        using var child=StartChild("--phase16-open",file,path);await child.WaitForExitAsync();Assert.Equal(1,child.ExitCode);
        SqliteConnection.ClearAllPools();Assert.Equal(hash,BaselineCorpus.Hash(File.ReadAllBytes(path)));
    }
    [Theory]
    [InlineData("unknown")][InlineData("gap")][InlineData("duplicate")][InlineData("malformed")]
    public async Task InvalidMigrationLedgerFailsSafely(string kind)
    {
        using var copy=new HistoricalDataCopy("inventory.sqlite");var path=copy.PathFor("inventory.sqlite");
        var ddl="CREATE TABLE hispania_schema_history(owner TEXT,version INTEGER,migration_id TEXT);";
        ddl+=kind switch
        {
            "unknown"=>"INSERT INTO hispania_schema_history VALUES('Inventory.Persistence',1,'unrecognized');",
            "gap"=>"INSERT INTO hispania_schema_history VALUES('Inventory.Persistence',0,'missing-first');",
            "duplicate"=>"INSERT INTO hispania_schema_history VALUES('Inventory.Persistence',1,'0001_baseline07_adoption'),('Inventory.Persistence',1,'0001_baseline07_adoption');",
            _=>"DROP TABLE hispania_schema_history; CREATE TABLE hispania_schema_history(bogus TEXT);"
        };
        HistoricalDataCorpus.Execute(path,ddl);var hash=BaselineCorpus.Hash(File.ReadAllBytes(path));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OpenOwnerAsync("inventory.sqlite",path));SqliteConnection.ClearAllPools();Assert.Equal(hash,BaselineCorpus.Hash(File.ReadAllBytes(path)));
    }
    [Fact]
    public async Task MissingDatabaseInitializesButCorruptDatabaseIsNeverReset()
    {
        using var copy=new HistoricalDataCopy("inventory.sqlite");var missing=copy.PathFor("missing.sqlite");await OpenOwnerAsync("inventory.sqlite",missing);
        Assert.Equal("1",HistoricalDataCorpus.Scalar(missing,"SELECT version FROM hispania_schema_history"));
        var corrupt=copy.PathFor("corrupt.sqlite");await File.WriteAllBytesAsync(corrupt,"TEST ONLY corrupt SQLite header"u8.ToArray());
        var bytes=File.ReadAllBytes(corrupt);await Assert.ThrowsAsync<InvalidOperationException>(()=>OpenOwnerAsync("inventory.sqlite",corrupt));SqliteConnection.ClearAllPools();Assert.Equal(bytes,File.ReadAllBytes(corrupt));
    }
    [Fact]
    public void ConstraintBreakingHistoricalRowsAndIncompatibleShapeAreRejected()
    {
        using var copy=new HistoricalDataCopy("inventory.sqlite");var path=copy.PathFor("inventory.sqlite");
        HistoricalDataCorpus.Execute(path,"ALTER TABLE stock RENAME COLUMN quantity TO incompatible_quantity");var hash=BaselineCorpus.Hash(File.ReadAllBytes(path));
        Assert.Throws<InvalidOperationException>(()=>new Hispania.Inventory.Persistence.Sqlite.InventorySqliteStore(path));
        SqliteConnection.ClearAllPools();
        Assert.Equal(hash,BaselineCorpus.Hash(File.ReadAllBytes(path)));Assert.Equal("0",HistoricalDataCorpus.Scalar(path,"SELECT COUNT(*) FROM sqlite_master WHERE name='hispania_schema_history'"));
    }
    [Theory][InlineData("DELETE")][InlineData("WAL")]
    public async Task ProcessDeathRollsBackActualDdlDataAndLedger(string journalMode)
    {
        using var copy=new HistoricalDataCopy("inventory.sqlite");var path=copy.PathFor("inventory.sqlite");var marker=copy.PathFor("mutation-ready");
        HistoricalDataCorpus.Execute(path,"PRAGMA journal_mode="+journalMode);
        using(var child=StartChild("--phase16-crash",path,marker))
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while(!File.Exists(marker)) { if(child.HasExited)Assert.Fail("Child exited before uncommitted mutation");await Task.Delay(20,deadline.Token); }
            child.Kill(entireProcessTree:true);await child.WaitForExitAsync();Assert.NotEqual(0,child.ExitCode);
        }
        // Opening SQLite exercises real hot-journal/WAL recovery after abrupt process death.
        HistoricalDataCorpus.AssertPreserved("inventory.sqlite",path);
        Assert.Equal("0",HistoricalDataCorpus.Scalar(path,"SELECT COUNT(*) FROM sqlite_master WHERE name IN ('hispania_schema_history','interrupted_probe')"));
        await OpenOwnerAsync("inventory.sqlite",path);HistoricalDataCorpus.AssertPreserved("inventory.sqlite",path);
    }
    [Fact]
    public void OrderedMultiHopAndExceptionRollbackKeepSchemaAndDataAtomic()
    {
        using var copy=new HistoricalDataCopy("inventory.sqlite");var path=copy.PathFor("inventory.sqlite");using var db=HistoricalDataCorpus.Open(path);
        SqliteSchemaMigration[] failed=[new(1,"0001_probe",(c,t)=>SqliteSchemaUpgrade.Execute(c,t,"CREATE TABLE evolution_probe(value INTEGER NOT NULL);INSERT INTO evolution_probe VALUES(1)")),new(2,"0002_failure",(c,t)=>{SqliteSchemaUpgrade.Execute(c,t,"UPDATE evolution_probe SET value=2");throw new InvalidOperationException("TEST ONLY interrupted migration");})];
        Assert.Throws<InvalidOperationException>(()=>SqliteSchemaUpgrade.Apply(db,"Test.SchemaChain",failed));
        Assert.Equal("0",HistoricalDataCorpus.Scalar(path,"SELECT COUNT(*) FROM sqlite_master WHERE name='evolution_probe'"));
        SqliteSchemaMigration[] ordered=[failed[0],new(2,"0002_forward",(c,t)=>SqliteSchemaUpgrade.Execute(c,t,"ALTER TABLE evolution_probe ADD COLUMN label TEXT;UPDATE evolution_probe SET value=2,label='preserved'")),new(3,"0003_forward",(c,t)=>SqliteSchemaUpgrade.Execute(c,t,"UPDATE evolution_probe SET value=3"))];
        SqliteSchemaUpgrade.Apply(db,"Test.SchemaChain",ordered);SqliteSchemaUpgrade.Apply(db,"Test.SchemaChain",ordered);
        var state=SqliteSchemaUpgrade.Read(db,"Test.SchemaChain",ordered);Assert.Equal(3,state.CurrentSchemaVersion);Assert.Equal(0,state.MinimumSupportedSchema);Assert.Equal(3,state.AppliedMigrations.Count);
        Assert.Equal("3",HistoricalDataCorpus.Scalar(path,"SELECT value FROM evolution_probe"));Assert.Equal("preserved",HistoricalDataCorpus.Scalar(path,"SELECT label FROM evolution_probe"));
        Assert.Throws<ArgumentException>(()=>SqliteSchemaUpgrade.Apply(db,"Test.BadChain",[ordered[1]]));
        HistoricalDataCorpus.Execute(path,"DELETE FROM hispania_schema_history WHERE owner='Test.SchemaChain' AND version=2");
        Assert.Throws<InvalidOperationException>(()=>SqliteSchemaUpgrade.Apply(db,"Test.SchemaChain",ordered));
        Assert.Equal("3",HistoricalDataCorpus.Scalar(path,"SELECT value FROM evolution_probe"));
    }
    [Fact]
    public async Task ConcurrentProcessesSerializeMigrationAndNormalOpens()
    {
        using var copy=new HistoricalDataCopy("inventory.sqlite");var path=copy.PathFor("inventory.sqlite");
        for(var repetition=0;repetition<3;repetition++)
        {
            using var a=StartChild("--phase16-open","inventory.sqlite",path);using var b=StartChild("--phase16-open","inventory.sqlite",path);
            await Task.WhenAll(a.WaitForExitAsync(),b.WaitForExitAsync());Assert.Equal(0,a.ExitCode);Assert.Equal(0,b.ExitCode);
            HistoricalDataCorpus.AssertPreserved("inventory.sqlite",path);
        }
        Assert.Equal("1",HistoricalDataCorpus.Scalar(path,"SELECT COUNT(*) FROM hispania_schema_history"));
        await Task.WhenAll(Enumerable.Range(0,4).Select(_=>OpenOwnerAsync("inventory.sqlite",path)));HistoricalDataCorpus.AssertPreserved("inventory.sqlite",path);
    }
    internal static Process StartChild(params string[] args)
    {
        var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add(typeof(Phase16HistoricalDataUpgradeTests).Assembly.Location);
        foreach(var arg in args)start.ArgumentList.Add(arg);return Process.Start(start)??throw new InvalidOperationException("Could not start schema test executable");
    }
    internal static async Task<int> RunChildAsync(string[] args)
    {
        try
        {
            if(args[0]=="--phase16-open"){await OpenOwnerAsync(args[1],args[2]);return 0;}
            if(args[0]=="--phase16-crash")
            {
                using var db=HistoricalDataCorpus.Open(args[1]);
                SqliteSchemaUpgrade.Apply(db,"Inventory.Persistence",[new(1,"0001_baseline07_adoption",(c,t)=>
                {
                    SqliteSchemaUpgrade.Execute(c,t,"CREATE TABLE interrupted_probe(value INTEGER);UPDATE stock SET quantity=999;");
                    File.WriteAllText(args[2],"Uncommitted DDL/data mutation reached");
                    // Parent kills this process at a deterministic mutation boundary.
                    using var waiting=new ManualResetEventSlim(false);waiting.Wait();
                })]);return 0;
            }
            return 2;
        }
        catch(InvalidOperationException){Console.Error.WriteLine("Store startup rejected incompatible/corrupt schema; no reset performed.");return 1;}
    }
}
