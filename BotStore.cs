using Microsoft.Data.Sqlite;

namespace Vozduhan.AI;

public sealed class BotStore : IDisposable
{
    private readonly SqliteConnection db;
    private readonly object gate = new();
    public BotConfig Config { get; }

    public BotStore(BotConfig config)
    {
        Config = config;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.DataPath))!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = config.DataPath, DefaultTimeout = 10 }.ToString());
        db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version > 1) throw new InvalidDataException("Database schema is newer than this application");
        Exec("""
            CREATE TABLE IF NOT EXISTS kv(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS inbox(id INTEGER PRIMARY KEY, actor INTEGER NOT NULL, payload TEXT NOT NULL,
                priority INTEGER NOT NULL, state INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0,
                next_at INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS messages(chat INTEGER NOT NULL, topic INTEGER NOT NULL, id INTEGER NOT NULL,
                sent INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(chat,id));
            CREATE INDEX IF NOT EXISTS messages_topic ON messages(chat,topic,id);
            CREATE TABLE IF NOT EXISTS deadlines(id INTEGER PRIMARY KEY AUTOINCREMENT, version INTEGER NOT NULL,
                date TEXT NOT NULL, status TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS drafts(actor INTEGER PRIMARY KEY, token TEXT UNIQUE NOT NULL,
                expires INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS deliveries(key TEXT PRIMARY KEY, chat INTEGER NOT NULL, topic INTEGER NOT NULL,
                text TEXT NOT NULL, due INTEGER NOT NULL, expires INTEGER NOT NULL, state INTEGER NOT NULL DEFAULT 0,
                attempts INTEGER NOT NULL DEFAULT 0, next_at INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS posts(key TEXT PRIMARY KEY, message INTEGER NOT NULL, text TEXT NOT NULL,
                pinned INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY AUTOINCREMENT, time INTEGER NOT NULL,
                actor INTEGER NOT NULL, action TEXT NOT NULL, payload TEXT NOT NULL);
            PRAGMA user_version=1;
            """);
        Exec("UPDATE inbox SET state=0 WHERE state=1");
        var binding = $"{config.GroupId}:{config.ChatTopicId}:{config.DeadlinesTopicId}";
        var oldBinding = Get("binding");
        if (oldBinding is not null && oldBinding != binding)
            throw new InvalidDataException("Use a separate database when changing group/topic IDs");
        Set("binding", binding);
        Set("registry", Json.Write(config));
    }

    private SqliteCommand Command(string sql, params (string Key, object? Value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue("$" + key, value ?? DBNull.Value);
        return cmd;
    }
    private int Exec(string sql, params (string Key, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteNonQuery();
    }
    private object? Scalar(string sql, params (string Key, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteScalar();
    }
    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Key, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        using var reader = cmd.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(read(reader));
        return result;
    }
    public string? Get(string key) { lock (gate) return Scalar("SELECT value FROM kv WHERE key=$k", ("k", key)) as string; }
    public void Set(string key, string value)
    {
        lock (gate) Exec("INSERT INTO kv VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("k", key), ("v", value));
    }
    public void Ingest(long id, long actor, string? payload, int priority)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            if (payload is not null) Exec("INSERT OR IGNORE INTO inbox(id,actor,payload,priority) VALUES($id,$a,$p,$r)",
                ("id", id), ("a", actor), ("p", payload), ("r", priority));
            Set("offset", (id + 1).ToString());
            tx.Commit();
        }
    }
    public Inbox? Claim(int priority = 0)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var item = Query("""
                SELECT id,payload,attempts,actor FROM inbox i WHERE state=0 AND next_at<=$now AND priority=$priority
                AND NOT EXISTS(SELECT 1 FROM inbox b WHERE b.actor=i.actor AND b.priority=i.priority AND b.id<i.id AND b.state IN (0,1))
                ORDER BY priority DESC,id LIMIT 1
                """, r => new Inbox(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3)), ("now", now), ("priority", priority)).FirstOrDefault();
            if (item is not null) Exec("UPDATE inbox SET state=1 WHERE id=$id", ("id", item.Id));
            return item;
        }
    }
    public void Finish(long id) { lock (gate) Exec("UPDATE inbox SET state=2,payload='{}' WHERE id=$id", ("id", id)); }
    public void Retry(Inbox item)
    {
        lock (gate) Exec("UPDATE inbox SET state=$s,attempts=attempts+1,next_at=$n WHERE id=$id",
            ("s", item.Attempts >= 4 ? 3 : 0), ("n", DateTimeOffset.UtcNow.AddSeconds(Math.Min(120, 5 * Math.Pow(2, item.Attempts))).ToUnixTimeSeconds()), ("id", item.Id));
    }
    public void Remember(long chat, int topic, int id, long sent, string payload)
    {
        lock (gate) Exec("""
            INSERT INTO messages VALUES($c,$t,$id,$s,$p) ON CONFLICT(chat,id)
            DO UPDATE SET topic=$t,sent=$s,payload=$p
            """, ("c", chat), ("t", topic), ("id", id), ("s", sent), ("p", payload));
    }
    public List<string> History(long chat, int topic, int through, int count)
    {
        lock (gate) return Query("SELECT payload FROM messages WHERE chat=$c AND topic=$t AND id<=$id ORDER BY id DESC LIMIT $n",
            r => r.GetString(0), ("c", chat), ("t", topic), ("id", through), ("n", count)).AsEnumerable().Reverse().ToList();
    }
    public void RememberReference(long chat, int topic, int id, long sent, string payload)
    {
        lock (gate) Exec("INSERT OR IGNORE INTO messages VALUES($c,$t,$id,$s,$p)",
            ("c", chat), ("t", topic), ("id", id), ("s", sent), ("p", payload));
    }
    public bool ChatBudget(long actor, DateTimeOffset now)
    {
        lock (gate)
        {
            var key = $"chat_budget:{Config.LocalNow(now):yyyy-MM-dd}:{actor}";
            var used = int.TryParse(Get(key), out var n) ? n : 0;
            if (used >= Config.MaxDailyChatRequestsPerUser) return false;
            Set(key, (used + 1).ToString());
            return true;
        }
    }
    public string? Message(long chat, int id)
    {
        lock (gate) return Scalar("SELECT payload FROM messages WHERE chat=$c AND id=$id", ("c", chat), ("id", id)) as string;
    }
    public List<Deadline> AllDeadlines()
    {
        lock (gate) return Query("SELECT payload FROM deadlines ORDER BY date,id", r => Json.Read<Deadline>(r.GetString(0)));
    }
    public Draft? Draft(long actor, string? token = null)
    {
        lock (gate)
        {
            var text = Scalar("SELECT payload FROM drafts WHERE actor=$a AND expires>$now AND ($token IS NULL OR token=$token)",
                ("a", actor), ("now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("token", token)) as string;
            return text is null ? null : Json.Read<Draft>(text);
        }
    }
    public void SaveDraft(Draft draft)
    {
        lock (gate) Exec("INSERT OR REPLACE INTO drafts VALUES($a,$t,$e,$p)", ("a", draft.ActorId), ("t", draft.Token),
            ("e", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()), ("p", Json.Write(draft)));
    }
    public void Discard(long actor, string token)
    {
        lock (gate) Exec("DELETE FROM drafts WHERE actor=$a AND token=$t", ("a", actor), ("t", token));
    }
    public Deadline Confirm(long actor, string token, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!Config.CanEdit(actor)) throw new InvalidOperationException("Редактировать могут только разработчик и староста.");
            using var tx = db.BeginTransaction();
            var draft = Draft(actor, token) ?? throw new InvalidOperationException("Карточка уже обработана, заменена или устарела.");
            if (draft.Missing.Length != 0) throw new InvalidOperationException("Сначала уточни недостающие поля.");
            var all = AllDeadlines();
            var old = all.FirstOrDefault(d => d.Id == draft.TargetId);
            if (draft.Action != "create" && (old is null || old.Version != draft.ExpectedVersion))
                throw new InvalidOperationException("Этот дедлайн уже изменился. Открой его заново через /edit.");
            if (draft.Action != "cancel" && Config.Utc(DateOnly.Parse(draft.Date!).ToDateTime(TimeOnly.Parse(draft.End!))) <= now)
                throw new InvalidOperationException("Время сдачи уже прошло. Создай новую карточку.");
            var duplicate = all.Any(d => d.Id != draft.TargetId && d.Status == "active" && d.SubjectId == draft.SubjectId &&
                d.Date == draft.Date && d.Period == draft.Period && string.Equals(d.Work, draft.Work, StringComparison.OrdinalIgnoreCase));
            if (draft.Action != "cancel" && duplicate) throw new InvalidOperationException("Такой дедлайн уже существует: смотри /deadlines.");
            var id = old?.Id ?? 0;
            if (id == 0)
            {
                Exec("INSERT INTO deadlines(version,date,status,payload) VALUES(1,$d,'active','')", ("d", draft.Date));
                id = Convert.ToInt32(Scalar("SELECT last_insert_rowid()"));
            }
            var d = new Deadline(id, (old?.Version ?? 0) + 1, draft.SubjectId!, draft.Work!, draft.Date!, draft.Period!.Value,
                draft.Start!, draft.End!, draft.Room!, draft.Action == "cancel" ? "cancelled" : "active", actor,
                draft.Source, draft.Intro, draft.MorningIntro, draft.Note);
            Exec("UPDATE deadlines SET version=$v,date=$date,status=$s,payload=$p WHERE id=$id", ("v", d.Version),
                ("date", d.Date), ("s", d.Status), ("p", Json.Write(d)), ("id", d.Id));
            Exec("INSERT INTO audit(time,actor,action,payload) VALUES($t,$a,$op,$p)", ("t", now.ToUnixTimeSeconds()),
                ("a", actor), ("op", draft.Action), ("p", Json.Write(new { before = old, after = d })));
            if (old is not null)
            {
                Exec("UPDATE deliveries SET state=2 WHERE key LIKE $key AND state=0", ("key", $"change:{id}:%"));
                var text = (string.IsNullOrWhiteSpace(d.Intro) ? "Гайс, по сдаче новости." : d.Intro) + "\n\n" +
                    (d.Status == "cancelled" ? "❌ Отмена\n" : "🔄 Изменение\n") + Deadlines.Facts(Config, d) +
                    (string.IsNullOrWhiteSpace(d.Note) ? "" : "\nКомментарий редактора: " + d.Note) +
                    $"\n\nРаньше: {old.Date}, {old.Period}-я пара, {old.Start}–{old.End}, {old.Room}.";
                PutDelivery($"change:{id}:{d.Version}", Config.DeadlinesTopicId, text, now.ToUnixTimeSeconds(), now.AddDays(1).ToUnixTimeSeconds());
            }
            Discard(actor, token);
            RebuildMorning(d.Date, now);
            if (old is not null && old.Date != d.Date) RebuildMorning(old.Date, now);
            tx.Commit();
            return d;
        }
    }
    private void RebuildMorning(string date, DateTimeOffset now)
    {
        var items = AllDeadlines().Where(d => d.Status == "active" && d.Date == date).OrderBy(d => d.Start).ThenBy(d => d.Id).ToArray();
        var key = "morning:" + date;
        var hadDelivery = Scalar("SELECT key FROM deliveries WHERE key=$k", ("k", key)) is not null;
        var hadPost = Post(key) is not null;
        if (items.Length == 0 && !hadPost)
        {
            if (hadDelivery) Exec("UPDATE deliveries SET state=2 WHERE key=$k", ("k", key));
            return;
        }
        var intro = items.FirstOrDefault()?.MorningIntro;
        if (string.IsNullOrWhiteSpace(intro)) intro = "Гайс, сегодня у нас:";
        var text = $"📅 {DateOnly.Parse(date):dd.MM.yyyy}\n{intro}\n\n" +
            (items.Length == 0 ? "Все сдачи из этого поста отменены или перенесены. Смотрите обновлённые карточки." :
                string.Join("\n\n", items.Select(d => Deadlines.Facts(Config, d))));
        var day = DateOnly.Parse(date);
        var due = Config.Utc(day.ToDateTime(TimeOnly.Parse(Config.MorningTime))).ToUnixTimeSeconds();
        var expires = Config.Utc(day.AddDays(1).ToDateTime(TimeOnly.MinValue)).ToUnixTimeSeconds();
        if (hadPost) { due = now.ToUnixTimeSeconds(); expires = now.AddDays(1).ToUnixTimeSeconds(); }
        PutDelivery(key, Config.DeadlinesTopicId, text, due, expires);
    }
    private void PutDelivery(string key, int topic, string text, long due, long expires)
    {
        Exec("""
            INSERT INTO deliveries(key,chat,topic,text,due,expires) VALUES($k,$c,$t,$text,$due,$exp)
            ON CONFLICT(key) DO UPDATE SET text=$text,due=$due,expires=$exp,
                state=CASE WHEN deliveries.text<>$text OR deliveries.state=2 THEN 0 ELSE deliveries.state END,
                attempts=CASE WHEN deliveries.text<>$text THEN 0 ELSE deliveries.attempts END,next_at=0
            """, ("k", key), ("c", Config.GroupId), ("t", topic), ("text", text), ("due", due), ("exp", expires));
    }
    public List<Delivery> Due(DateTimeOffset now)
    {
        lock (gate)
        {
            Exec("UPDATE deliveries SET state=2 WHERE state=0 AND expires<=$now", ("now", now.ToUnixTimeSeconds()));
            return Query("SELECT key,chat,topic,text,due,expires,attempts FROM deliveries WHERE state=0 AND due<=$now AND next_at<=$now ORDER BY due LIMIT 10",
                r => new Delivery(r.GetString(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetInt32(6)),
                ("now", now.ToUnixTimeSeconds()));
        }
    }
    public bool CurrentDelivery(Delivery d)
    {
        lock (gate) return Scalar("SELECT text FROM deliveries WHERE key=$k AND state=0", ("k", d.Key)) as string == d.Text;
    }
    public void Delivered(Delivery d)
    {
        lock (gate) Exec("UPDATE deliveries SET state=1 WHERE key=$k AND text=$t", ("k", d.Key), ("t", d.Text));
    }
    public void DeliveryFailed(Delivery d, int retryAfter = 0)
    {
        lock (gate) Exec("UPDATE deliveries SET attempts=attempts+1,next_at=$n WHERE key=$k AND text=$t",
            ("n", DateTimeOffset.UtcNow.AddSeconds(Math.Max(retryAfter, Math.Min(900, 5 * Math.Pow(2, Math.Min(8, d.Attempts))))).ToUnixTimeSeconds()),
            ("k", d.Key), ("t", d.Text));
    }
    public SavedPost? Post(string key)
    {
        lock (gate) return Query("SELECT key,message,text,pinned FROM posts WHERE key=$k",
            r => new SavedPost(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3) != 0), ("k", key)).FirstOrDefault();
    }
    public List<SavedPost> Posts(string prefix)
    {
        lock (gate) return Query("SELECT key,message,text,pinned FROM posts WHERE key LIKE $k",
            r => new SavedPost(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3) != 0), ("k", prefix + "%"));
    }
    public void SavePost(string key, int id, string text)
    {
        lock (gate) Exec("INSERT INTO posts(key,message,text) VALUES($k,$id,$t) ON CONFLICT(key) DO UPDATE SET message=$id,text=$t",
            ("k", key), ("id", id), ("t", text));
    }
    public void ForgetPost(string key) { lock (gate) Exec("DELETE FROM posts WHERE key=$k", ("k", key)); }
    public void Pinned(string key, bool value) { lock (gate) Exec("UPDATE posts SET pinned=$p WHERE key=$k", ("p", value ? 1 : 0), ("k", key)); }
    public void Maintain(DateTimeOffset now)
    {
        lock (gate)
        {
            Exec("DELETE FROM messages WHERE sent<$s", ("s", now.AddDays(-Config.HistoryDays).ToUnixTimeSeconds()));
            Exec("DELETE FROM inbox WHERE state IN (2,3) AND id<(SELECT COALESCE(MAX(id),0)-10000 FROM inbox)");
            Exec("DELETE FROM drafts WHERE expires<$s", ("s", now.ToUnixTimeSeconds()));
            var day = now.ToString("yyyy-MM-dd");
            if (Get("backup_day") == day) return;
            var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Config.DataPath))!, "backups");
            Directory.CreateDirectory(folder);
            using var target = new SqliteConnection($"Data Source={Path.Combine(folder, day + ".db")}");
            target.Open();
            db.BackupDatabase(target);
            Set("backup_day", day);
            foreach (var file in Directory.GetFiles(folder, "*.db").OrderByDescending(Path.GetFileName).Skip(7)) File.Delete(file);
        }
    }
    public void Dispose() { lock (gate) db.Dispose(); }
}
