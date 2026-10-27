using System.Net;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

public sealed partial class BotWorker : BackgroundService
{
    private readonly BotConfig config;
    private readonly BotStore store;
    private readonly TelegramClient telegram;
    private readonly OpenRouterClient ai;
    private readonly ILogger<BotWorker> log;
    private long botId;
    private string username = "";
    private readonly SemaphoreSlim publicationGate = new(1);
    private readonly Conversation conversation;
    private readonly MediaDigest digest;
    private readonly System.Threading.Channels.Channel<string> digestQueue =
        System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(200) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
    /// <summary>Live chat: at most this many messages are handled at once; waiting for a coalescing window does not hold a handler.</summary>
    private const int ChatHandlers = 3;
    private readonly SemaphoreSlim chatHandlers = new(ChatHandlers);
    private static readonly AsyncLocal<bool> holdsHandler = new();
    public static TimeSpan CoalesceQuiet = TimeSpan.FromSeconds(1.5), CoalesceMax = TimeSpan.FromSeconds(6);

    public BotWorker(BotConfig config, BotStore store, TelegramClient telegram, OpenRouterClient ai, ILogger<BotWorker> log)
    {
        this.config = config; this.store = store; this.telegram = telegram; this.ai = ai; this.log = log;
        conversation = new(config, store);
        digest = new(config, store, telegram, ai, log);
        Catalog = new(config, BuildCatalog());
        telegram.Observer = LogOutgoing;
        ai.Log ??= log;
        ai.Trace ??= t => store.AiLog(t.Kind, t.Tag, t.Model, t.System, t.Request, t.Response, t.Ms, t.Error);
        ai.Usage ??= u => { store.AddUsage(u); SpendCheck(); };
        ai.DiscardUsage ??= store.DiscardUsage;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var me = await telegram.Call("getMe", new { }, ct);
                botId = me.Number("id"); username = me.Text("username") ?? "";
                await telegram.Call("deleteWebhook", new { drop_pending_updates = false }, ct);
                log.LogInformation("Started @{Name}; allowed group {Group}", username, config.GroupId);
                break;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Telegram startup failed ({Type}); retrying", Failure(e));
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
        await Task.WhenAll(Enumerable.Range(1, ChatHandlers * 2).Select(i => Supervise($"worker-{i}", () => ProcessLoop(0, ct), ct)).Concat([
            Supervise("poll", () => Poll(ct), ct),
            Supervise("digest", () => DigestLoop(ct), ct),
            Supervise("moderation", () => ProcessLoop(1, ct), ct),
            Supervise("callbacks", () => ProcessLoop(2, ct), ct),
            Supervise("publisher", () => PublishLoop(ct), ct),
            Supervise("ambient", () => AmbientLoop(ct), ct),
            Supervise("wakeplan", () => PlanLoop(ct), ct)]));
    }

    private async Task Supervise(string name, Func<Task> run, CancellationToken ct)
    {
        try
        {
            await run();
            if (ct.IsCancellationRequested) return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception e) { log.LogCritical("Loop {Loop} stopped: {Error}", name, Failure(e)); }
        // A dead worker must not leave a healthy-looking process and claimed jobs behind.
        Environment.FailFast($"Critical bot loop stopped: {name}");
    }

    internal static string Failure(Exception e) => e is TelegramError t
        ? $"Telegram {t.Method}: {t.Code}; {System.Text.RegularExpressions.Regex.Replace(t.Description, @"\d{5,}:[A-Za-z0-9_-]+", "[redacted]")}; retry_after={t.RetryAfter}"
        : e is HttpRequestException h ? $"HTTP/network error; status={h.StatusCode}"
        : e is OperationCanceledException ? "Request timeout/cancellation"
        : e is Microsoft.Data.Sqlite.SqliteException sql ? $"SQLite {sql.SqliteErrorCode}/{sql.SqliteExtendedErrorCode}"
        : e.GetType().Name;

    private async Task Poll(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SyncCommands(ct);
            try
            {
                var offset = long.TryParse(store.Get("offset"), out var n) ? n : 0;
                var updates = await telegram.Call("getUpdates", new { offset, limit = 100, timeout = 30,
                    allowed_updates = new[] { "message", "edited_message", "callback_query" } }, ct);
                foreach (var update in updates.EnumerateArray())
                {
                    try { LogIncoming(update); }
                    catch (Exception e) { log.LogWarning("Message log failed ({Type})", Failure(e)); }
                    var m = update.Field("message");
                    if (m.ValueKind != JsonValueKind.Object) m = update.Field("edited_message");
                    var cb = update.Field("callback_query");
                    var actor = cb.ValueKind == JsonValueKind.Object ? cb.Field("from").Number("id") : m.Field("from").Number("id");
                    var priority = 0;
                    var allowed = false;
                    var chatId = cb.ValueKind == JsonValueKind.Object ? cb.Field("message").Field("chat").Number("id") : m.Field("chat").Number("id");
                    if (cb.ValueKind == JsonValueKind.Object)
                    {
                        allowed = CallbackAllowed(config, actor, chatId, cb.Text("data"));
                        priority = 2;
                    }
                    else
                    {
                        var chat = m.Field("chat"); var topic = m.TopicId();
                        if (chat.Number("id") == config.GroupId && config.AiTopic(topic) && HasContent(m))
                        { allowed = true; priority = 1; }
                        else if (!m.Field("from").Flag("is_bot") && config.AllowedUser(actor))
                        {
                            allowed = (chat.Text("type") == "private" && chat.Number("id") == actor) ||
                                (chat.Number("id") == config.GroupId && topic == config.ChatTopicId && HasContent(m));
                            if (allowed)
                            {
                                var fresh = update.Field("message").ValueKind == JsonValueKind.Object;
                                store.Remember(chat.Number("id"), topic, (int)m.Number("message_id"), m.Number("date"), m.GetRawText(),
                                    fresh ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0);
                                if (fresh && config.Features is { MediaDigest: true, DigestAll: true } && chat.Number("id") == config.GroupId && MediaDigest.Find(m) is not null)
                                    digestQueue.Writer.TryWrite(m.GetRawText());
                                var reply = m.Field("reply_to_message");
                                if (reply.ValueKind == JsonValueKind.Object)
                                    store.RememberReference(chat.Number("id"), topic, (int)reply.Number("message_id"), reply.Number("date"), reply.GetRawText());
                            }
                        }
                    }
                    store.Ingest(update.Number("update_id"), actor, allowed ? update.GetRawText() : null, priority, chatId);
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Telegram polling failed ({Type})", Failure(e));
                await Task.Delay(TimeSpan.FromSeconds(e is TelegramError t ? Math.Max(5, t.RetryAfter) : 5), ct);
            }
        }
    }
    /// <summary>
    /// Which button presses are queued: clarification buttons («slot:») for everybody in the roster, in the group and in DM
    /// (the author check is in SlotCallback); every other button only for editors in their DM, as before.
    /// </summary>
    public static bool CallbackAllowed(BotConfig config, long actor, long chat, string? data) =>
        (data ?? "").StartsWith(SlotPrefix, StringComparison.Ordinal)
            ? config.AllowedUser(actor) && (chat == actor || chat == config.GroupId)
            : config.CanEdit(actor) && chat == actor;
    private static bool HasContent(JsonElement m) => new[] { "text", "caption", "photo", "video", "voice", "audio", "document", "sticker", "animation", "poll", "contact", "location", "venue", "dice", "video_note", "game", "checklist", "paid_media", "live_photo", "story" }
        .Any(key => m.Field(key).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null));

    /// <summary>Everything the bot receives goes to the admin log, including people outside the roster.</summary>
    private void LogIncoming(JsonElement update)
    {
        var cb = update.Field("callback_query");
        if (cb.ValueKind == JsonValueKind.Object)
        {
            var msg = cb.Field("message");
            store.SeenUser(cb.Field("from"), msg.Field("chat").Number("id"));
            store.LogMessage(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), msg.Field("chat").Number("id"), msg.Field("chat").Text("type") ?? "",
                msg.TopicId(), (int)msg.Number("message_id"), cb.Field("from").Number("id"), "callback", "[кнопка] " + cb.Text("data"), cb.GetRawText());
            return;
        }
        var m = update.Field("message");
        var direction = "in";
        if (m.ValueKind != JsonValueKind.Object) { m = update.Field("edited_message"); direction = "edit"; }
        if (m.ValueKind != JsonValueKind.Object) return;
        var chat = m.Field("chat");
        store.SeenUser(m.Field("from"), chat.Number("id"));
        store.LogMessage(m.Number(direction == "edit" ? "edit_date" : "date") is > 0 and var t ? t : m.Number("date"), chat.Number("id"),
            chat.Text("type") ?? "", m.TopicId(), (int)m.Number("message_id"), m.Field("from").Number("id"), direction, m.Body(), m.GetRawText());
    }
    private void LogOutgoing(string method, object data, JsonElement result)
    {
        if (method is not ("sendMessage" or "editMessageText" or "deleteMessage")) return;
        var request = JsonSerializer.SerializeToElement(data, Json.Options);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (method == "editMessageText" && result.ValueKind == JsonValueKind.Object)
            store.Remember(result.Field("chat").Number("id"), result.TopicId(), (int)result.Number("message_id"), result.Number("date"), result.GetRawText());
        if (method == "deleteMessage") store.ForgetMessage(request.Number("chat_id"), (int)request.Number("message_id"));
        if (method == "sendMessage" && result.ValueKind == JsonValueKind.Object)
            store.LogMessage(now, result.Field("chat").Number("id"), result.Field("chat").Text("type") ?? "", result.TopicId(),
                (int)result.Number("message_id"), botId, "out", result.Body(), result.GetRawText());
        else if (method == "editMessageText")
            store.LogMessage(now, request.Number("chat_id"), request.Number("chat_id") > 0 ? "private" : "supergroup", 0,
                (int)request.Number("message_id"), botId, "out-edit", request.Text("text") ?? "", request.GetRawText());
        else if (method == "deleteMessage")
            store.LogMessage(now, request.Number("chat_id"), request.Number("chat_id") > 0 ? "private" : "supergroup", 0,
                (int)request.Number("message_id"), botId, "delete", "[удалено ботом]", request.GetRawText());
    }

    private async Task ProcessLoop(int priority, CancellationToken ct)
    {
        holdsHandler.Value = priority == 0;
        while (!ct.IsCancellationRequested)
        {
            if (priority == 0) await chatHandlers.WaitAsync(ct);
            try
            {
                var item = store.Claim(priority);
                if (item is null) { await Task.Delay(250, ct); continue; }
                try
                {
                    using var doc = JsonDocument.Parse(item.Payload);
                    await Process(doc.RootElement, ct);
                    store.Finish(item.Id);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning("Update {Id} failed ({Type}), attempt {Attempt}", item.Id, Failure(e), item.Attempts + 1);
                    store.Retry(item, e);
                    if (e is TelegramError { Code: 401 or 409 })
                        await NotifyOwner("Telegram отклонил запрос: проверь токен и отсутствие второго polling-процесса. Подробности в journalctl.", ct);
                    if (priority == 1) await NotifyOwner("Не могу удалить сообщение в AI-теме. Проверь права бота и доступность Telegram.", ct);
                }
            }
            finally { if (priority == 0) chatHandlers.Release(); }
        }
    }

    private async Task DigestLoop(CancellationToken ct)
    {
        await foreach (var raw in digestQueue.Reader.ReadAllAsync(ct))
        {
            using var doc = JsonDocument.Parse(raw);
            await digest.Describe(doc.RootElement, "digest-bg", ct);
        }
    }

    /// <summary>
    /// Waits until the author is quiet for CoalesceQuiet (at most CoalesceMax from the message). If they wrote again, this message is
    /// marked as coalesced into the newest one, which will be answered instead (and counts as addressed to the bot).
    /// </summary>
    private async Task<bool> Coalesced(JsonElement m, string key, long actor, string text, CancellationToken ct)
    {
        if (!config.Features.Coalesce) return false;
        var chat = m.Field("chat").Number("id"); var topic = m.TopicId(); var id = (int)m.Number("message_id");
        var arrived = store.Arrived(chat, id) ?? m.Number("date") * 1000;
        int? newest;
        while (true)
        {
            var later = store.Around(chat, topic, actor, afterId: id)
                .Where(x => config.Features.Router || !(JsonSerializer.Deserialize<JsonElement>(x.Payload).Text("text") ?? "").StartsWith('/')).ToList();
            newest = later.Count > 0 ? later[^1].Id : null;
            var wait = Math.Min(later.Select(x => x.Arrived).Append(arrived).Max() + (long)CoalesceQuiet.TotalMilliseconds,
                arrived + (long)CoalesceMax.TotalMilliseconds) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (wait <= 0) break;
            // The handler is free for other chats while we wait; this chat stays claimed, so its order holds.
            if (holdsHandler.Value) chatHandlers.Release();
            try { await Task.Delay(TimeSpan.FromMilliseconds(wait), ct); }
            finally { if (holdsHandler.Value) await chatHandlers.WaitAsync(CancellationToken.None); }
        }
        if (newest is null) return false;
        var before = store.Post($"coalesce:{chat}:{id}")?.Text;
        store.SavePost($"coalesce:{chat}:{newest}", id, Conversation.Limit(string.Join("\n", new[] { before, text }.Where(x => !string.IsNullOrEmpty(x))), 4000));
        store.SavePost(key, 0, $"coalesced:{newest}");
        store.LogMessage(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), chat, m.Field("chat").Text("type") ?? "", topic, id, actor, "coalesced", $"[склеено с сообщением {newest}]", "{}");
        return true;
    }

    private async Task Process(JsonElement update, CancellationToken ct)
    {
        var cb = update.Field("callback_query");
        if (cb.ValueKind == JsonValueKind.Object) { await ProcessCallback(cb, ct); return; }
        var m = update.Field("message");
        var edited = m.ValueKind != JsonValueKind.Object;
        if (edited) m = update.Field("edited_message");
        var chat = m.Field("chat").Number("id");
        var actor = m.Field("from").Number("id");
        var topic = m.TopicId();
        var id = (int)m.Number("message_id");
        if (chat == config.GroupId && config.AiTopic(topic))
        {
            try { await telegram.Delete(chat, id, ct); }
            catch (TelegramError e) when (e.Code == 400 && e.Description.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase)) { }
            return;
        }
        if (!config.AllowedUser(actor) || edited) return;
        var isPrivate = m.Field("chat").Text("type") == "private" && chat == actor;
        if (!isPrivate && (chat != config.GroupId || topic != config.ChatTopicId)) return;
        var text = m.Text("text") ?? m.Text("caption") ?? "";
        var words = text.Split(' ', '\n', StringSplitOptions.RemoveEmptyEntries);
        var command = text.StartsWith('/') ? words[0].Split('@')[0].ToLowerInvariant() : "";
        var responseKey = $"reply:{chat}:{id}";
        if (store.Post(responseKey) is not null)
        {
            store.ForgetPost("answer:" + responseKey);
            var orphan = store.Post("loading:" + responseKey);
            if (orphan is not null)
            {
                try { await telegram.Delete(chat, orphan.MessageId, ct); } catch (TelegramError) { }
                store.ForgetPost("loading:" + responseKey);
            }
            return;
        }
        var editor = config.CanEdit(actor);
        var joined = store.Post($"coalesce:{chat}:{id}")?.Text;
        var router = config.Features.Router;
        // In the group a message to someone else is ignored, unless its author owes the bot an answer to a pending question.
        var addressed = isPrivate || Conversation.CallsBot(m, botId, username) || joined is not null;
        if (!addressed && !(router && store.ActiveSlot(actor, chat, DateTimeOffset.UtcNow) is { } open && open.Topic == topic)) return;
        // Without the Router commands are answered deterministically through the catalog; with it there are no commands, only text.
        if (command.Length > 0 && !router) { await SlashCommand(m, responseKey, actor, isPrivate ? Capabilities.Dm : Capabilities.Group, command, words, text, ct); return; }
        if (command == "/start" && isPrivate) { await Respond(m, responseKey, Greeting(Catalog.Role(actor)), ct); return; }
        if (await Coalesced(m, responseKey, actor, text, ct)) return;
        if (router) { await Routed(m, responseKey, actor, isPrivate, joined, addressed, ct); return; }
        if (isPrivate && editor) { await AdminDialog(m, responseKey, actor, joined is null ? text : joined + "\n" + text, ct); return; }
        await ChatGuarded(m, responseKey, isPrivate, actor, ct);
    }

    /// <summary>Command answers in Telegram HTML (kept for checks). In the group editor/owner extras are never shown.</summary>
    private string Info(string command, string[] words, bool editor = false, bool owner = false)
    {
        var r = config.R;
        var local = config.LocalNow(DateTimeOffset.UtcNow);
        var today = DateOnly.FromDateTime(local);
        switch (command)
        {
            case "/today": return ScheduleHtml.Day(r, today, "Сегодня");
            case "/tomorrow": return ScheduleHtml.Day(r, today.AddDays(1), "Завтра");
            case "/next": return ScheduleHtml.Next(r, local);
            case "/deadlines": return DeadlinesHtml();
            case "/subjects": return SubjectsHtml(editor, owner);
            case "/roster": return RosterHtml(editor);
        }
        return Help(owner ? "developer" : editor ? "head" : "member", Capabilities.Dm);
    }
    public static DateOnly? ParseDate(string text, DateOnly today)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", out var d)) return d;
        if (DateOnly.TryParseExact(text, ["dd.MM.yyyy", "d.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
        if (DateOnly.TryParseExact(text + "." + today.Year, ["dd.MM.yyyy", "d.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return d < today.AddDays(-60) ? d.AddYears(1) : d;
        return null;
    }

    private async Task ChatGuarded(JsonElement m, string key, bool isPrivate, long actor, CancellationToken ct, RouteDecision? decision = null)
    {
        var stored = store.Post("answer:" + key) is not null;
        if (!stored && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - m.Number("date") > 600) return;
        if (!stored && actor != config.OwnerId && !store.ChatBudget(actor, DateTimeOffset.UtcNow))
        {
            await Respond(m, key, config.Features.Router
                ? "На сегодня лимит болтовни со мной закончился. Расписание и дедлайны всё равно подскажу — спроси «что завтра?» или «какие дедлайны?»."
                : "На сегодня лимит болтовни со мной закончился. Дедлайны и /today продолжают работать.", ct); return;
        }
        await Chat(m, key, isPrivate, ct, actor, decision);
    }

    private static string Tag(JsonElement m) => $"chat:{m.Field("chat").Number("id")}:{m.Number("message_id")}";

    public const string WebExhausted = "🔎 <i>Поиск на сегодня исчерпан — ответил без него.</i>";
    private async Task Chat(JsonElement m, string key, bool isPrivate, CancellationToken ct, long actor = 0, RouteDecision? decision = null)
    {
        var chat = m.Field("chat").Number("id"); var id = (int)m.Number("message_id");
        var topic = m.TopicId();
        var answerKey = "answer:" + key;
        var tailKey = "tail:" + key;
        var answer = store.Post(answerKey)?.Text;
        if (answer is null && !config.Features.Router) await React(chat, id, ct);
        var started = DateTimeOffset.UtcNow;
        if (answer is null)
        {
            using var typing = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var indicator = KeepTyping(chat, topic, typing.Token);
            var tail = "";
            try
            {
                await digest.Prepare(Related(m), "digest", ct);
                var features = config.Features;
                WebCall? web = null; string? webNote = null;
                if (features.WebSearch && decision?.Web is { Length: > 0 } query)
                {
                    if (store.WebBudget(actor, actor == config.OwnerId, started, key, features.WebSearchDailyLimit, features.WebSearchPerUserDaily)) web = new(query);
                    else
                    {
                        webNote = "Поиск в интернете на сегодня исчерпан: ответь без него, из того, что знаешь, и не выдавай догадки за свежие данные.";
                        tail = WebExhausted;
                    }
                }
                if (features.WriterDiet)
                {
                    var (trusted, user) = conversation.Writer(m, isPrivate, decision?.Blocks, webNote);
                    answer = await ai.Chat(trusted, user, Tag(m), config.SplitReplies, ct, web);
                }
                else answer = await ai.Chat(conversation.AdminContext(isPrivate), conversation.Context(m, isPrivate, webNote), Tag(m), config.SplitReplies, ct, web);
                if (web is { Sources.Count: > 0 }) tail = OpenRouterClient.SourcesHtml(web.Sources);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("AI chat failed: {Error}", Failure(e));
                answer = config.Features.Router ? NoAiAnswer
                    : "Сейчас не достучался до нейронки. Попробуй чуть позже — сохранённые дедлайны и /today от этого не пропали.";
                tail = "";
            }
            finally { typing.Cancel(); await indicator; }
            // Sources and the «search exhausted» mark are built by code, kept next to the answer for a replay after a restart.
            if (tail.Length > 0) store.SavePost(tailKey, 0, tail);
            store.SavePost(answerKey, 0, answer);
        }
        var parts = config.SplitReplies ? OpenRouterClient.Parts(answer) : [answer.Replace("\n" + OpenRouterClient.Split + "\n", "\n")];
        var formatted = false;
        if (store.Post(tailKey)?.Text is { Length: > 0 } code)
        {
            // The model's text is converted first, so the links the code adds are never touched by the markdown rules.
            parts = parts.Select(TelegramHtml.FromMarkdown).ToList();
            parts[^1] += "\n\n" + code;
            formatted = true;
        }
        var first = await SendParts(chat, topic, parts, id, (DateTimeOffset.UtcNow - started).TotalMilliseconds, ct, i => key + ":part:" + i, formatted);
        store.SavePost(key, first ?? 0, "");
        store.ForgetPost(answerKey);
        store.ForgetPost(tailKey);
        for (var i = 0; i < parts.Count; i++) store.ForgetPost(key + ":part:" + i);
    }

    /// <summary>The message, what it replies to and the author's messages just before it (a photo and then «что тут?»).</summary>
    private IEnumerable<JsonElement> Related(JsonElement m)
    {
        if (!config.Features.MediaDigest) return [];
        var chat = m.Field("chat").Number("id"); var id = (int)m.Number("message_id");
        var since = (store.Arrived(chat, id) ?? m.Number("date") * 1000) - (long)CoalesceMax.TotalMilliseconds;
        return store.Around(chat, m.TopicId(), m.Field("from").Number("id"), beforeId: id).Where(x => x.Arrived >= since)
            .Select(x => JsonSerializer.Deserialize<JsonElement>(x.Payload)).Prepend(m.Field("reply_to_message")).Prepend(m)
            .Where(x => x.ValueKind == JsonValueKind.Object).ToList();
    }

    private static readonly string[] Acknowledgements = ["👀", "👍", "👌"];
    /// <summary>A quick human acknowledgement: sometimes 👀, 👍 or 👌, sometimes nothing. The rest of config.Reactions is for the Router.</summary>
    private async Task React(long chat, int id, CancellationToken ct)
    {
        var pool = config.Reactions.Where(Acknowledgements.Contains).ToArray() is { Length: > 0 } ack ? ack : config.Reactions;
        if (pool.Length == 0 || Random.Shared.NextDouble() >= config.ReactionChance) return;
        try { await telegram.React(chat, id, pool[Random.Shared.Next(pool.Length)], ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { }
    }
    private async Task KeepTyping(long chat, int topic, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await telegram.Typing(chat, topic, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
                await Task.Delay(4500, ct);
            }
        }
        catch (OperationCanceledException) { }
    }
    /// <summary>Typing time like a person: roughly by length, with jitter; time already spent thinking counts.</summary>
    private async Task HumanPause(long chat, int topic, string text, double alreadyMs, CancellationToken ct)
    {
        if (config.ReplyDelayScale <= 0) return;
        var ms = (500 + text.Length * 1000.0 / config.TypingCharsPerSecond) * (0.75 + Random.Shared.NextDouble() * 0.5);
        ms = Math.Clamp(ms, 700, 7000) * config.ReplyDelayScale - alreadyMs;
        if (ms < 250) return;
        try { await telegram.Typing(chat, topic, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
        await Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
    }

    private async Task Respond(JsonElement to, string key, string text, CancellationToken ct, object? keyboard = null, bool html = false)
    {
        var parts = TelegramHtml.Split(html ? text : WebUtility.HtmlEncode(text)).ToArray();
        int first = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var partKey = key + ":part:" + i;
            if (store.Post(partKey) is { } done) { if (i == 0) first = done.MessageId; continue; }
            JsonElement sent;
            try { sent = await telegram.Send(to.Field("chat").Number("id"), to.TopicId(), parts[i], ct, keyboard, i == 0 ? (int)to.Number("message_id") : null, html: true); }
            catch (TelegramError e) when (e.Code == 400 && e.Description.Contains("parse", StringComparison.OrdinalIgnoreCase))
            { sent = await telegram.Send(to.Field("chat").Number("id"), to.TopicId(), TelegramHtml.Plain(parts[i]), ct, keyboard); }
            var id = (int)sent.Number("message_id");
            if (i == 0) first = id;
            store.SavePost(partKey, id, "");
            store.Remember(sent.Field("chat").Number("id"), sent.TopicId(), id, sent.Number("date"), sent.GetRawText());
        }
        store.SavePost(key, first, "");
        for (var i = 0; i < parts.Length; i++) store.ForgetPost(key + ":part:" + i);
    }

    private async Task<bool> NotifyOwner(string text, CancellationToken ct, bool throttle = true)
    {
        var now = DateTimeOffset.UtcNow;
        if (throttle && long.TryParse(store.Get("last_owner_warning"), out var t) && now.ToUnixTimeSeconds() - t < 3600) return false;
        if (throttle) store.Set("last_owner_warning", now.ToUnixTimeSeconds().ToString());
        try { await telegram.Send(config.OwnerId, 0, text, ct); return true; } catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    internal Task<AiKeyInfo?> KeyInfo(CancellationToken ct) => ai.KeyInfo(ct);
    private long spendCheckedAt = long.MinValue / 2;
    private int spendQueued;
    private readonly SemaphoreSlim spendGate = new(1);
    /// <summary>Runs the spending check after a usage row, at most once a minute; a skipped check is queued for the end of that minute.</summary>
    private void SpendCheck()
    {
        var wait = Interlocked.Read(ref spendCheckedAt) + 60_000 - Environment.TickCount64;
        if (wait > 0)
        {
            if (Interlocked.Exchange(ref spendQueued, 1) == 0)
                _ = Task.Delay(TimeSpan.FromMilliseconds(wait)).ContinueWith(_ => { Interlocked.Exchange(ref spendQueued, 0); SpendCheck(); }, TaskScheduler.Default);
            return;
        }
        Interlocked.Exchange(ref spendCheckedAt, Environment.TickCount64);
        _ = Task.Run(async () =>
        {
            try { await SpendAlerts(DateTimeOffset.UtcNow, CancellationToken.None); }
            catch (Exception e) { log.LogWarning("Spending check failed ({Type})", Failure(e)); }
        });
    }
    /// <summary>One private message to the developer per local day and per month when spending passes a threshold; the mark survives restarts.</summary>
    internal async Task SpendAlerts(DateTimeOffset now, CancellationToken ct)
    {
        var alert = store.SpendAlert();
        if (alert.DailyUsd <= 0 && alert.MonthlyUsd <= 0) return;
        await spendGate.WaitAsync(ct);
        try
        {
            var today = DateOnly.FromDateTime(config.LocalNow(now));
            var end = config.Utc(today.AddDays(1).ToDateTime(TimeOnly.MinValue));
            var checks = new[]
            {
                (Limit: alert.DailyUsd, Key: "usage_alert_sent:day", Mark: today.ToString("yyyy-MM-dd"), From: config.Utc(today.ToDateTime(TimeOnly.MinValue)), Label: "Сегодня"),
                (Limit: alert.MonthlyUsd, Key: "usage_alert_sent:month", Mark: today.ToString("yyyy-MM"), From: config.Utc(new DateOnly(today.Year, today.Month, 1).ToDateTime(TimeOnly.MinValue)), Label: "За месяц")
            };
            foreach (var c in checks)
            {
                var mark = c.Mark + "|" + c.Limit.ToString("G", CultureInfo.InvariantCulture);
                if (c.Limit <= 0 || store.Get(c.Key) == mark) continue;
                var spent = store.UsageTotal(c.From, end).Cost;
                if (spent <= c.Limit) continue;
                var text = string.Create(CultureInfo.InvariantCulture, $"💸 {c.Label} потрачено ${spent:0.00} (порог ${c.Limit:0.00})");
                if (await NotifyOwner(text, ct, throttle: false)) store.Set(c.Key, mark);
            }
        }
        finally { spendGate.Release(); }
    }
}
