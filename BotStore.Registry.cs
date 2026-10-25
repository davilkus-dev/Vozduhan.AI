using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vozduhan.AI;

public sealed record LogRow(long Id, long Time, long Chat, string ChatType, int Topic, int Message, long Sender,
    string Direction, string Text, string? Username, string? Name);
public sealed record ChatSummary(long Chat, string ChatType, int Topic, long Count, long Last, string Title);
public sealed record SeenUser(long Id, string Username, string Name, long FirstSeen, long LastSeen, long LastChat, long Count);
public sealed record AiRow(long Id, long Time, string Kind, string Tag, string Model, string System, string Request, string Response, long Ms, string? Error);
/// <summary>A moment when the bot wakes up to push the group about a deadline. source: ai / manual.</summary>
public sealed record Wakeup(long Id, int Deadline, long Due, string Reason, string Source, string Status, long Created);
public sealed class RegistryConflict(string message) : InvalidOperationException(message);

public sealed partial class BotStore
{
    // ---------- Registry ----------

    private void LoadRegistry(bool tool)
    {
        lock (gate)
        {
            var latest = Query("SELECT id,payload FROM registry_versions ORDER BY id DESC LIMIT 1",
                r => (Id: r.GetInt32(0), Payload: r.GetString(1))).FirstOrDefault();
            var imported = int.TryParse(Get("registry_seed_version"), out var v) ? v : 0;
            Registry? current = latest.Payload is null ? null : Json.Read<Registry>(latest.Payload).Normalize();
            if (tool || (current is not null && Config.RegistryVersion <= imported))
            {
                Config.Use(current ?? Config.Seed());
                return;
            }
            var seed = Config.Seed(current);
            seed.Validate(Config.OwnerId);
            var lines = current is null ? ["Первичный импорт из конфига"] : RegistryOps.Diff(current, seed);
            var summary = current is null ? "Импорт из конфига" : $"Импорт из конфига (registryVersion {Config.RegistryVersion}): {lines.Count} изм.";
            using var tx = db.BeginTransaction();
            Exec("INSERT INTO registry_versions(time,actor,source,summary,payload) VALUES($t,0,'config',$s,$p)",
                ("t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("s", summary + "\n" + string.Join("\n", lines.Take(60))), ("p", Json.Write(seed)));
            Set("registry_seed_version", Config.RegistryVersion.ToString());
            tx.Commit();
            Config.Use(seed);
        }
    }

    public int RegistryVersion()
    {
        lock (gate) return Convert.ToInt32(Scalar("SELECT COALESCE(MAX(id),0) FROM registry_versions"));
    }

    /// <summary>Atomically applies an editor change: validate, check head permissions, store a version, swap the live registry.</summary>
    public (int Version, List<string> Lines) CommitRegistry(long actor, string source, int? expectedVersion,
        Func<Registry, (Registry Next, List<string> Lines)> change)
    {
        lock (gate)
        {
            if (actor != 0 && !Config.CanEdit(actor)) throw new InvalidOperationException("Менять реестр могут только разработчик и староста.");
            var version = RegistryVersion();
            if (expectedVersion is not null && expectedVersion != version)
                throw new RegistryConflict("Реестр уже изменили (версия " + version + "). Обнови страницу и повтори.");
            var current = Config.R;
            var (next, lines) = change(current.Clone());
            next.Normalize();
            try { next.Validate(Config.OwnerId); }
            catch (InvalidDataException e) { throw new InvalidOperationException(e.Message); }
            RegistryOps.CheckPermissions(current, next, actor == Config.OwnerId);
            if (lines.Count == 0 || Json.Write(next) == Json.Write(current)) throw new InvalidOperationException("Нет изменений.");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var tx = db.BeginTransaction();
            Exec("INSERT INTO registry_versions(time,actor,source,summary,payload) VALUES($t,$a,$s,$sum,$p)",
                ("t", now), ("a", actor), ("s", source), ("sum", string.Join("\n", lines.Take(80))), ("p", Json.Write(next)));
            var id = Convert.ToInt32(Scalar("SELECT last_insert_rowid()"));
            Exec("INSERT INTO audit(time,actor,action,payload) VALUES($t,$a,'registry',$p)", ("t", now), ("a", actor),
                ("p", Json.Write(new { version = id, source, lines })));
            tx.Commit();
            Config.Use(next);
            return (id, lines);
        }
    }

    public List<RegistryVersionInfo> RegistryHistory(int limit = 50)
    {
        lock (gate) return Query("SELECT id,time,actor,source,summary FROM registry_versions ORDER BY id DESC LIMIT $n",
            r => new RegistryVersionInfo(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4)), ("n", limit));
    }
    public Registry? RegistryAt(int version)
    {
        lock (gate) return Scalar("SELECT payload FROM registry_versions WHERE id=$id", ("id", version)) is string s ? Json.Read<Registry>(s).Normalize() : null;
    }
    public int? PreviousRegistryVersion()
    {
        lock (gate) return Scalar("SELECT id FROM registry_versions ORDER BY id DESC LIMIT 1 OFFSET 1") is long id ? (int)id : null;
    }

    public void SaveRegistryDraft(RegistryDraft draft)
    {
        lock (gate) Exec("INSERT OR REPLACE INTO rdrafts VALUES($a,$t,$e,$p)", ("a", draft.ActorId), ("t", draft.Token),
            ("e", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()), ("p", Json.Write(draft)));
    }
    public RegistryDraft? RegistryDraft(long actor, string? token = null)
    {
        lock (gate)
        {
            var text = Scalar("SELECT payload FROM rdrafts WHERE actor=$a AND expires>$now AND ($token IS NULL OR token=$token)",
                ("a", actor), ("now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("token", token)) as string;
            return text is null ? null : Json.Read<RegistryDraft>(text);
        }
    }
    public void DiscardRegistryDraft(long actor, string? token = null)
    {
        lock (gate) Exec("DELETE FROM rdrafts WHERE actor=$a AND ($t IS NULL OR token=$t)", ("a", actor), ("t", token));
    }
    /// <summary>Applies a confirmed Telegram draft once. Operations are re-applied to the registry as it is now.</summary>
    public (int Version, List<string> Lines) ConfirmRegistryDraft(long actor, string token)
    {
        lock (gate)
        {
            var draft = RegistryDraft(actor, token) ?? throw new InvalidOperationException("Карточка уже обработана, заменена или устарела.");
            (int, List<string>) result;
            if (draft.Kind == "restore")
            {
                var target = RegistryAt(draft.RestoreVersion ?? 0) ?? throw new InvalidOperationException("Нет такой версии реестра.");
                result = CommitRegistry(actor, $"telegram: откат к версии {draft.RestoreVersion}", draft.BaseVersion,
                    current => (target, RegistryOps.Diff(current, target)));
            }
            else
            {
                var ops = draft.Operations.Select(o => JsonDocument.Parse(o).RootElement.Clone()).ToArray();
                result = CommitRegistry(actor, "telegram", draft.BaseVersion, current => RegistryOps.ApplyAll(current, ops, Config.OwnerId, actor == Config.OwnerId));
            }
            DiscardRegistryDraft(actor, token);
            return result;
        }
    }

    // ---------- Message and AI logs ----------

    public void LogMessage(long time, long chat, string chatType, int topic, int message, long sender, string direction, string text, string raw)
    {
        lock (gate) Exec("INSERT INTO chat_log(time,chat,chat_type,topic,message,sender,direction,text,raw) VALUES($t,$c,$ct,$tp,$m,$s,$d,$x,$r)",
            ("t", time), ("c", chat), ("ct", chatType), ("tp", topic), ("m", message), ("s", sender), ("d", direction), ("x", text), ("r", raw));
    }
    public void SeenUser(JsonElement user, long chat)
    {
        var id = user.Number("id");
        if (id <= 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var name = $"{user.Text("first_name")} {user.Text("last_name")}".Trim();
        lock (gate) Exec("""
            INSERT INTO seen_users VALUES($id,$u,$n,$now,$now,$c,1)
            ON CONFLICT(id) DO UPDATE SET username=$u,name=$n,last_seen=$now,last_chat=$c,count=count+1
            """, ("id", id), ("u", user.Text("username") ?? ""), ("n", name), ("now", now), ("c", chat));
    }
    public List<SeenUser> SeenUsers(int limit = 500)
    {
        lock (gate) return Query("SELECT id,username,name,first_seen,last_seen,last_chat,count FROM seen_users ORDER BY last_seen DESC LIMIT $n",
            r => new SeenUser(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)), ("n", limit));
    }
    public List<LogRow> Log(long? chat = null, int? topic = null, long? sender = null, string? q = null, long? beforeId = null, int limit = 100, long? viewer = null)
    {
        lock (gate) return Query("""
            SELECT l.id,l.time,l.chat,l.chat_type,l.topic,l.message,l.sender,l.direction,l.text,u.username,u.name
            FROM chat_log l LEFT JOIN seen_users u ON u.id=l.sender
            WHERE ($c IS NULL OR l.chat=$c) AND ($tp IS NULL OR l.topic=$tp) AND ($s IS NULL OR l.sender=$s)
              AND ($q IS NULL OR l.text LIKE '%' || $q || '%') AND ($b IS NULL OR l.id<$b) AND ($v IS NULL OR (l.chat IN ($v,$g) AND l.direction NOT IN ('out-edit','delete')))
            ORDER BY l.id DESC LIMIT $n
            """, r => new LogRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetInt64(6),
                r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)),
            ("c", chat), ("tp", topic), ("s", sender), ("q", string.IsNullOrWhiteSpace(q) ? null : q), ("b", beforeId), ("n", Math.Clamp(limit, 1, 500)), ("v", viewer), ("g", Config.GroupId));
    }
    public (long Chat, int Message, string Direction, string Text, string Raw)? LogEntry(long id)
    {
        lock (gate) return Query("SELECT chat,message,direction,text,raw FROM chat_log WHERE id=$id",
            r => ((long, int, string, string, string)?)(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4)), ("id", id)).FirstOrDefault();
    }
    public (long Chat, string Raw)? LogRow(long id)
    {
        lock (gate) return Query("SELECT chat,raw FROM chat_log WHERE id=$id", r => ((long Chat, string Raw)?)(r.GetInt64(0), r.GetString(1)), ("id", id)).FirstOrDefault();
    }
    public List<ChatSummary> LogChats()
    {
        lock (gate)
        {
            var rows = Query("SELECT chat,chat_type,topic,COUNT(*),MAX(time) FROM chat_log GROUP BY chat,topic ORDER BY MAX(time) DESC LIMIT 300",
                r => (Chat: r.GetInt64(0), Type: r.GetString(1), Topic: r.GetInt32(2), Count: r.GetInt64(3), Last: r.GetInt64(4)));
            var users = SeenUsers(2000).ToDictionary(u => u.Id);
            return rows.Select(x =>
            {
                string title;
                if (x.Chat > 0)
                {
                    var m = Config.R.Members.FirstOrDefault(m => m.UserId == x.Chat);
                    title = "Личка: " + (m?.FullName ?? (users.TryGetValue(x.Chat, out var u) ? (u.Name + (u.Username.Length > 0 ? " @" + u.Username : "")) : x.Chat.ToString()));
                }
                else if (x.Chat == Config.GroupId)
                    title = x.Topic == Config.ChatTopicId ? "Воздуханство" : x.Topic == Config.DeadlinesTopicId ? "Дедлайны" : x.Topic == Config.SummaryTopicId ? "Сводка" : $"Группа, тема {x.Topic}";
                else title = $"Чужой чат {x.Chat}, тема {x.Topic}";
                return new ChatSummary(x.Chat, x.Type, x.Topic, x.Count, x.Last, title);
            }).ToList();
        }
    }
    public long AiLog(string kind, string tag, string model, string system, string request, string response, long ms, string? error)
    {
        lock (gate)
        {
            Exec("INSERT INTO ai_log(time,kind,tag,model,system,request,response,ms,error) VALUES($t,$k,$tag,$m,$s,$rq,$rs,$ms,$e)",
                ("t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("k", kind), ("tag", tag), ("m", model), ("s", system),
                ("rq", request), ("rs", response), ("ms", ms), ("e", error));
            return Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
        }
    }
    public List<AiRow> AiCalls(string? tag = null, long? id = null, long? beforeId = null, int limit = 50, bool full = false)
    {
        lock (gate) return Query($"""
            SELECT id,time,kind,tag,model,{(full ? "system,request,response" : "'','',substr(response,1,300)")},ms,error FROM ai_log
            WHERE ($tag IS NULL OR tag=$tag) AND ($id IS NULL OR id=$id) AND ($b IS NULL OR id<$b) ORDER BY id DESC LIMIT $n
            """, r => new AiRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.GetString(7), r.GetInt64(8), r.IsDBNull(9) ? null : r.GetString(9)),
            ("tag", tag), ("id", id), ("b", beforeId), ("n", Math.Clamp(limit, 1, 200)));
    }

    // ---------- Deadline wake-ups ----------

    public long AddWakeup(int deadline, long due, string reason, string source, long actor = 0)
    {
        lock (gate)
        {
            Exec("INSERT INTO wakeups(deadline,due,reason,source,status,created,actor) VALUES($d,$due,$r,$s,'pending',$c,$a)",
                ("d", deadline), ("due", due), ("r", reason), ("s", source), ("c", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("a", actor));
            return Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
        }
    }
    public List<Wakeup> Wakeups(int? deadline = null)
    {
        lock (gate) return Query("SELECT id,deadline,due,reason,source,status,created FROM wakeups WHERE ($d IS NULL OR deadline=$d) AND status<>'deleted' ORDER BY due",
            r => new Wakeup(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6)), ("d", deadline));
    }
    public List<Wakeup> DueWakeups(long now)
    {
        lock (gate) return Query("SELECT id,deadline,due,reason,source,status,created FROM wakeups WHERE status='pending' AND due<=$n ORDER BY due",
            r => new Wakeup(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6)), ("n", now));
    }
    /// <summary>Atomically moves a pending wake-up to a final state; false when somebody else already did.</summary>
    public bool SetWakeup(long id, string status, string from = "pending")
    {
        lock (gate) return Exec("UPDATE wakeups SET status=$s WHERE id=$id AND status=$f", ("s", status), ("id", id), ("f", from)) == 1;
    }
    public void DropPendingWakeups(int deadline, string? source = null)
    {
        lock (gate) Exec("UPDATE wakeups SET status='deleted' WHERE deadline=$d AND status='pending' AND ($s IS NULL OR source=$s)", ("d", deadline), ("s", source));
    }

    // ---------- Web admin sessions ----------

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    public string CreateWebToken(long actor, string kind, TimeSpan ttl)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow;
        lock (gate) Exec("INSERT INTO web_tokens VALUES($h,$a,$k,$e,$c)", ("h", Hash(raw)), ("a", actor), ("k", kind),
            ("e", now.Add(ttl).ToUnixTimeSeconds()), ("c", now.ToUnixTimeSeconds()));
        return raw;
    }
    /// <summary>Returns the actor for a valid token; login tokens are single-use.</summary>
    public long? WebToken(string? raw, string kind)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 100) return null;
        lock (gate)
        {
            var hash = Hash(raw);
            var actor = Scalar("SELECT actor FROM web_tokens WHERE hash=$h AND kind=$k AND expires>$now", ("h", hash), ("k", kind),
                ("now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())) as long?;
            if (actor is not null && kind == "login") Exec("DELETE FROM web_tokens WHERE hash=$h", ("h", hash));
            return actor;
        }
    }
    /// <summary>Login link check without using it up (the GET shows a confirmation page; only the POST logs in).</summary>
    public (long? Actor, string State) PeekLogin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 100) return (null, "missing");
        lock (gate)
        {
            var hash = Hash(raw);
            var row = Query("SELECT actor,expires FROM web_tokens WHERE hash=$h AND kind='login'", r => (Actor: r.GetInt64(0), Expires: r.GetInt64(1)), ("h", hash)).FirstOrDefault();
            if (row.Actor != 0) return row.Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? (row.Actor, "ok") : (null, "expired");
            return (null, Get("login_used:" + hash) ?? "unknown");
        }
    }
    /// <summary>Uses a login link once and remembers who used it, so a "used" link can be explained.</summary>
    public long? ConsumeLogin(string? raw, string ip, string agent)
    {
        var actor = WebToken(raw, "login");
        if (actor is not null)
            Set("login_used:" + Hash(raw!), Json.Write(new { at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ip, agent = Conversation.Limit(agent, 120) }));
        return actor;
    }
    public void DeleteWebToken(string? raw)
    {
        if (raw is null) return;
        lock (gate) Exec("DELETE FROM web_tokens WHERE hash=$h", ("h", Hash(raw)));
    }
}
