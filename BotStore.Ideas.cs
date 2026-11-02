namespace Vozduhan.AI;

/// <summary>
/// An idea for a spontaneous drop. Status: new (AI) → approved (developer) → used | rejected | expired.
/// PublicationKey — the drop it is taken by (or was sent with).
/// </summary>
public sealed record Idea(long Id, string Text, string[] Conditions, string[] People, string Source, string Status, double Score,
    long Created, long Expires, long UsedAt, string? PublicationKey);

public sealed partial class BotStore
{
    private void InitIdeas()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS ideas(id INTEGER PRIMARY KEY AUTOINCREMENT, text TEXT NOT NULL, conditions TEXT NOT NULL DEFAULT '[]',
                people TEXT NOT NULL DEFAULT '[]', source TEXT NOT NULL, status TEXT NOT NULL, score REAL NOT NULL DEFAULT 0,
                created INTEGER NOT NULL, expires INTEGER NOT NULL, used_at INTEGER NOT NULL DEFAULT 0, publication_key TEXT NULL);
            CREATE INDEX IF NOT EXISTS ideas_status ON ideas(status);
            """);
    }

    private const string IdeaColumns = "id,text,conditions,people,source,status,score,created,expires,used_at,publication_key";
    private static Idea ReadIdea(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), Json.Read<string[]>(r.GetString(2)),
        Json.Read<string[]>(r.GetString(3)), r.GetString(4), r.GetString(5), r.GetDouble(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9),
        r.IsDBNull(10) ? null : r.GetString(10));

    public List<Idea> Ideas()
    {
        lock (gate) return Query($"SELECT {IdeaColumns} FROM ideas ORDER BY id DESC LIMIT 500", ReadIdea);
    }
    public Idea? Idea(long id)
    {
        lock (gate) return Query($"SELECT {IdeaColumns} FROM ideas WHERE id=$id", ReadIdea, ("id", id)).FirstOrDefault();
    }
    public long AddIdea(string text, string[] conditions, string[] people, string source, string status, double score, long created, long expires)
    {
        lock (gate)
        {
            Exec("INSERT INTO ideas(text,conditions,people,source,status,score,created,expires) VALUES($t,$c,$p,$s,$st,$sc,$n,$e)",
                ("t", text), ("c", Json.Write(conditions)), ("p", Json.Write(people)), ("s", source), ("st", status), ("sc", score), ("n", created), ("e", expires));
            return Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
        }
    }
    public bool UpdateIdea(long id, string text, string[] conditions, string[] people, double score)
    {
        lock (gate) return Exec("UPDATE ideas SET text=$t, conditions=$c, people=$p, score=$sc WHERE id=$id AND status NOT IN ('used','expired')",
            ("id", id), ("t", text), ("c", Json.Write(conditions)), ("p", Json.Write(people)), ("sc", score)) == 1;
    }
    public bool SetIdeaStatus(long id, string status)
    {
        lock (gate) return Exec("UPDATE ideas SET status=$s WHERE id=$id AND status IN ('new','approved','rejected') AND publication_key IS NULL",
            ("id", id), ("s", status)) == 1;
    }
    public bool DeleteIdea(long id)
    {
        lock (gate) return Exec("DELETE FROM ideas WHERE id=$id AND (publication_key IS NULL OR status IN ('used','expired','rejected'))", ("id", id)) == 1;
    }
    /// <summary>Takes a free idea for a drop; false — someone took it meanwhile.</summary>
    public bool LinkIdea(long id, string key)
    {
        lock (gate) return Exec("UPDATE ideas SET publication_key=$k WHERE id=$id AND publication_key IS NULL AND status IN ('new','approved')",
            ("id", id), ("k", key)) == 1;
    }
    public Idea? IdeaFor(string key)
    {
        lock (gate) return Query($"SELECT {IdeaColumns} FROM ideas WHERE publication_key=$k AND status IN ('new','approved') ORDER BY id LIMIT 1", ReadIdea, ("k", key)).FirstOrDefault();
    }
    /// <summary>The drop is closed: sent — the idea is used; otherwise it goes back to the pool.</summary>
    public void IdeaFinished(string key, bool sent, long at)
    {
        lock (gate) Exec(sent
            ? "UPDATE ideas SET status='used', used_at=$at WHERE publication_key=$k AND status IN ('new','approved')"
            : "UPDATE ideas SET publication_key=NULL WHERE publication_key=$k AND status IN ('new','approved')", ("k", key), ("at", at));
    }
}
