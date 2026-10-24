using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

/// <summary>
/// The bot's own initiative: a morning message, random drops, evening reminders about tomorrow's submissions,
/// AI-planned wake-ups before each deadline and scheduled posts. Every job is marked done before it runs, so a crash
/// can lose one message but never doubles it; jobs more than two hours late are skipped instead of flooding the chat.
/// </summary>
public sealed partial class BotWorker
{
    private static readonly TimeSpan Late = TimeSpan.FromHours(2);
    private readonly SemaphoreSlim ambientGate = new(1);

    private async Task AmbientLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await AmbientTick(DateTimeOffset.UtcNow, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning("Ambient tick failed ({Type})", Failure(e)); }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    internal async Task AmbientTick(DateTimeOffset utc, CancellationToken ct)
    {
        var a = config.Ambient;
        if (!a.Enabled) return;
        var local = config.LocalNow(utc);
        var today = DateOnly.FromDateTime(local);
        var date = today.ToString("yyyy-MM-dd");
        if (config.SummaryTopicId > 0) await Job($"ambient:summary:{date}", today, a.SummaryTime, local, k => MorningSummary(today, ct, TakePrep(k)));
        if (a.MorningTime.Length > 0) await Job($"ambient:morning:{date}", today, a.MorningTime, local, k => PostAmbient("morning", null, ct, TakePrep(k), config.ChatTopicId));
        foreach (var (i, time) in SpontaneousPlan(today).Select((t, i) => (i, t)))
            await Job($"ambient:spont:{date}:{i}", today, time, local, k => PostAmbient("spontaneous", null, ct, TakePrep(k), config.ChatTopicId));
        if (a.EveReminders) await Job($"ambient:eve:{date}", today, a.EveReminderTime, local, k => EveReminder(today.AddDays(1), ct, TakePrep(k)));
        await PlanMissingWakeups(utc, ct);
        foreach (var w in store.DueWakeups(utc.ToUnixTimeSeconds()))
        {
            var late = utc.ToUnixTimeSeconds() - w.Due > Late.TotalSeconds;
            if (!store.SetWakeup(w.Id, late ? "skipped" : "fired")) continue;
            if (!late) await FireWakeup(w, utc, ct);
        }
    }

    private async Task Job(string key, DateOnly day, string time, DateTime local, Func<string, Task> run)
    {
        if (store.Get(key) is not null) return;
        var due = day.ToDateTime(TimeOnly.Parse(time));
        if (local < due) return;
        var late = local - due > Late;
        store.Set(key, late ? "skipped" : "done");
        if (!late) await run(key);
        else store.Delete("prep:" + key);
    }

    /// <summary>Random drop times for a day, chosen once and stored so restarts do not reshuffle them.</summary>
    internal List<string> SpontaneousPlan(DateOnly day)
    {
        var key = $"ambient:plan:{day:yyyy-MM-dd}";
        if (store.Get(key) is { } saved) return Json.Read<List<string>>(saved);
        var a = config.Ambient;
        var from = TimeOnly.Parse(a.ActiveFrom).ToTimeSpan().TotalMinutes;
        var to = TimeOnly.Parse(a.ActiveTo).ToTimeSpan().TotalMinutes;
        var plan = Enumerable.Range(0, a.SpontaneousPerDay).Where(_ => Random.Shared.NextDouble() < a.SpontaneousChance)
            .Select(_ => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(from + Random.Shared.NextDouble() * (to - from))).ToString("HH:mm"))
            .Order().ToList();
        store.Set(key, Json.Write(plan));
        return plan;
    }

    /// <summary>
    /// Generates and posts the bot's own message. Default target is «Сводка» (announcements and reminders);
    /// only drops in Воздуханство are split into several chat-like messages.
    /// </summary>
    internal async Task<string?> PostAmbient(string kind, object? extra, CancellationToken ct, string? ready = null, int? topic = null)
    {
        await ambientGate.WaitAsync(ct);
        try
        {
            var target = topic ?? ReminderTopic;
            var chatty = target == config.ChatTopicId;
            if (ready == OpenRouterClient.Skip) return null; // a prepared "stay silent"
            var text = ready ?? await GenerateAmbient(kind, extra, ct, chatty);
            if (text is null) return null;
            var parts = chatty && config.SplitReplies ? OpenRouterClient.Parts(text) : [OpenRouterClient.Join(text)];
            await SendParts(config.GroupId, target, parts, null, chatty ? 0 : double.MaxValue, ct);
            conversation.AddRecentPost(text);
            return text;
        }
        finally { ambientGate.Release(); }
    }
    internal async Task<string?> GenerateAmbient(string kind, object? extra, CancellationToken ct, bool split = false)
    {
        string text;
        try { text = await ai.Ambient(kind, conversation.AdminContext(false), conversation.AmbientContext(kind, extra), split, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning("Ambient {Kind} generation failed: {Error}", kind, Failure(e)); return null; }
        text = Conversation.Limit(Json.Unescape(text).Trim(), 3900);
        return text.StartsWith(OpenRouterClient.Skip, StringComparison.OrdinalIgnoreCase) || text.Length == 0 ? null : text;
    }

    /// <summary>Human-paced multi-part sending with HTML and a plain-text fallback; every part joins the chat history.</summary>
    private async Task<int?> SendParts(long chat, int topic, IReadOnlyList<string> parts, int? replyTo, double thoughtMs, CancellationToken ct,
        Func<int, string>? partKey = null)
    {
        int? first = null;
        for (var i = 0; i < parts.Count; i++)
        {
            if (partKey is not null && store.Post(partKey(i)) is { } done) { first ??= done.MessageId; continue; }
            await HumanPause(chat, topic, parts[i], i == 0 ? thoughtMs : 0, ct);
            JsonElement sent;
            var html = TelegramHtml.Sanitize(TelegramHtml.FromMarkdown(parts[i]));
            var reply = i == 0 ? replyTo : null;
            try { sent = await telegram.Send(chat, topic, html, ct, reply: reply, html: true); }
            catch (TelegramError e) when (e.Code == 400 && e.Description.Contains("parse", StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("Telegram rejected HTML, sending plain text: {Error}", Failure(e));
                sent = await telegram.Send(chat, topic, TelegramHtml.Plain(html), ct, reply: reply);
            }
            if (partKey is not null) store.SavePost(partKey(i), (int)sent.Number("message_id"), "");
            store.Remember(sent.Field("chat").Number("id"), sent.TopicId(), (int)sent.Number("message_id"), sent.Number("date"), sent.GetRawText());
            first ??= (int)sent.Number("message_id");
        }
        return first;
    }

    /// <summary>Evening reminder in the deadlines topic: creative lead from the model, exact facts from the program.</summary>
    private int ReminderTopic => config.SummaryTopicId > 0 ? config.SummaryTopicId : config.ChatTopicId;
    private int EveTopic => config.SummaryTopicId > 0 ? config.SummaryTopicId : config.DeadlinesTopicId;
    private List<Deadline> DueOn(DateOnly day) =>
        store.AllDeadlines().Where(d => d.Status == "active" && d.Date == day.ToString("yyyy-MM-dd")).OrderBy(d => d.Start).ToList();

    /// <summary>Evening reminder: creative lead from the model (or a prepared one), exact facts from the program.</summary>
    private async Task EveReminder(DateOnly tomorrow, CancellationToken ct, string? lead = null)
    {
        if (DueOn(tomorrow).Count == 0) return;
        lead ??= await GenerateAmbient("eve", new { for_date = tomorrow.AddDays(-1).ToString("yyyy-MM-dd") }, ct);
        await telegram.Send(config.GroupId, EveTopic, ComposeEve(tomorrow, lead)!, ct, html: true);
    }
    internal string? ComposeEve(DateOnly tomorrow, string? lead)
    {
        var due = DueOn(tomorrow);
        if (due.Count == 0) return null;
        var head = TelegramHtml.Sanitize(TelegramHtml.FromMarkdown(OpenRouterClient.Join(lead ?? "⏰ Завтра сдаём:")));
        return Conversation.Limit(head, 600) + "\n\n" + string.Join("\n", due.Select(d => Deadlines.FactsHtml(config, d)));
    }

    /// <summary>Morning post in «Сводка»: lively lead, the day's schedule and the submissions of the next days (exact facts).</summary>
    internal async Task MorningSummary(DateOnly today, CancellationToken ct, string? lead = null)
    {
        lead ??= await GenerateAmbient("summary", new { for_date = today.ToString("yyyy-MM-dd") }, ct);
        var sent = await telegram.Send(config.GroupId, config.SummaryTopicId, ComposeSummary(today, lead), ct, html: true);
        store.Remember(config.GroupId, config.SummaryTopicId, (int)sent.Number("message_id"), sent.Number("date"), sent.GetRawText());
        if (lead is not null) conversation.AddRecentPost(lead);
    }
    internal string ComposeSummary(DateOnly today, string? lead)
    {
        var sb = new System.Text.StringBuilder();
        if (lead is not null) sb.Append(TelegramHtml.Sanitize(TelegramHtml.FromMarkdown(Conversation.Limit(OpenRouterClient.Join(lead), 700)))).Append("\n\n");
        sb.Append(ScheduleHtml.Day(config.R, today, DateOnly.FromDateTime(config.LocalNow(DateTimeOffset.UtcNow)) == today ? "Сегодня" : null));
        var soon = store.AllDeadlines().Where(d => d.Status == "active" && Deadlines.Ends(config, d) > DateTimeOffset.UtcNow &&
            DateOnly.Parse(d.Date) >= today && DateOnly.Parse(d.Date) <= today.AddDays(3)).OrderBy(d => d.Date).ThenBy(d => d.Start).ToList();
        if (soon.Count > 0)
        {
            sb.Append("\n\n⏰ <b>Сдачи</b>");
            foreach (var d in soon)
            {
                var day = DateOnly.Parse(d.Date);
                var when = day == today ? "<b>СЕГОДНЯ</b>" : day == today.AddDays(1) ? "завтра" : $"{Schedule.DayShort[Schedule.Dow(day)].ToLowerInvariant()} {day:dd.MM}";
                sb.Append($"\n• {when}, {d.Period} пара ({d.Start}) — <b>{System.Net.WebUtility.HtmlEncode(config.R.SubjectName(d.SubjectId))}</b>: {System.Net.WebUtility.HtmlEncode(d.Work)} · <code>{System.Net.WebUtility.HtmlEncode(d.Room)}</code>");
            }
        }
        return Conversation.Limit(sb.ToString(), 4000);
    }

    private async Task FireWakeup(Wakeup w, DateTimeOffset utc, CancellationToken ct)
    {
        var ready = TakePrep("wake:" + w.Id);
        if (w.Deadline == 0) { await PostAmbient("manual", new { idea = w.Reason }, ct, ready); return; } // a scheduled post
        var d = store.AllDeadlines().FirstOrDefault(x => x.Id == w.Deadline);
        if (d is null || d.Status != "active" || Deadlines.Ends(config, d) < utc) return;
        await PostAmbient("wakeup", WakeupExtra(w, d, utc), ct, ready);
    }
    private object WakeupExtra(Wakeup w, Deadline d, DateTimeOffset at)
    {
        var left = config.Utc(DateOnly.Parse(d.Date).ToDateTime(TimeOnly.Parse(d.Start))) - at;
        var timeLeft = left.TotalHours < 1 ? $"{Math.Max(0, (int)left.TotalMinutes)} мин" : left.TotalHours < 48 ? $"{(int)left.TotalHours} ч" : $"{(int)left.TotalDays} дн";
        return new { for_time = config.LocalNow(at).ToString("yyyy-MM-dd HH:mm"), wakeup = new { deadline = conversation.DeadlineView(d), reason = w.Reason, time_left = timeLeft } };
    }

    /// <summary>
    /// The lively lead of a deadline post (create / update / cancel), written in the bot's voice with full context.
    /// The editor's note is backstage context for the model, never printed by itself.
    /// </summary>
    internal async Task<string> DeadlineIntro(Draft d, CancellationToken ct)
    {
        var old = store.AllDeadlines().FirstOrDefault(x => x.Id == d.TargetId);
        var start = d.Date is null || d.Start is null ? (DateTimeOffset?)null : config.Utc(DateOnly.Parse(d.Date).ToDateTime(TimeOnly.Parse(d.Start)));
        var left = start - DateTimeOffset.UtcNow;
        var text = await GenerateAmbient("deadline", new
        {
            deadline_post = new
            {
                action = d.Action, subject = config.R.SubjectName(d.SubjectId), work = d.Work, date = d.Date,
                weekday = d.Date is null ? null : Schedule.DayNames[Schedule.Dow(DateOnly.Parse(d.Date))], period = d.Period, start = d.Start, room = d.Room,
                time_left = left is null ? null : left.Value.TotalHours < 48 ? $"{Math.Max(0, (int)left.Value.TotalHours)} ч" : $"{(int)left.Value.TotalDays} дн",
                backstage_note = d.Note,
                previous = old is null ? null : new { old.Date, old.Period, old.Start, old.Room, old.Work }
            }
        }, ct);
        return Conversation.Limit(OpenRouterClient.Join(text ?? (d.Action switch
        {
            "cancel" => "Отбой, эту сдачу отменили.",
            "update" => "Гайс, по сдаче новости.",
            _ => "Гайс, держим в голове."
        })), 700);
    }

    // ---------- Manual edits of anything the bot already sent ----------

    internal async Task EditSent(long chat, int message, string html, CancellationToken ct)
    {
        await telegram.Edit(chat, message, TelegramHtml.Sanitize(TelegramHtml.FromMarkdown(html)), ct, html: true);
        // The publisher must not "repair" a hand-edited card back to its generated text.
        store.Set($"manual_msg:{chat}:{message}", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    }
    internal Task<string> TestModel(string model, CancellationToken ct) =>
        ai.Complete("Ответь ровно одним словом по-русски.", "Скажи «работаю».", false, ct, "test", "admin-test", model);
    internal async Task DeleteSent(long chat, int message, CancellationToken ct) => await telegram.Delete(chat, message, ct);

    // ---------- Warm-up: prepare a planned post in advance, review and edit it ----------

    public sealed record Prep(string Text, long At, string Source);
    public Prep? Prepared(string key) => store.Get("prep:" + key) is { Length: > 2 } s ? Json.Read<Prep>(s) : null;
    private string? TakePrep(string key)
    {
        var p = Prepared(key);
        if (p is not null) store.Delete("prep:" + key);
        return p?.Text;
    }
    public void SavePrep(string key, string text, string source) =>
        store.Set("prep:" + key, Json.Write(new Prep(text, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), source)));
    public void ClearPrep(string key) => store.Delete("prep:" + key);

    /// <summary>Generates the text a planned job would post, without posting. Keys: ambient:{summary|morning|eve}:{date}, ambient:spont:{date}:{i}, wake:{id}.</summary>
    internal async Task<Prep?> Warm(string key, CancellationToken ct)
    {
        var parts = key.Split(':');
        string? text;
        if (parts[0] == "wake" && long.TryParse(parts[1], out var id))
        {
            var w = store.Wakeups().FirstOrDefault(x => x.Id == id && x.Status == "pending") ?? throw new InvalidOperationException("Будильник уже сработал или удалён.");
            var at = DateTimeOffset.FromUnixTimeSeconds(w.Due);
            if (w.Deadline == 0) text = await GenerateAmbient("manual", new { idea = w.Reason, for_time = config.LocalNow(at).ToString("yyyy-MM-dd HH:mm") }, ct);
            else
            {
                var d = store.AllDeadlines().FirstOrDefault(x => x.Id == w.Deadline && x.Status == "active") ?? throw new InvalidOperationException("Дедлайн уже не активен.");
                text = await GenerateAmbient("wakeup", WakeupExtra(w, d, at), ct);
            }
        }
        else if (parts.Length >= 3 && parts[0] == "ambient" && DateOnly.TryParseExact(parts[2], "yyyy-MM-dd", out var day))
        {
            if (store.Get(key) is not null) throw new InvalidOperationException("Это событие уже прошло.");
            var forDate = new { for_date = parts[2] };
            text = parts[1] switch
            {
                "summary" => await GenerateAmbient("summary", forDate, ct),
                "morning" => await GenerateAmbient("morning", forDate, ct, split: true),
                "eve" => DueOn(day.AddDays(1)).Count == 0 ? throw new InvalidOperationException("На следующий день сдач нет — напоминания не будет.")
                    : await GenerateAmbient("eve", forDate, ct),
                "spont" => await GenerateAmbient("spontaneous", null, ct, split: true) ?? OpenRouterClient.Skip,
                _ => throw new InvalidOperationException("Неизвестное событие.")
            };
        }
        else throw new InvalidOperationException("Неизвестное событие.");
        if (text is null) throw new InvalidOperationException("Нейронка не ответила — попробуй ещё раз.");
        SavePrep(key, text, "ai");
        return Prepared(key);
    }
    /// <summary>What exactly will be posted, as Telegram HTML, for the admin preview.</summary>
    public string? Preview(string key, string text)
    {
        if (text == OpenRouterClient.Skip) return "<i>Бот решил промолчать: повода нет.</i>";
        var parts = key.Split(':');
        if (parts[0] == "ambient" && DateOnly.TryParseExact(parts[2], "yyyy-MM-dd", out var day))
        {
            if (parts[1] == "summary") return ComposeSummary(day, text);
            if (parts[1] == "eve") return ComposeEve(day.AddDays(1), text);
            if (parts[1] is "morning" or "spont") return string.Join("\n<hr>\n", OpenRouterClient.Parts(text).Select(p => TelegramHtml.Sanitize(TelegramHtml.FromMarkdown(p))));
        }
        return TelegramHtml.Sanitize(TelegramHtml.FromMarkdown(OpenRouterClient.Join(text)));
    }

    /// <summary>Every active future deadline gets an AI wake-up plan once per version; edits and moves re-plan it.</summary>
    private async Task PlanMissingWakeups(DateTimeOffset utc, CancellationToken ct)
    {
        foreach (var d in store.AllDeadlines().Where(d => d.Status == "active" && Deadlines.Ends(config, d) > utc))
        {
            var key = $"wakeplan:{d.Id}";
            if (store.Get(key) == d.Version.ToString()) continue;
            store.Set(key, d.Version.ToString());
            await PlanWakeups(d, utc, ct);
        }
        foreach (var d in store.AllDeadlines().Where(d => d.Status != "active")) store.DropPendingWakeups(d.Id);
    }
    internal async Task<int> PlanWakeups(Deadline d, DateTimeOffset utc, CancellationToken ct)
    {
        store.DropPendingWakeups(d.Id, "ai");
        var start = config.Utc(DateOnly.Parse(d.Date).ToDateTime(TimeOnly.Parse(d.Start)));
        List<OpenRouterClient.WakeupPlan> plan;
        try
        {
            plan = await ai.PlanWakeups(new
            {
                local_now = config.LocalNow(utc).ToString("yyyy-MM-dd HH:mm dddd", new System.Globalization.CultureInfo("ru-RU")),
                deadline = conversation.DeadlineView(d),
                lessons_before = Enumerable.Range(0, Math.Clamp((DateOnly.Parse(d.Date).DayNumber - DateOnly.FromDateTime(config.LocalNow(utc)).DayNumber) + 1, 1, 21))
                    .Select(i => DateOnly.FromDateTime(config.LocalNow(utc)).AddDays(i))
                    .Select(x => new { date = x.ToString("yyyy-MM-dd"), lessons = Schedule.Day(config.R, x).Where(s => s.Status != "cancelled").Select(s => $"{s.Period} {s.Start} {s.Subject}") }),
                other_deadlines = store.AllDeadlines().Where(x => x.Status == "active" && x.Id != d.Id && Deadlines.Ends(config, x) > utc).Take(15).Select(conversation.DeadlineView)
            }, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Wake-up planning failed, using defaults: {Error}", Failure(e));
            plan = [];
        }
        var accepted = new List<DateTimeOffset>();
        foreach (var p in plan.OrderBy(p => p.At))
        {
            var at = config.Utc(p.At);
            var t = TimeOnly.FromDateTime(p.At);
            if (at <= utc.AddMinutes(5) || at >= start || (t < new TimeOnly(8, 0) || t > new TimeOnly(23, 30)) ||
                accepted.Any(x => Math.Abs((x - at).TotalHours) < 2) || accepted.Count >= 5) continue;
            accepted.Add(at);
            store.AddWakeup(d.Id, at.ToUnixTimeSeconds(), string.IsNullOrWhiteSpace(p.Reason) ? "напомнить" : p.Reason, "ai");
        }
        if (accepted.Count == 0)
        {
            // Sensible defaults when the model is unavailable or proposed nothing valid.
            foreach (var (at, why) in new[] { (start.AddDays(-3), "за три дня"), (start.AddHours(-20), "накануне"), (start.AddHours(-2), "прямо перед парой") })
                if (at > utc.AddMinutes(5) && TimeOnly.FromDateTime(config.LocalNow(at)) is var lt && lt >= new TimeOnly(8, 0) && lt <= new TimeOnly(23, 30))
                { store.AddWakeup(d.Id, at.ToUnixTimeSeconds(), why, "ai"); accepted.Add(at); }
        }
        return accepted.Count;
    }

    // ---------- Manual trigger by the owner: /post [идея] ----------

    private async Task PostPreview(JsonElement m, string key, long actor, string? idea, CancellationToken ct)
    {
        var text = await GenerateAmbient("manual", new { idea, requested_at = config.LocalNow(DateTimeOffset.UtcNow).ToString("HH:mm") }, ct);
        if (text is null) { await Respond(m, key, "Не получилось сгенерировать — нейронка молчит. Попробуй ещё раз.", ct); return; }
        var token = Guid.NewGuid().ToString("N")[..12];
        store.Set($"postdraft:{actor}", Json.Write(new { token, idea, text }));
        await Respond(m, key, PostCard(text), ct, PostButtons(token), html: true);
    }
    private static string PostCard(string text) =>
        "📝 <b>Черновик</b> — куда отправить?\n\n" + TelegramHtml.Sanitize(TelegramHtml.FromMarkdown(OpenRouterClient.Join(text)));
    private static object PostButtons(string token) => new
    {
        inline_keyboard = new[]
        {
            new[] { new { text = "📋 В Сводку", callback_data = "apost:" + token }, new { text = "💬 В Воздуханство", callback_data = "achat:" + token } },
            new[] { new { text = "Ещё вариант", callback_data = "aredo:" + token }, new { text = "Не надо", callback_data = "ano:" + token } }
        }
    };
    private async Task PostCallback(JsonElement cb, string action, string token, CancellationToken ct)
    {
        var actor = cb.Field("from").Number("id");
        var messageId = (int)cb.Field("message").Number("message_id");
        var raw = store.Get($"postdraft:{actor}");
        using var doc = raw is null ? null : JsonDocument.Parse(raw);
        if (doc is null || doc.RootElement.Text("token") != token) { await SafeCallback(cb.Text("id")!, "Черновик уже обработан.", ct); return; }
        var idea = doc.RootElement.Text("idea");
        switch (action)
        {
            case "ano":
                store.Set($"postdraft:{actor}", "{}");
                await SafeCallback(cb.Text("id")!, "Ок", ct);
                await telegram.Edit(actor, messageId, "Не отправлено.", ct);
                return;
            case "aredo":
                await SafeCallback(cb.Text("id")!, "Пишу заново…", ct);
                var text = await GenerateAmbient("manual", new { idea, requested_at = config.LocalNow(DateTimeOffset.UtcNow).ToString("HH:mm"), previous_variant = doc.RootElement.Text("text") }, ct);
                if (text is null) { await telegram.Send(actor, 0, "Нейронка молчит, попробуй ещё раз.", ct); return; }
                var next = Guid.NewGuid().ToString("N")[..12];
                store.Set($"postdraft:{actor}", Json.Write(new { token = next, idea, text }));
                await telegram.Edit(actor, messageId, PostCard(text), ct, PostButtons(next), html: true);
                return;
            default:
                store.Set($"postdraft:{actor}", "{}");
                await SafeCallback(cb.Text("id")!, "Отправляю…", ct);
                var chat = action == "achat";
                await PostAmbient("manual", null, ct, doc.RootElement.Text("text"), chat ? config.ChatTopicId : ReminderTopic);
                await telegram.Edit(actor, messageId, chat ? "✅ Отправлено в Воздуханство." : "✅ Отправлено в Сводку.", ct);
                return;
        }
    }
}
