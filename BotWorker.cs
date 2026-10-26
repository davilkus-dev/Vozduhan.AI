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
                        allowed = config.CanEdit(actor) && cb.Field("message").Field("chat").Number("id") == actor;
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
            var later = store.Around(chat, topic, actor, afterId: id).Where(x => !(JsonSerializer.Deserialize<JsonElement>(x.Payload).Text("text") ?? "").StartsWith('/')).ToList();
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

    private static readonly string[] PublicCommands = ["/today", "/tomorrow", "/week", "/date", "/next", "/deadlines", "/subjects", "/roster", "/help", "/start"];
    private static readonly string[] ManagementCommands = ["/web", "/post", "/undo", "/discard", "/where", "/deadline", "/edit", "/move", "/cancel"];
    private static readonly string[] DeadlineCommands = ["/deadline", "/edit", "/move", "/cancel"];

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
        if (!isPrivate)
        {
            if (!Conversation.CallsBot(m, botId, username) && joined is null) return;
            // Commands in the group are answered deterministically and never reveal editor/owner data.
            if (PublicCommands.Contains(command)) { await Respond(m, responseKey, Info(command, words), ct, html: true); return; }
            if (command.Length > 0)
            {
                await Respond(m, responseKey, ManagementCommands.Contains(command)
                    ? "Это команда для лички со мной 🙂"
                    : $"Не знаю команду {WebUtility.HtmlEncode(command)} 🤔\n\n" + Help(false), ct, html: true);
                return;
            }
            if (await Coalesced(m, responseKey, actor, text, ct)) return;
            await ChatGuarded(m, responseKey, false, actor, ct);
            return;
        }
        switch (command)
        {
            case "/start" or "/help":
                await Respond(m, responseKey, Help(editor), ct, html: true); return;
            case "/where" when actor == config.OwnerId:
                await Respond(m, responseKey, $"Твой ID: {actor}\nРазрешённая группа: {config.GroupId}\nВоздуханство: {config.ChatTopicId}\nДедлайны: {config.DeadlinesTopicId}\nВерсия реестра: {store.RegistryVersion()}", ct); return;
            case "/deadlines" or "/subjects" or "/roster" or "/today" or "/tomorrow" or "/week" or "/date" or "/next":
                await Respond(m, responseKey, Info(command, words, editor, actor == config.OwnerId), ct, html: true); return;
            case "/web" when editor:
                await Respond(m, responseKey, WebLink(actor), ct); return;
            case "/post" when actor == config.OwnerId:
                await PostPreview(m, responseKey, actor, text.Length > 5 ? text[5..].Trim() : null, ct); return;
            case "/undo" when editor:
                await Undo(m, responseKey, actor, ct); return;
            case "/discard" when editor:
                store.DiscardRegistryDraft(actor);
                store.Delete($"planner_question:{actor}");
                await ManageDeadline(m, responseKey, ct); return;
        }
        if (DeadlineCommands.Contains(command))
        {
            if (editor) await ManageDeadline(m, responseKey, ct);
            else await Respond(m, responseKey, "Дедлайны меняют только разработчик и староста. Я могу показать их через /deadlines.", ct);
            return;
        }
        if (command.Length > 0) { await Respond(m, responseKey, $"Не знаю команду {WebUtility.HtmlEncode(command)} 🤔\n\n" + Help(editor), ct, html: true); return; }
        if (await Coalesced(m, responseKey, actor, text, ct)) return;
        if (editor) { await AdminDialog(m, responseKey, actor, joined is null ? text : joined + "\n" + text, ct); return; }
        await ChatGuarded(m, responseKey, true, actor, ct);
    }

    private static string Help(bool editor)
    {
        var help = "👋 <b>Что я умею</b>\n\n" +
            "🗓 <b>Расписание</b>\n/today — пары сегодня\n/tomorrow — пары завтра\n/next — какая пара следующая\n" +
            "/week — неделя (<code>/week next</code> — следующая)\n/date 31.10 — пары на дату\n\n" +
            "📌 <b>Учёба</b>\n/deadlines — ближайшие сдачи\n/subjects — предметы и преподаватели\n/roster — список группы\n\n" +
            "💬 А ещё можно просто спросить: «где матан?», «что завтра?». В Воздуханстве — через @упоминание или ответом на моё сообщение, в личке — как угодно.";
        if (editor) help += "\n\n🛠 <b>Для редакторов</b>\nПиши обычным текстом — пойму, болтовня это, дедлайн или изменение расписания/списка, и покажу карточку перед сохранением.\n" +
            "<i>«31-го в субботу матан лекция 6-й парой дистанционно», «Бутова отчислили»</i>\n" +
            "/web — веб-админка (через VPN)\n/undo — откатить последнее изменение\n/post [идея] — я напишу в Воздуханство (только разработчик)\n" +
            "<code>/deadline id | работа | YYYY-MM-DD | пара [| кабинет]</code> — дедлайн без ИИ\n/edit ID текст · /move ID дата [пара] · /cancel ID\n/discard — убрать черновики · /where — IDs";
        return help;
    }

    /// <summary>Command answers in Telegram HTML. In the group editor/owner extras are never shown.</summary>
    private string Info(string command, string[] words, bool editor = false, bool owner = false)
    {
        static string H(string? x) => WebUtility.HtmlEncode(x ?? "");
        var r = config.R;
        var local = config.LocalNow(DateTimeOffset.UtcNow);
        var today = DateOnly.FromDateTime(local);
        switch (command)
        {
            case "/today": return ScheduleHtml.Day(r, today, "Сегодня");
            case "/tomorrow": return ScheduleHtml.Day(r, today.AddDays(1), "Завтра");
            case "/week":
                var next = words.Length > 1 && (words[1].StartsWith("next", StringComparison.OrdinalIgnoreCase) || words[1].StartsWith("след", StringComparison.OrdinalIgnoreCase));
                return ScheduleHtml.Week(r, next ? today.AddDays(7) : today, today);
            case "/date":
                return words.Length > 1 && ParseDate(words[1], today) is { } d ? ScheduleHtml.Day(r, d) : "Формат: <code>/date 31.10</code> или <code>/date 2026-10-31</code>";
            case "/next": return ScheduleHtml.Next(r, local);
            case "/deadlines":
            {
                var rows = store.AllDeadlines().Where(x => x.Status == "active" && Deadlines.Ends(config, x) >= DateTimeOffset.UtcNow).ToArray();
                if (rows.Length == 0) return "⏰ <b>Дедлайны</b>\n\nПодтверждённых будущих сдач пока нет 🎉";
                return "⏰ <b>Ближайшие дедлайны</b>" + string.Concat(rows.Take(25).Select(x =>
                {
                    var day = DateOnly.Parse(x.Date);
                    var left = day.DayNumber - today.DayNumber;
                    var when = left == 0 ? "сегодня" : left == 1 ? "завтра" : $"{Schedule.DayShort[Schedule.Dow(day)].ToLowerInvariant()} {day:dd.MM}";
                    return $"\n\n<b>{H(r.SubjectName(x.SubjectId))}</b> — {H(x.Work)}\n📅 {when}, {x.Period} пара ({x.Start}) · 🚪 <code>{H(x.Room)}</code>" +
                         $" <i>#{x.Id}</i>";
                }));
            }
            case "/subjects":
                if (r.Subjects.Length == 0) return "Предметов пока нет.";
                return "🎓 <b>Предметы</b>" + string.Concat(r.Subjects.Where(x => !x.Archived).Select(x =>
                {
                    var lessons = r.Lessons.Where(l => l.SubjectId == x.Id && (l.Until is null || DateOnly.ParseExact(l.Until, "yyyy-MM-dd") >= today)).ToArray();
                    var who = lessons.GroupBy(l => l.Kind).OrderBy(g => g.Key == "lecture" ? 0 : 1).Select(g =>
                        $"{(g.Key == "lecture" ? "Лекции" : g.Key == "lab" ? "Лабы" : "Практики")}: " +
                        string.Join(", ", g.Select(l => l.Teacher).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Select(H).DefaultIfEmpty("—")));
                    return $"\n\n<b>{H(x.Name)}</b>" + (editor ? $" <code>{H(x.Id)}</code>" : "") +
                        (lessons.Length > 0 ? "\n" + string.Join("\n", who) : "") +
                        (owner && (x.Aliases ?? []).Length > 0 ? "\n🤫 сленг: " + H(string.Join(", ", x.Aliases!)) : "") +
                        (owner && !string.IsNullOrWhiteSpace(x.AdminComment) ? "\n🤫 " + H(x.AdminComment) : "");
                }));
            case "/roster":
            {
                var active = r.Members.Where(x => x.Active).OrderBy(x => x.ListNumber == 0 ? int.MaxValue : x.ListNumber).ThenBy(x => x.FullName)
                    .Select(x => $"{(x.ListNumber > 0 ? x.ListNumber + "." : "•")} {H(x.FullName)}" + (x.Role == "head" ? " ⭐" : "") +
                        (editor ? (string.IsNullOrWhiteSpace(x.Username) ? "" : " @" + H(x.Username)) + (x.UserId is null ? " ❔" : "") : ""));
                var text = $"👥 <b>Список группы</b> · {r.Members.Count(x => x.Active)} чел.\n<blockquote expandable>" + string.Join("\n", active) + "</blockquote>";
                if (editor) text += "\n<i>⭐ староста" + (r.Members.Any(x => x.Active && x.UserId is null) ? " · ❔ нет Telegram ID — бот такому не отвечает" : "") + "</i>";
                var gone = r.Members.Where(x => !x.Active).ToArray();
                if (editor && gone.Length > 0) text += "\n\n<b>Неактивные</b>\n" + string.Join("\n", gone.Select(x => $"• {H(x.FullName)}" + (string.IsNullOrWhiteSpace(x.Note) ? "" : $" — <i>{H(x.Note)}</i>")));
                return text;
            }
        }
        return Help(editor);
    }
    public static DateOnly? ParseDate(string text, DateOnly today)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", out var d)) return d;
        if (DateOnly.TryParseExact(text, ["dd.MM.yyyy", "d.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
        if (DateOnly.TryParseExact(text + "." + today.Year, ["dd.MM.yyyy", "d.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return d < today.AddDays(-60) ? d.AddYears(1) : d;
        return null;
    }

    private async Task ChatGuarded(JsonElement m, string key, bool isPrivate, long actor, CancellationToken ct)
    {
        var stored = store.Post("answer:" + key) is not null;
        if (!stored && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - m.Number("date") > 600) return;
        if (!stored && actor != config.OwnerId && !store.ChatBudget(actor, DateTimeOffset.UtcNow))
        {
            await Respond(m, key, "На сегодня лимит болтовни со мной закончился. Дедлайны и /today продолжают работать.", ct); return;
        }
        await Chat(m, key, isPrivate, ct);
    }

    private static string Tag(JsonElement m) => $"chat:{m.Field("chat").Number("id")}:{m.Number("message_id")}";

    private async Task Chat(JsonElement m, string key, bool isPrivate, CancellationToken ct)
    {
        var chat = m.Field("chat").Number("id"); var id = (int)m.Number("message_id");
        var topic = m.TopicId();
        var answerKey = "answer:" + key;
        var answer = store.Post(answerKey)?.Text;
        if (answer is null) await React(chat, id, ct);
        var started = DateTimeOffset.UtcNow;
        if (answer is null)
        {
            using var typing = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var indicator = KeepTyping(chat, topic, typing.Token);
            try
            {
                await digest.Prepare(Related(m), "digest", ct);
                answer = await ai.Chat(conversation.AdminContext(isPrivate), conversation.Context(m, isPrivate), Tag(m), config.SplitReplies, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("AI chat failed: {Error}", Failure(e));
                answer = "Сейчас не достучался до нейронки. Попробуй чуть позже — сохранённые дедлайны и /today от этого не пропали.";
            }
            finally { typing.Cancel(); await indicator; }
            store.SavePost(answerKey, 0, answer);
        }
        var parts = config.SplitReplies ? OpenRouterClient.Parts(answer) : [answer.Replace("\n" + OpenRouterClient.Split + "\n", "\n")];
        var first = await SendParts(chat, topic, parts, id, (DateTimeOffset.UtcNow - started).TotalMilliseconds, ct, i => key + ":part:" + i);
        store.SavePost(key, first ?? 0, "");
        store.ForgetPost(answerKey);
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

    /// <summary>A quick human acknowledgement: sometimes 👀, 👍 or 👌, sometimes nothing.</summary>
    private async Task React(long chat, int id, CancellationToken ct)
    {
        if (config.Reactions.Length == 0 || Random.Shared.NextDouble() >= config.ReactionChance) return;
        try { await telegram.React(chat, id, config.Reactions[Random.Shared.Next(config.Reactions.Length)], ct); }
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
