using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

public sealed partial class BotWorker(BotConfig config, BotStore store, TelegramClient telegram, OpenRouterClient ai,
    ILogger<BotWorker> log) : BackgroundService
{
    private long botId;
    private string username = "";
    private readonly SemaphoreSlim publicationGate = new(1);
    private readonly Conversation conversation = new(config, store);

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

    private static string Failure(Exception e) => e is TelegramError t
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
        var command = text.Split(' ', '\n')[0].Split('@')[0].ToLowerInvariant();
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
        if (isPrivate && command is "/start" or "/help")
        {
            var help = "Я на месте. В Воздуханстве отвечаю по @упоминанию и на ответы мне.\n/deadlines — ближайшие сдачи.\n/subjects — предметы и кабинеты.";
            if (config.CanEdit(actor)) help += "\n\nДедлайны задавай здесь обычной фразой. Сначала уточню поля и покажу карточку.\n" +
                "/deadline subjectId | работа | YYYY-MM-DD | пара — ввод без ИИ\n/edit ID текст изменений\n/move ID YYYY-MM-DD [пара]\n/cancel ID\n/discard — убрать черновик\n/where — IDs чата и тем";
            await Respond(m, responseKey, help, ct); return;
        }
        if (isPrivate && command == "/where" && actor == config.OwnerId)
        {
            await Respond(m, responseKey, $"Твой ID: {actor}\nРазрешённая группа: {config.GroupId}\nВоздуханство: {config.ChatTopicId}\nДедлайны: {config.DeadlinesTopicId}", ct); return;
        }
        if (isPrivate && command == "/deadlines")
        {
            var rows = store.AllDeadlines().Where(d => d.Status == "active" && Deadlines.Ends(config, d) >= DateTimeOffset.UtcNow).ToArray();
            await Respond(m, responseKey, rows.Length == 0 ? "Подтверждённых будущих дедлайнов пока нет." :
                Conversation.Limit(string.Join("\n\n", rows.Select(d => Deadlines.Facts(config, d))), 3900), ct); return;
        }
        if (isPrivate && command == "/subjects")
        {
            await Respond(m, responseKey, config.Subjects.Length == 0 ? "Реестр предметов пуст. Добавь subjects в BOT_CONFIG_PATH и перезапусти сервис." : Conversation.Limit(string.Join("\n", config.Subjects.Select(s => $"{s.Id} — {s.Name}, кабинет {s.Room}")), 3900), ct); return;
        }
        if (isPrivate && config.CanEdit(actor)) { await ManageDeadline(m, responseKey, ct); return; }
        if (!isPrivate && !Conversation.CallsBot(m, botId, username)) return;
        if (isPrivate && command is "/deadline" or "/edit" or "/move" or "/cancel")
        {
            await Respond(m, responseKey, "Дедлайны меняют только разработчик и староста. Я могу показать их через /deadlines.", ct); return;
        }
        if (store.Post("answer:" + responseKey) is null && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - m.Number("date") > 600) return;
        if (store.Post("answer:" + responseKey) is null && !store.ChatBudget(actor, DateTimeOffset.UtcNow))
        {
            await Respond(m, responseKey, "На сегодня лимит болтовни со мной закончился. Дедлайны продолжают работать.", ct); return;
        }
        await Chat(m, responseKey, isPrivate, ct);
    }

    private async Task Chat(JsonElement m, string key, bool isPrivate, CancellationToken ct)
    {
        var chat = m.Field("chat").Number("id"); var id = (int)m.Number("message_id");
        var topic = m.TopicId(); var loadingKey = "loading:" + key;
        var old = store.Post(loadingKey);
        if (old is not null)
        {
            try { await telegram.Delete(chat, old.MessageId, ct); } catch (TelegramError) { }
            store.ForgetPost(loadingKey);
        }
        try { await telegram.React(chat, id, "👀", ct); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            try { await telegram.React(chat, id, "👍", ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        int? loadingId = null;
        try
        {
            var loading = await telegram.Send(chat, topic, "⏳ Сейчас посмотрю…", ct, reply: id);
            loadingId = (int)loading.Number("message_id");
            store.SavePost(loadingKey, loadingId.Value, "");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        { log.LogWarning("Loading indicator failed: {Error}", Failure(e)); }
        var answerKey = "answer:" + key;
        var answer = store.Post(answerKey)?.Text;
        try
        {
            if (answer is null)
            {
                try { answer = await ai.Chat(conversation.Context(m, isPrivate), ct); }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning("AI chat failed: {Error}", Failure(e));
                    answer = "Сейчас не достучался до нейронки. Попробуй чуть позже — сохранённые дедлайны от этого не пропали.";
                }
                answer = Conversation.Limit(answer, 3900);
                store.SavePost(answerKey, 0, answer);
            }
            await Respond(m, key, answer, ct);
            store.ForgetPost(answerKey);
        }
        finally
        {
            if (loadingId.HasValue && !ct.IsCancellationRequested)
            {
                try { await telegram.Delete(chat, loadingId.Value, ct); store.ForgetPost(loadingKey); }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    try { await telegram.Edit(chat, loadingId.Value, "Запрос обработан. Если ответа ещё нет, доставка будет повторена.", ct); }
                    catch (Exception) when (!ct.IsCancellationRequested) { }
                }
            }
        }
    }

    private async Task Respond(JsonElement to, string key, string text, CancellationToken ct, object? keyboard = null)
    {
        var sent = await telegram.Send(to.Field("chat").Number("id"), to.TopicId(), text, ct,
            keyboard, (int)to.Number("message_id"));
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
