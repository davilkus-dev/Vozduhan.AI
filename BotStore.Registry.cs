using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Vozduhan.AI;

public sealed record LogRow(long Id, long Time, long Chat, string ChatType, int Topic, int Message, long Sender,
    string Direction, string Text, string? Username, string? Name);
public sealed record ChatSummary(long Chat, string ChatType, int Topic, long Count, long Last, string Title);
public sealed record SeenUser(long Id, string Username, string Name, long FirstSeen, long LastSeen, long LastChat, long Count);
public sealed record AiRow(long Id, long Time, string Kind, string Tag, string Model, string System, string Request, string Response, long Ms, string? Error);
/// <summary>A moment when the bot wakes up to push the group about a deadline. source: ai / manual.</summary>
public sealed record UsageTotals(double Cost, long Attempts, long PromptTokens, long CachedTokens, long CompletionTokens, long ReasoningTokens, long CostMissing);
public sealed record UsageDay(string Date, UsageTotals Totals);
public sealed record UsageKind(string Kind, long Calls, long Attempts, double Cost, double Share, double? AvgPerCall, double? CacheShare, long CostMissing);
public sealed record UsageModel(string Model, UsageTotals Totals);
public sealed record UsageWaste(string Kind, string Discard, long Attempts, double Cost);
public sealed record UsageAttempt(long Time, string Kind, string Tag, string Model, int Attempt, long PromptTokens, long CachedTokens,
    long CompletionTokens, long ReasoningTokens, double? Cost, string? Finish, long Ms);
public sealed record UsageSummary(UsageTotals Today, UsageTotals Yesterday, UsageTotals Week, UsageTotals Month);
public sealed record UsageReport(int Days, string Date, UsageSummary Summary, double? Forecast,
    List<UsageDay> ByDay, List<UsageKind> ByKind, List<UsageModel> ByModel, double WastedCost, long WastedAttempts, List<UsageWaste> Wasted, List<UsageAttempt> Top);
public sealed record UsageUtc(UsageTotals Daily, UsageTotals Weekly, UsageTotals Monthly);
public sealed record SpendAlert(double DailyUsd, double MonthlyUsd);
public sealed record Wakeup(long Id, int Deadline, long Due, string Reason, string Source, string Status, long Created, int Attempts = 0, string? Note = null, long NextTry = 0, long Actor = 0);
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
              AND ($q IS NULL OR l.text LIKE '%' || $q || '%') AND ($b IS NULL OR l.id<$b) AND ($v IS NULL OR (l.chat IN ($v,$g)))
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

    // ---------- AI spending ----------

    public const int UsageDays = 400;
    public void AddUsage(AiUsage u, DateTimeOffset? at = null)
    {
        lock (gate) Exec("""
            INSERT INTO ai_usage(time,kind,tag,requested_model,model,attempt,prompt_tokens,cached_tokens,completion_tokens,reasoning_tokens,cost,finish,ms,call_id,discard)
            VALUES($t,$k,$tag,$rm,$m,$a,$p,$c,$o,$r,$cost,$f,$ms,$call,$discard)
            """, ("t", (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()), ("k", u.Kind), ("tag", u.Tag), ("rm", u.RequestedModel), ("m", u.Model),
            ("a", u.Attempt), ("p", u.PromptTokens), ("c", u.CachedTokens), ("o", u.CompletionTokens), ("r", u.ReasoningTokens), ("cost", u.CostUsd),
            ("f", u.Finish), ("ms", u.Ms), ("call", u.CallId), ("discard", u.Discard));
    }
    public void DiscardUsage(string callId, string reason)
    {
        lock (gate) Exec("UPDATE ai_usage SET discard=$r WHERE id=(SELECT id FROM ai_usage WHERE call_id=$c AND discard IS NULL ORDER BY id DESC LIMIT 1)",
            ("c", callId), ("r", reason));
    }
    private const string UsageSums = "COALESCE(SUM(cost),0.0),COUNT(*),COALESCE(SUM(prompt_tokens),0),COALESCE(SUM(cached_tokens),0)," +
        "COALESCE(SUM(completion_tokens),0),COALESCE(SUM(reasoning_tokens),0),COALESCE(SUM(cost IS NULL),0)";
    private static UsageTotals Sums(SqliteDataReader r, int i) =>
        new(r.GetDouble(i), r.GetInt64(i + 1), r.GetInt64(i + 2), r.GetInt64(i + 3), r.GetInt64(i + 4), r.GetInt64(i + 5), r.GetInt64(i + 6));
    public UsageTotals UsageTotal(DateTimeOffset from, DateTimeOffset to)
    {
        lock (gate) return Query($"SELECT {UsageSums} FROM ai_usage WHERE time>=$f AND time<$t", r => Sums(r, 0),
            ("f", from.ToUnixTimeSeconds()), ("t", to.ToUnixTimeSeconds()))[0];
    }
    /// <summary>The same periods as the OpenRouter key counters: UTC day, UTC week from Monday, UTC month.</summary>
    public UsageUtc UsageUtc(DateTimeOffset now)
    {
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var end = day.AddDays(1);
        return new(UsageTotal(day, end), UsageTotal(day.AddDays(-(((int)day.DayOfWeek + 6) % 7)), end), UsageTotal(new DateTimeOffset(day.Year, day.Month, 1, 0, 0, 0, TimeSpan.Zero), end));
    }
    /// <summary>Spending by the group's local days: period bounds are local midnights, rows are bucketed with the zone offset at the current moment.</summary>
    public UsageReport UsageReport(DateTimeOffset now, int days)
    {
        var today = DateOnly.FromDateTime(Config.LocalNow(now));
        DateTimeOffset At(DateOnly d) => Config.Utc(d.ToDateTime(TimeOnly.MinValue));
        var end = At(today.AddDays(1));
        (string, object?) f = ("f", At(today.AddDays(1 - days)).ToUnixTimeSeconds()), t = ("t", end.ToUnixTimeSeconds());
        lock (gate)
        {
            var daily = Query($"SELECT date(time+$o,'unixepoch') d,{UsageSums} FROM ai_usage WHERE time>=$f AND time<$t GROUP BY d",
                r => (Day: r.GetString(0), Totals: Sums(r, 1)), ("o", (long)Config.Zone.GetUtcOffset(now).TotalSeconds), f, t).ToDictionary(x => x.Day, x => x.Totals);
            var none = new UsageTotals(0, 0, 0, 0, 0, 0, 0);
            var byDay = Enumerable.Range(1 - days, days).Select(i => today.AddDays(i).ToString("yyyy-MM-dd")).Select(d => new UsageDay(d, daily.GetValueOrDefault(d) ?? none)).ToList();
            double? forecast = null;
            if (Scalar("SELECT MIN(time) FROM ai_usage") is long first)
            {
                var start = DateOnly.FromDateTime(Config.LocalNow(DateTimeOffset.FromUnixTimeSeconds(first)));
                if (start < today.AddDays(-7)) start = today.AddDays(-7);
                var full = today.DayNumber - start.DayNumber;
                if (full >= 3) forecast = UsageTotal(At(start), At(today)).Cost / full * DateTime.DaysInMonth(today.Year, today.Month);
            }
            var kinds = Query("""
                SELECT kind,COUNT(DISTINCT call_id)+COALESCE(SUM(call_id IS NULL AND attempt=1),0),COUNT(*),COALESCE(SUM(cost),0.0),COALESCE(SUM(prompt_tokens),0),COALESCE(SUM(cached_tokens),0),COALESCE(SUM(cost IS NULL),0)
                FROM ai_usage WHERE time>=$f AND time<$t GROUP BY kind ORDER BY 4 DESC,1
                """, r => (Kind: r.GetString(0), Calls: r.GetInt64(1), Attempts: r.GetInt64(2), Cost: r.GetDouble(3), Prompt: r.GetInt64(4), Cached: r.GetInt64(5), Missing: r.GetInt64(6)), f, t);
            var total = kinds.Sum(k => k.Cost);
            var models = Query($"SELECT COALESCE(model,requested_model) m,{UsageSums} FROM ai_usage WHERE time>=$f AND time<$t GROUP BY m ORDER BY 2 DESC,1",
                r => new UsageModel(r.GetString(0), Sums(r, 1)), f, t);
            var wasted = Query("""
                SELECT kind,COALESCE(discard,'length') reason,COUNT(*),COALESCE(SUM(cost),0.0) FROM ai_usage
                WHERE time>=$f AND time<$t AND (discard IS NOT NULL OR (call_id IS NULL AND finish IN ('length','max_tokens')))
                GROUP BY kind,reason ORDER BY 4 DESC,1,2
                """, r => new UsageWaste(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetDouble(3)), f, t);
            var top = Query("""
                SELECT time,kind,tag,COALESCE(model,requested_model),attempt,prompt_tokens,cached_tokens,completion_tokens,reasoning_tokens,cost,finish,ms
                FROM ai_usage WHERE time>=$f AND time<$t ORDER BY cost IS NULL,cost DESC,id DESC LIMIT 20
                """, r => new UsageAttempt(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7),
                    r.GetInt64(8), r.IsDBNull(9) ? null : r.GetDouble(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetInt64(11)), f, t);
            return new(days, today.ToString("yyyy-MM-dd"), new(UsageTotal(At(today), end), UsageTotal(At(today.AddDays(-1)), At(today)), UsageTotal(At(today.AddDays(-6)), end),
                UsageTotal(At(new DateOnly(today.Year, today.Month, 1)), end)), forecast, byDay,
                kinds.Select(k => new UsageKind(k.Kind, k.Calls, k.Attempts, k.Cost, total > 0 ? k.Cost / total : 0, k.Calls > 0 ? k.Cost / k.Calls : null,
                    k.Prompt > 0 ? (double)k.Cached / k.Prompt : null, k.Missing)).ToList(),
                models, wasted.Sum(w => w.Cost), wasted.Sum(w => w.Attempts), wasted, top);
        }
    }
    public SpendAlert SpendAlert() => Get("usage_alert") is { } saved ? Json.Read<SpendAlert>(saved) : new(0, 0);
    public void SetSpendAlert(SpendAlert alert) => Set("usage_alert", Json.Write(alert));

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
    private const string WakeupColumns = "id,deadline,due,reason,source,status,created,attempts,note,next_try,actor";
    private static Wakeup ReadWakeup(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.GetInt64(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetInt64(9), r.GetInt64(10));
    public List<Wakeup> Wakeups(int? deadline = null)
    {
        lock (gate) return Query($"SELECT {WakeupColumns} FROM wakeups WHERE ($d IS NULL OR deadline=$d) AND status<>'deleted' ORDER BY due", ReadWakeup, ("d", deadline));
    }
    /// <summary>Atomically moves a wake-up from one state to another; false when somebody else already did.</summary>
    public bool SetWakeup(long id, string status, string from = "pending", string? note = null)
    {
        lock (gate) return Exec("UPDATE wakeups SET status=$s, note=COALESCE($note,note) WHERE id=$id AND status=$f", ("s", status), ("id", id), ("f", from), ("note", note)) == 1;
    }
    public void DropPendingWakeups(int deadline, string? source = null)
    {
        lock (gate) Exec("UPDATE wakeups SET status='deleted' WHERE deadline=$d AND status='pending' AND ($s IS NULL OR source=$s)", ("d", deadline), ("s", source));
    }
    /// <summary>Swaps the automatic plan of a deadline in one transaction; manual reminders and posts are never touched.</summary>
    public void ReplaceWakeups(int deadline, IReadOnlyList<(long Due, string Reason)> points, string source)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            Exec("UPDATE wakeups SET status='deleted' WHERE deadline=$d AND status='pending' AND source IN ('ai','default')", ("d", deadline));
            foreach (var (due, reason) in points)
                Exec("INSERT INTO wakeups(deadline,due,reason,source,status,created,actor) VALUES($d,$due,$r,$s,'pending',$c,0)",
                    ("d", deadline), ("due", due), ("r", reason), ("s", source), ("c", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            tx.Commit();
        }
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
