using Microsoft.Data.Sqlite;

namespace Vozduhan.AI;

/// <summary>A change of the developer's note; null text clears it.</summary>
public sealed record DevNoteUpdate(string? Text);

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
            CREATE TABLE IF NOT EXISTS ai_usage(id INTEGER PRIMARY KEY AUTOINCREMENT, time INTEGER NOT NULL, kind TEXT NOT NULL, tag TEXT NOT NULL,
                requested_model TEXT NOT NULL, model TEXT NULL, attempt INTEGER NOT NULL, prompt_tokens INTEGER NOT NULL, cached_tokens INTEGER NOT NULL,
                completion_tokens INTEGER NOT NULL, reasoning_tokens INTEGER NOT NULL, cost REAL NULL, finish TEXT NULL, ms INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ai_usage_time ON ai_usage(time);
            CREATE INDEX IF NOT EXISTS ai_usage_kind ON ai_usage(kind,time);
            CREATE TABLE IF NOT EXISTS media_digest(file_unique_id TEXT PRIMARY KEY, kind TEXT NOT NULL, summary TEXT NOT NULL,
                transcript TEXT NULL, created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS pending_slots(id INTEGER PRIMARY KEY AUTOINCREMENT, actor INTEGER NOT NULL, chat INTEGER NOT NULL,
                topic INTEGER NOT NULL, capability TEXT NOT NULL, args TEXT NOT NULL, options TEXT NOT NULL, question TEXT NOT NULL,
                created INTEGER NOT NULL, expires INTEGER NOT NULL, state TEXT NOT NULL, source TEXT NOT NULL, payload TEXT NOT NULL,
                message INTEGER NOT NULL DEFAULT 0, choice INTEGER NULL);
            CREATE INDEX IF NOT EXISTS pending_slots_actor ON pending_slots(actor,chat,state);
            CREATE UNIQUE INDEX IF NOT EXISTS pending_slots_source ON pending_slots(source);
            PRAGMA user_version=2;
            """);
        AddColumn("wakeups", "attempts", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("wakeups", "note", "TEXT NULL");
        AddColumn("wakeups", "next_try", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("deadlines", "dev_note", "TEXT NULL");
        AddColumn("ai_usage", "call_id", "TEXT NULL");
        AddColumn("ai_usage", "discard", "TEXT NULL");
        AddColumn("inbox", "chat", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("messages", "arrived", "INTEGER NOT NULL DEFAULT 0");
        Exec("CREATE INDEX IF NOT EXISTS inbox_chat ON inbox(chat,state)");
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
        if (Get("feature_settings") is { } features) Config.Features = Json.Read<FeatureSettings>(features);
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
    public void Ingest(long id, long actor, string? payload, int priority, long chat = 0)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            if (payload is not null) Exec("INSERT OR IGNORE INTO inbox(id,actor,payload,priority,chat) VALUES($id,$a,$p,$r,$c)",
                ("id", id), ("a", actor), ("p", payload), ("r", priority), ("c", chat));
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
                AND NOT EXISTS(SELECT 1 FROM inbox b WHERE b.priority=i.priority AND b.id<>i.id AND (b.id<i.id OR b.state=1) AND b.state IN (0,1)
                    AND CASE WHEN i.chat<>0 THEN b.chat=i.chat ELSE b.actor=i.actor END)
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
    /// <summary>arrived: when the bot received a new message, unix ms; 0 keeps the stored value.</summary>
    public void Remember(long chat, int topic, int id, long sent, string payload, long arrived = 0)
    {
        lock (gate) Exec("""
            INSERT INTO messages(chat,topic,id,sent,payload,arrived) VALUES($c,$t,$id,$s,$p,$a) ON CONFLICT(chat,id)
            DO UPDATE SET topic=$t,sent=$s,payload=$p,arrived=CASE WHEN $a>0 THEN $a ELSE arrived END
            """, ("c", chat), ("t", topic), ("id", id), ("s", sent), ("p", payload), ("a", arrived));
    }
    /// <summary>Messages of one author in a conversation strictly between two IDs, oldest first, with arrival in unix ms.</summary>
    public List<(int Id, long Arrived, string Payload)> Around(long chat, int topic, long actor, int afterId = int.MinValue, int beforeId = int.MaxValue)
    {
        lock (gate) return Query("""
            SELECT id,CASE WHEN arrived>0 THEN arrived ELSE sent*1000 END,payload FROM messages
            WHERE chat=$c AND topic=$t AND id>$a AND id<$b AND json_extract(payload,'$.from.id')=$u ORDER BY id
            """, r => (r.GetInt32(0), r.GetInt64(1), r.GetString(2)), ("c", chat), ("t", topic), ("a", afterId), ("b", beforeId), ("u", actor));
    }
    public long? Arrived(long chat, int id)
    {
        lock (gate) return Scalar("SELECT CASE WHEN arrived>0 THEN arrived ELSE sent*1000 END FROM messages WHERE chat=$c AND id=$id", ("c", chat), ("id", id)) as long?;
    }
    public List<string> History(long chat, int topic, int through, int count)
    {
        lock (gate) return Query("SELECT payload FROM messages WHERE chat=$c AND topic=$t AND id<=$id ORDER BY id DESC LIMIT $n",
            r => r.GetString(0), ("c", chat), ("t", topic), ("id", through), ("n", count)).AsEnumerable().Reverse().ToList();
    }
    public void RememberReference(long chat, int topic, int id, long sent, string payload)
    {
        lock (gate) Exec("INSERT OR IGNORE INTO messages(chat,topic,id,sent,payload) VALUES($c,$t,$id,$s,$p)",
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
    public bool DigestBudget(DateTimeOffset now, int limit)
    {
        lock (gate)
        {
            var key = $"digest_budget:{Config.LocalNow(now):yyyy-MM-dd}";
            var used = int.TryParse(Get(key), out var n) ? n : 0;
            if (used >= limit) return false;
            Set(key, (used + 1).ToString());
            return true;
        }
    }
    public (string Summary, string? Transcript)? Digest(string uniqueId)
    {
        lock (gate) return Query("SELECT summary,transcript FROM media_digest WHERE file_unique_id=$id",
            r => ((string, string?)?)(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)), ("id", uniqueId)).FirstOrDefault();
    }
    public void SaveDigest(string uniqueId, string kind, string summary, string? transcript)
    {
        lock (gate) Exec("INSERT OR REPLACE INTO media_digest VALUES($id,$k,$s,$t,$c)", ("id", uniqueId), ("k", kind), ("s", summary),
            ("t", transcript), ("c", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
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
    // ---------- Developer's private note: kept outside Deadline/Draft so no serialized record can carry it ----------

    public string? DevNote(int id)
    {
        lock (gate) return Scalar("SELECT dev_note FROM deadlines WHERE id=$id", ("id", id)) as string;
    }
    public Dictionary<int, string> DevNotes()
    {
        lock (gate) return Query("SELECT id,dev_note FROM deadlines WHERE dev_note IS NOT NULL", r => (r.GetInt32(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
    }
    public void SetDevNote(int id, string? text, long actor, DateTimeOffset now)
    {
        lock (gate)
        {
            var note = CheckDevNote(text, actor);
            if (!AllDeadlines().Any(d => d.Id == id)) throw new InvalidOperationException("Нет такого дедлайна.");
            using var tx = db.BeginTransaction();
            WriteDevNote(id, note, actor, now);
            tx.Commit();
        }
    }
    public const int DevNoteMax = 600;
    private string? CheckDevNote(string? text, long actor)
    {
        if (actor != Config.OwnerId) throw new InvalidOperationException("Заметку разработчика может менять только разработчик.");
        var note = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (note?.Length > DevNoteMax) throw new InvalidOperationException($"Заметка разработчика длиннее {DevNoteMax} символов.");
        return note;
    }
    private void WriteDevNote(int id, string? note, long actor, DateTimeOffset now)
    {
        var before = Scalar("SELECT dev_note FROM deadlines WHERE id=$id", ("id", id)) as string;
        Exec("UPDATE deadlines SET dev_note=$n WHERE id=$id", ("n", note), ("id", id));
        Exec("INSERT INTO audit(time,actor,action,payload) VALUES($t,$a,'devnote',$p)", ("t", now.ToUnixTimeSeconds()), ("a", actor),
            ("p", Json.Write(new { deadline = id, before, after = note })));
    }

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
    public Deadline ConfirmDirect(long actor, Draft draft, DateTimeOffset now, DevNoteUpdate? devNote = null) => Commit(actor, draft, null, now, devNote);
    private Deadline Commit(long actor, Draft? direct, string? token, DateTimeOffset now, DevNoteUpdate? devNote = null)
    {
        lock (gate)
        {
            if (!Config.CanEdit(actor)) throw new InvalidOperationException("Редактировать могут только разработчик и староста.");
            var note = devNote is null ? null : CheckDevNote(devNote.Text, actor);
            using var tx = db.BeginTransaction();
            var draft = direct ?? Draft(actor, token!) ?? throw new InvalidOperationException("Карточка уже обработана, заменена или устарела.");
            if (draft.Missing.Length != 0) throw new InvalidOperationException("Сначала уточни недостающие поля.");
            var all = AllDeadlines();
            var old = draft.Action == "create" ? null : all.FirstOrDefault(d => d.Id == draft.TargetId && d.Status != "deleted");
            if (draft.Action != "create" && (old is null || old.Version != draft.ExpectedVersion))
                throw new InvalidOperationException(Config.Features.Router ? "Этот дедлайн уже изменился — открой его заново." : "Этот дедлайн уже изменился. Открой его заново через /edit.");
            if (draft.Action != "cancel" && Config.Utc(DateOnly.Parse(draft.Date!).ToDateTime(TimeOnly.Parse(draft.End!))) <= now)
                throw new InvalidOperationException("Время сдачи уже прошло. Создай новую карточку.");
            var duplicate = all.Any(d => d.Id != draft.TargetId && d.Status == "active" && d.SubjectId == draft.SubjectId &&
                d.Date == draft.Date && d.Period == draft.Period && string.Equals(d.Work, draft.Work, StringComparison.OrdinalIgnoreCase));
            if (draft.Action != "cancel" && duplicate) throw new InvalidOperationException(Config.Features.Router ? "Такой дедлайн уже существует: он есть в списке дедлайнов." : "Такой дедлайн уже существует: смотри /deadlines.");
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
            if (devNote is not null) WriteDevNote(d.Id, note, actor, now);
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
    /// <summary>Hides a mistaken deadline everywhere; the row and its ID stay, the publisher removes its posts.</summary>
    public Deadline DeleteDeadline(long actor, int id, int expectedVersion, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!Config.CanEdit(actor)) throw new InvalidOperationException("Удалять могут только разработчик и староста.");
            var old = AllDeadlines().FirstOrDefault(d => d.Id == id && d.Status != "deleted") ?? throw new InvalidOperationException("Нет такого дедлайна.");
            if (old.Version != expectedVersion) throw new InvalidOperationException("Этот дедлайн уже изменился. Обнови и повтори.");
            var d = old with { Version = old.Version + 1, Status = "deleted", EditorId = actor };
            using var tx = db.BeginTransaction();
            Exec("UPDATE deadlines SET version=$v,status=$s,payload=$p WHERE id=$id", ("v", d.Version), ("s", d.Status), ("p", Json.Write(d)), ("id", id));
            Exec("INSERT INTO audit(time,actor,action,payload) VALUES($t,$a,'delete',$p)", ("t", now.ToUnixTimeSeconds()), ("a", actor),
                ("p", Json.Write(new { before = old })));
            Exec("UPDATE deliveries SET state=2 WHERE key LIKE $key AND state=0", ("key", $"change:{id}:%"));
            DropPendingWakeups(id);
            RebuildMorning(d.Date, now);
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
    public const int DigestDays = 180;
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
            Exec("DELETE FROM ai_usage WHERE time<$s", ("s", now.AddDays(-UsageDays).ToUnixTimeSeconds()));
            Exec("DELETE FROM media_digest WHERE created<$s", ("s", now.AddDays(-DigestDays).ToUnixTimeSeconds()));
            // Router decisions are kept two days for debugging (kv "route:{chat}:{message}" with an expiry inside).
            Exec("DELETE FROM kv WHERE key LIKE 'route:%' AND (CASE WHEN json_valid(value) THEN COALESCE(json_extract(value,'$.expires'),0) ELSE 0 END)<$s",
                ("s", now.ToUnixTimeSeconds()));
            // Unanswered clarifications: marked, not deleted (the rows stay as history).
            Exec("UPDATE pending_slots SET state='expired' WHERE state='active' AND expires<$s", ("s", now.ToUnixTimeSeconds()));
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
