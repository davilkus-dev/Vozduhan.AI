using System.Text.Json;

namespace Vozduhan.AI;

/// <summary>
/// One self-initiated post of the bot (morning, drops, evening reminder, deadline wake-up, scheduled post) and its delivery state.
/// Status: planned → ready (text and preview sent to the developer) → sent | cancelled | failed | skipped | silent.
/// Due may move forward (anti-dup); FirstDue is the original moment.
/// </summary>
public sealed record Publication(long Id, string Key, string Kind, long Due, int Topic, string Status, string Mode, string? Text, string? Source, string? Basis,
    int Preview, int Attempts, long NextTry, string? Note, long Created, long Updated, long FirstDue, long Sent)
{
    public bool Active => Status is "planned" or "ready";
}

/// <summary>A deadline named by a self-initiated post: full (the post is about it, with facts) or brief (only mentioned in passing).</summary>
public sealed record DeadlineMention(int Deadline, string Key, string Kind, long Time);

public sealed partial class BotStore
{
    private void InitPublications()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS publications(id INTEGER PRIMARY KEY AUTOINCREMENT, key TEXT NOT NULL UNIQUE, kind TEXT NOT NULL,
                due INTEGER NOT NULL, topic INTEGER NOT NULL, status TEXT NOT NULL, mode TEXT NOT NULL, text TEXT NULL, source TEXT NULL,
                basis TEXT NULL, preview_message_id INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0,
                next_try INTEGER NOT NULL DEFAULT 0, note TEXT NULL, created INTEGER NOT NULL, updated INTEGER NOT NULL,
                first_due INTEGER NOT NULL DEFAULT 0, sent_at INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS publications_status ON publications(status,due);
            CREATE TABLE IF NOT EXISTS deadline_mentions(id INTEGER PRIMARY KEY AUTOINCREMENT, deadline INTEGER NOT NULL,
                publication_key TEXT NOT NULL, kind TEXT NOT NULL, time INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS deadline_mentions_once ON deadline_mentions(deadline,publication_key);
            CREATE INDEX IF NOT EXISTS deadline_mentions_time ON deadline_mentions(time);
            """);
    }

    private const string PublicationColumns =
        "id,key,kind,due,topic,status,mode,text,source,basis,preview_message_id,attempts,next_try,note,created,updated,first_due,sent_at";
    private static Publication ReadPublication(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
        r.GetInt32(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.GetInt32(10), r.GetInt32(11), r.GetInt64(12), r.IsDBNull(13) ? null : r.GetString(13),
        r.GetInt64(14), r.GetInt64(15), r.GetInt64(16), r.GetInt64(17));
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public Publication? Publication(string key)
    {
        lock (gate) return Query($"SELECT {PublicationColumns} FROM publications WHERE key=$k", ReadPublication, ("k", key)).FirstOrDefault();
    }
    public Publication? Publication(long id)
    {
        lock (gate) return Query($"SELECT {PublicationColumns} FROM publications WHERE id=$id", ReadPublication, ("id", id)).FirstOrDefault();
    }
    public List<Publication> ActivePublications()
    {
        lock (gate) return Query($"SELECT {PublicationColumns} FROM publications WHERE status IN ('planned','ready') ORDER BY due,id", ReadPublication);
    }
    public List<Publication> Publications(string keyPrefix)
    {
        lock (gate) return Query($"SELECT {PublicationColumns} FROM publications WHERE substr(key,1,length($p))=$p ORDER BY due,id", ReadPublication, ("p", keyPrefix));
    }

    /// <summary>
    /// The row of an event, created on first sight. A new row takes over what the earlier storage knew about the same key
    /// (a finished event, a prepared text), so an upgrade neither repeats today's posts nor loses warmed texts.
    /// A planned row without a preview follows the current time and mode from the settings.
    /// </summary>
    public Publication EnsurePublication(string key, string kind, long due, int topic, string mode)
    {
        lock (gate)
        {
            var now = Now();
            if (Publication(key) is { } p)
            {
                if (p.Status == "planned" && p.Preview == 0 && p.FirstDue == p.Due && (p.Due != due || p.Mode != mode || p.Topic != topic))
                    Exec("UPDATE publications SET due=$d, first_due=$d, mode=$m, topic=$t, updated=$n WHERE id=$id", ("d", due), ("m", mode), ("t", topic), ("n", now), ("id", p.Id));
                return Publication(key)!;
            }
            using var tx = db.BeginTransaction();
            var status = Get(key) switch { "done" => "sent", "skipped" => "skipped", "cancelled" => "cancelled", _ => "planned" };
            Exec("""
                INSERT INTO publications(key,kind,due,topic,status,mode,created,updated,first_due) VALUES($k,$kind,$d,$t,$s,$m,$n,$n,$d)
                """, ("k", key), ("kind", kind), ("d", due), ("t", topic), ("s", status), ("m", mode), ("n", now));
            if (Get("prep:" + key) is { Length: > 2 } raw)
            {
                using var doc = JsonDocument.Parse(raw);
                var r = doc.RootElement;
                Exec("UPDATE publications SET text=$x, source=$s, basis=$b WHERE key=$k", ("k", key), ("x", r.Text("text")),
                    ("s", r.Text("source") ?? "ai"), ("b", r.Text("basis")));
            }
            tx.Commit();
            return Publication(key)!;
        }
    }

    /// <summary>Atomic status change; false when the row is not in the expected status any more (pressed twice, sent meanwhile).</summary>
    /// <summary>at: the moment of sending (sent_at), the scheduler's clock.</summary>
    public bool MovePublication(string key, string from, string to, string? note = null, long? at = null)
    {
        lock (gate) return Exec("""
            UPDATE publications SET status=$to, note=COALESCE($note,note), updated=$n, sent_at=CASE WHEN $to='sent' THEN $at ELSE sent_at END
            WHERE key=$k AND status=$from
            """, ("k", key), ("from", from), ("to", to), ("note", note), ("n", Now()), ("at", at ?? Now())) == 1;
    }
    /// <summary>Closes an active row (planned or ready) with a final status.</summary>
    public bool FinishPublication(string key, string status, string? note = null, long? at = null) =>
        MovePublication(key, "ready", status, note, at) || MovePublication(key, "planned", status, note, at);
    /// <summary>A cancelled event goes back to the plan; its text stays, the preview is sent anew.</summary>
    public bool RestorePublication(string key)
    {
        lock (gate) return Exec("UPDATE publications SET status='planned', preview_message_id=0, note=NULL, attempts=0, next_try=0, updated=$n WHERE key=$k AND status='cancelled'",
            ("k", key), ("n", Now())) == 1;
    }
    public void SetPublicationText(string key, string? text, string? source, string? basis)
    {
        lock (gate) Exec("UPDATE publications SET text=$x, source=$s, basis=$b, updated=$n WHERE key=$k", ("k", key), ("x", text), ("s", source), ("b", basis), ("n", Now()));
    }
    /// <summary>The preview message in the developer's DM; 0 — none (the row goes back to planned, so a new preview is sent).</summary>
    public void SetPublicationPreview(string key, int message)
    {
        lock (gate) Exec("UPDATE publications SET preview_message_id=$m, status=CASE WHEN $m=0 AND status='ready' THEN 'planned' ELSE status END, updated=$n WHERE key=$k",
            ("k", key), ("m", message), ("n", Now()));
    }
    public void MovePublicationDue(string key, long due, string note)
    {
        lock (gate) Exec("UPDATE publications SET due=$d, note=$note, updated=$n WHERE key=$k", ("k", key), ("d", due), ("note", note), ("n", Now()));
    }
    public void RetryPublication(string key, int attempts, long nextTry, string note)
    {
        lock (gate) Exec("UPDATE publications SET attempts=$a, next_try=$t, note=$note, updated=$n WHERE key=$k",
            ("k", key), ("a", attempts), ("t", nextTry), ("note", note), ("n", Now()));
    }
    /// <summary>When the bot last posted on its own (any topic) up to this moment; null — never.</summary>
    public long? LastSelfPost(long now)
    {
        lock (gate) return Scalar("SELECT MAX(sent_at) FROM publications WHERE status='sent' AND sent_at>0 AND sent_at<=$n", ("n", now)) is long t ? t : null;
    }

    // ---------- Deadline mentions ----------

    public void AddMention(int deadline, string key, string kind, long time)
    {
        lock (gate) Exec("INSERT OR IGNORE INTO deadline_mentions(deadline,publication_key,kind,time) VALUES($d,$k,$kind,$t)",
            ("d", deadline), ("k", key), ("kind", kind), ("t", time));
    }
    public List<DeadlineMention> Mentions(long since)
    {
        lock (gate) return Query("SELECT deadline,publication_key,kind,time FROM deadline_mentions WHERE time>=$s ORDER BY time",
            r => new DeadlineMention(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt64(3)), ("s", since));
    }
    public long? LastFullMention(int deadline)
    {
        lock (gate) return Scalar("SELECT MAX(time) FROM deadline_mentions WHERE deadline=$d AND kind='full'", ("d", deadline)) is long t ? t : null;
    }
}
