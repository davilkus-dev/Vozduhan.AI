namespace Vozduhan.AI;

/// <summary>
/// A submission queue for one lesson. Status: open (buttons) → locked (after the lesson, kept in the chat) | deleted.
/// Version grows on every change; the Telegram message shows Synced — a newer version is (re)rendered.
/// </summary>
public sealed record SubmissionQueue(long Id, string SubjectId, string? Work, string Date, int Period, string Start, string End, string Room,
    long Closes, string Status, int MessageId, long Actor, long Created, int Version, int Synced);
public sealed record QueueEntry(long Id, string Member, long UserId, string Name, long Joined);

public sealed partial class BotStore
{
    private void InitQueues()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS queues(id INTEGER PRIMARY KEY AUTOINCREMENT, subject_id TEXT NOT NULL, work TEXT NULL, date TEXT NOT NULL,
                period INTEGER NOT NULL, start TEXT NOT NULL, end TEXT NOT NULL, room TEXT NOT NULL, closes INTEGER NOT NULL, status TEXT NOT NULL,
                message_id INTEGER NOT NULL DEFAULT 0, actor INTEGER NOT NULL, created INTEGER NOT NULL, version INTEGER NOT NULL DEFAULT 1,
                synced INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS queue_entries(id INTEGER PRIMARY KEY AUTOINCREMENT, queue INTEGER NOT NULL, member TEXT NOT NULL,
                user_id INTEGER NOT NULL, name TEXT NOT NULL, joined INTEGER NOT NULL, seq INTEGER NOT NULL, UNIQUE(queue, member));
            """);
    }

    private const string QueueColumns = "id,subject_id,work,date,period,start,end,room,closes,status,message_id,actor,created,version,synced";
    private static SubmissionQueue ReadQueue(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
        r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt64(8), r.GetString(9), r.GetInt32(10), r.GetInt64(11),
        r.GetInt64(12), r.GetInt32(13), r.GetInt32(14));

    public long AddQueue(string subject, string? work, string date, int period, string start, string end, string room, long closes, long actor, long now)
    {
        lock (gate)
        {
            Exec("INSERT INTO queues(subject_id,work,date,period,start,end,room,closes,status,actor,created) VALUES($s,$w,$d,$p,$st,$e,$r,$c,'open',$a,$n)",
                ("s", subject), ("w", work), ("d", date), ("p", period), ("st", start), ("e", end), ("r", room), ("c", closes), ("a", actor), ("n", now));
            return Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
        }
    }
    public SubmissionQueue? Queue(long id)
    {
        lock (gate) return Query($"SELECT {QueueColumns} FROM queues WHERE id=$id", ReadQueue, ("id", id)).FirstOrDefault();
    }
    public List<SubmissionQueue> Queues(int limit = 50)
    {
        lock (gate) return Query($"SELECT {QueueColumns} FROM queues WHERE status<>'deleted' ORDER BY id DESC LIMIT $n", ReadQueue, ("n", limit));
    }
    /// <summary>Open queues whose lesson is over, and queues whose message is behind.</summary>
    public List<SubmissionQueue> QueuesToTend(long now)
    {
        lock (gate) return Query($"SELECT {QueueColumns} FROM queues WHERE (status='open' AND closes<=$n) OR synced<>version ORDER BY id", ReadQueue, ("n", now));
    }
    public List<QueueEntry> QueueEntries(long queue)
    {
        lock (gate) return Query("SELECT id,member,user_id,name,joined FROM queue_entries WHERE queue=$q ORDER BY seq,id",
            r => new QueueEntry(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4)), ("q", queue));
    }

    /// <summary>Puts a person at the end. Position — 1-based; Added false — already there. force: the admin, even into a locked queue.</summary>
    public (int Position, bool Added)? JoinQueue(long queue, string member, long userId, string name, long now, bool force = false)
    {
        lock (gate)
        {
            if (Queue(queue) is not { } q || q.Status == "deleted" || (q.Status != "open" && !force)) return null;
            var added = Exec("""
                INSERT OR IGNORE INTO queue_entries(queue,member,user_id,name,joined,seq)
                VALUES($q,$m,$u,$name,$n,(SELECT COALESCE(MAX(seq),0)+1 FROM queue_entries WHERE queue=$q))
                """, ("q", queue), ("m", member), ("u", userId), ("name", name), ("n", now)) == 1;
            if (added) Touch(queue);
            return (QueueEntries(queue).FindIndex(e => e.Member == member) + 1, added);
        }
    }
    /// <summary>Removes a person; everyone after them moves up. force: the admin, even from a locked queue.</summary>
    public bool LeaveQueue(long queue, string member, bool force = false)
    {
        lock (gate)
        {
            if (Queue(queue) is not { } q || q.Status == "deleted" || (q.Status != "open" && !force)) return false;
            var removed = Exec("DELETE FROM queue_entries WHERE queue=$q AND member=$m", ("q", queue), ("m", member)) == 1;
            if (removed) Touch(queue);
            return removed;
        }
    }
    /// <summary>Swaps a person with the neighbour above (-1) or below (+1).</summary>
    public bool MoveInQueue(long queue, string member, int delta)
    {
        lock (gate)
        {
            var list = Query("SELECT id,seq,member FROM queue_entries WHERE queue=$q ORDER BY seq,id",
                r => (Id: r.GetInt64(0), Seq: r.GetInt64(1), Member: r.GetString(2)), ("q", queue));
            var i = list.FindIndex(e => e.Member == member);
            var j = i + delta;
            if (i < 0 || j < 0 || j >= list.Count) return false;
            using var tx = db.BeginTransaction();
            Exec("UPDATE queue_entries SET seq=$s WHERE id=$id", ("s", list[j].Seq), ("id", list[i].Id));
            Exec("UPDATE queue_entries SET seq=$s WHERE id=$id", ("s", list[i].Seq), ("id", list[j].Id));
            Touch(queue);
            tx.Commit();
            return true;
        }
    }
    public bool SetQueueStatus(long queue, string status, long? closes = null)
    {
        lock (gate) return Exec("UPDATE queues SET status=$s, closes=COALESCE($c,closes), version=version+1 WHERE id=$id AND status<>'deleted'",
            ("id", queue), ("s", status), ("c", closes)) == 1;
    }
    public void SetQueueMessage(long queue, int message)
    {
        lock (gate) Exec("UPDATE queues SET message_id=$m WHERE id=$id", ("id", queue), ("m", message));
    }
    public void QueueSynced(long queue, int version)
    {
        lock (gate) Exec("UPDATE queues SET synced=$v WHERE id=$id AND synced<$v", ("id", queue), ("v", version));
    }
    private void Touch(long queue) => Exec("UPDATE queues SET version=version+1 WHERE id=$id", ("id", queue));
}
