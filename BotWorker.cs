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

    public BotWorker(BotConfig config, BotStore store, TelegramClient telegram, OpenRouterClient ai, ILogger<BotWorker> log)
    {
        this.config = config; this.store = store; this.telegram = telegram; this.ai = ai; this.log = log;
        conversation = new(config, store);
        telegram.Observer = LogOutgoing;
        ai.Trace ??= t => store.AiLog(t.Kind, t.Tag, t.Model, t.System, t.Request, t.Response, t.Ms, t.Error);
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
        await Task.WhenAll(Supervise("poll", () => Poll(ct), ct),
            Supervise("worker-1", () => ProcessLoop(0, ct), ct),
            Supervise("worker-2", () => ProcessLoop(0, ct), ct),
            Supervise("worker-3", () => ProcessLoop(0, ct), ct),
            Supervise("moderation", () => ProcessLoop(1, ct), ct),
            Supervise("callbacks", () => ProcessLoop(2, ct), ct),
            Supervise("publisher", () => PublishLoop(ct), ct));
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
                                store.Remember(chat.Number("id"), topic, (int)m.Number("message_id"), m.Number("date"), m.GetRawText());
                                var reply = m.Field("reply_to_message");
                                if (reply.ValueKind == JsonValueKind.Object)
                                    store.RememberReference(chat.Number("id"), topic, (int)reply.Number("message_id"), reply.Number("date"), reply.GetRawText());
                            }
                        }
                    }
                    store.Ingest(update.Number("update_id"), actor, allowed ? update.GetRawText() : null, priority);
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
        while (!ct.IsCancellationRequested)
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
    }

    private static readonly string[] PublicCommands = ["/today", "/tomorrow", "/week", "/date", "/next", "/deadlines", "/subjects", "/roster", "/help", "/start"];
    private static readonly string[] ManagementCommands = ["/web", "/undo", "/discard", "/where", "/deadline", "/edit", "/move", "/cancel"];
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
        if (!isPrivate)
        {
            if (!Conversation.CallsBot(m, botId, username)) return;
            // Commands in the group are answered deterministically and never reveal editor/owner data.
            if (PublicCommands.Contains(command)) { await Respond(m, responseKey, Info(command, words), ct, html: true); return; }
            if (command.Length > 0)
            {
                await Respond(m, responseKey, ManagementCommands.Contains(command)
                    ? "Это команда для лички со мной 🙂"
                    : $"Не знаю команду {WebUtility.HtmlEncode(command)} 🤔\n\n" + Help(false), ct, html: true);
                return;
            }
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
            case "/undo" when editor:
                await Undo(m, responseKey, actor, ct); return;
            case "/discard" when editor:
                store.DiscardRegistryDraft(actor);
                await ManageDeadline(m, responseKey, ct); return;
        }
        if (DeadlineCommands.Contains(command))
        {
            if (editor) await ManageDeadline(m, responseKey, ct);
            else await Respond(m, responseKey, "Дедлайны меняют только разработчик и староста. Я могу показать их через /deadlines.", ct);
            return;
        }
        if (command.Length > 0) { await Respond(m, responseKey, $"Не знаю команду {WebUtility.HtmlEncode(command)} 🤔\n\n" + Help(editor), ct, html: true); return; }
        if (editor) { await AdminDialog(m, responseKey, actor, text, ct); return; }
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
            "<i>«31-го в субботу матан лекция 6-й парой дистанционно», «Иванова отчислили»</i>\n" +
            "/web — веб-админка (через VPN)\n/undo — откатить последнее изменение\n" +
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
                        (string.IsNullOrWhiteSpace(x.Note) ? "" : $"\n<i>{H(x.Note)}</i>") + $" <i>#{x.Id}</i>";
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
            try { answer = await ai.Chat(conversation.AdminContext(isPrivate), conversation.Context(m, isPrivate), Tag(m), config.SplitReplies, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("AI chat failed: {Error}", Failure(e));
                answer = "Сейчас не достучался до нейронки. Попробуй чуть позже — сохранённые дедлайны и /today от этого не пропали.";
            }
            finally { typing.Cancel(); await indicator; }
            answer = Conversation.Limit(Json.Unescape(answer), 3900);
            store.SavePost(answerKey, 0, answer);
        }
        var parts = config.SplitReplies ? OpenRouterClient.Parts(answer) : [answer.Replace("\n" + OpenRouterClient.Split + "\n", "\n")];
        int? first = null;
        for (var i = 0; i < parts.Count; i++)
        {
            var partKey = key + ":part:" + i;
            if (store.Post(partKey) is { } done) { first ??= done.MessageId; continue; }
            var thought = i == 0 ? (DateTimeOffset.UtcNow - started).TotalMilliseconds : 0;
            await HumanPause(chat, topic, parts[i], thought, ct);
            JsonElement sent;
            var html = TelegramHtml.Sanitize(parts[i]);
            try { sent = await telegram.Send(chat, topic, html, ct, reply: i == 0 ? id : null, html: true); }
            catch (TelegramError e) when (e.Code == 400 && e.Description.Contains("parse", StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("Telegram rejected HTML, sending plain text: {Error}", Failure(e));
                sent = await telegram.Send(chat, topic, TelegramHtml.Plain(html), ct, reply: i == 0 ? id : null);
            }
            store.SavePost(partKey, (int)sent.Number("message_id"), "");
            store.Remember(sent.Field("chat").Number("id"), sent.TopicId(), (int)sent.Number("message_id"), sent.Number("date"), sent.GetRawText());
            first ??= (int)sent.Number("message_id");
        }
        store.SavePost(key, first ?? 0, "");
        store.ForgetPost(answerKey);
        for (var i = 0; i < parts.Count; i++) store.ForgetPost(key + ":part:" + i);
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
        JsonElement sent;
        try { sent = await telegram.Send(to.Field("chat").Number("id"), to.TopicId(), text, ct, keyboard, (int)to.Number("message_id"), html); }
        catch (TelegramError e) when (html && e.Code == 400 && e.Description.Contains("parse", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Telegram rejected command HTML: {Error}", Failure(e));
            sent = await telegram.Send(to.Field("chat").Number("id"), to.TopicId(), TelegramHtml.Plain(text), ct, keyboard, (int)to.Number("message_id"));
        }
        store.SavePost(key, (int)sent.Number("message_id"), "");
        store.Remember(sent.Field("chat").Number("id"), sent.TopicId(), (int)sent.Number("message_id"), sent.Number("date"), sent.GetRawText());
    }
    private async Task NotifyOwner(string text, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (long.TryParse(store.Get("last_owner_warning"), out var t) && now.ToUnixTimeSeconds() - t < 3600) return;
        store.Set("last_owner_warning", now.ToUnixTimeSeconds().ToString());
        try { await telegram.Send(config.OwnerId, 0, text, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
    }
}
