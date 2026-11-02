namespace Vozduhan.AI;

/// <summary>
/// A personal reminder: who, when (UTC) and what about — the text itself is written by the AI at delivery.
/// Status: pending → sent | cancelled | missed. FloodId / Dm: what was already delivered (Dm -1 — no DM with the person).
/// </summary>
public sealed record Reminder(long Id, string? Member, long UserId, string Name, string About, long Due, string Status, string Source,
    long Created, string? Text, int FloodId, int Dm, long Sent);

public sealed partial class BotStore
{
    private void InitReminders()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS reminders(id INTEGER PRIMARY KEY AUTOINCREMENT, member TEXT NULL, user_id INTEGER NOT NULL, name TEXT NOT NULL,
                about TEXT NOT NULL, due INTEGER NOT NULL, status TEXT NOT NULL DEFAULT 'pending', source TEXT NOT NULL, created INTEGER NOT NULL,
                text TEXT NULL, flood_id INTEGER NOT NULL DEFAULT 0, dm INTEGER NOT NULL DEFAULT 0, sent INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS reminders_due ON reminders(status, due);
            """);
    }

    private const string ReminderColumns = "id,member,user_id,name,about,due,status,source,created,text,flood_id,dm,sent";
    private static Reminder ReadReminder(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2),
        r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetString(6), r.GetString(7), r.GetInt64(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt32(10),
        r.GetInt32(11), r.GetInt64(12));

    public long AddReminder(string? member, long userId, string name, string about, long due, string source, long now)
    {
        lock (gate)
        {
            Exec("INSERT INTO reminders(member,user_id,name,about,due,source,created) VALUES($m,$u,$n,$a,$d,$s,$c)",
                ("m", member), ("u", userId), ("n", name), ("a", about), ("d", due), ("s", source), ("c", now));
            return Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
        }
    }
    public Reminder? Reminder(long id)
    {
        lock (gate) return Query($"SELECT {ReminderColumns} FROM reminders WHERE id=$id", ReadReminder, ("id", id)).FirstOrDefault();
    }
    /// <summary>Pending ones first (soonest first), then the latest others.</summary>
    public List<Reminder> Reminders(int limit = 100)
    {
        lock (gate) return Query($"SELECT {ReminderColumns} FROM reminders ORDER BY status<>'pending', CASE WHEN status='pending' THEN due ELSE -due END LIMIT $n",
            ReadReminder, ("n", limit));
    }
    public List<Reminder> DueReminders(long now)
    {
        lock (gate) return Query($"SELECT {ReminderColumns} FROM reminders WHERE status='pending' AND due<=$n ORDER BY due", ReadReminder, ("n", now));
    }
    public int PendingReminders(long userId)
    {
        lock (gate) return Convert.ToInt32(Scalar("SELECT COUNT(*) FROM reminders WHERE status='pending' AND user_id=$u", ("u", userId)));
    }
    public void SetReminderText(long id, string text) { lock (gate) Exec("UPDATE reminders SET text=$t WHERE id=$id", ("id", id), ("t", text)); }
    public void SetReminderFlood(long id, int message) { lock (gate) Exec("UPDATE reminders SET flood_id=$m WHERE id=$id", ("id", id), ("m", message)); }
    public void SetReminderDm(long id, int message) { lock (gate) Exec("UPDATE reminders SET dm=$m WHERE id=$id", ("id", id), ("m", message)); }
    /// <summary>Only a pending reminder changes: sent, missed or cancelled.</summary>
    public bool FinishReminder(long id, string status, long now)
    {
        lock (gate) return Exec("UPDATE reminders SET status=$s, sent=$n WHERE id=$id AND status='pending'", ("id", id), ("s", status), ("n", now)) == 1;
    }
}
