using Microsoft.Data.Sqlite;

namespace Vozduhan.AI;

public sealed partial class BotStore : IDisposable
{
    private readonly SqliteConnection db;
    private readonly object gate = new();
    public BotConfig Config { get; }

    public BotStore(BotConfig config, bool tool = false)
    {
        Config = config;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.DataPath))!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = config.DataPath, DefaultTimeout = 10 }.ToString());
        db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version > 2) throw new InvalidDataException("Database schema is newer than this application");
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
            CREATE TABLE IF NOT EXISTS registry_versions(id INTEGER PRIMARY KEY AUTOINCREMENT, time INTEGER NOT NULL,
                actor INTEGER NOT NULL, source TEXT NOT NULL, summary TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS rdrafts(actor INTEGER PRIMARY KEY, token TEXT UNIQUE NOT NULL,
                expires INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS chat_log(id INTEGER PRIMARY KEY AUTOINCREMENT, time INTEGER NOT NULL, chat INTEGER NOT NULL,
                chat_type TEXT NOT NULL, topic INTEGER NOT NULL, message INTEGER NOT NULL, sender INTEGER NOT NULL,
                direction TEXT NOT NULL, text TEXT NOT NULL, raw TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS chat_log_chat ON chat_log(chat,topic,id);
            CREATE INDEX IF NOT EXISTS chat_log_sender ON chat_log(sender,id);
            CREATE INDEX IF NOT EXISTS chat_log_time ON chat_log(time);
            CREATE TABLE IF NOT EXISTS ai_log(id INTEGER PRIMARY KEY AUTOINCREMENT, time INTEGER NOT NULL, kind TEXT NOT NULL,
                tag TEXT NOT NULL, model TEXT NOT NULL, system TEXT NOT NULL, request TEXT NOT NULL, response TEXT NOT NULL,
                ms INTEGER NOT NULL, error TEXT);
            CREATE INDEX IF NOT EXISTS ai_log_tag ON ai_log(tag);
            CREATE INDEX IF NOT EXISTS ai_log_time ON ai_log(time);
            CREATE TABLE IF NOT EXISTS seen_users(id INTEGER PRIMARY KEY, username TEXT NOT NULL, name TEXT NOT NULL,
                first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, last_chat INTEGER NOT NULL, count INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS web_tokens(hash TEXT PRIMARY KEY, actor INTEGER NOT NULL, kind TEXT NOT NULL,
                expires INTEGER NOT NULL, created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS wakeups(id INTEGER PRIMARY KEY AUTOINCREMENT, deadline INTEGER NOT NULL, due INTEGER NOT NULL,
                reason TEXT NOT NULL, source TEXT NOT NULL, status TEXT NOT NULL, created INTEGER NOT NULL, actor INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS wakeups_due ON wakeups(status,due);
            PRAGMA user_version=2;
            """);
        AddColumn("wakeups", "attempts", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("wakeups", "note", "TEXT NULL");
        AddColumn("wakeups", "next_try", "INTEGER NOT NULL DEFAULT 0");
        if (!tool)
        {
            Exec("UPDATE inbox SET state=0 WHERE state=1");
            Exec("UPDATE wakeups SET status='pending' WHERE status='sending'");
            Exec("UPDATE inbox SET priority=2 WHERE state=0 AND json_valid(payload) AND json_type(payload,'$.callback_query')='object'");
        }
        var binding = $"{config.GroupId}:{config.ChatTopicId}:{config.DeadlinesTopicId}";
        var oldBinding = Get("binding");
        if (oldBinding is not null && oldBinding != binding)
            throw new InvalidDataException("Use a separate database when changing group/topic IDs");
        if (!tool)
        {
            Set("binding", binding);
            Set("registry", Json.Write(config));
        }
        LoadRegistry(tool);
        // Initiative settings edited in the web admin override the config file until reset.
        Config.FileAmbient ??= Config.Ambient;
        if (Get("ambient_settings") is { } saved) Config.Ambient = Json.Read<AmbientSettings>(saved);
        Config.FileAi ??= Config.AiSnapshot();
        if (Get("ai_settings") is { } ai) Config.ApplyAi(Json.Read<AiSettings>(ai));
    }

    private void AddColumn(string table, string column, string definition)
    {
        if (!Query($"PRAGMA table_info({table})", r => r.GetString(1)).Contains(column)) Exec($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
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
    public void Delete(string key) { lock (gate) Exec("DELETE FROM kv WHERE key=$k", ("k", key)); }
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
    public void Retry(Inbox item, Exception? error = null)
    {
        var transient = error is HttpRequestException or OperationCanceledException ||
            error is TelegramError t && (t.Code >= 500 || t.Code is 408 or 409 or 429);
        var permanent = error is TelegramError p && p.Code is >= 400 and < 500 && p.Code is not (408 or 409 or 429);
        var delay = Math.Max(error is TelegramError telegram ? telegram.RetryAfter : 0,
            Math.Min(120, 5 * Math.Pow(2, Math.Min(6, item.Attempts))));
        lock (gate) Exec("UPDATE inbox SET state=$s,attempts=attempts+1,next_at=$n WHERE id=$id",
            ("s", permanent || !transient && item.Attempts >= 4 ? 3 : 0),
            ("n", DateTimeOffset.UtcNow.AddSeconds(delay).ToUnixTimeSeconds()), ("id", item.Id));
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
    public void ForgetMessage(long chat, int id)
    {
        lock (gate) Exec("DELETE FROM messages WHERE chat=$c AND id=$id", ("c", chat), ("id", id));
    }
    public string? Message(long chat, int id)
    {
        lock (gate) return Scalar("SELECT payload FROM messages WHERE chat=$c AND id=$id", ("c", chat), ("id", id)) as string;
    }
    public List<Deadline> AllDeadlines()
    {
        lock (gate) return Query("SELECT payload FROM deadlines ORDER BY date,id", r => Json.Read<Deadline>(r.GetString(0)));
    }
    /// <summary>Replaces only the model-written lead of a deadline; the publisher then edits the card in place.</summary>
    /// <summary>Replaces the published lead without a new version (wake-ups stay); the change is audited.</summary>
    public void SetDeadlineIntro(int id, string intro, long actor, DateTimeOffset now)
    {
        lock (gate)
        {
            var d = AllDeadlines().FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Нет такого дедлайна.");
            using var tx = db.BeginTransaction();
            Exec("UPDATE deadlines SET payload=$p WHERE id=$id", ("p", Json.Write(d with { Intro = intro })), ("id", id));
            Exec("INSERT INTO audit(time,actor,action,payload) VALUES($t,$a,'intro',$p)", ("t", now.ToUnixTimeSeconds()), ("a", actor),
                ("p", Json.Write(new { before = d.Intro, after = intro, deadline = id })));
            tx.Commit();
        }
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
    public Deadline Confirm(long actor, string token, DateTimeOffset now) => Commit(actor, null, token, now);
    /// <summary>Saves a fully resolved draft without the per-editor draft slot (web admin).</summary>
    public Deadline ConfirmDirect(long actor, Draft draft, DateTimeOffset now) => Commit(actor, draft, null, now);
    private Deadline Commit(long actor, Draft? direct, string? token, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!Config.CanEdit(actor)) throw new InvalidOperationException("Редактировать могут только разработчик и староста.");
            using var tx = db.BeginTransaction();
            var draft = direct ?? Draft(actor, token!) ?? throw new InvalidOperationException("Карточка уже обработана, заменена или устарела.");
            if (draft.Missing.Length != 0) throw new InvalidOperationException("Сначала уточни недостающие поля.");
            var all = AllDeadlines();
            var old = draft.Action == "create" ? null : all.FirstOrDefault(d => d.Id == draft.TargetId);
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
                var text = Deadlines.ChangeNews(Config, d, old);
                PutDelivery($"change:{id}:{d.Version}", Config.DeadlinesTopicId, text, now.ToUnixTimeSeconds(), now.AddDays(1).ToUnixTimeSeconds());
            }
            if (token is not null) Discard(actor, token);
            RebuildMorning(d.Date, now);
            if (old is not null && old.Date != d.Date) RebuildMorning(old.Date, now);
            tx.Commit();
            return d;
        }
    }
    private void RebuildMorning(string date, DateTimeOffset now)
    {
        // With a summary topic the morning summary lists the day's submissions; no separate post in the deadlines topic.
        if (Config.SummaryTopicId > 0) { Exec("UPDATE deliveries SET state=2 WHERE key=$k AND state=0", ("k", "morning:" + date)); return; }
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
        var text = $"📅 <b>{DateOnly.Parse(date):dd.MM.yyyy}</b>\n{Deadlines.Lead(intro, "Сегодня сдаём:")}\n\n" +
            (items.Length == 0 ? "Все сдачи из этого поста отменены или перенесены. Смотрите обновлённые карточки." :
                string.Join("\n", items.Select(d => Deadlines.FactsHtml(Config, d))));
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
    public List<Delivery> PendingDeliveries()
    {
        lock (gate) return Query("SELECT key,chat,topic,text,due,expires,attempts FROM deliveries WHERE state=0 ORDER BY due LIMIT 100",
            r => new Delivery(r.GetString(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetInt32(6)));
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
            Exec("DELETE FROM rdrafts WHERE expires<$s", ("s", now.ToUnixTimeSeconds()));
            Exec("DELETE FROM web_tokens WHERE expires<$s", ("s", now.ToUnixTimeSeconds()));
            Exec("DELETE FROM chat_log WHERE time<$s", ("s", now.AddDays(-Config.LogDays).ToUnixTimeSeconds()));
            Exec("DELETE FROM ai_log WHERE time<$s", ("s", now.AddDays(-Config.AiLogDays).ToUnixTimeSeconds()));
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
