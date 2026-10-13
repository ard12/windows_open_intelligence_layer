using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Woi.Core;

public sealed class CacheStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly string directory;
    private readonly Dictionary<(double Start,double End,string? Process),IReadOnlyList<AggregateRow>> contributorCache=[];
    private readonly Queue<(double Start,double End,string? Process)> contributorCacheOrder=[];
    public CacheStore(string directory, bool readOnly = false)
    {
        this.directory=directory;
        if (!readOnly) Directory.CreateDirectory(directory);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = Path.Combine(directory, "analysis.db"), Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, Vfs = "win32-longpath" // Preserve Windows file locking while supporting deep cache paths.
        }.ToString());
        connection.Open();
        if (readOnly) return;
        using var command = connection.CreateCommand();
        command.CommandText = """
        PRAGMA journal_mode=DELETE;
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS entities(id TEXT PRIMARY KEY, kind TEXT, json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS aggregates(second INTEGER, process TEXT, samples INTEGER, cpu REAL,
          weighted INTEGER, disk INTEGER, file INTEGER, network INTEGER, io REAL, diskops INTEGER,
          p95 REAL, memory REAL, evidence TEXT, PRIMARY KEY(second, process));
        CREATE INDEX IF NOT EXISTS process_time ON aggregates(process, second);
        CREATE TABLE IF NOT EXISTS bookmarks(id TEXT PRIMARY KEY, json TEXT NOT NULL);
        """;
        command.ExecuteNonQuery();
    }
    public void Save(TraceSession session, IEnumerable<EntityLifetime> entities, IEnumerable<AggregateRow> rows,
        CancellationToken cancellationToken = default, Action? checkpoint = null)
    {
        cancellationToken.ThrowIfCancellationRequested();contributorCache.Clear();contributorCacheOrder.Clear();
        var saved = 0;
        void Check() { cancellationToken.ThrowIfCancellationRequested(); if (++saved % 512 == 0) checkpoint?.Invoke(); }
        using var transaction = connection.BeginTransaction();
        using var entity = connection.CreateCommand();
        entity.Transaction = transaction;
        entity.CommandText = "INSERT OR REPLACE INTO entities VALUES($id,$kind,$json)";
        foreach (var value in entities) {
            Check();
            entity.Parameters.Clear(); entity.Parameters.AddWithValue("$id", value.Id);
            entity.Parameters.AddWithValue("$kind", value.Kind); entity.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Wire.Json));
            entity.ExecuteNonQuery();
        }
        using var aggregate = connection.CreateCommand();
        aggregate.Transaction = transaction;
        aggregate.CommandText = "INSERT OR REPLACE INTO aggregates VALUES($s,$p,$n,$c,$w,$d,$f,$net,$io,$ops,$lat,$mem,$e)";
        foreach (var value in rows) {
            Check();
            aggregate.Parameters.Clear();
            aggregate.Parameters.AddWithValue("$s", value.Second); aggregate.Parameters.AddWithValue("$p", value.ProcessId);
            aggregate.Parameters.AddWithValue("$n", value.CpuSamples); aggregate.Parameters.AddWithValue("$c", value.CpuMs);
            aggregate.Parameters.AddWithValue("$w", value.WeightedSamples); aggregate.Parameters.AddWithValue("$d", value.DiskBytes);
            aggregate.Parameters.AddWithValue("$f", value.FileBytes); aggregate.Parameters.AddWithValue("$net", value.NetworkBytes);
            aggregate.Parameters.AddWithValue("$io", value.IoMs); aggregate.Parameters.AddWithValue("$ops", value.DiskOperations);
            aggregate.Parameters.AddWithValue("$lat", (object?)value.DiskP95Ms ?? DBNull.Value);
            aggregate.Parameters.AddWithValue("$mem", (object?)value.WorkingSetBytes ?? DBNull.Value);
            aggregate.Parameters.AddWithValue("$e", JsonSerializer.Serialize(value.Evidence, Wire.Json)); aggregate.ExecuteNonQuery();
        }
        using var meta = connection.CreateCommand();
        meta.Transaction = transaction; meta.CommandText = "INSERT OR REPLACE INTO meta VALUES('session',$session)";
        meta.Parameters.AddWithValue("$session", JsonSerializer.Serialize(session, Wire.Json)); meta.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested(); checkpoint?.Invoke();
        transaction.Commit();
    }
    public void UpdateSession(TraceSession session)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO meta VALUES('session',$session)";
        command.Parameters.AddWithValue("$session", JsonSerializer.Serialize(session, Wire.Json));
        command.ExecuteNonQuery();
    }
    public void VerifyIntegrity()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
            throw new WoiException("InvalidCache", "The derived SQLite cache failed its integrity check.");
    }
    public CacheStatistics Statistics()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT (SELECT COUNT(*) FROM entities), (SELECT COUNT(*) FROM aggregates),
        COALESCE(SUM(samples),0), COALESCE(SUM(diskops),0), COALESCE(SUM(disk),0),
        COALESCE(SUM(file),0), COALESCE(SUM(network),0) FROM aggregates WHERE process=''
        """;
        using var reader = command.ExecuteReader(); reader.Read();
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
    }
    public TraceSession GetSession()
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT value FROM meta WHERE key='session'";
        return JsonSerializer.Deserialize<TraceSession>((string)(command.ExecuteScalar() ??
            throw new WoiException("InvalidCache", "The cache has no completed session.")), Wire.Json)!;
    }
    public IReadOnlyList<EntityLifetime> Entities(string? kind = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM entities WHERE $kind IS NULL OR kind=$kind ORDER BY id";
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        using var reader = command.ExecuteReader(); List<EntityLifetime> result = [];
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<EntityLifetime>(reader.GetString(0), Wire.Json)!);
        return result;
    }
    public IReadOnlyList<AggregateRow> ContributorTotals(double startMs,double endMs,string? process,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var key=(startMs,endMs,process);
        if(contributorCache.TryGetValue(key,out var cached))return cached;
        using var command=connection.CreateCommand();
        command.CommandText="""
        SELECT process,SUM(samples),SUM(cpu),SUM(weighted),SUM(disk),SUM(file),SUM(network),SUM(io),SUM(diskops)
        FROM aggregates WHERE second >= $start AND second < $end AND process!=''
        AND ($process IS NULL OR process=$process) GROUP BY process
        """;
        command.Parameters.AddWithValue("$start",startMs/1000);command.Parameters.AddWithValue("$end",endMs/1000);
        command.Parameters.AddWithValue("$process",(object?)process??DBNull.Value);
        using var reader=command.ExecuteReader();List<AggregateRow> result=[];
        while(reader.Read()){
            token.ThrowIfCancellationRequested();
            result.Add(new(0,reader.GetString(0),reader.GetInt64(1),reader.GetDouble(2),reader.GetInt64(3),
                reader.GetInt64(4),reader.GetInt64(5),reader.GetInt64(6),reader.GetDouble(7),reader.GetInt64(8),null,null,[]));
        }
        reader.Close();
        for(int index=0;index<result.Count;index++){
            token.ThrowIfCancellationRequested();using var evidence=connection.CreateCommand();
            evidence.CommandText="SELECT evidence FROM aggregates WHERE process=$process AND second >= $start AND second < $end AND evidence!='[]' ORDER BY second LIMIT 2";
            evidence.Parameters.AddWithValue("$process",result[index].ProcessId);evidence.Parameters.AddWithValue("$start",startMs/1000);evidence.Parameters.AddWithValue("$end",endMs/1000);
            using var records=evidence.ExecuteReader();List<EvidenceReference> references=[];
            while(records.Read())references.AddRange(JsonSerializer.Deserialize<List<EvidenceReference>>(records.GetString(0),Wire.Json)!);
            result[index]=result[index] with{Evidence=references.Take(6).ToArray()};
        }
        if(contributorCache.Count==16)contributorCache.Remove(contributorCacheOrder.Dequeue());
        var immutable=result.AsReadOnly();contributorCache[key]=immutable;contributorCacheOrder.Enqueue(key);
        return immutable;
    }
    public IReadOnlyList<AggregateRow> Rows(double startMs, double endMs, string? process = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM aggregates WHERE second >= $start AND second < $end AND ($process IS NULL OR process=$process) ORDER BY second,process";
        command.Parameters.AddWithValue("$start", startMs / 1000); command.Parameters.AddWithValue("$end", endMs / 1000);
        command.Parameters.AddWithValue("$process", (object?)process ?? DBNull.Value);
        using var reader = command.ExecuteReader(); List<AggregateRow> result = [];
        while (reader.Read()) result.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2), reader.GetDouble(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetDouble(8), reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetDouble(10), reader.IsDBNull(11) ? null : reader.GetDouble(11),
            JsonSerializer.Deserialize<List<EvidenceReference>>(reader.GetString(12), Wire.Json)!));
        return result;
    }
    public void AddBookmark(Bookmark value)
    {
        using var state=new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource=Path.Combine(directory,"bookmarks.db"),Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false,Vfs="win32-longpath"
        }.ToString());
        state.Open();using var command=state.CreateCommand();
        command.CommandText="CREATE TABLE IF NOT EXISTS bookmarks(id TEXT PRIMARY KEY,json TEXT NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText="INSERT OR REPLACE INTO bookmarks VALUES($id,$json)";
        command.Parameters.AddWithValue("$id",value.Id);command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(value,Wire.Json));
        command.ExecuteNonQuery();
    }
    public IReadOnlyList<Bookmark> Bookmarks()=>ReadBookmarks(directory);
    public static IReadOnlyList<Bookmark> ReadBookmarks(string directory)
    {
        // Read mutable state independently so a corrupt analysis database cannot erase bookmarks.
        var path=Path.Combine(directory,"bookmarks.db");
        if(!File.Exists(path))path=Path.Combine(directory,"analysis.db");
        using var state=new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,Vfs="win32-longpath"
        }.ToString());
        state.Open();using var command=state.CreateCommand();
        command.CommandText="SELECT json FROM bookmarks ORDER BY id";
        using var reader=command.ExecuteReader();List<Bookmark> result=[];
        while(reader.Read())result.Add(JsonSerializer.Deserialize<Bookmark>(reader.GetString(0),Wire.Json)!);
        return result;
    }
    public void Dispose() => connection.Dispose();
}

public static class LifetimeResolver
{
    public static EntityLifetime? Resolve(IEnumerable<EntityLifetime> lifetimes, int pid, double timeMs, string kind = "Process")
    {
        var matches = lifetimes.Where(x => x.Kind == kind && x.OsId == pid &&
            timeMs >= x.StartMs && timeMs < x.EndMs).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
