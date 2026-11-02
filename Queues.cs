using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

/// <summary>
/// Submission queues in «Очередь на сдачу»: started by an editor (message or web admin), one message with ✋/🚪 buttons,
/// edited on every change; after the lesson the buttons go and the list stays, marked as closed.
/// </summary>
public sealed partial class BotWorker
{
    public const string QueuePrefix = "queue:";
    public const int QueueSlots = 10;
    private readonly SemaphoreSlim queueGate = new(1);

    /// <summary>Opens a queue for the subject's lesson on that date and posts it. Error — a Russian reason, nothing created.</summary>
    internal async Task<(SubmissionQueue? Queue, string? Error, bool Existing)> CreateQueue(long actor, string? subjectId, string? date, int? period, string? work, CancellationToken ct)
    {
        if (config.QueueTopicId <= 0) return (null, "Тема «Очередь на сдачу» не настроена (queueTopicId в конфиге).", false);
        var r = config.R;
        if (r.Subject(subjectId) is not { } subject) return (null, "По какому предмету очередь?", false);
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var day)) return (null, "На какой день очередь?", false);
        var slots = Schedule.Day(r, day).Where(s => s.SubjectId == subject.Id && s.Status != "cancelled").ToList();
        if (period is null)
        {
            var pool = slots.Any(s => s.Kind != "lecture") ? slots.Where(s => s.Kind != "lecture").ToList() : slots;
            var candidates = pool.Select(s => s.Period).Distinct().ToArray();
            if (candidates.Length != 1)
                return (null, candidates.Length == 0 ? $"В расписании нет пары «{subject.Name}» {day:dd.MM}. На какой паре сдаём?"
                    : $"{day:dd.MM} несколько пар «{subject.Name}»: {string.Join(", ", candidates)}. На какой сдаём?", false);
            period = candidates[0];
        }
        var slot = slots.FirstOrDefault(s => s.Period == period);
        if (slot is null && r.PeriodOf(period.Value) is null) return (null, $"Нет {period}-й пары в звонках.", false);
        var (start, end) = slot is not null ? (slot.Start, slot.End) : (r.PeriodOf(period.Value)!.Start, r.PeriodOf(period.Value)!.End);
        var closes = config.Utc(day.ToDateTime(TimeOnly.Parse(end)));
        if (closes <= DateTimeOffset.UtcNow) return (null, "Эта пара уже закончилась — очередь не открыть.", false);
        if (store.Queues(200).FirstOrDefault(q => q.SubjectId == subject.Id && q.Date == date && q.Period == period) is { } existing)
            return (existing, null, true);
        work ??= store.AllDeadlines().FirstOrDefault(d => d.Status == "active" && d.SubjectId == subject.Id && d.Date == date && d.Period == period)?.Work;
        var room = slot?.RoomText is { Length: > 0 } rt ? rt : subject.Room ?? "";
        var id = store.AddQueue(subject.Id, string.IsNullOrWhiteSpace(work) ? null : Conversation.Limit(work.Trim(), 200), date!, period.Value, start, end, room,
            closes.ToUnixTimeSeconds(), actor, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        try { await SyncQueue(id, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning("Queue {Id} not posted yet ({Type})", id, Failure(e)); }
        return (store.Queue(id), null, false);
    }

    /// <summary>An editor asked for a queue in words. False — something is missing (the planner remembers the request).</summary>
    private async Task<bool> StartQueue(JsonElement m, string key, long actor, ParsedDeadline? p, CancellationToken ct)
    {
        var (q, error, existing) = await CreateQueue(actor, p?.SubjectId, p?.Date, p?.Period, p?.Work, ct);
        if (q is null) { await Respond(m, key, error!, ct); return false; }
        var day = DateOnly.Parse(q.Date);
        var what = $"«{H(config.R.SubjectName(q.SubjectId))}», {Schedule.DayShort[Schedule.Dow(day)].ToLowerInvariant()} {day:dd.MM}, {q.Period} пара";
        await Respond(m, key, existing
            ? $"📋 Очередь {what} уже есть" + (q.Status == "open" ? "." : " и уже закрыта.")
            : $"📋 Открыл очередь {what} в «Очереди на сдачу»" + (q.MessageId > 0 ? "" : " (отправлю в течение минуты)") +
              $". Кнопки пропадут в {q.End}.", ct, html: true);
        return true;
    }

    /// <summary>The queue message: what and when, numbered places (at least 10, one free place more when full), the state.</summary>
    internal string QueueHtml(SubmissionQueue q, IReadOnlyList<QueueEntry> entries)
    {
        var r = config.R;
        var day = DateOnly.Parse(q.Date);
        var open = q.Status == "open";
        string Name(QueueEntry e) => H(r.Members.FirstOrDefault(m => m.Key == e.Member)?.FullName ?? e.Name);
        string Place(int i) => i switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => $"{i + 1}." };
        var count = open ? Math.Max(QueueSlots, entries.Count + 1) : entries.Count;
        var lines = Enumerable.Range(0, count).Select(i => i < entries.Count ? $"{Place(i)} {Name(entries[i])}" : $"{i + 1}. ———");
        var where = string.IsNullOrWhiteSpace(q.Room) ? "" : $" · 🚪 <code>{H(q.Room)}</code>";
        return $"📋 <b>Очередь на сдачу</b>\n<b>{H(r.SubjectName(q.SubjectId))}</b>" + (q.Work is null ? "" : " — " + H(q.Work)) +
            $"\n📅 <b>{Schedule.DayShort[Schedule.Dow(day)].ToLowerInvariant()}, {day:dd.MM}</b> · {q.Period} пара · {q.Start}–{q.End}{where}\n\n" +
            (count == 0 ? "<i>Никто не записался.</i>" : "<blockquote>" + string.Join("\n", lines) + "</blockquote>") +
            (open ? $"\n👥 В очереди: <b>{entries.Count}</b> · запись до {config.LocalNow(DateTimeOffset.FromUnixTimeSeconds(q.Closes)):HH:mm}" : $"\n🔒 <b>Очередь закрыта</b> · сдавали: {entries.Count}");
    }
    private static object QueueButtons(long id) => new
    {
        inline_keyboard = new[] { new[] { new { text = "✋ Записаться", callback_data = $"{QueuePrefix}j:{id}" }, new { text = "🚪 Выйти", callback_data = $"{QueuePrefix}l:{id}" } } }
    };

    /// <summary>Brings the Telegram message up to the queue's current version: posts it, edits it, or deletes it.</summary>
    internal async Task SyncQueue(long id, CancellationToken ct)
    {
        await queueGate.WaitAsync(ct);
        try
        {
            if (store.Queue(id) is not { } q || q.Synced == q.Version) return;
            if (q.Status == "deleted")
            {
                if (q.MessageId > 0)
                    try { await telegram.Delete(config.GroupId, q.MessageId, ct); }
                    catch (TelegramError e) when (e.Code == 400) { }
                store.QueueSynced(id, q.Version);
                return;
            }
            var html = QueueHtml(q, store.QueueEntries(id));
            var keyboard = q.Status == "open" ? QueueButtons(id) : null;
            if (q.MessageId > 0)
                try { await telegram.Edit(config.GroupId, q.MessageId, html, ct, keyboard, html: true); }
                catch (TelegramError e) when (e.Code == 400 && e.Description.Contains("not found", StringComparison.OrdinalIgnoreCase)) { q = q with { MessageId = 0 }; }
            if (q.MessageId == 0)
            {
                var sent = await telegram.Send(config.GroupId, config.QueueTopicId, html, ct, keyboard, html: true);
                store.SetQueueMessage(id, (int)sent.Number("message_id"));
            }
            store.QueueSynced(id, q.Version);
        }
        finally { queueGate.Release(); }
    }

    /// <summary>Locks queues whose lesson is over and retries messages that are behind.</summary>
    internal async Task QueueTick(DateTimeOffset utc, CancellationToken ct)
    {
        if (config.QueueTopicId <= 0) return;
        foreach (var q in store.QueuesToTend(utc.ToUnixTimeSeconds()))
        {
            if (q.Status == "open" && q.Closes <= utc.ToUnixTimeSeconds()) store.SetQueueStatus(q.Id, "locked");
            try { await SyncQueue(q.Id, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning("Queue {Id} not updated ({Type})", q.Id, Failure(e)); }
        }
    }

    /// <summary>✋ / 🚪 under a queue: anyone from the roster, in the group.</summary>
    private async Task QueueCallback(JsonElement cb, long actor, CancellationToken ct)
    {
        var callback = cb.Text("id") ?? "";
        var parts = (cb.Text("data") ?? "").Split(':');
        if (parts.Length != 3 || !long.TryParse(parts[2], out var id)) return;
        if (config.FindMember(actor) is not { Key: { } key } member) { await SafeCallback(callback, "Тебя нет в списке группы", ct); return; }
        var q = store.Queue(id);
        if (q is null || q.Status == "deleted") { await SafeCallback(callback, "Этой очереди уже нет", ct); return; }
        if (q.Status == "open" && q.Closes <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) store.SetQueueStatus(id, "locked");
        if (store.Queue(id)!.Status != "open") { await SafeCallback(callback, "Очередь уже закрыта", ct); await SyncQueue(id, ct); return; }
        if (parts[1] == "j")
        {
            var joined = store.JoinQueue(id, key, actor, member.FullName, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await SafeCallback(callback, joined is not { } j ? "Очередь уже закрыта" : j.Added ? $"Записал: ты {j.Position}-й" : $"Ты уже в очереди: {j.Position}-й", ct);
        }
        else await SafeCallback(callback, store.LeaveQueue(id, key) ? "Вычеркнул, очередь сдвинулась" : "Тебя и так нет в очереди", ct);
        await SyncQueue(id, ct);
    }

    /// <summary>The web admin's changes: add, remove, up, down, lock, reopen (optionally until HH:mm), delete.</summary>
    internal async Task<string?> AdminQueue(long id, string action, string? member, string? until, CancellationToken ct)
    {
        if (store.Queue(id) is not { Status: not "deleted" } q) return "Нет такой очереди.";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        switch (action)
        {
            case "add":
                if (config.R.Members.FirstOrDefault(m => m.Key == member && m.Active) is not { } who) return "Нет такого участника.";
                if (store.JoinQueue(id, who.Key!, who.UserId ?? 0, who.FullName, now, force: true) is not { Added: true }) return "Он уже в очереди.";
                break;
            case "remove":
                if (!store.LeaveQueue(id, member ?? "", force: true)) return "Его нет в очереди.";
                break;
            case "up" or "down":
                if (!store.MoveInQueue(id, member ?? "", action == "up" ? -1 : 1)) return "Дальше двигать некуда.";
                break;
            case "lock":
                if (q.Status != "open") return "Очередь уже закрыта.";
                store.SetQueueStatus(id, "locked");
                break;
            case "reopen":
                long closes = q.Closes;
                if (until is not null)
                {
                    if (!TimeOnly.TryParseExact(until, "HH:mm", out var t)) return "Время — ЧЧ:ММ.";
                    closes = config.Utc(DateOnly.Parse(q.Date).ToDateTime(t)).ToUnixTimeSeconds();
                }
                if (closes <= now) return "Это время уже прошло — укажи, до скольки держать очередь открытой.";
                store.SetQueueStatus(id, "open", closes);
                break;
            case "delete":
                store.SetQueueStatus(id, "deleted");
                break;
            default: return "Неизвестное действие.";
        }
        try { await SyncQueue(id, ct); }
        catch (TelegramError e) { log.LogWarning("Queue {Id} not updated, the tick retries ({Type})", id, Failure(e)); }
        return null;
    }
}
