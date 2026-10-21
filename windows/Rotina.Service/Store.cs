using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rotina.Core;

namespace Rotina.Service;

/// <summary>Each record is one JSON row; rev grows on every write so the phase 5 sync can ask for "changes since rev".</summary>
public sealed class Store
{
    static readonly JsonSerializerOptions Json = Api.Json;

    readonly SqliteConnection db;

    public Store(string path)
    {
        db = new SqliteConnection($"Data Source={path}");
        db.Open();
        Exec("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS records(
                kind TEXT NOT NULL,
                id TEXT NOT NULL,
                json TEXT NOT NULL,
                rev INTEGER NOT NULL,
                PRIMARY KEY(kind, id));
            """);
    }

    public List<T> All<T>(string kind) => Query<T>("SELECT json FROM records WHERE kind = $k", kind);

    public T? Get<T>(string kind, object id) where T : class =>
        Query<T>("SELECT json FROM records WHERE kind = $k AND id = $i", kind, id.ToString()).FirstOrDefault();

    public bool Exists(string kind, object id) =>
        Query<JsonElement>("SELECT json FROM records WHERE kind = $k AND id = $i", kind, id.ToString()).Count > 0;

    /// <summary>Records of a kind written after the given revision (deleted ones included, so the phone sees them go).</summary>
    public List<T> Since<T>(string kind, long rev) => Query<T>("SELECT json FROM records WHERE kind = $k AND rev > $r", kind, rev: rev);

    public long MaxRev()
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT IFNULL(MAX(rev), 0) FROM records";
        return (long)c.ExecuteScalar()!;
    }

    public Session? CurrentSession() =>
        Query<Session>("SELECT json FROM records WHERE kind = $k AND json_extract(json, '$.endedAt') IS NULL", "session")
            .FirstOrDefault();

    public void Put<T>(string kind, object id, T value)
    {
        using var c = db.CreateCommand();
        c.CommandText = """
            INSERT INTO records(kind, id, json, rev)
            VALUES($k, $i, $j, (SELECT IFNULL(MAX(rev), 0) + 1 FROM records))
            ON CONFLICT(kind, id) DO UPDATE SET json = excluded.json, rev = excluded.rev
            """;
        c.Parameters.AddWithValue("$k", kind);
        c.Parameters.AddWithValue("$i", id.ToString());
        c.Parameters.AddWithValue("$j", JsonSerializer.Serialize(value, Json));
        c.ExecuteNonQuery();
    }

    /// <summary>All writes inside commit together, so ending one session and starting the next never half-applies.</summary>
    public void Atomically(Action write)
    {
        Exec("BEGIN");
        try { write(); Exec("COMMIT"); }
        catch { Exec("ROLLBACK"); throw; }
    }

    List<T> Query<T>(string sql, string kind, string? id = null, long? rev = null)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.Parameters.AddWithValue("$k", kind);
        if (id is not null) c.Parameters.AddWithValue("$i", id);
        if (rev is not null) c.Parameters.AddWithValue("$r", rev);
        using var r = c.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(JsonSerializer.Deserialize<T>(r.GetString(0), Json)!);
        return list;
    }

    void Exec(string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }
}
