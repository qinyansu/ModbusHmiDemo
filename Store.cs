using Microsoft.Data.Sqlite;

namespace ModbusHmiDemo;

/// <summary>采集数据落库：一次采集一批写入同一个事务，避免每条都开事务。</summary>
public sealed class SampleStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public string DbPath { get; }

    public SampleStore(string dbPath)
    {
        DbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec(@"CREATE TABLE IF NOT EXISTS samples(
                   id      INTEGER PRIMARY KEY AUTOINCREMENT,
                   ts      TEXT    NOT NULL,
                   name    TEXT    NOT NULL,
                   value   REAL,
                   quality TEXT    NOT NULL);");
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void InsertMany(IEnumerable<(string Name, double Value, string Quality)> rows)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO samples(ts,name,value,quality) VALUES($ts,$n,$v,$q)";

        var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
        var pName = cmd.Parameters.Add("$n", SqliteType.Text);
        var pVal = cmd.Parameters.Add("$v", SqliteType.Real);
        var pQ = cmd.Parameters.Add("$q", SqliteType.Text);

        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var r in rows)
        {
            pTs.Value = ts;
            pName.Value = r.Name;
            pVal.Value = r.Value;
            pQ.Value = r.Quality;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public long Count()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM samples";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public List<(string Ts, string Name, double Value)> Recent(string name, int limit = 100)
    {
        var list = new List<(string, string, double)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT ts,name,value FROM samples WHERE name=$n ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$l", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2)));
        return list;
    }

    /// <summary>导出 CSV：字段里可能含逗号，统一加引号并转义。</summary>
    public int ExportCsv(string filePath)
    {
        int n = 0;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT ts,name,value,quality FROM samples ORDER BY id";
        using var reader = cmd.ExecuteReader();
        using var w = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);
        w.WriteLine("时间,点位,数值,质量");
        while (reader.Read())
        {
            w.WriteLine($"{reader.GetString(0)},{Escape(reader.GetString(1))},{reader.GetDouble(2)},{reader.GetString(3)}");
            n++;
        }
        return n;
    }

    private static string Escape(string s) => s.Contains(',') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    public void Dispose() => _conn.Dispose();
}
