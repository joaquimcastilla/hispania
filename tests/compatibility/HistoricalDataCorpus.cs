using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Hispania.Compatibility.Tests;
internal static class HistoricalDataCorpus
{
    internal const string ManifestHash = "68a28dafbf9709284b361dacf8113219ac44d130d0ca77a7646e6df29c7bc898";
    internal static string PathFor(string file) => BaselineCorpus.PathFor("data/0.7/" + file);
    internal static SqliteConnection Open(string path)
    { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path, Pooling=false, ForeignKeys=true }.ToString()); db.Open(); return db; }
    internal static void Execute(string path, string sql)
    { using var db=Open(path); using var q=db.CreateCommand(); q.CommandText=sql; q.ExecuteNonQuery(); }
    internal static string? Scalar(string path, string sql)
    { using var db=Open(path); using var q=db.CreateCommand(); q.CommandText=sql; return Convert.ToString(q.ExecuteScalar(),CultureInfo.InvariantCulture); }
    internal static void AssertPreserved(string file, string path)
    {
        using var snapshots=JsonDocument.Parse(File.ReadAllBytes(PathFor("semantic-snapshots.json")));
        using var db=Open(path);
        foreach (var table in snapshots.RootElement.GetProperty(file).EnumerateObject())
        {
            var columns=table.Value.GetProperty("columns").EnumerateArray().Select(v=>"\""+v.GetString()+"\"");
            using var q=db.CreateCommand();q.CommandText=$"SELECT {string.Join(',',columns)} FROM \"{table.Name}\"";
            using var r=q.ExecuteReader();var rows=new List<string>();
            while(r.Read())
            {
                var values=Enumerable.Range(0,r.FieldCount).Select(i=>r.IsDBNull(i)?null:r.GetValue(i) is byte[] bytes?Convert.ToHexString(bytes):Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)).ToArray();
                rows.Add(JsonSerializer.Serialize(values));
            }
            var expected=table.Value.GetProperty("rows").EnumerateArray().Select(row=>JsonSerializer.Serialize(row.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.Null?null:v.GetString()).ToArray())).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected,rows.Order(StringComparer.Ordinal));
        }
        using var integrity=db.CreateCommand();integrity.CommandText="PRAGMA integrity_check";Assert.Equal("ok",integrity.ExecuteScalar());
        integrity.CommandText="PRAGMA foreign_key_check";using var fk=integrity.ExecuteReader();Assert.False(fk.Read());
    }
}
internal sealed class HistoricalDataCopy : IDisposable
{
    internal string DirectoryPath { get; }=Path.Combine(Path.GetTempPath(),"hispania-historical-data-"+Guid.NewGuid().ToString("N"));
    internal string PathFor(string file)=>Path.Combine(DirectoryPath,file);
    internal HistoricalDataCopy(string file)
    { Directory.CreateDirectory(DirectoryPath);File.Copy(HistoricalDataCorpus.PathFor(file),PathFor(file)); }
    public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(DirectoryPath,true);}
}
